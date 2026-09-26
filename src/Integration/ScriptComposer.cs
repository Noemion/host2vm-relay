using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Host2VMRelay;

public sealed record ScriptImport(string Original, string Kind, string? ManagedVersion = null);

/// <summary>Updates the managed section without executing or rewriting the user's JavaScript.</summary>
public static class ScriptComposer
{
    public const int MaxSourceLength = 262144;
    public const int MaxGeneratedLength = MaxSourceLength + 65536;
    public const string Header = "// Host2VMRelay composed script v2\n";
    public const string LegacyHeader = "// Host2VMRelay composed script v1\n// original-length: ";
    public const string Open = "const __h2vmOriginalMain = (() => {\n";
    public const string Close = "\n\n  return typeof main === \"function\" ? main : null;\n})();\n\n";
    public const string UserStart = "// <host2vm-relay:user>\n";
    public const string UserEnd = "// </host2vm-relay:user>\n";
    public const string ManagedStart = "// <host2vm-relay:managed>\n";
    public const string ManagedEnd = "// </host2vm-relay:managed>\n";
    private const string ClosingWrapper = "  return typeof main === \"function\" ? main : null;\n})();\n\n";
    private const string VersionPrefix = "// generator-version: ";
    private const string HashPrefix = "// managed-sha256: ";
    private const string GeneratorVersion = "0.6.0";
    private static readonly HashSet<string> LegacyFingerprints = new(StringComparer.Ordinal)
    {
        "b0b8a56bc87d85fe2bec2b63349965a7de3c7f00660dfa3d2a4a7412d76d138f", // 0.4.1-0.5.0
        "8c0a421d06f47de60cd5730c7c158f6776162332a12251939d05010b83ea7aa9"  // 0.4.0
    };
    public static string Normalize(string text) => text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
    private static string CanonicalManaged(string text) => Normalize(text).TrimEnd('\n');
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static FormatException Conflict(string reason) => new(reason + " 原文件未修改。请将自定义改动放入原始脚本区域后重新合成，不要覆盖无法确认的托管区。");

    public static ScriptImport Inspect(string? script)
    {
        string source = Normalize(script ?? "");
        if (source.Length > MaxGeneratedLength) throw new ArgumentException("脚本过大，请将原始脚本控制在 256K 字符以内。");
        if (source.StartsWith(Header, StringComparison.Ordinal)) return ReadV2(source);
        if (source.StartsWith(LegacyHeader, StringComparison.Ordinal)) return ReadV1(source);
        if (source.Contains("Host2VMRelay composed script v", StringComparison.Ordinal) || ContainsReservedMarker(source))
            throw Conflict("生成脚本头或区域标记不完整，不能安全识别。");
        CheckUserSource(source);
        return new(source, "original");
    }
    public static string ExtractOriginal(string? script) => Inspect(script).Original;
    public static string Compose(string? existingScript, string relayScript)
    {
        string original = Inspect(existingScript).Original;
        if (string.IsNullOrWhiteSpace(original)) original = "function main(config) { return config; }";
        CheckUserSource(original);
        string managed = CanonicalManaged(relayScript);
        if (managed.Length == 0 || ContainsReservedMarker(managed)) throw new ArgumentException("托管模板为空或包含保留的区域标记。");
        return Header + VersionPrefix + GeneratorVersion + "\n" + HashPrefix + Digest(managed) + "\n" +
            Open + UserStart + original + "\n" + UserEnd + ClosingWrapper +
            ManagedStart + managed + "\n" + ManagedEnd;
    }
    private static ScriptImport ReadV2(string source)
    {
        int position = Header.Length;
        string version = ReadMetadata(source, ref position, VersionPrefix);
        string expected = ReadMetadata(source, ref position, HashPrefix);
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$") ||
            !Regex.IsMatch(expected, "^[a-f0-9]{64}$")) throw Conflict("生成脚本元数据已损坏。");
        string before = Open + UserStart;
        if (!source.AsSpan(position).StartsWith(before, StringComparison.Ordinal)) throw Conflict("原始脚本包装结构已修改。");
        position += before.Length;
        string boundary = "\n" + UserEnd;
        int end = source.IndexOf(boundary, position, StringComparison.Ordinal);
        if (end < 0 || source.IndexOf(boundary, end + boundary.Length, StringComparison.Ordinal) >= 0)
            throw Conflict("原始脚本区域缺失或重复。");
        string original = source[position..end];
        position = end + boundary.Length;
        string after = ClosingWrapper + ManagedStart;
        if (!source.AsSpan(position).StartsWith(after, StringComparison.Ordinal)) throw Conflict("用户区与托管区之间的结构已修改。");
        position += after.Length;
        string trailer = "\n" + ManagedEnd;
        if (position > source.Length - trailer.Length || !source.EndsWith(trailer, StringComparison.Ordinal)) throw Conflict("托管区结束标记缺失，或末尾存在无法归属的自定义代码。");
        string managed = source[position..^trailer.Length];
        if (ContainsReservedMarker(managed) || !string.Equals(Digest(managed), expected, StringComparison.Ordinal))
            throw Conflict("检测到托管区被手动修改或截断，已停止覆盖。");
        CheckUserSource(original);
        return new(original, "composed-v2", version);
    }
    private static string ReadMetadata(string source, ref int position, string prefix)
    {
        if (!source.AsSpan(position).StartsWith(prefix, StringComparison.Ordinal)) throw Conflict("生成脚本元数据缺失。");
        int start = position + prefix.Length;
        int end = source.IndexOf('\n', start);
        if (end < 0) throw Conflict("生成脚本元数据不完整。");
        position = end + 1; return source[start..end];
    }
    private static ScriptImport ReadV1(string source)
    {
        int end = source.IndexOf('\n', LegacyHeader.Length);
        if (end < 0 || !int.TryParse(source.AsSpan(LegacyHeader.Length, end - LegacyHeader.Length),
            NumberStyles.None, CultureInfo.InvariantCulture, out int previousLength) || previousLength < 0 || previousLength > MaxSourceLength)
            throw Conflict("旧版脚本的长度标记损坏。");
        int start = end + 1;
        if (!source.AsSpan(start).StartsWith(Open, StringComparison.Ordinal)) throw Conflict("旧版脚本包装结构已修改。");
        start += Open.Length;
        int boundary = source.IndexOf(Close, start, StringComparison.Ordinal);
        if (boundary < 0 || source.IndexOf(Close, boundary + Close.Length, StringComparison.Ordinal) >= 0)
            throw Conflict("旧版原始脚本边界无法唯一确定。");
        // Recover edited user code even if the old length changed; require an exact known managed template.
        string original = source[start..boundary];
        string managed = CanonicalManaged(source[(boundary + Close.Length)..]);
        if (!IsKnownLegacyTemplate(managed)) throw Conflict("旧版托管区不是已知模板，或包含手动改动。");
        CheckUserSource(original);
        return new(original, "composed-v1", "0.4.0-0.5.0");
    }
    private static bool IsKnownLegacyTemplate(string managed)
    {
        var portMatches = Regex.Matches(managed, @"port: ([0-9]+), udp: false }");
        if (portMatches.Count != 1 || !int.TryParse(portMatches[0].Groups[1].Value, out int port) || port is < 1 or > 65535) return false;
        var route = Regex.Match(managed, "^    \"(IP-CIDR6|IP-CIDR),([^\"]+),DIRECT,no-resolve\",$", RegexOptions.Multiline);
        if (!route.Success) return false;
        string kind = route.Groups[1].Value, cidr = route.Groups[2].Value;
        int slash = cidr.LastIndexOf('/');
        if (slash < 0 || !IPAddress.TryParse(cidr[..slash], out var address)) return false;
        bool ipv6 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        if (kind != (ipv6 ? "IP-CIDR6" : "IP-CIDR") || cidr[(slash + 1)..] != (ipv6 ? "128" : "32")) return false;
        string normalized = managed.Replace(portMatches[0].Value, "port: __SOCKS_PORT__, udp: false }", StringComparison.Ordinal)
            .Replace(kind + "," + cidr + ",DIRECT,no-resolve", "__VM_RULE_TYPE__,__VM_CIDR__,DIRECT,no-resolve", StringComparison.Ordinal)
            .Replace(cidr, "__VM_CIDR__", StringComparison.Ordinal);
        return LegacyFingerprints.Contains(Digest(normalized));
    }
    private static bool ContainsReservedMarker(string source) =>
        source.Contains("<host2vm-relay:", StringComparison.Ordinal) || source.Contains("</host2vm-relay:", StringComparison.Ordinal);
    private static void CheckUserSource(string source)
    {
        if (source.Length > MaxSourceLength) throw new ArgumentException("原始脚本过大，请控制在 256K 字符以内。");
        if (ContainsReservedMarker(source) || source.Contains("Host2VMRelay composed script v", StringComparison.Ordinal))
            throw Conflict("原始脚本包含嵌套生成文件或保留标记。");
    }
}
