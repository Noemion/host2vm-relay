using System.IO.Pipes;
using System.Text.Json;

namespace Host2VMRelay;

/// <summary>Uses the existing local controller; never enables a network API.</summary>
internal static class ClashControlClient
{
    public static HttpClient Open()
    {
        var pipes = Directory.GetFiles(@"\\.\pipe\")
            .Where(p => Path.GetFileName(p).StartsWith("verge-mihomo-production-", StringComparison.Ordinal)).ToArray();
        if (pipes.Length != 1) throw new IOException("无法唯一确定 Clash Verge 控制接口，请确认 Clash 正在运行。");
        string name = Path.GetFileName(pipes[0]);
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(token); return pipe; }
                catch { pipe.Dispose(); throw; }
            }
        }) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    }

    public static async Task<JsonDocument> ReadAsync(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
    }
}
