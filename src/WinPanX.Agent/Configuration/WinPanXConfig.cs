using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Configuration;

public sealed class WinPanXConfig
{
    public int SlotCount { get; init; } = 8;

    public int PollIntervalMs { get; init; } = 500;

    public int InactiveGraceSeconds { get; init; } = 3;

    public string VirtualEndpointNamePrefix { get; init; } = "WinPanX Slot ";

    public string OutputDeviceId { get; init; } = "default";

    public string[] ExcludedProcesses { get; init; } = ["System", "svchost"];

    public float ActivityPeakThreshold { get; init; } = 0.001f;

    public OverflowPanPolicy OverflowPanPolicy { get; init; } = OverflowPanPolicy.Center;

    public int TargetSampleRate { get; init; } = 48000;

    public int Channels { get; init; } = 2;

    public int FramesPerBuffer { get; init; } = 480;

    public void Validate()
    {
        if (SlotCount != 8)
        {
            throw new InvalidOperationException("MVP requires SlotCount = 8.");
        }

        if (PollIntervalMs < 50)
        {
            throw new InvalidOperationException("PollIntervalMs must be at least 50.");
        }

        if (InactiveGraceSeconds < 0)
        {
            throw new InvalidOperationException("InactiveGraceSeconds cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(VirtualEndpointNamePrefix))
        {
            throw new InvalidOperationException("VirtualEndpointNamePrefix is required.");
        }

        if (TargetSampleRate <= 0)
        {
            throw new InvalidOperationException("TargetSampleRate must be positive.");
        }

        if (Channels != 2)
        {
            throw new InvalidOperationException("MVP currently supports stereo only (Channels = 2).");
        }

        if (FramesPerBuffer <= 0)
        {
            throw new InvalidOperationException("FramesPerBuffer must be positive.");
        }
    }
}

