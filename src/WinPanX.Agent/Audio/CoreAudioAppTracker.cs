using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WinPanX.Agent.Configuration;
using WinPanX.Agent.Runtime;
using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Audio;

internal sealed class CoreAudioAppTracker : IAppTracker
{
    private const int SmXvirtualscreen = 76;
    private const int SmCxvirtualscreen = 78;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const uint Th32csSnapprocess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly object _sync = new();
    private readonly HashSet<string> _excludedProcessNames;
    private readonly int _pollIntervalMs;
    private readonly float _activityPeakThreshold;
    private readonly TimeSpan _inactiveRetention;
    private readonly int _selfProcessId;

    private MMDeviceEnumerator? _enumerator;
    private Dictionary<AppRuntimeId, TrackedState> _states = [];
    private readonly Dictionary<IntPtr, WindowObservation> _windowByHandle = [];
    private readonly Dictionary<int, HashSet<IntPtr>> _windowHandlesByPid = [];
    private IReadOnlyCollection<TrackedAppSnapshot> _snapshot = [];
    private IntPtr _windowHook = IntPtr.Zero;
    private WinEventDelegate? _windowHookCallback;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _started;
    private bool _disposed;

    public CoreAudioAppTracker(WinPanXConfig config)
    {
        _selfProcessId = Environment.ProcessId;
        _pollIntervalMs = config.PollIntervalMs;
        _activityPeakThreshold = config.ActivityPeakThreshold;
        _inactiveRetention = TimeSpan.FromSeconds(Math.Max(config.InactiveGraceSeconds, 1));
        _excludedProcessNames = new HashSet<string>(
            config.ExcludedProcesses ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public event EventHandler<AppTrackerSnapshotChangedEventArgs>? SnapshotChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_started)
            {
                return Task.CompletedTask;
            }

            _enumerator = new MMDeviceEnumerator();
            RegisterWindowHook();
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => PollLoopAsync(_loopCts.Token), CancellationToken.None);
            _started = true;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loopTask;

        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _loopCts?.Cancel();
            loopTask = _loopTask;
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Expected when stopping the internal poll loop.
            }
        }

        lock (_sync)
        {
            UnregisterWindowHook();
            _windowByHandle.Clear();
            _windowHandlesByPid.Clear();
            _enumerator?.Dispose();
            _enumerator = null;

            _loopTask = null;
            _loopCts?.Dispose();
            _loopCts = null;
            _started = false;
        }
    }

    public IReadOnlyCollection<TrackedAppSnapshot> GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }

        await StopAsync(CancellationToken.None);

        lock (_sync)
        {
            _disposed = true;
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PollOnce();
            }
            catch (Exception ex)
            {
                SimpleLog.Warn($"App tracker poll failed: {ex.Message}");
            }

            await Task.Delay(_pollIntervalMs, cancellationToken);
        }
    }

    private void PollOnce()
    {
        Dictionary<AppRuntimeId, AppAggregate> aggregates;
        IReadOnlyCollection<TrackedAppSnapshot> snapshot;
        var nowUtc = DateTime.UtcNow;

        lock (_sync)
        {
            if (!_started || _enumerator is null)
            {
                return;
            }

            aggregates = CollectAggregatesFromAllRenderEndpoints(nowUtc);
            UpdateStateFromAggregates(aggregates, nowUtc);
            snapshot = _states.Values
                .Select(s => s.ToSnapshot())
                .OrderBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.AppId.ProcessId)
                .ToArray();
            _snapshot = snapshot;
        }

        SnapshotChanged?.Invoke(this, new AppTrackerSnapshotChangedEventArgs(snapshot, nowUtc));
    }

    private Dictionary<AppRuntimeId, AppAggregate> CollectAggregatesFromAllRenderEndpoints(DateTime nowUtc)
    {
        var aggregates = new Dictionary<AppRuntimeId, AppAggregate>();
        var processInfoCache = new Dictionary<int, ProcessInfo?>();
        // Browser/Chromium audio often comes from child processes without windows; keep a parent map for fallback lookup.
        var parentPidByPid = BuildParentPidMap();

        var devices = _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                var sessionManager = device.AudioSessionManager;
                sessionManager.RefreshSessions();
                var sessions = sessionManager.Sessions;

                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    var pid = (int)session.GetProcessID;
                    if (pid <= 0 || pid == _selfProcessId || session.IsSystemSoundsSession)
                    {
                        continue;
                    }

                    if (!processInfoCache.TryGetValue(pid, out var processInfo))
                    {
                        processInfo = TryGetProcessInfo(pid);
                        processInfoCache[pid] = processInfo;
                    }

                    if (processInfo is null)
                    {
                        continue;
                    }

                    if (_excludedProcessNames.Contains(processInfo.ProcessName))
                    {
                        continue;
                    }

                    var appId = new AppRuntimeId(pid, processInfo.ProcessStartTimeUtc);
                    // "Producing audio" is defined by measured signal level rather than session state,
                    // which avoids tracking long-lived but silent sessions.
                    var isActive = session.AudioMeterInformation.MasterPeakValue >= _activityPeakThreshold;

                    if (!aggregates.TryGetValue(appId, out var aggregate))
                    {
                        var (hasWindow, rect, pan, hwnd) = ResolveWindowAndPan(
                            processInfo,
                            appId,
                            processInfoCache,
                            parentPidByPid);
                        aggregate = new AppAggregate(
                            AppId: appId,
                            ProcessName: processInfo.ProcessName,
                            IsAudioActive: isActive,
                            LastAudioUtc: isActive ? nowUtc : DateTime.MinValue,
                            HasWindow: hasWindow,
                            WindowRect: rect,
                            Pan: pan,
                            WindowHandle: hwnd);
                        aggregates[appId] = aggregate;
                    }
                    else if (isActive)
                    {
                        aggregates[appId] = aggregate with
                        {
                            IsAudioActive = true,
                            LastAudioUtc = nowUtc
                        };
                    }
                }
            }
        }

        return aggregates;
    }

    private void UpdateStateFromAggregates(
        IReadOnlyDictionary<AppRuntimeId, AppAggregate> aggregates,
        DateTime nowUtc)
    {
        var seen = new HashSet<AppRuntimeId>(aggregates.Keys);

        foreach (var aggregate in aggregates.Values)
        {
            if (_states.TryGetValue(aggregate.AppId, out var existing))
            {
                existing.ProcessName = aggregate.ProcessName;
                existing.IsAudioActive = aggregate.IsAudioActive;
                existing.HasWindow = aggregate.HasWindow;
                existing.WindowRect = aggregate.WindowRect;
                existing.Pan = aggregate.Pan;
                if (existing.PinnedWindowHandle == IntPtr.Zero
                    && aggregate.IsAudioActive
                    && aggregate.WindowHandle != IntPtr.Zero)
                {
                    existing.PinnedWindowHandle = aggregate.WindowHandle;
                }
                existing.LastSeenUtc = nowUtc;
                if (aggregate.IsAudioActive)
                {
                    existing.LastAudioUtc = nowUtc;
                }
            }
            else
            {
                _states[aggregate.AppId] = new TrackedState
                {
                    AppId = aggregate.AppId,
                    ProcessName = aggregate.ProcessName,
                    IsAudioActive = aggregate.IsAudioActive,
                    LastAudioUtc = aggregate.LastAudioUtc == DateTime.MinValue ? nowUtc : aggregate.LastAudioUtc,
                    LastSeenUtc = nowUtc,
                    HasWindow = aggregate.HasWindow,
                    WindowRect = aggregate.WindowRect,
                    Pan = aggregate.Pan,
                    PinnedWindowHandle = aggregate.IsAudioActive ? aggregate.WindowHandle : IntPtr.Zero
                };
            }
        }

        foreach (var stale in _states.Values.Where(s => !seen.Contains(s.AppId)).ToArray())
        {
            stale.IsAudioActive = false;
            stale.HasWindow = false;
            stale.WindowRect = null;
            stale.Pan = 0.0f;
        }

        foreach (var staleKey in _states
                     .Where(kvp => !seen.Contains(kvp.Key))
                     .Where(kvp => nowUtc - kvp.Value.LastAudioUtc >= _inactiveRetention)
                     .Select(kvp => kvp.Key)
                     .ToArray())
        {
            _states.Remove(staleKey);
            RemoveWindowCacheForPidIfUnused(staleKey.ProcessId);
        }
    }

    private (bool HasWindow, WindowRect? Rect, float Pan, IntPtr WindowHandle) ResolveWindowAndPan(
        ProcessInfo processInfo,
        AppRuntimeId appId,
        Dictionary<int, ProcessInfo?> processInfoCache,
        IReadOnlyDictionary<int, int> parentPidByPid)
    {
        if (_states.TryGetValue(appId, out var tracked)
            && tracked.PinnedWindowHandle != IntPtr.Zero)
        {
            if (TryGetWindowObservation(tracked.PinnedWindowHandle, processInfo.ProcessId, out var pinned))
            {
                return (true, pinned.Rect, ComputePan(pinned.Rect.CenterX), pinned.Hwnd);
            }

            tracked.PinnedWindowHandle = IntPtr.Zero;
        }

        if (TryGetWindowObservation(processInfo.MainWindowHandle, processInfo.ProcessId, out var mainWindow))
        {
            return (true, mainWindow.Rect, ComputePan(mainWindow.Rect.CenterX), mainWindow.Hwnd);
        }

        if (TryGetMostRecentObservedWindow(processInfo.ProcessId, out var observed))
        {
            return (true, observed.Rect, ComputePan(observed.Rect.CenterX), observed.Hwnd);
        }

        if (TryResolveAncestorWindowObservation(
                processInfo.ProcessId,
                processInfoCache,
                parentPidByPid,
                out var ancestor))
        {
            return (true, ancestor.Rect, ComputePan(ancestor.Rect.CenterX), ancestor.Hwnd);
        }

        return (false, null, 0.0f, IntPtr.Zero);
    }

    private bool TryResolveAncestorWindowObservation(
        int processId,
        Dictionary<int, ProcessInfo?> processInfoCache,
        IReadOnlyDictionary<int, int> parentPidByPid,
        out WindowObservation observation)
    {
        observation = default!;
        var visited = new HashSet<int> { processId };
        var currentPid = processId;

        for (var depth = 0; depth < 8; depth++)
        {
            if (!parentPidByPid.TryGetValue(currentPid, out var parentPid) || parentPid <= 0 || !visited.Add(parentPid))
            {
                return false;
            }

            if (!processInfoCache.TryGetValue(parentPid, out var parentInfo))
            {
                parentInfo = TryGetProcessInfo(parentPid);
                processInfoCache[parentPid] = parentInfo;
            }

            if (parentInfo is not null)
            {
                if (TryGetWindowObservation(parentInfo.MainWindowHandle, parentInfo.ProcessId, out observation))
                {
                    return true;
                }

                if (TryGetMostRecentObservedWindow(parentInfo.ProcessId, out observation))
                {
                    return true;
                }
            }

            currentPid = parentPid;
        }

        return false;
    }

    private static Dictionary<int, int> BuildParentPidMap()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                DwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.Th32ProcessID] = (int)entry.Th32ParentProcessID;
                entry.DwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return result;
    }

    private void RegisterWindowHook()
    {
        _windowHookCallback = OnWindowEvent;
        _windowHook = SetWinEventHook(
            EventObjectLocationChange,
            EventObjectLocationChange,
            IntPtr.Zero,
            _windowHookCallback,
            0,
            0,
            WineventOutofcontext | WineventSkipownprocess);

        if (_windowHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            SimpleLog.Warn($"Failed to register WinEventHook (error {error}).");
        }
    }

    private void UnregisterWindowHook()
    {
        if (_windowHook == IntPtr.Zero)
        {
            _windowHookCallback = null;
            return;
        }

        _ = UnhookWinEvent(_windowHook);
        _windowHook = IntPtr.Zero;
        _windowHookCallback = null;
    }

    private void OnWindowEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != EventObjectLocationChange || hwnd == IntPtr.Zero || idObject != ObjidWindow || idChild != ChildidSelf)
        {
            return;
        }

        if (!IsWindowVisible(hwnd))
        {
            return;
        }

        if (!GetWindowRect(hwnd, out var nativeRect))
        {
            return;
        }

        _ = GetWindowThreadProcessId(hwnd, out var nativePid);
        if (nativePid == 0)
        {
            return;
        }

        var rect = new WindowRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
        var nowUtc = DateTime.UtcNow;
        lock (_sync)
        {
            if (!_started || _disposed)
            {
                return;
            }

            var pid = (int)nativePid;
            _windowByHandle[hwnd] = new WindowObservation(pid, hwnd, rect, nowUtc);
            if (!_windowHandlesByPid.TryGetValue(pid, out var handles))
            {
                handles = [];
                _windowHandlesByPid[pid] = handles;
            }

            handles.Add(hwnd);
        }
    }

    private ProcessInfo? TryGetProcessInfo(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessInfo(
                ProcessId: process.Id,
                ProcessName: process.ProcessName,
                ProcessStartTimeUtc: process.StartTime.ToUniversalTime(),
                MainWindowHandle: process.MainWindowHandle);
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetMostRecentObservedWindow(int processId, out WindowObservation observation)
    {
        observation = default!;
        if (!_windowHandlesByPid.TryGetValue(processId, out var handles) || handles.Count == 0)
        {
            return false;
        }

        WindowObservation? best = null;
        foreach (var handle in handles.ToArray())
        {
            if (!TryGetWindowObservation(handle, processId, out var candidate))
            {
                handles.Remove(handle);
                continue;
            }

            if (best is null || candidate.CapturedUtc > best.CapturedUtc)
            {
                best = candidate;
            }
        }

        if (handles.Count == 0)
        {
            _windowHandlesByPid.Remove(processId);
        }

        if (best is null)
        {
            return false;
        }

        observation = best;
        return true;
    }

    private bool TryGetWindowObservation(IntPtr windowHandle, int processId, out WindowObservation observation)
    {
        observation = default!;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!IsWindowVisible(windowHandle))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var nativePid);
        if (nativePid == 0 || (int)nativePid != processId)
        {
            return false;
        }

        if (!GetWindowRect(windowHandle, out var nativeRect))
        {
            return false;
        }

        var rect = new WindowRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
        var created = new WindowObservation(processId, windowHandle, rect, DateTime.UtcNow);
        _windowByHandle[windowHandle] = created;
        if (!_windowHandlesByPid.TryGetValue(processId, out var handles))
        {
            handles = [];
            _windowHandlesByPid[processId] = handles;
        }

        handles.Add(windowHandle);

        observation = created;
        return true;
    }

    private void RemoveWindowCacheForPidIfUnused(int processId)
    {
        if (_states.Keys.Any(k => k.ProcessId == processId))
        {
            return;
        }

        if (!_windowHandlesByPid.Remove(processId, out var handles))
        {
            return;
        }

        foreach (var handle in handles)
        {
            _windowByHandle.Remove(handle);
        }
    }

    private static float ComputePan(int centerX)
    {
        var desktopMinX = GetSystemMetrics(SmXvirtualscreen);
        var desktopWidth = GetSystemMetrics(SmCxvirtualscreen);
        if (desktopWidth <= 1)
        {
            return 0.0f;
        }

        var normalized = (centerX - desktopMinX) / (double)desktopWidth;
        var pan = (normalized * 2.0) - 1.0;
        return (float)Math.Clamp(pan, -1.0, 1.0);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CoreAudioAppTracker));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessID;
        public IntPtr Th32DefaultHeapID;
        public uint Th32ModuleID;
        public uint CntThreads;
        public uint Th32ParentProcessID;
        public int PcPriClassBase;
        public uint DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }

    private sealed record ProcessInfo(int ProcessId, string ProcessName, DateTime ProcessStartTimeUtc, IntPtr MainWindowHandle);

    private sealed record WindowObservation(int ProcessId, IntPtr Hwnd, WindowRect Rect, DateTime CapturedUtc);

    private sealed record AppAggregate(
        AppRuntimeId AppId,
        string ProcessName,
        bool IsAudioActive,
        DateTime LastAudioUtc,
        bool HasWindow,
        WindowRect? WindowRect,
        float Pan,
        IntPtr WindowHandle);

    private sealed class TrackedState
    {
        public AppRuntimeId AppId { get; set; }

        public string ProcessName { get; set; } = string.Empty;

        public bool IsAudioActive { get; set; }

        public DateTime LastAudioUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public bool HasWindow { get; set; }

        public WindowRect? WindowRect { get; set; }

        public float Pan { get; set; }

        public IntPtr PinnedWindowHandle { get; set; }

        public TrackedAppSnapshot ToSnapshot()
        {
            return new TrackedAppSnapshot(
                AppId,
                ProcessName,
                IsAudioActive,
                LastAudioUtc,
                HasWindow,
                WindowRect,
                Pan);
        }
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}



