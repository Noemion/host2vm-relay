using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Host2VMRelay;

public static class Rules
{
    public static string Compile(string input)
    {
        var result = new List<string>();
        foreach (var raw in input.Split('\n'))
        {
            var value = raw.Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            if (value.Contains(',')) throw new FormatException("请输入域名或 IP，不要输入 Clash 规则：" + value);
            if (value.Contains('/'))
            {
                var parts = value.Split('/');
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
                    !int.TryParse(parts[1], out int bits) || bits < 0 || bits > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
                    throw new FormatException("无效网段（不要输入网址或路径）：" + value);
                result.Add((address.AddressFamily == AddressFamily.InterNetwork ? "IP-CIDR," : "IP-CIDR6,") + address + "/" + bits + ",no-resolve");
            }
            else if (IPAddress.TryParse(value, out var ip))
            {
                result.Add((ip.AddressFamily == AddressFamily.InterNetwork ? "IP-CIDR," + ip + "/32" : "IP-CIDR6," + ip + "/128") + ",no-resolve");
            }
            else
            {
                bool suffix = value.StartsWith("*.") || value.StartsWith("+.");
                string domain = suffix ? value[2..] : value;
                try { domain = new System.Globalization.IdnMapping().GetAscii(domain.TrimEnd('.')).ToLowerInvariant(); }
                catch { throw new FormatException("无效域名：" + value); }
                if (domain.Length > 253 || !domain.Contains('.') || domain.Split('.').Any(x => !Regex.IsMatch(x, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")) || Regex.IsMatch(domain, "^[0-9.]+$"))
                    throw new FormatException("无效域名或 IP：" + value);
                result.Add((suffix ? "DOMAIN-SUFFIX," : "DOMAIN,") + domain);
            }
        }
        return result.Count == 0 ? "# No forwarding rules\n" : string.Join("\n", result.Distinct()) + "\n";
    }
}
