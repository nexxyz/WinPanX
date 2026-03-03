using System.Threading;
using System.Threading.Tasks;

namespace WinPanX.Core.Contracts;

public interface IMixer : IAsyncDisposable
{
    Task StartAsync(MixerStartRequest request, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets pan for dedicated slot indexes 1..7 (MVP).
    /// </summary>
    void SetDedicatedSlotPan(int slotIndex, float pan);

    /// <summary>
    /// Sets aggregate pan for overflow slot 8.
    /// </summary>
    void SetOverflowPan(float pan);

    Task SwitchOutputDeviceAsync(string outputEndpointId, CancellationToken cancellationToken);

    MixerStats GetStats();
}

