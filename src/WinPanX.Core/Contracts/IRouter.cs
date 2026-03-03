using System.Threading;
using System.Threading.Tasks;

namespace WinPanX.Core.Contracts;

/// <summary>
/// Provides per-app endpoint routing behind a safe abstraction.
/// Implementations may use undocumented Windows policy APIs.
/// </summary>
public interface IRouter
{
    bool IsSupported { get; }

    Task<RoutingResult> BindToEndpointAsync(
        AppRuntimeId appId,
        string endpointId,
        RoutingRoles roles,
        CancellationToken cancellationToken);

    Task<RoutingResult> ResetToSystemDefaultAsync(
        AppRuntimeId appId,
        RoutingRoles roles,
        CancellationToken cancellationToken);
}

