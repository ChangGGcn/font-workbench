using System.Buffers;
using System.Globalization;
using System.Text;

namespace FontBook.Workbench;

internal sealed record MissingScalar(uint Value, int Occurrences)
{
    public string Code => $"U+{Value:X4}";
    public string Character => char.ConvertFromUtf32((int)Value);
}

// Count Unicode scalars, never UTF-16 code units or glyph-run output. An absent cmap entry
// is a useful diagnostic, not proof that contextual layout cannot display the sample.
internal sealed record SampleCoverage(int Unique, int Occurrences, int Ignored, int Invalid, MissingScalar[] Missing)
{
    public int Mapped => Unique - Missing.Length;
    public string Summary => Unique == 0 ? "无可检查字符" : Missing.Length == 0
        ? $"直接映射 {Unique}/{Unique} · 未见缺字"
        : $"缺 {Missing.Length} 种 · {string.Join("、", Missing.Take(4).Select(m => m.Code))}{(Missing.Length > 4 ? "…" : "")}";
    public string Explain => $"当前字面的直接字符映射：{Mapped}/{Unique} 种；计入 {Occurrences} 次。跳过空白、格式与控制 {Ignored} 次；无效 UTF-16 {Invalid} 次。此结果不判断 GSUB 或最终排版。" +
        (Missing.Length == 0 ? "" : "\n" + string.Join("、", Missing.Take(32).Select(m => $"{m.Code} ×{m.Occurrences}")));
    public string Report => string.Join(Environment.NewLine, Missing.Select(m => $"{m.Code}\t{m.Character}\t{m.Occurrences}"));

    public static SampleCoverage Analyze(string text, Func<uint[], ushort[]> map)
    {
        var counts = new Dictionary<uint, int>();
        var order = new List<uint>();
        int offset = 0, ignored = 0, invalid = 0, occurrences = 0;
        while (offset < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out int used);
            offset += Math.Max(1, used);
            if (status != OperationStatus.Done)
            {
                invalid++;
                continue;
            }
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format ||
               rune.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF)
            {
                ignored++;
                continue;
            }
            uint cp = (uint)rune.Value;
            if (!counts.TryAdd(cp, 1))
                counts[cp]++;
            else
                order.Add(cp);
            occurrences++;
        }
        uint[] chars = order.ToArray();
        var glyphs = chars.Length == 0 ? [] : map(chars);
        if (glyphs.Length != chars.Length)
            throw new InvalidOperationException("字符映射结果与输入长度不符。");
        var missing = new List<MissingScalar>();
        for (int i = 0; i < chars.Length; i++)
        if (glyphs[i] == 0)
            missing.Add(new(chars[i], counts[chars[i]]));
        return new(chars.Length, occurrences, ignored, invalid, missing.ToArray());
    }
}
