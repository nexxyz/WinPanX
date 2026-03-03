using System.Threading.Channels;
using WinPanX.Agent.Audio;
using WinPanX.Agent.Configuration;
using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Runtime;

internal sealed class RuntimeCoordinator : IAsyncDisposable
{
    private readonly WinPanXConfig _config;
    private readonly IAppTracker _appTracker;
    private readonly SessionChannelPanController _panController;
    private readonly Channel<IReadOnlyCollection<TrackedAppSnapshot>> _snapshotQueue;

    private CancellationTokenSource? _snapshotProcessorCts;
    private Task? _snapshotProcessorTask;
    private volatile bool _isDisposing;

    public RuntimeCoordinator(
        WinPanXConfig config,
        IAppTracker appTracker,
        SessionChannelPanController panController)
    {
        _config = config;
        _appTracker = appTracker;
        _panController = panController;

        _snapshotQueue = Channel.CreateBounded<IReadOnlyCollection<TrackedAppSnapshot>>(
            new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _snapshotProcessorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _snapshotProcessorTask = Task.Run(
            () => ProcessSnapshotsAsync(_snapshotProcessorCts.Token),
            CancellationToken.None);

        _appTracker.SnapshotChanged += OnSnapshotChanged;
        await _appTracker.StartAsync(cancellationToken);

        _snapshotQueue.Writer.TryWrite(_appTracker.GetSnapshot().ToArray());

        SimpleLog.Info("Runtime started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_config.PollIntervalMs, cancellationToken);
        }
    }

    private async Task ProcessSnapshotsAsync(CancellationToken cancellationToken)
    {
        var reader = _snapshotQueue.Reader;

        while (await reader.WaitToReadAsync(cancellationToken))
        {
            IReadOnlyCollection<TrackedAppSnapshot>? latest = null;
            while (reader.TryRead(out var snapshot))
            {
                latest = snapshot;
            }

            if (latest is null)
            {
                continue;
            }

            await ProcessSnapshotAsync(latest, cancellationToken);
        }
    }

    private async Task ProcessSnapshotAsync(
        IReadOnlyCollection<TrackedAppSnapshot> apps,
        CancellationToken cancellationToken)
    {
        if (_isDisposing)
        {
            return;
        }

        await _panController.ApplyPanAsync(apps, cancellationToken);
    }

    private void OnSnapshotChanged(object? sender, AppTrackerSnapshotChangedEventArgs e)
    {
        if (_isDisposing)
        {
            return;
        }

        _snapshotQueue.Writer.TryWrite(e.Apps.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposing = true;
        _appTracker.SnapshotChanged -= OnSnapshotChanged;

        _snapshotQueue.Writer.TryComplete();
        if (_snapshotProcessorCts is not null)
        {
            _snapshotProcessorCts.Cancel();
        }

        if (_snapshotProcessorTask is not null)
        {
            try
            {
                await _snapshotProcessorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _panController.RestoreAllAsync(CancellationToken.None);
        await _appTracker.StopAsync(CancellationToken.None);

        await _appTracker.DisposeAsync();
        await _panController.DisposeAsync();

        _snapshotProcessorCts?.Dispose();
    }
}

