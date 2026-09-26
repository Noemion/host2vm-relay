using System.Globalization;

namespace Host2VMRelay;

/// <summary>Wraps user JavaScript without executing it or rewriting its declarations.</summary>
public static class ScriptComposer
{
    public const int MaxSourceLength = 262144;
    public const string Header = "// Host2VMRelay composed script v1\n// original-length: ";
    public const string Open = "const __h2vmOriginalMain = (() => {\n";
    public const string Close = "\n\n  return typeof main === \"function\" ? main : null;\n})();\n\n";

    public static string ExtractOriginal(string? script)
    {
        string source = (script ?? "").TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        if (source.Length > MaxSourceLength + 32768)
            throw new ArgumentException("扩展脚本过大，请将原始脚本控制在 256K 字符以内。");
        if (source.StartsWith(Header, StringComparison.Ordinal))
        {
            int end = source.IndexOf('\n', Header.Length);
            if (end < 0 || !int.TryParse(source.AsSpan(Header.Length, end - Header.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out int length) || length < 0 || length > MaxSourceLength)
                throw new FormatException("生成脚本的标记已被修改，请重新提供原始扩展脚本。");
            int start = end + 1;
            if (!source.AsSpan(start).StartsWith(Open, StringComparison.Ordinal))
                throw new FormatException("生成脚本的结构已被修改，请重新提供原始扩展脚本。");
            start += Open.Length;
            if (length > source.Length - start || !source.AsSpan(start + length).StartsWith(Close, StringComparison.Ordinal))
                throw new FormatException("原始脚本的长度已变化，请只粘贴修改后的原始扩展脚本。");
            source = source.Substring(start, length);
        }
        if (source.Length > MaxSourceLength)
            throw new ArgumentException("扩展脚本过大，请控制在 256K 字符以内。");
        return source;
    }

    public static string Compose(string? existingScript, string relayScript)
    {
        string original = ExtractOriginal(existingScript);
        if (string.IsNullOrWhiteSpace(original)) original = "function main(config) { return config; }";
        return Header + original.Length.ToString(CultureInfo.InvariantCulture) + "\n" + Open + original + Close + relayScript + "\n";
    }
}
