namespace Host2VMRelay;

/// <summary>Health budget for a local VM; scheduling delays are measured separately.</summary>
internal static class RelayHealthTiming
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan Lease = TimeSpan.FromMilliseconds(600);
}
