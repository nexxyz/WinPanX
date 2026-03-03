using System;
using System.Collections.Generic;

namespace WinPanX.Core.Contracts;

public readonly record struct AppRuntimeId(int ProcessId, DateTime ProcessStartTimeUtc);

public readonly record struct WindowRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => Left + (Width / 2);
}

public sealed record TrackedAppSnapshot(
    AppRuntimeId AppId,
    string ProcessName,
    bool IsAudioActive,
    DateTime LastAudioUtc,
    bool HasWindow,
    WindowRect? WindowRect,
    float Pan);

public sealed class AppTrackerSnapshotChangedEventArgs : EventArgs
{
    public AppTrackerSnapshotChangedEventArgs(
        IReadOnlyCollection<TrackedAppSnapshot> apps,
        DateTime capturedUtc)
    {
        Apps = apps;
        CapturedUtc = capturedUtc;
    }

    public IReadOnlyCollection<TrackedAppSnapshot> Apps { get; }

    public DateTime CapturedUtc { get; }
}
