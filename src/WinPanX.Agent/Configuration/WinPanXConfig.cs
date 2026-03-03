namespace WinPanX.Agent.Configuration;

public sealed class WinPanXConfig
{
    public int PollIntervalMs { get; init; } = 500;

    public int InactiveGraceSeconds { get; init; } = 3;

    public string[] ExcludedProcesses { get; init; } = ["System", "svchost", "WinPanX.Agent"];

    public float ActivityPeakThreshold { get; init; } = 0.001f;

    public void Validate()
    {
        if (PollIntervalMs < 50)
        {
            throw new InvalidOperationException("PollIntervalMs must be at least 50.");
        }

        if (InactiveGraceSeconds < 0)
        {
            throw new InvalidOperationException("InactiveGraceSeconds cannot be negative.");
        }

        if (ActivityPeakThreshold is < 0.0f or > 1.0f)
        {
            throw new InvalidOperationException("ActivityPeakThreshold must be within [0.0, 1.0].");
        }
    }
}
