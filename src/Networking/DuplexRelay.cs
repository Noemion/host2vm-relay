namespace Host2VMRelay;

/// <summary>Owns both copy tasks until they finish, including cancellation and fault cleanup.</summary>
internal static class DuplexRelay
{
    internal static async Task RunAsync(Stream left, Stream right, Action shutdownLeft,
        Action shutdownRight, Action abort, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Closing both sockets also unblocks transports that do not honor async cancellation.
        using var registration = stop.Token.Register(abort);
        async Task Pump(Stream source, Stream destination, Action shutdown)
        {
            try
            {
                await source.CopyToAsync(destination, stop.Token).ConfigureAwait(false);
                // Normal EOF is a half-close: the peer may still have a response to send.
                shutdown();
            }
            catch
            {
                stop.Cancel();
                throw;
            }
        }
        await Task.WhenAll(Pump(left, right, shutdownRight), Pump(right, left, shutdownLeft))
            .ConfigureAwait(false);
    }
}
