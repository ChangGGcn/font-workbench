using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace FontBook.Workbench;

internal sealed class LayoutHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.fb_layout_close(handle);
        return true;
    }
}
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Feature(uint Tag, uint Value);
    [StructLayout(LayoutKind.Sequential)]
    internal struct TextRect
    {
        public float X, Y, Width, Height;
        public readonly Rect AsRect() => new(X, Y, Math.Max(0, Width), Math.Max(0, Height));
    }
    [DllImport(Dll, ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int fb_layout_create(FaceHandle face, string text, float sizeDip, float scale,
        uint width, uint height, [In] float[] axes, uint axisCount, [In] Feature[] features, uint featureCount, out LayoutHandle layout);
    [DllImport(Dll)] internal static extern void fb_layout_close(IntPtr layout);
    [DllImport(Dll)] internal static extern int fb_layout_draw_at(LayoutHandle layout, float scrollY, [Out] byte[] pixels, uint bytes);
    [DllImport(Dll)] internal static extern int fb_layout_extent(LayoutHandle layout, out float height);
    [DllImport(Dll)] internal static extern int fb_layout_hit(LayoutHandle layout, float x, float y, out uint position);
    [DllImport(Dll)] internal static extern int fb_layout_caret(LayoutHandle layout, uint position, out TextRect rectangle);
    [DllImport(Dll)]
    internal static extern int fb_layout_ranges(LayoutHandle layout, uint start, uint length,
        [Out] TextRect[]? rectangles, uint capacity, out uint count);
}

/// Shaping, glyph ink and all caret/selection coordinates come from this exact same layout.
internal sealed class SpecimenLayout : IDisposable
{
    private readonly LayoutHandle _handle;
    private readonly int _width, _height;
    private readonly float _scale;
    public float Extent
    {
        get;
    }
    public float ViewHeight => _height / _scale;
    public float MaxScroll => Math.Max(0, Extent - ViewHeight);
    internal SpecimenLayout(LayoutHandle handle, int width, int height, float scale)
    {
        _handle = handle;
        _width = width;
        _height = height;
        _scale = scale;
        Native.Check(Native.fb_layout_extent(handle, out float extent), "读取标本排版高度");
        Extent = extent;
    }
    public int Hit(Point at, float scroll)
    {
        Native.Check(Native.fb_layout_hit(_handle, (float)at.X, (float)at.Y + scroll, out uint offset), "定位光标");
        return checked((int)offset);
    }
    public Rect Caret(int offset)
    {
        Native.Check(Native.fb_layout_caret(_handle, checked((uint)offset), out var rect), "读取插入点");
        return rect.AsRect();
    }
    public Rect[] Selection(int start, int length)
    {
        if (length == 0)
            return [];
        var buffer = new Native.TextRect[16];
        int hr = Native.fb_layout_ranges(_handle, (uint)start, (uint)length, buffer, (uint)buffer.Length, out uint n);
        if (hr == unchecked((int)0x8007007A) && n > buffer.Length && n <= 8192)
        {
            buffer = new Native.TextRect[n];
            hr = Native.fb_layout_ranges(_handle, (uint)start, (uint)length, buffer, n, out n);
        }
        Native.Check(hr, "读取字形选择区");
        if (n > buffer.Length)
            throw new InvalidDataException("排版区域数量无效。");
        return buffer.Take((int)n).Select(b => b.AsRect()).ToArray();
    }
    internal float ScrollFor(int offset, float prior, bool ensureVisible)
    {
        float scroll = Math.Clamp(prior, 0, MaxScroll);
        if (!ensureVisible)
            return scroll;
        Rect caret = Caret(offset);
        if (caret.Y < scroll)
            scroll = (float)caret.Y;
        else if (caret.Bottom > scroll + ViewHeight)
            scroll = (float)caret.Bottom - ViewHeight;
        return Math.Clamp(scroll, 0, MaxScroll);
    }
    internal BitmapSource Draw(float scroll, Color ink)
    {
        byte[] pixels = new byte[checked(_width * _height * 4)];
        Native.Check(Native.fb_layout_draw_at(_handle, scroll, pixels, (uint)pixels.Length), "绘制标本");
        return FaceSelection.FreezePixels(_width, _height, _scale, pixels, ink);
    }
    public void Dispose() => _handle.Dispose();
}
internal sealed record EditorFrame(SpecimenLayout? Layout, BitmapSource? Image, SampleCoverage Coverage, float Scroll, string? Error) : IDisposable
{
    public void Dispose() => Layout?.Dispose();
}
internal sealed partial class FaceSelection
{
    internal EditorFrame BuildEditorFrame(string text, float points, float scale, int w, int h, float[] axes,
        Native.Feature[] features, int caret, float oldScroll, bool ensureVisible, Color ink, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var coverage = AnalyzeSample(text);
        // NUL is not representable in a nul-terminated native string. Keep editor UTF-16 offsets intact.
        string layoutText = text.Contains('\0') ? text.Replace('\0', '\u200b') : text;
        int hr = Native.fb_layout_create(_handle, layoutText, points * (96f / 72), scale, (uint)w, (uint)h, axes, (uint)axes.Length,
            features, (uint)features.Length, out var handle);
        if (hr < 0)
        {
            handle.Dispose();
            return new(null, null, coverage, 0, $"此字面无法排版（0x{hr:X8}）。");
        }
        try
        {
            var layout = new SpecimenLayout(handle, w, h, scale);
            token.ThrowIfCancellationRequested();
            float scroll = layout.ScrollFor(caret, oldScroll, ensureVisible);
            // A blank buffer isn't a font specimen, but an empty document still needs a real caret.
            var image = string.IsNullOrWhiteSpace(text) ? null : layout.Draw(scroll, ink);
            return new(layout, image, coverage, scroll, null);
        }
        catch { handle.Dispose(); throw; }
    }
}
