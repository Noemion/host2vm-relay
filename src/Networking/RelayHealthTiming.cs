namespace Host2VMRelay;

/// <summary>Shared timing budget for sequential SSH and UDP probes.</summary>
internal static class RelayHealthTiming
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    // One refresh can spend a timeout on SSH and another on UDP before the next
    // interval starts. Retain one extra interval for scheduling jitter.
    internal static readonly TimeSpan Lease = PollInterval * 2 + ProbeTimeout * 2;
}
