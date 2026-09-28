using System.Text;
using System.Text.Json;

namespace Host2VMRelay;

internal static class RouteDiagnosticChecks
{
    public static void Run(Action<bool, string> check)
    {
        var target = RouteDiagnostics.ParseTarget("https://pm.example.com/login?token=private#fragment");
        check(target.AbsoluteUri == "https://pm.example.com/", "route diagnostics discard paths, tokens and fragments");
        bool rejected = false;
        try { RouteDiagnostics.ParseTarget("https://user:secret@pm.example.com/"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "route diagnostics reject embedded credentials");
        check(RouteDiagnostics.MatchLocal(Rules.Compile("example.com"), target.Host) is null &&
              RouteDiagnostics.MatchLocal(Rules.Compile("*.example.com"), target.Host) is not null &&
              RouteDiagnostics.MatchLocal(Rules.Compile("*.example.com"), "notexample.com") is null,
              "route diagnostics distinguish exact domains, subdomains and suffix boundaries");
        string networks = Rules.Compile("10.44.1.8/31\nfd00::/64");
        check(RouteDiagnostics.MatchLocal(networks, "10.44.1.9") is not null &&
              RouteDiagnostics.MatchLocal(networks, "10.44.1.10") is null &&
              RouteDiagnostics.MatchLocal(networks, "fd00::1234") is not null &&
              RouteDiagnostics.MatchLocal(networks, "fd00:0:0:1::1") is null,
              "route diagnostics honor IPv4 and IPv6 prefix boundaries");
        using var connections = JsonDocument.Parse("""
            {"connections":[
              {"metadata":{"network":"tcp","sourceIP":"127.0.0.1","sourcePort":"43210","destinationPort":"443","host":"other.example.com"},"chains":["Host2VMRelay"]},
              {"metadata":{"network":"tcp","sourceIP":"127.0.0.1","sourcePort":"43211","destinationPort":"443","host":"pm.example.com"},"chains":["Host2VMRelay"]},
              {"metadata":{"network":"tcp","sourceIP":"127.0.0.1","sourcePort":"43210","destinationPort":"443","host":"pm.example.com"},"chains":["DIRECT"],"rule":"GeoIP","rulePayload":"private"}
            ]}
            """);
        var actual = RouteDiagnostics.FindConnection(connections.RootElement, 43210, target);
        check(actual is not null && !RouteDiagnostics.UsesRelay(actual.Value), "unrelated relay connections cannot turn an observed DIRECT path into success");
        check(RouteDiagnostics.FindConnection(connections.RootElement, 54321, target) is null, "missing connection evidence stays unconfirmed");
        using var relay = JsonDocument.Parse("""{"chains":["Host2VMRelay","Host2VMRelay-TCP"]}""");
        using var fallback = JsonDocument.Parse("""{"chains":["DIRECT","Host2VMRelay-TCP"]}""");
        check(RouteDiagnostics.UsesRelay(relay.RootElement) && !RouteDiagnostics.UsesRelay(fallback.RootElement), "group names alone do not prove the relay node was selected");
        using var response = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\nextra"));
        string header = RouteDiagnostics.ReadProxyHeaderAsync(response, CancellationToken.None).GetAwaiter().GetResult();
        check(header.EndsWith("\r\n\r\n") && response.Position == header.Length, "proxy header reader leaves tunnel bytes untouched");
        using var truncated = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200"));
        rejected = false;
        try { RouteDiagnostics.ReadProxyHeaderAsync(truncated, CancellationToken.None).GetAwaiter().GetResult(); } catch (IOException) { rejected = true; }
        check(rejected, "truncated proxy responses cannot certify a connection");
    }
}

