using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinPanX.Core.Contracts;

public interface IEndpointCatalog : IAsyncDisposable
{
    event EventHandler<OutputDeviceChangedEventArgs>? OutputDeviceChanged;

    Task<EndpointCatalogSnapshot> InitializeAsync(
        string virtualEndpointNamePrefix,
        int slotCount,
        string outputDevicePreference,
        CancellationToken cancellationToken);

    IReadOnlyList<EndpointDescriptor> GetVirtualSlots();

    EndpointDescriptor GetOverflowSlot();

    EndpointDescriptor GetOutputDevice();

    Task<EndpointCatalogSnapshot> RefreshOutputDeviceAsync(CancellationToken cancellationToken);
}

