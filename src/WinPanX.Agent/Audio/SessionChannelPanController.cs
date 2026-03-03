using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using WinPanX.Agent.Runtime;
using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Audio;

internal sealed class SessionChannelPanController : IAsyncDisposable
{
    private static readonly Guid ChannelAudioVolumeIid = new("1C158861-B533-4B30-B1CF-E853E51C59B8");
    private static readonly Guid EventContext = new("D08C3A0F-9DF4-4D40-B658-0A22D462D7D4");
    // NAudio does not expose IChannelAudioVolume directly; we bridge from the internal IAudioSessionControl2 COM object.
    private static readonly FieldInfo? SessionControl2Field = typeof(AudioSessionControl)
        .GetField("audioSessionControlInterface2", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly object _sync = new();
    private readonly Dictionary<SessionKey, SessionPanState> _states = [];
    private readonly HashSet<string> _warnedTokens = [];
    private readonly int _selfProcessId = Environment.ProcessId;
    private bool _disposed;

    public Task ApplyPanAsync(
        IReadOnlyCollection<TrackedAppSnapshot> apps,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activePanByPid = BuildActivePanByPid(apps);
        var seenKeys = new HashSet<SessionKey>();

        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (device)
            {
                var sessionManager = device.AudioSessionManager;
                sessionManager.RefreshSessions();
                var sessions = sessionManager.Sessions;

                for (var i = 0; i < sessions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var session = sessions[i];

                    var pid = (int)session.GetProcessID;
                    if (pid <= 0 || pid == _selfProcessId || session.IsSystemSoundsSession)
                    {
                        continue;
                    }

                    var sessionId = session.GetSessionInstanceIdentifier ?? string.Empty;
                    var key = new SessionKey(device.ID, pid, sessionId);
                    seenKeys.Add(key);

                    if (!activePanByPid.TryGetValue(pid, out var pan))
                    {
                        TryRestoreAndForget(session, key);
                        continue;
                    }

                    if (!TryGetChannelAudioVolume(session, out var channelAudioVolume))
                    {
                        WarnOnce($"no-channel-api:{key}",
                            $"Session pan unavailable for PID {pid} on '{device.FriendlyName}' (IChannelAudioVolume not supported).");
                        continue;
                    }

                    try
                    {
                        if (!TryGetOrCreateState(channelAudioVolume, key, out var state))
                        {
                            continue;
                        }

                        ApplyPan(channelAudioVolume, state, pan, key);
                    }
                    finally
                    {
                        if (Marshal.IsComObject(channelAudioVolume))
                        {
                            _ = Marshal.ReleaseComObject(channelAudioVolume);
                        }
                    }
                }
            }
        }

        PruneMissingSessions(seenKeys);
        return Task.CompletedTask;
    }

    public Task RestoreAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<SessionKey, SessionPanState> snapshot;
        lock (_sync)
        {
            snapshot = new Dictionary<SessionKey, SessionPanState>(_states);
        }

        if (snapshot.Count == 0)
        {
            return Task.CompletedTask;
        }

        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (device)
            {
                var sessionManager = device.AudioSessionManager;
                sessionManager.RefreshSessions();
                var sessions = sessionManager.Sessions;

                for (var i = 0; i < sessions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var session = sessions[i];

                    var pid = (int)session.GetProcessID;
                    if (pid <= 0)
                    {
                        continue;
                    }

                    var sessionId = session.GetSessionInstanceIdentifier ?? string.Empty;
                    var key = new SessionKey(device.ID, pid, sessionId);
                    if (!snapshot.TryGetValue(key, out var state))
                    {
                        continue;
                    }

                    if (!TryGetChannelAudioVolume(session, out var channelAudioVolume))
                    {
                        continue;
                    }

                    try
                    {
                        RestoreToBaseline(channelAudioVolume, state, key);
                    }
                    finally
                    {
                        if (Marshal.IsComObject(channelAudioVolume))
                        {
                            _ = Marshal.ReleaseComObject(channelAudioVolume);
                        }
                    }
                }
            }
        }

        lock (_sync)
        {
            _states.Clear();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await RestoreAllAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"Session pan restore during dispose failed: {ex.Message}");
        }
    }

    private static Dictionary<int, float> BuildActivePanByPid(IReadOnlyCollection<TrackedAppSnapshot> apps)
    {
        var result = new Dictionary<int, float>();

        foreach (var group in apps.Where(a => a.IsAudioActive).GroupBy(a => a.AppId.ProcessId))
        {
            var selected = group
                .OrderByDescending(a => a.HasWindow)
                .ThenByDescending(a => a.LastAudioUtc)
                .First();

            result[group.Key] = Math.Clamp(selected.Pan, -1.0f, 1.0f);
        }

        return result;
    }

    private bool TryGetOrCreateState(
        IChannelAudioVolume channelAudioVolume,
        SessionKey key,
        out SessionPanState state)
    {
        lock (_sync)
        {
            if (_states.TryGetValue(key, out state!))
            {
                return true;
            }
        }

        var hr = channelAudioVolume.GetChannelCount(out var channelCount);
        if (hr < 0)
        {
            WarnOnce($"channel-count:{key}",
                $"Failed to read channel count for PID {key.ProcessId} session '{key.SessionInstanceId}' (HRESULT 0x{hr:X8}).");
            state = default!;
            return false;
        }

        if (channelCount != 2)
        {
            WarnOnce($"non-stereo:{key}",
                $"Skipping PID {key.ProcessId} session '{key.SessionInstanceId}' because channel count is {channelCount}, expected 2.");
            state = default!;
            return false;
        }

        hr = channelAudioVolume.GetChannelVolume(0, out var baselineLeft);
        if (hr < 0)
        {
            WarnOnce($"baseline-left:{key}",
                $"Failed to read baseline left volume for PID {key.ProcessId} (HRESULT 0x{hr:X8}).");
            state = default!;
            return false;
        }

        hr = channelAudioVolume.GetChannelVolume(1, out var baselineRight);
        if (hr < 0)
        {
            WarnOnce($"baseline-right:{key}",
                $"Failed to read baseline right volume for PID {key.ProcessId} (HRESULT 0x{hr:X8}).");
            state = default!;
            return false;
        }

        state = new SessionPanState(
            BaselineLeft: ClampVolume(baselineLeft),
            BaselineRight: ClampVolume(baselineRight),
            LastAppliedLeft: float.NaN,
            LastAppliedRight: float.NaN);

        lock (_sync)
        {
            _states[key] = state;
        }

        return true;
    }

    private void ApplyPan(
        IChannelAudioVolume channelAudioVolume,
        SessionPanState state,
        float pan,
        SessionKey key)
    {
        var (leftGain, rightGain) = ComputeBalanceGains(pan);
        var desiredLeft = ClampVolume(state.BaselineLeft * leftGain);
        var desiredRight = ClampVolume(state.BaselineRight * rightGain);

        if (ApproximatelyEqual(desiredLeft, state.LastAppliedLeft)
            && ApproximatelyEqual(desiredRight, state.LastAppliedRight))
        {
            return;
        }

        var leftHr = channelAudioVolume.SetChannelVolume(0, desiredLeft, EventContext);
        if (leftHr < 0)
        {
            WarnOnce($"set-left:{key}",
                $"Failed to set left pan volume for PID {key.ProcessId} (HRESULT 0x{leftHr:X8}).");
            return;
        }

        var rightHr = channelAudioVolume.SetChannelVolume(1, desiredRight, EventContext);
        if (rightHr < 0)
        {
            WarnOnce($"set-right:{key}",
                $"Failed to set right pan volume for PID {key.ProcessId} (HRESULT 0x{rightHr:X8}).");
            return;
        }

        lock (_sync)
        {
            if (_states.TryGetValue(key, out var current))
            {
                _states[key] = current with
                {
                    LastAppliedLeft = desiredLeft,
                    LastAppliedRight = desiredRight
                };
            }
        }
    }

    private void TryRestoreAndForget(AudioSessionControl session, SessionKey key)
    {
        SessionPanState state;
        lock (_sync)
        {
            if (!_states.TryGetValue(key, out state))
            {
                return;
            }
        }

        if (TryGetChannelAudioVolume(session, out var channelAudioVolume))
        {
            try
            {
                RestoreToBaseline(channelAudioVolume, state, key);
            }
            finally
            {
                if (Marshal.IsComObject(channelAudioVolume))
                {
                    _ = Marshal.ReleaseComObject(channelAudioVolume);
                }
            }
        }

        lock (_sync)
        {
            _states.Remove(key);
        }
    }

    private void RestoreToBaseline(
        IChannelAudioVolume channelAudioVolume,
        SessionPanState state,
        SessionKey key)
    {
        var leftHr = channelAudioVolume.SetChannelVolume(0, state.BaselineLeft, EventContext);
        if (leftHr < 0)
        {
            WarnOnce($"restore-left:{key}",
                $"Failed to restore left channel volume for PID {key.ProcessId} (HRESULT 0x{leftHr:X8}).");
        }

        var rightHr = channelAudioVolume.SetChannelVolume(1, state.BaselineRight, EventContext);
        if (rightHr < 0)
        {
            WarnOnce($"restore-right:{key}",
                $"Failed to restore right channel volume for PID {key.ProcessId} (HRESULT 0x{rightHr:X8}).");
        }
    }

    private void PruneMissingSessions(HashSet<SessionKey> seenKeys)
    {
        lock (_sync)
        {
            foreach (var key in _states.Keys.Where(key => !seenKeys.Contains(key)).ToArray())
            {
                _states.Remove(key);
            }
        }
    }

    // Query IChannelAudioVolume from the underlying session COM object to adjust L/R levels per session.
    private static bool TryGetChannelAudioVolume(
        AudioSessionControl session,
        out IChannelAudioVolume channelAudioVolume)
    {
        channelAudioVolume = default!;

        if (SessionControl2Field is null)
        {
            return false;
        }

        var sessionControl2 = SessionControl2Field.GetValue(session);
        if (sessionControl2 is null)
        {
            return false;
        }

        var unknownPtr = Marshal.GetIUnknownForObject(sessionControl2);
        if (unknownPtr == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var hr = Marshal.QueryInterface(unknownPtr, in ChannelAudioVolumeIid, out var channelPtr);
            if (hr < 0 || channelPtr == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                channelAudioVolume = (IChannelAudioVolume)Marshal.GetObjectForIUnknown(channelPtr);
                return true;
            }
            finally
            {
                _ = Marshal.Release(channelPtr);
            }
        }
        finally
        {
            _ = Marshal.Release(unknownPtr);
        }
    }

    private void WarnOnce(string token, string message)
    {
        lock (_sync)
        {
            if (!_warnedTokens.Add(token))
            {
                return;
            }
        }

        SimpleLog.Warn(message);
    }

    private static (float LeftGain, float RightGain) ComputeBalanceGains(float pan)
    {
        var clampedPan = Math.Clamp(pan, -1.0f, 1.0f);

        if (clampedPan < 0.0f)
        {
            return (1.0f, 1.0f + clampedPan);
        }

        if (clampedPan > 0.0f)
        {
            return (1.0f - clampedPan, 1.0f);
        }

        return (1.0f, 1.0f);
    }

    private static float ClampVolume(float volume) => Math.Clamp(volume, 0.0f, 1.0f);

    private static bool ApproximatelyEqual(float x, float y) => Math.Abs(x - y) <= 0.002f;

    private readonly record struct SessionKey(string DeviceId, int ProcessId, string SessionInstanceId);

    private readonly record struct SessionPanState(
        float BaselineLeft,
        float BaselineRight,
        float LastAppliedLeft,
        float LastAppliedRight);

    // Undocumented session-channel interface used by Windows audio engine for per-channel session volume.`r`n    [ComImport]
    [Guid("1C158861-B533-4B30-B1CF-E853E51C59B8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChannelAudioVolume
    {
        int GetChannelCount(out uint channelCount);

        int SetChannelVolume(uint channelIndex, float level, [MarshalAs(UnmanagedType.LPStruct)] Guid eventContext);

        int GetChannelVolume(uint channelIndex, out float level);

        int SetAllVolumes(uint channelCount, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] levels, [MarshalAs(UnmanagedType.LPStruct)] Guid eventContext);

        int GetAllVolumes(uint channelCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] levels);
    }
}


