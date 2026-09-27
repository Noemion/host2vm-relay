using System.Text;

namespace Host2VMRelay;

/// <summary>Bounds producer memory and UI work independently of network concurrency.</summary>
internal sealed class PendingLogBuffer
{
    internal const int MaxBatchCharacters = 16384;
    private readonly object gate = new();
    private readonly Queue<string> lines = new();
    private int dropped;
    public void Add(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " +
            (message.Length > 4096 ? message[..4096] + "…" : message) + Environment.NewLine;
        lock (gate)
        {
            if (lines.Count == 1000) { lines.Dequeue(); dropped++; }
            lines.Enqueue(line);
        }
    }
    public string Drain()
    {
        lock (gate)
        {
            var result = new StringBuilder();
            if (dropped > 0) { result.AppendLine($"日志繁忙，已省略 {dropped} 条较早记录。"); dropped = 0; }
            for (int i = 0; i < 100 && lines.Count > 0; i++)
            {
                if (result.Length + lines.Peek().Length > MaxBatchCharacters) break;
                result.Append(lines.Dequeue());
            }
            return result.ToString();
        }
    }
    public void Clear() { lock (gate) { lines.Clear(); dropped = 0; } }
}
