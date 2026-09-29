namespace Host2VMRelay;

internal sealed record ClashRelayProtocol(string Label, string Provider)
{
    internal const string Node = "Host2VMRelay";
    internal string Group => Node + "-" + Label;
    internal string FileName => Provider + ".txt";
    internal string HealthUrl => "http://health.host2vm-relay.invalid/" + Label.ToLowerInvariant();
    internal static readonly ClashRelayProtocol Tcp = new("TCP", "host2vm-relay-rules");
    internal static readonly ClashRelayProtocol Udp = new("UDP", "host2vm-relay-udp-rules");
    internal static IEnumerable<ClashRelayProtocol> All => new[] { Tcp, Udp };
}
