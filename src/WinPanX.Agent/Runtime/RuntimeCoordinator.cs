using System.Threading.Channels;
using WinPanX.Agent.Configuration;
using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Runtime;

public sealed class RuntimeCoordinator : IAsyncDisposable
{
    private readonly WinPanXConfig _config;
    private readonly IAppTracker _appTracker;
    private readonly IEndpointCatalog _endpointCatalog;
    private readonly IRouter _router;
    private readonly IMixer _mixer;
    private readonly SlotAssignmentTable _assignments = new();
    private readonly Dictionary<int, string> _endpointIdBySlot = [];
    private readonly Channel<IReadOnlyCollection<TrackedAppSnapshot>> _snapshotQueue;

    private CancellationTokenSource? _snapshotProcessorCts;
    private Task? _snapshotProcessorTask;
    private volatile bool _isDisposing;

    public RuntimeCoordinator(
        WinPanXConfig config,
        IAppTracker appTracker,
        IEndpointCatalog endpointCatalog,
        IRouter router,
        IMixer mixer)
    {
        _config = config;
        _appTracker = appTracker;
        _endpointCatalog = endpointCatalog;
        _router = router;
        _mixer = mixer;

        _snapshotQueue = Channel.CreateBounded<IReadOnlyCollection<TrackedAppSnapshot>>(
            new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var catalog = await _endpointCatalog.InitializeAsync(
            _config.VirtualEndpointNamePrefix,
            _config.SlotCount,
            _config.OutputDeviceId,
            cancellationToken);

        SimpleLog.Info(
            $"Output device: {catalog.OutputDevice.FriendlyName} [{catalog.OutputDevice.EndpointId}]");
        _endpointIdBySlot.Clear();
        foreach (var slot in catalog.VirtualSlots.OrderBy(s => s.SlotIndex))
        {
            SimpleLog.Info(
                $"Slot {slot.SlotIndex:00}: {slot.FriendlyName} [{slot.EndpointId}]");
            _endpointIdBySlot[slot.SlotIndex] = slot.EndpointId;
        }

        await _mixer.StartAsync(
            new MixerStartRequest(
                InputSlots: catalog.VirtualSlots,
                OutputEndpointId: catalog.OutputDevice.EndpointId,
                OverflowPolicy: _config.OverflowPanPolicy,
                TargetSampleRate: _config.TargetSampleRate,
                Channels: _config.Channels,
                FramesPerBuffer: _config.FramesPerBuffer),
            cancellationToken);

        _snapshotProcessorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _snapshotProcessorTask = Task.Run(
            () => ProcessSnapshotsAsync(_snapshotProcessorCts.Token),
            CancellationToken.None);

        _endpointCatalog.OutputDeviceChanged += OnOutputDeviceChanged;
        _appTracker.SnapshotChanged += OnSnapshotChanged;

        await _appTracker.StartAsync(cancellationToken);

        SimpleLog.Info("Runtime started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_config.PollIntervalMs, cancellationToken);
            ReconcileGoneApps();
        }
    }

    private async Task ProcessSnapshotsAsync(CancellationToken cancellationToken)
    {
        var reader = _snapshotQueue.Reader;

        while (await reader.WaitToReadAsync(cancellationToken))
        {
            IReadOnlyCollection<TrackedAppSnapshot>? latest = null;
            while (reader.TryRead(out var snapshot))
            {
                latest = snapshot;
            }

            if (latest is null)
            {
                continue;
            }

            await ProcessSnapshotAsync(latest, cancellationToken);
        }
    }

    private async Task ProcessSnapshotAsync(
        IReadOnlyCollection<TrackedAppSnapshot> apps,
        CancellationToken cancellationToken)
    {
        if (_isDisposing)
        {
            return;
        }

        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDisposing)
            {
                return;
            }

            if (!app.IsAudioActive)
            {
                continue;
            }

            var hadAssignment = _assignments.TryGet(app.AppId, out var assignedSlot);
            var slotIndex = hadAssignment ? assignedSlot : _assignments.GetOrAssign(app.AppId);
            if (!hadAssignment)
            {
                SimpleLog.Info(
                    $"Assigned PID {app.AppId.ProcessId} ({app.ProcessName}) to slot {slotIndex}");
            }

            if (slotIndex <= 7)
            {
                _mixer.SetDedicatedSlotPan(slotIndex, app.Pan);
            }
            else
            {
                _mixer.SetOverflowPan(0.0f);
            }

            var endpointId = ResolveEndpointIdForSlot(slotIndex);
            var result = await _router.BindToEndpointAsync(
                app.AppId,
                endpointId,
                RoutingRoles.All,
                cancellationToken);

            if (result.Status is RoutingStatus.Success)
            {
                continue;
            }

            var details = string.Join(
                ", ",
                result.RoleResults.Select(r => $"{r.Role}:{(r.Succeeded ? "ok" : r.ErrorCode ?? "fail")}"));
            SimpleLog.Warn(
                $"Routing result for PID {app.AppId.ProcessId} ({app.ProcessName}) slot {slotIndex}: {result.Status} [{details}]");
        }
    }

    private void OnOutputDeviceChanged(object? sender, OutputDeviceChangedEventArgs e)
    {
        if (_isDisposing)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _mixer.SwitchOutputDeviceAsync(e.Device.EndpointId, CancellationToken.None);
                SimpleLog.Info($"Output device switched: {e.Device.FriendlyName}");
            }
            catch (Exception ex)
            {
                SimpleLog.Error($"Failed to switch output device: {ex.Message}");
            }
        });
    }

    private void OnSnapshotChanged(object? sender, AppTrackerSnapshotChangedEventArgs e)
    {
        if (_isDisposing)
        {
            return;
        }

        _snapshotQueue.Writer.TryWrite(e.Apps.ToArray());
    }

    private void ReconcileGoneApps()
    {
        var nowUtc = DateTime.UtcNow;
        var apps = _appTracker.GetSnapshot();
        var activeSet = new HashSet<AppRuntimeId>(apps.Where(a => a.IsAudioActive).Select(a => a.AppId));

        foreach (var kvp in _assignments.Snapshot())
        {
            if (activeSet.Contains(kvp.Key))
            {
                continue;
            }

            var snapshot = apps.FirstOrDefault(a => a.AppId == kvp.Key);
            var lastAudioUtc = snapshot?.LastAudioUtc ?? DateTime.MinValue;
            var isPastGrace = lastAudioUtc == DateTime.MinValue
                || (nowUtc - lastAudioUtc).TotalSeconds >= _config.InactiveGraceSeconds;

            if (!isPastGrace)
            {
                continue;
            }

            if (_assignments.Release(kvp.Key))
            {
                SimpleLog.Info($"Released slot assignment for PID {kvp.Key.ProcessId}");
                _ = TryResetRoutingAsync(kvp.Key);
            }
        }
    }

    private async Task TryResetRoutingAsync(AppRuntimeId appId)
    {
        try
        {
            var result = await _router.ResetToSystemDefaultAsync(
                appId,
                RoutingRoles.All,
                CancellationToken.None);
            if (result.Status is RoutingStatus.Failed or RoutingStatus.NotSupported)
            {
                SimpleLog.Warn($"Routing reset failed for PID {appId.ProcessId}: {result.Status}");
            }
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"Routing reset threw for PID {appId.ProcessId}: {ex.Message}");
        }
    }

    private string ResolveEndpointIdForSlot(int slotIndex)
    {
        if (!_endpointIdBySlot.TryGetValue(slotIndex, out var endpointId))
        {
            throw new InvalidOperationException($"No endpoint configured for slot index {slotIndex}.");
        }

        return endpointId;
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposing = true;
        _endpointCatalog.OutputDeviceChanged -= OnOutputDeviceChanged;
        _appTracker.SnapshotChanged -= OnSnapshotChanged;

        _snapshotQueue.Writer.TryComplete();
        if (_snapshotProcessorCts is not null)
        {
            _snapshotProcessorCts.Cancel();
        }

        if (_snapshotProcessorTask is not null)
        {
            try
            {
                await _snapshotProcessorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await ResetAllAssignedRoutesAsync();
        await _appTracker.StopAsync(CancellationToken.None);
        await _mixer.StopAsync(CancellationToken.None);

        await _mixer.DisposeAsync();
        await _appTracker.DisposeAsync();
        await _endpointCatalog.DisposeAsync();

        _snapshotProcessorCts?.Dispose();
    }

    private async Task ResetAllAssignedRoutesAsync()
    {
        var assigned = _assignments.Snapshot().Keys.ToArray();
        if (assigned.Length == 0)
        {
            return;
        }

        SimpleLog.Info($"Resetting routing to system default for {assigned.Length} app(s) before shutdown.");
        foreach (var appId in assigned)
        {
            try
            {
                var result = await _router.ResetToSystemDefaultAsync(
                    appId,
                    RoutingRoles.All,
                    CancellationToken.None);
                if (result.Status is RoutingStatus.Failed or RoutingStatus.NotSupported)
                {
                    SimpleLog.Warn($"Shutdown reset failed for PID {appId.ProcessId}: {result.Status}");
                }
            }
            catch (Exception ex)
            {
                SimpleLog.Warn($"Shutdown reset threw for PID {appId.ProcessId}: {ex.Message}");
            }
        }
    }
}

