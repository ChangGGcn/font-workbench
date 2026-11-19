using System.IO;
using System.Text.RegularExpressions;

namespace FontBook.Services;

/// <summary>纯校验 API：规范化字体路径并拒绝遍历、设备路径和不支持的后缀。</summary>
public static class FontOperationSafety
{
    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ttf", ".otf", ".ttc", ".otc" };
    private static readonly Regex HashPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static bool TryNormalizeFontPath(string? path, out string normalized, out string reason)
    {
        normalized = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        { reason = "路径为空或包含非法字符。"; return false; }
        if (!string.Equals(path, path.Trim(), StringComparison.Ordinal))
        { reason = "路径首尾包含空白字符；拒绝隐式修剪后指向不同文件。"; return false; }
        var raw = path;
        if (raw.StartsWith("\\\\?\\", StringComparison.Ordinal) || raw.StartsWith("\\\\.\\", StringComparison.Ordinal) || raw.StartsWith("\\\\", StringComparison.Ordinal))
        { reason = "不接受设备路径或网络路径。"; return false; }
        if (!Path.IsPathFullyQualified(raw)) { reason = "必须提供完整绝对路径。"; return false; }
        var root = Path.GetPathRoot(raw) ?? "";
        if (raw.AsSpan(root.Length).Contains(':')) { reason = "不接受备用数据流（ADS）路径。"; return false; }
        var segments = raw.Replace('/', '\\').Split('\\');
        if (segments.Any(s => s == "..")) { reason = "路径包含遍历段。"; return false; }
        if (segments.Any(s => s.EndsWith(' ') || s.EndsWith('.'))) { reason = "路径段末尾空格或句点可能造成路径别名；已拒绝。"; return false; }
        try { normalized = Path.GetFullPath(raw); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { reason = "路径无法规范化。"; return false; }
        if (!FontExtensions.Contains(Path.GetExtension(normalized)))
        { reason = "仅支持 TTF、OTF、TTC、OTC 字体文件。"; normalized = ""; return false; }
        return true;
    }

    public static bool IsPathWithin(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool IsValidId(string? id) => id is { Length: >= 20 and <= 80 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    internal static bool IsValidHash(string? hash) => hash != null && HashPattern.IsMatch(hash);
}
