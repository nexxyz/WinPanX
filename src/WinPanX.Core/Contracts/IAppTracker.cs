using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinPanX.Core.Contracts;

public interface IAppTracker : IAsyncDisposable
{
    event EventHandler<AppTrackerSnapshotChangedEventArgs>? SnapshotChanged;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    IReadOnlyCollection<TrackedAppSnapshot> GetSnapshot();
}

