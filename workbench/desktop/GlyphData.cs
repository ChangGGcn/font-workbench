using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace FontBook.Workbench;

internal sealed class GlyphBookHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.fb_glyph_book_close(handle);
        return true;
    }
}
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GlyphInfo
    {
        internal IntPtr Name; internal uint First, Count;
    }
    [DllImport(Dll)] internal static extern int fb_glyph_book_open(FaceHandle face, Cancel stop, IntPtr ctx, out GlyphBookHandle book);
    [DllImport(Dll)] internal static extern void fb_glyph_book_close(IntPtr book);
    [DllImport(Dll)] internal static extern int fb_glyph_name_status(GlyphBookHandle book);
    [DllImport(Dll)] internal static extern int fb_glyph_info_at(GlyphBookHandle book, uint gid, out GlyphInfo info);
    [DllImport(Dll)] internal static extern int fb_glyph_scalar_at(GlyphBookHandle book, uint gid, uint ordinal, out uint scalar);
    [DllImport(Dll)] internal static extern int fb_render_glyph(FaceHandle face, uint gid, uint w, uint h, [In] float[] axes, uint axisCount, [Out] byte[] pixels, uint bytes);
}
internal sealed record GlyphEntry(uint Gid, string Name, uint First, uint MappingCount, BitmapSource? Image)
{
    public string Label => $"GID {Gid}";
    public string Code => MappingCount == 0 ? "—" : $"U+{First:X4}" + (MappingCount > 1 ? $" +{MappingCount - 1}" : "");
    public string Description => $"{Label} · {Code}" + (Name.Length != 0 ? $" · {Name}" : "");
    public string AutomationLabel => Image is null ? Description + " · 图像不可用" : Description;
}
internal sealed record GlyphPage(GlyphEntry[] Items, int Unavailable);
internal sealed record MappedScalar(uint Value)
{
    public string Label => $"U+{Value:X4}";
    public string Character => char.ConvertFromUtf32(checked((int)Value));
    public bool CanCopy => System.Text.Rune.GetUnicodeCategory(new System.Text.Rune((int)Value)) is not
        (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format);
}
internal sealed class GlyphBook(GlyphBookHandle handle, int nameStatus) : IDisposable
{
    public const int PageSize = 48;
    public int NameStatus { get; } = nameStatus;
    internal const int ScalarPageSize = 64;

    public MappedScalar[] ReadScalars(GlyphEntry row, uint start, CancellationToken token)
    {
        if (start > row.MappingCount || row.MappingCount > 0x110000)
            throw new InvalidOperationException("字符映射范围无效。");
        var count = Math.Min((uint)ScalarPageSize, row.MappingCount - start);
        var values = new MappedScalar[count];
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            values[i] = new(ScalarAt(row.Gid, start + i));
        }
        return values;
    }

    public uint PageForScalar(GlyphEntry row, uint? target)
    {
        if (!target.HasValue)
            return 0;
        uint low = 0, high = row.MappingCount;
        while (low < high)
        {
            uint mid = low + (high - low) / 2;
            if (ScalarAt(row.Gid, mid) < target.Value)
                low = mid + 1;
            else
                high = mid;
        }
        return low < row.MappingCount ? low / ScalarPageSize * ScalarPageSize : 0;
    }

    private uint ScalarAt(uint gid, uint ordinal)
    {
        Native.Check(Native.fb_glyph_scalar_at(handle, gid, ordinal, out uint scalar), "读取 Unicode 映射");
        return scalar;
    }
    public GlyphPage Page(FaceSelection face, uint first, float scale, float[] axes, Color? ink, CancellationToken token)
    {
        if (first >= face.Glyphs)
            throw new ArgumentOutOfRangeException(nameof(first));
        var items = new GlyphEntry[Math.Min((uint)PageSize, face.Glyphs - first)];
        int unavailable = 0;
        for (uint i = 0; i < items.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            uint gid = first + i;
            var info = ReadInfo(gid);
            BitmapSource? image = null;
            try
            {
                image = face.RenderGlyph(gid, 32, scale, axes, ink);
            }
            catch (InvalidOperationException) { unavailable++; } // one unrenderable outline doesn't conceal the other GIDs
            items[i] = new(gid, info.Name, info.First, info.Count, image);
        }
        return new(items, unavailable);
    }
    private (string Name, uint First, uint Count) ReadInfo(uint gid)
    {
        // A face change can dispose the book on the dispatcher. Keep its borrowed name valid
        // after P/Invoke returns, until the managed string copy is complete.
        bool held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            Native.Check(Native.fb_glyph_info_at(handle, gid, out var info), "读取字形元数据");
            return (Native.Copy(info.Name), info.First, info.Count);
        }
        finally
        {
            if (held)
                handle.DangerousRelease();
        }
    }
    public void Dispose() => handle.Dispose();
}
internal sealed partial class FaceSelection
{
    public GlyphBook OpenBook(CancellationToken ct)
    {
        Native.Cancel stop = _ => ct.IsCancellationRequested ? 1 : 0;
        int hr = Native.fb_glyph_book_open(_handle, stop, IntPtr.Zero, out var book);
        try
        {
            ct.ThrowIfCancellationRequested();
            Native.Check(hr, "打开字形索引");
            int status = Native.fb_glyph_name_status(book);
            Native.Check(status, "读取名称表状态");
            return new(book, status);
        }
        catch { book.Dispose(); throw; }
    }
    public ushort MapOne(uint scalar)
    {
        ushort[] result = new ushort[1];
        Native.Check(Native.fb_map(_handle, [scalar], 1, result), "查找码点");
        return result[0];
    }
    public BitmapSource RenderGlyph(uint gid, int sideDip, float scale, float[] axes, Color? ink)
    {
        int side = Math.Clamp((int)Math.Ceiling(sideDip * scale), 8, 512);
        var pixels = new byte[checked(side * side * 4)];
        Native.Check(Native.fb_render_glyph(_handle, gid, (uint)side, (uint)side, axes, (uint)axes.Length, pixels, (uint)pixels.Length), "绘制所选 GID");
        return FreezePixels(side, side, scale, pixels, ink);
    }
    internal static BitmapSource FreezePixels(int w, int h, float scale, byte[] pixels, Color? ink)
    {
        if (ink is Color color)
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                pixels[i] = (byte)(color.B * a / 255);
                pixels[i + 1] = (byte)(color.G * a / 255);
                pixels[i + 2] = (byte)(color.R * a / 255);
            }
        var image = BitmapSource.Create(w, h, 96d * scale, 96d * scale, PixelFormats.Pbgra32, null, pixels, w * 4);
        image.Freeze();
        return image;
    }
}
