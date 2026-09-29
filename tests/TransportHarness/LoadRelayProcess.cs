using System.Diagnostics;
using System.Text.Json;
using Host2VMRelay;

/// <summary>Runs only the production relay in a child, excluding test peers from its memory/CPU readings.</summary>
internal sealed class LoadRelayProcess : IAsyncDisposable
{
    internal sealed record Snapshot(int Active, int Limit, int Sockets, long Rejected,
        long WorkingSet, long PrivateBytes, double CpuMilliseconds, int Threads, int Handles);
    private readonly Process process;
    private readonly Task<string> errors;
    internal int Port { get; }
    private LoadRelayProcess(Process process, int port, Task<string> errors)
    { this.process = process; Port = port; this.errors = errors; }
    internal static async Task<LoadRelayProcess> StartAsync(int upstream, int limit, CancellationToken token)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Missing process path");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(LoadRelayProcess).Assembly.Location);
        start.ArgumentList.Add("--load-relay"); start.ArgumentList.Add(upstream.ToString()); start.ArgumentList.Add(limit.ToString());
        var child = Process.Start(start) ?? throw new IOException("Cannot start isolated relay");
        Task<string> error = child.StandardError.ReadToEndAsync();
        try
        {
            string? line = await child.StandardOutput.ReadLineAsync(token);
            return new(child, int.Parse(line ?? throw new IOException("Relay exited before readiness")), error);
        }
        catch { if (!child.HasExited) child.Kill(); child.Dispose(); throw; }
    }
    internal async Task<Snapshot> ReadAsync(CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync("status"); await process.StandardInput.FlushAsync(token);
        string? line = await process.StandardOutput.ReadLineAsync(token);
        return JsonSerializer.Deserialize<Snapshot>(line ?? throw new IOException("Relay exited"))!;
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited) { process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        catch (TimeoutException) { process.Kill(); await process.WaitForExitAsync(); }
        finally { string error = await errors; process.Dispose(); if (error.Length > 0) Console.Error.WriteLine(error); }
    }
    internal static async Task RunChildAsync(int upstream, int limit)
    {
        using var relay = new RelaySocksServer(0, maxTransfers: limit);
        relay.SetUpstream(upstream, null);
        using var refresh = new System.Threading.Timer(_ => relay.SetUpstream(upstream, null), null,
            RelayHealthTiming.PollInterval, RelayHealthTiming.PollInterval);
        Console.WriteLine(relay.Port);
        while (await Console.In.ReadLineAsync() is not null)
        {
            using var own = Process.GetCurrentProcess(); own.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new Snapshot(relay.ActiveTransfers, relay.TransferLimit, relay.ActiveConnections,
                relay.RejectedConnections, own.WorkingSet64, own.PrivateMemorySize64, own.TotalProcessorTime.TotalMilliseconds,
                own.Threads.Count, own.HandleCount)));
        }
    }
}
