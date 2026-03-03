using System;
using System.Collections.Generic;

namespace WinPanX.Core.Contracts;

[Flags]
public enum RoutingRoles
{
    None = 0,
    Console = 1,
    Multimedia = 2,
    Communications = 4,
    All = Console | Multimedia | Communications
}

public enum RoutingStatus
{
    Success,
    PartialSuccess,
    Failed,
    NotSupported
}

public enum OverflowPanPolicy
{
    Center,
    AverageAssignedPan
}

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
    float Pan,
    int? AssignedSlotIndex);

public sealed record EndpointDescriptor(
    int SlotIndex,
    string EndpointId,
    string FriendlyName,
    int Channels,
    bool IsVirtualSlot,
    bool IsOverflowSlot);

public sealed record EndpointCatalogSnapshot(
    IReadOnlyList<EndpointDescriptor> VirtualSlots,
    EndpointDescriptor OutputDevice,
    DateTime CapturedUtc);

public sealed record RoleRoutingResult(
    RoutingRoles Role,
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record RoutingResult(
    AppRuntimeId AppId,
    string EndpointId,
    RoutingStatus Status,
    IReadOnlyList<RoleRoutingResult> RoleResults,
    DateTime CompletedUtc);

public sealed record MixerStartRequest(
    IReadOnlyList<EndpointDescriptor> InputSlots,
    string OutputEndpointId,
    OverflowPanPolicy OverflowPolicy,
    int TargetSampleRate,
    int Channels,
    int FramesPerBuffer);

public sealed record MixerStats(
    long TotalFramesMixed,
    long UnderrunCount,
    long OverrunCount,
    float PeakMasterLeft,
    float PeakMasterRight,
    DateTime CapturedUtc);

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

public sealed class OutputDeviceChangedEventArgs : EventArgs
{
    public OutputDeviceChangedEventArgs(EndpointDescriptor device, DateTime changedUtc)
    {
        Device = device;
        ChangedUtc = changedUtc;
    }

    public EndpointDescriptor Device { get; }

    public DateTime ChangedUtc { get; }
}

