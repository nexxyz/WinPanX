using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WinPanX.Agent.Runtime;
using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Audio;

internal sealed class CoreAudioEndpointCatalog : IEndpointCatalog, IMMNotificationClient
{
    private readonly object _sync = new();
    private MMDeviceEnumerator? _enumerator;
    private List<EndpointDescriptor> _virtualSlots = [];
    private EndpointDescriptor? _outputDevice;
    private string _virtualEndpointNamePrefix = string.Empty;
    private string _outputDevicePreference = "default";
    private int _slotCount;
    private bool _initialized;
    private bool _notificationRegistered;
    private bool _disposed;

    public event EventHandler<OutputDeviceChangedEventArgs>? OutputDeviceChanged;

    public Task<EndpointCatalogSnapshot> InitializeAsync(
        string virtualEndpointNamePrefix,
        int slotCount,
        string outputDevicePreference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(virtualEndpointNamePrefix))
        {
            throw new ArgumentException("Virtual endpoint name prefix is required.", nameof(virtualEndpointNamePrefix));
        }

        if (slotCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount), "Slot count must be positive.");
        }

        if (string.IsNullOrWhiteSpace(outputDevicePreference))
        {
            throw new ArgumentException("Output device preference is required.", nameof(outputDevicePreference));
        }

        lock (_sync)
        {
            ThrowIfDisposed();

            _virtualEndpointNamePrefix = virtualEndpointNamePrefix;
            _slotCount = slotCount;
            _outputDevicePreference = outputDevicePreference;
            _enumerator ??= new MMDeviceEnumerator();

            _virtualSlots = DiscoverVirtualSlotsLocked();
            _outputDevice = ResolveOutputDeviceLocked(_outputDevicePreference, _virtualSlots);

            RegisterNotificationCallbackLocked();
            _initialized = true;

            return Task.FromResult(CreateSnapshotLocked());
        }
    }

    public IReadOnlyList<EndpointDescriptor> GetVirtualSlots()
    {
        lock (_sync)
        {
            ThrowIfNotInitialized();
            return new ReadOnlyCollection<EndpointDescriptor>(_virtualSlots.ToList());
        }
    }

    public EndpointDescriptor GetOverflowSlot()
    {
        lock (_sync)
        {
            ThrowIfNotInitialized();
            return _virtualSlots.Single(s => s.IsOverflowSlot);
        }
    }

    public EndpointDescriptor GetOutputDevice()
    {
        lock (_sync)
        {
            ThrowIfNotInitialized();
            return _outputDevice!;
        }
    }

    public Task<EndpointCatalogSnapshot> RefreshOutputDeviceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EndpointDescriptor? changedDevice = null;
        EndpointCatalogSnapshot snapshot;

        lock (_sync)
        {
            ThrowIfNotInitialized();

            var previous = _outputDevice!;
            var refreshed = ResolveOutputDeviceLocked(_outputDevicePreference, _virtualSlots);
            _outputDevice = refreshed;

            if (!string.Equals(previous.EndpointId, refreshed.EndpointId, StringComparison.OrdinalIgnoreCase))
            {
                changedDevice = refreshed;
            }

            snapshot = CreateSnapshotLocked();
        }

        if (changedDevice is not null)
        {
            OutputDeviceChanged?.Invoke(this, new OutputDeviceChangedEventArgs(changedDevice, DateTime.UtcNow));
        }

        return Task.FromResult(snapshot);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            if (_notificationRegistered && _enumerator is not null)
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
                _notificationRegistered = false;
            }

            _enumerator?.Dispose();
            _enumerator = null;
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        if (!string.Equals(_outputDevicePreference, "default", StringComparison.OrdinalIgnoreCase))
        {
            TryRefreshExplicitOutput(deviceId);
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
    }

    public void OnDeviceRemoved(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        if (!string.Equals(_outputDevicePreference, "default", StringComparison.OrdinalIgnoreCase))
        {
            TryRefreshExplicitOutput(deviceId);
        }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render)
        {
            return;
        }

        if (!string.Equals(_outputDevicePreference, "default", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryRefreshDefaultOutput();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    private void TryRefreshDefaultOutput()
    {
        EndpointDescriptor? changedDevice = null;

        lock (_sync)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            try
            {
                var previous = _outputDevice!;
                var refreshed = ResolveOutputDeviceLocked("default", _virtualSlots);
                _outputDevice = refreshed;

                if (!string.Equals(previous.EndpointId, refreshed.EndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    changedDevice = refreshed;
                }
            }
            catch (Exception ex)
            {
                SimpleLog.Warn($"Failed to refresh default output device: {ex.Message}");
                return;
            }
        }

        if (changedDevice is not null)
        {
            OutputDeviceChanged?.Invoke(this, new OutputDeviceChangedEventArgs(changedDevice, DateTime.UtcNow));
        }
    }

    private void TryRefreshExplicitOutput(string changedDeviceId)
    {
        EndpointDescriptor? changedDevice = null;

        lock (_sync)
        {
            if (_disposed || !_initialized || _outputDevice is null)
            {
                return;
            }

            var outputMatches = string.Equals(
                _outputDevice.EndpointId,
                changedDeviceId,
                StringComparison.OrdinalIgnoreCase);
            if (!outputMatches)
            {
                return;
            }

            try
            {
                var refreshed = ResolveOutputDeviceLocked(_outputDevicePreference, _virtualSlots);
                _outputDevice = refreshed;
                changedDevice = refreshed;
            }
            catch (Exception ex)
            {
                SimpleLog.Warn($"Failed to refresh configured output device: {ex.Message}");
                return;
            }
        }

        if (changedDevice is not null)
        {
            OutputDeviceChanged?.Invoke(this, new OutputDeviceChangedEventArgs(changedDevice, DateTime.UtcNow));
        }
    }

    private void RegisterNotificationCallbackLocked()
    {
        if (_notificationRegistered)
        {
            return;
        }

        var hr = _enumerator!.RegisterEndpointNotificationCallback(this);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        _notificationRegistered = true;
    }

    private EndpointCatalogSnapshot CreateSnapshotLocked()
    {
        return new EndpointCatalogSnapshot(
            _virtualSlots.ToList(),
            _outputDevice!,
            DateTime.UtcNow);
    }

    private List<EndpointDescriptor> DiscoverVirtualSlotsLocked()
    {
        var matches = new List<DeviceCandidate>();
        var devices = _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (var device in devices)
        {
            using (device)
            {
                if (!device.FriendlyName.StartsWith(_virtualEndpointNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var channels = device.AudioClient.MixFormat.Channels;
                if (channels != 2)
                {
                    throw new InvalidOperationException(
                        $"Virtual endpoint '{device.FriendlyName}' must be stereo (found {channels} channels).");
                }

                matches.Add(new DeviceCandidate(
                    device.ID,
                    device.FriendlyName,
                    ParseSlotIndexFromName(device.FriendlyName, _virtualEndpointNamePrefix)));
            }
        }

        if (matches.Count != _slotCount)
        {
            throw new InvalidOperationException(
                $"Expected exactly {_slotCount} virtual endpoints with prefix '{_virtualEndpointNamePrefix}', found {matches.Count}.");
        }

        var explicitOrderUsable = matches.All(m => m.ParsedSlotIndex is >= 1 and <= int.MaxValue)
            && matches.Select(m => m.ParsedSlotIndex!.Value).Distinct().Count() == matches.Count
            && matches.All(m => m.ParsedSlotIndex!.Value <= _slotCount);

        if (explicitOrderUsable)
        {
            return matches
                .OrderBy(m => m.ParsedSlotIndex)
                .Select(m => new EndpointDescriptor(
                    SlotIndex: m.ParsedSlotIndex!.Value,
                    EndpointId: m.EndpointId,
                    FriendlyName: m.FriendlyName,
                    Channels: 2,
                    IsVirtualSlot: true,
                    IsOverflowSlot: m.ParsedSlotIndex.Value == _slotCount))
                .ToList();
        }

        return matches
            .OrderBy(m => m.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.EndpointId, StringComparer.OrdinalIgnoreCase)
            .Select((m, index) => new EndpointDescriptor(
                SlotIndex: index + 1,
                EndpointId: m.EndpointId,
                FriendlyName: m.FriendlyName,
                Channels: 2,
                IsVirtualSlot: true,
                IsOverflowSlot: index + 1 == _slotCount))
            .ToList();
    }

    private EndpointDescriptor ResolveOutputDeviceLocked(
        string outputDevicePreference,
        IReadOnlyCollection<EndpointDescriptor> virtualSlots)
    {
        if (string.Equals(outputDevicePreference, "default", StringComparison.OrdinalIgnoreCase))
        {
            using var defaultDevice = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            EnsureNotVirtualOutput(defaultDevice.ID, virtualSlots);
            return new EndpointDescriptor(
                SlotIndex: 0,
                EndpointId: defaultDevice.ID,
                FriendlyName: defaultDevice.FriendlyName,
                Channels: defaultDevice.AudioClient.MixFormat.Channels,
                IsVirtualSlot: false,
                IsOverflowSlot: false);
        }

        using var selected = _enumerator!.GetDevice(outputDevicePreference);
        if (selected.DataFlow != DataFlow.Render)
        {
            throw new InvalidOperationException($"Output device '{outputDevicePreference}' is not a render endpoint.");
        }

        EnsureNotVirtualOutput(selected.ID, virtualSlots);
        return new EndpointDescriptor(
            SlotIndex: 0,
            EndpointId: selected.ID,
            FriendlyName: selected.FriendlyName,
            Channels: selected.AudioClient.MixFormat.Channels,
            IsVirtualSlot: false,
            IsOverflowSlot: false);
    }

    private static void EnsureNotVirtualOutput(string outputEndpointId, IReadOnlyCollection<EndpointDescriptor> virtualSlots)
    {
        if (virtualSlots.Any(v => string.Equals(v.EndpointId, outputEndpointId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Selected output device resolves to one of the configured virtual slot endpoints. " +
                "Choose a real output endpoint ID or set a non-virtual default multimedia render device.");
        }
    }

    private static int? ParseSlotIndexFromName(string friendlyName, string prefix)
    {
        if (!friendlyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = friendlyName.Substring(prefix.Length).Trim();
        return int.TryParse(suffix, out var parsed) ? parsed : null;
    }

    private void ThrowIfNotInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("Endpoint catalog is not initialized.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CoreAudioEndpointCatalog));
        }
    }

    private sealed record DeviceCandidate(string EndpointId, string FriendlyName, int? ParsedSlotIndex);
}

