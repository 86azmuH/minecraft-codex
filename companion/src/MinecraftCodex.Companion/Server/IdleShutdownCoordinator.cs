namespace MinecraftCodex.Companion.Server;

public sealed class IdleShutdownCoordinator : IDisposable
{
    private readonly object sync = new();
    private readonly TimeSpan grace;
    private readonly Action requestShutdown;
    private CancellationTokenSource? timer;
    private int clients;
    private int activeTasks;
    private bool started;
    private bool stopping;

    public IdleShutdownCoordinator(TimeSpan grace, Action requestShutdown)
    {
        if (grace <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grace));
        this.grace = grace;
        this.requestShutdown = requestShutdown;
    }

    public void Start(int initialActiveTasks)
    {
        lock (sync)
        {
            if (started) return;
            started = true;
            activeTasks = initialActiveTasks;
            RearmLocked();
        }
    }

    public bool TryClientConnected()
    {
        lock (sync)
        {
            if (stopping) return false;
            clients++;
            CancelTimerLocked();
            return true;
        }
    }

    public void ClientDisconnected()
    {
        lock (sync)
        {
            if (clients > 0) clients--;
            RearmLocked();
        }
    }

    public void ActiveTasksChanged(int count)
    {
        lock (sync)
        {
            activeTasks = Math.Max(0, count);
            RearmLocked();
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            stopping = true;
            CancelTimerLocked();
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void RearmLocked()
    {
        CancelTimerLocked();
        if (!started || stopping || clients != 0 || activeTasks != 0) return;
        timer = new CancellationTokenSource();
        _ = WaitForIdleAsync(timer.Token);
    }

    private async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(grace, cancellationToken); }
        catch (OperationCanceledException) { return; }

        lock (sync)
        {
            if (cancellationToken.IsCancellationRequested || stopping || clients != 0 || activeTasks != 0) return;
            stopping = true;
        }
        requestShutdown();
    }

    private void CancelTimerLocked()
    {
        if (timer is null) return;
        timer.Cancel();
        timer.Dispose();
        timer = null;
    }
}
