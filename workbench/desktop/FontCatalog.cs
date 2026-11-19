using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FontBook.Management;

namespace FontBook.Workbench;

public sealed record FamilyRow(uint Id, string Name, string Key, uint Styles)
{
    public bool IsWindowsSystemFont { get; init; }
    public string CompactDetail => IsWindowsSystemFont ? $"系统字体 · {Styles:N0}" : $"{Styles:N0}";
    public string Detail => IsWindowsSystemFont ? $"系统字体 · {Styles:N0} 个样式" : $"{Styles:N0} 个样式";
}
public sealed record StyleRow(uint Index, string Name, uint Weight)
{
    public override string ToString() => Name;
}
internal sealed record Specimen(BitmapSource? Image, SampleCoverage Coverage, string? Error);

internal sealed partial class FontCatalog : IDisposable
{
    private readonly CatalogHandle _handle;
    public FamilyRow[] Rows
    {
        get;
    }
    public Native.Stats Stats
    {
        get;
    }
    public long ElapsedMs
    {
        get;
    }
    private FontCatalog(CatalogHandle handle, FamilyRow[] rows, Native.Stats stats, long ms) => (_handle, Rows, Stats, ElapsedMs) = (handle, rows, stats, ms);
    public void Dispose() => _handle.Dispose();

    public static FontCatalog Open(CancellationToken ct, string[]? paths = null)
    {
        var clock = Stopwatch.StartNew();
        if (Native.fb_version() != 3)
            throw new InvalidOperationException("原生引擎版本与界面不匹配，请使用同一构建目录。");
        Native.Cancel callback = _ => ct.IsCancellationRequested ? 1 : 0;
        var locale = CultureInfo.CurrentUICulture.Name;
        int hr = paths is null ? Native.fb_open(locale, callback, IntPtr.Zero, out var handle) : Native.fb_open_files(locale, paths, (uint)paths.Length, callback, IntPtr.Zero, out handle);
        try
        {
            ct.ThrowIfCancellationRequested();
            Native.Check(hr, "读取 Windows 字体目录");
            Native.Check(Native.fb_statistics(handle, out var stats), "读取目录统计");
            var rows = new FamilyRow[stats.Families];
            for (uint i = 0; i < rows.Length; i++)
            {
                if (i % 64 == 0)
                    ct.ThrowIfCancellationRequested();
                Native.Check(Native.fb_family(handle, i, out var f), "读取家族");
                var name = Native.Copy(f.Name);
                var key = Native.Copy(f.Key);
                rows[i] = new FamilyRow(i, name, key, f.Styles)
                {
                    IsWindowsSystemFont = Windows11FontList.MatchesFamily(key) || Windows11FontList.MatchesFamily(name) ||
                        HasWindowsFontFile(handle, i, ct)
                };
            }
            return new FontCatalog(handle, rows, stats, clock.ElapsedMilliseconds);
        }
        catch { handle.Dispose(); throw; }
    }
    public FamilyRow[] Search(string query, IReadOnlyList<string>? members, CancellationToken ct)
    {
        if (query.Length == 0 && members is null)
            return Rows;
        HashSet<string>? filter = members?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new uint[Rows.Length];
        Native.Check(Native.fb_query(_handle, query, ids, (uint)ids.Length, out uint count), "筛选字体（上限 64 个搜索词）");
        var result = new List<FamilyRow>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (i % 128 == 0)
                ct.ThrowIfCancellationRequested();
            var f = Rows[ids[i]];
            if (filter is null || filter.Contains(f.Key))
                result.Add(f);
        }
        return result.ToArray();
    }
    public StyleRow[] GetStyles(uint id)
    {
        int hr = Native.fb_list_styles(_handle, id, out var list);
        using (list)
        {
            Native.Check(hr, "读取本地样式");
            var result = new StyleRow[Native.fb_styles_count(list)];
            for (uint i = 0; i < result.Length; i++)
            {
                Native.Check(Native.fb_style(list, i, out var row), "读取样式");
                result[i] = new StyleRow(row.Index, Native.Copy(row.Name), row.Weight);
            }
            return result;
        }
    }
    public FaceSelection Select(uint family, uint style)
    {
        int hr = Native.fb_select(_handle, family, style, out var handle);
        try
        {
            Native.Check(hr, "打开所选字面");
            return new FaceSelection(handle);
        }
        catch { handle.Dispose(); throw; }
    }
}
internal sealed partial class FaceSelection : IDisposable
{
    private readonly FaceHandle _handle;
    public string Path
    {
        get;
    }
    public string Postscript
    {
        get;
    }
    public uint FaceIndex
    {
        get;
    }
    public uint Glyphs
    {
        get;
    }
    public Native.Axis[] Axes
    {
        get;
    }
    public FaceSelection(FaceHandle handle)
    {
        _handle = handle;
        Native.Check(Native.fb_describe(handle, out var d), "读取字体信息");
        Path = Native.Copy(d.Path);
        Postscript = Native.Copy(d.Postscript);
        FaceIndex = d.Index;
        Glyphs = d.Glyphs;
        Axes = new Native.Axis[d.Axes];
        for (uint i = 0; i < Axes.Length; i++)
            Native.Check(Native.fb_axis_at(handle, i, out Axes[i]), "读取可变轴");
    }
    public SampleCoverage AnalyzeSample(string text)
    {
        if (text.Length > 2048)
            throw new ArgumentException("样本最多 2048 个 UTF-16 单元。");
        return SampleCoverage.Analyze(text, chars =>
        {
            var glyphs = new ushort[chars.Length];
            Native.Check(Native.fb_map(_handle, chars, (uint)chars.Length, glyphs), "查询字符映射");
            return glyphs;
        });
    }
    public Specimen Render(string text, float points, float scale, int w, int h, float[] axes, CancellationToken ct, Color? ink = null)
    {
        ct.ThrowIfCancellationRequested();
        var coverage = AnalyzeSample(text);
        ct.ThrowIfCancellationRequested();
        var pixels = new byte[checked(w * h * 4)];
        int hr = Native.fb_render(_handle, text, points * (96f / 72f), scale, (uint)w, (uint)h, axes, (uint)axes.Length, pixels, (uint)pixels.Length);
        ct.ThrowIfCancellationRequested();
        if (hr < 0)
            return new Specimen(null, coverage, $"此字面的标本无法绘制（0x{hr:X8}）。");
        return new Specimen(FreezePixels(w, h, scale, pixels, ink), coverage, null);
    }
    public void Dispose() => _handle.Dispose();
}
