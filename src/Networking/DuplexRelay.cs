namespace Host2VMRelay;

/// <summary>Owns both copy tasks until they finish, including cancellation and fault cleanup.</summary>
internal static class DuplexRelay
{
    internal static async Task RunAsync(Stream left, Stream right, Action shutdownLeft,
        Action shutdownRight, Action abort, CancellationToken token, Action<bool, long, string>? ended = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Closing both sockets also unblocks transports that do not honor async cancellation.
        // A socket completion can run this method's continuation inline while
        // the abort callback waits for that completion to return. An ordinary
        // Dispose would block both workers waiting for each other. Await the
        // callback asynchronously so the completion worker can return first.
        await using var registration = stop.Token.Register(abort);
        async Task Pump(Stream source, Stream destination, Action shutdown, bool fromClient)
        {
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32768);
            long transferred = 0;
            try
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, 32768), stop.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), stop.Token).ConfigureAwait(false);
                    transferred += read;
                }
                ended?.Invoke(fromClient, transferred, "收到 EOF（该方向停止发送）");
                // Normal EOF is a half-close: the peer may still have a response to send.
                shutdown();
            }
            catch (Exception ex)
            {
                ended?.Invoke(fromClient, transferred, stop.IsCancellationRequested ? "中继已取消" : "读写异常：" + ex.Message);
                stop.Cancel();
                throw;
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        }
        await Task.WhenAll(Pump(left, right, shutdownRight, true), Pump(right, left, shutdownLeft, false))
            .ConfigureAwait(false);
    }
}
