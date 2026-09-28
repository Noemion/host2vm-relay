using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace Host2VMRelay;

internal static class RouteDiagnostics
{
    public static Uri ParseTarget(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Host.Length == 0)
            throw new ArgumentException("请填写 HTTP 或 HTTPS 网址，不能包含用户名和密码。");
        // Routing needs only the authority. Never persist/send login tokens,
        // query strings, fragments, cookies or a user's browser credentials.
        return new UriBuilder(uri.Scheme, uri.IdnHost, uri.Port) { Path = "/" }.Uri;
    }

    public static string? MatchLocal(string compiled, string host)
    {
        host = host.TrimEnd('.');
        foreach (string line in compiled.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            if (parts[0] == "DOMAIN" && host.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) return line;
            if (parts[0] == "DOMAIN-SUFFIX" && (host.Equals(parts[1], StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + parts[1], StringComparison.OrdinalIgnoreCase))) return line;
            if (parts[0] is "IP-CIDR" or "IP-CIDR6" && IPAddress.TryParse(host, out var target))
            {
                string[] range = parts[1].Split('/');
                if (range.Length != 2 || !IPAddress.TryParse(range[0], out var network) ||
                    target.AddressFamily != network.AddressFamily || !int.TryParse(range[1], out int bits)) continue;
                byte[] address = target.GetAddressBytes(), prefix = network.GetAddressBytes();
                if (bits < 0 || bits > address.Length * 8) continue;
                bool matches = true;
                for (int bit = 0; bit < bits; bit++)
                    if ((address[bit / 8] & (128 >> (bit % 8))) != (prefix[bit / 8] & (128 >> (bit % 8)))) { matches = false; break; }
                if (matches) return line;
            }
        }
        return null;
    }

    // Match the socket we own, not merely another browser connection to the
    // same host. The client port remains reserved until observation completes.
    public static JsonElement? FindConnection(JsonElement root, int sourcePort, Uri target)
    {
        if (!root.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array) return null;
        foreach (var connection in connections.EnumerateArray())
        {
            if (!connection.TryGetProperty("metadata", out var meta)) continue;
            if (Text(meta, "network") != "tcp" || Text(meta, "sourcePort") != sourcePort.ToString() ||
                Text(meta, "destinationPort") != target.Port.ToString()) continue;
            if (!IPAddress.TryParse(Text(meta, "sourceIP"), out var source) || !IPAddress.IsLoopback(source)) continue;
            if (!Text(meta, "host").Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase)) continue;
            return connection.Clone();
        }
        return null;
    }

    public static bool UsesRelay(JsonElement connection) => connection.TryGetProperty("chains", out var chains) &&
        chains.ValueKind == JsonValueKind.Array && chains.EnumerateArray().Any(x => x.GetString() == "Host2VMRelay");

    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) ? found.ToString() : "";

    public static async Task RunAsync(Uri target, string savedRules, bool relayConnected,
        Action<string> report, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        var token = budget.Token;
        string stage = "检查本地规则";
        try
        {
            report($"目标：{target.IdnHost}:{target.Port}（TCP）");
            report(relayConnected ? "虚拟机连接：已建立。" : "虚拟机连接：未建立，不能确认虚拟机转发。");
            string compiled = Rules.Compile(savedRules);
            string? match = MatchLocal(compiled, target.IdnHost);
            report(match is null ? "本地规则：目标未直接命中。主域名不包含子域名，请使用 *.域名；域名解析后的 IP 匹配以实测为准。" : "本地规则命中：" + match);
            string active = await File.ReadAllTextAsync(ClashRuleFile.FilePath, token);
            report(active.Replace("\r\n", "\n").Trim() == compiled.Trim()
                ? "规则文件：与已保存规则一致。" : "规则文件：与已保存规则不同，可能因连接未就绪而暂停转发。");

            stage = "读取 Clash 当前状态";
            using var api = ClashControlClient.Open();
            using var config = await ClashControlClient.ReadAsync(api, "configs", token);
            report("Clash 当前模式：" + Text(config.RootElement, "mode"));
            using var rules = await ClashControlClient.ReadAsync(api, "rules", token);
            bool loaded = rules.RootElement.TryGetProperty("rules", out var list) && list.EnumerateArray().Any(r =>
                Text(r, "proxy") == "Host2VMRelay-TCP" && Text(r, "payload").Contains("host2vm-relay-rules", StringComparison.Ordinal));
            report(loaded ? "Clash 已加载本软件的 TCP 分流入口；目标是否生效仍以实际连接为准。" : "Clash 未找到本软件的 TCP 分流入口，请重新生成并应用扩展脚本。");
            using var providers = await ClashControlClient.ReadAsync(api, "providers/rules", token);
            if (providers.RootElement.TryGetProperty("providers", out var all) && all.TryGetProperty("host2vm-relay-rules", out var provider))
                report("Clash 规则集条数：" + Text(provider, "ruleCount") + "（条数相同不代表内容相同）。");
            else report("Clash 未加载本软件的规则集。");

            int proxyPort = Port(config.RootElement, "mixed-port");
            if (proxyPort == 0) proxyPort = Port(config.RootElement, "port");
            if (proxyPort == 0)
            {
                report("无法实测：Clash 未启用 HTTP 或混合代理端口。配置检查不能证明请求已转发。");
                return;
            }
            stage = "通过 Clash 建立目标 TCP 连接";
            using var socket = new TcpClient(AddressFamily.InterNetwork);
            await socket.ConnectAsync(IPAddress.Loopback, proxyPort, token);
            int sourcePort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            using var stream = socket.GetStream();
            string authority = target.HostNameType == UriHostNameType.IPv6 ? $"[{target.IdnHost}]:{target.Port}" : $"{target.IdnHost}:{target.Port}";
            byte[] request = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
            await stream.WriteAsync(request, token);
            string header = await ReadProxyHeaderAsync(stream, token);
            string status = header.Split('\r')[0];
            if (!status.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal) && !status.StartsWith("HTTP/1.0 200 ", StringComparison.Ordinal))
            {
                report("Clash 未建立目标连接：" + status + "。若返回 407，需要在 Clash 中检查代理认证设置。");
                return;
            }

            stage = "核对实际连接链路";
            JsonElement? observed = null;
            for (int attempt = 0; attempt < 5 && observed is null; attempt++)
            {
                using var snapshot = await ClashControlClient.ReadAsync(api, "connections", token);
                observed = FindConnection(snapshot.RootElement, sourcePort, target);
                if (observed is null) await Task.Delay(150, token);
            }
            if (observed is { } connection)
            {
                string chains = connection.TryGetProperty("chains", out var chain) ? string.Join(" → ", chain.EnumerateArray().Select(x => x.GetString())) : "未知";
                report("实际链路：" + chains);
                report("实际命中：" + Text(connection, "rule") + " / " + Text(connection, "rulePayload"));
                report(UsesRelay(connection) ? "已确认：本次 TCP 请求经过 Host2VMRelay 节点。" : "未经过 Host2VMRelay：检查规则匹配、策略组回退和脚本是否已应用。");
            }
            else report("Clash 已接受代理请求，但未捕获本次连接记录，无法确认转发链路。");

            if (target.Scheme == "https")
            {
                stage = "验证目标 TLS 证书";
                SslPolicyErrors certificateErrors = SslPolicyErrors.None;
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true, (_, _, _, errors) =>
                {
                    certificateErrors = errors;
                    return errors == SslPolicyErrors.None;
                });
                try
                {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = target.IdnHost }, token);
                    report("TLS：握手和证书校验通过。");
                }
                catch (AuthenticationException) when (certificateErrors != SslPolicyErrors.None)
                {
                    report("TLS 证书校验失败：" + certificateErrors + "。TCP 已连通；请检查证书信任、域名与有效期，勿关闭证书校验。");
                }
            }
            report("检测范围：本次经 Clash 代理端口的 TCP 请求；未验证浏览器 TUN/DNS 接管、UDP、网页内容或登录状态。");
        }
        catch (OperationCanceledException)
        {
            report(cancellationToken.IsCancellationRequested ? "检测已取消。" : "检测超时：" + stage + "，未完成的项目不能视为通过。");
        }
        catch (Exception ex) { report("检测未完成（" + stage + "）：" + ex.Message); }
    }

    private static int Port(JsonElement config, string name) =>
        int.TryParse(Text(config, name), out int port) && port is > 0 and <= 65535 ? port : 0;

    internal static async Task<string> ReadProxyHeaderAsync(Stream stream, CancellationToken token)
    {
        var header = new StringBuilder();
        byte[] one = new byte[1];
        while (header.Length < 8192)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Clash 在返回完整代理响应前关闭了连接。");
            header.Append((char)one[0]);
            if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") return header.ToString();
        }
        throw new IOException("Clash 代理响应头过长。");
    }
}
