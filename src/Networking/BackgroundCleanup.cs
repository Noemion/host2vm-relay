namespace Host2VMRelay;

/// <summary>Serializes blocking disposal off the caller's (usually UI) thread.</summary>
internal sealed class BackgroundCleanup
{
    private readonly object gate = new();
    private Task pending = Task.CompletedTask;

    public Task Enqueue(IDisposable? resource, Task? disposalCompleted = null)
    {
        lock (gate)
        {
            if (resource is null) return pending;
            Task previous = pending;
            return pending = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                finally
                {
                    resource.Dispose();
                    // A concurrent health probe may already own disposal. Its
                    // idempotent Dispose call can return before sockets are freed.
                    if (disposalCompleted is not null) await disposalCompleted.ConfigureAwait(false);
                }
            });
        }
    }
}
