using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FontBook.Workbench;

internal enum FontOutline : uint
{
    Unknown, TrueType, Cff, Cff2, Type1, Vector, Bitmap, RawCff
}

internal enum FontLayoutStatus : uint
{
    Absent, Read, Unavailable
}

[Flags]
internal enum FontFeatureTables : uint
{
    Gsub = 1,
    Gpos = 2
}

internal readonly record struct FontFeature(string Tag, FontFeatureTables Tables);
internal sealed record FontDetailsNames(string? Family, string? FullName, string? Postscript, string? Style,
    string? Version, string? Manufacturer, string? Designer, string? Copyright, string? License, string? LicenseUrl);
internal readonly record struct FontDesignMetrics(uint GlyphCount, uint UnitsPerEm, uint Ascent, uint Descent);

internal sealed record FontDetails(string? Path, uint FaceIndex, FontDetailsNames Names, FontDesignMetrics Metrics,
    uint Weight, uint Stretch, uint Style, uint Simulations, uint FaceType, FontOutline Outline,
    IReadOnlyList<Native.Axis> Axes, IReadOnlyList<FontFeature> Features,
    FontLayoutStatus GsubStatus, FontLayoutStatus GposStatus)
{
    /// <summary>Exact four-character tags from the selected face's readable GSUB/GPOS FeatureLists, ordinal sorted and deduplicated.</summary>
    internal IReadOnlyList<string> FeatureTags { get; } = Array.AsReadOnly(Features.Select(feature => feature.Tag).ToArray());

    internal static FontDetails Read(IntPtr selection)
    {
        int hr = Native.fb_font_details_open(selection, out var details);
        using (details)
        {
            Native.Check(hr, "读取所选字面详情");
            Native.Check(Native.fb_font_details_describe(details, out var info), "读取详情快照");
            var axes = new Native.Axis[info.AxisCount];
            for (uint i = 0; i < axes.Length; i++)
                Native.Check(Native.fb_font_details_axis_at(details, i, out axes[i]), "读取可变轴范围");
            var features = new FontFeature[info.FeatureCount];
            for (uint i = 0; i < features.Length; i++)
            {
                Native.Check(Native.fb_font_details_feature_at(details, i, out var entry), "读取 OpenType 功能标签");
                var tag = string.Create(4, entry.Tag, static (chars, value) =>
                {
                    for (int j = 0; j < chars.Length; j++)
                        chars[j] = (char)((value >> (j * 8)) & 0xff);
                });
                features[i] = new FontFeature(tag, entry.Tables);
            }
            return new FontDetails(Marshal.PtrToStringUni(info.Path), info.FaceIndex,
                new(Marshal.PtrToStringUni(info.Family), Marshal.PtrToStringUni(info.FullName),
                    Marshal.PtrToStringUni(info.Postscript), Marshal.PtrToStringUni(info.StyleName),
                    Marshal.PtrToStringUni(info.Version), Marshal.PtrToStringUni(info.Manufacturer),
                    Marshal.PtrToStringUni(info.Designer), Marshal.PtrToStringUni(info.Copyright),
                    Marshal.PtrToStringUni(info.License), Marshal.PtrToStringUni(info.LicenseUrl)),
                new(info.GlyphCount, info.UnitsPerEm, info.Ascent, info.Descent),
                info.Weight, info.Stretch, info.Style, info.Simulations, info.FaceType, info.Outline,
                Array.AsReadOnly(axes), Array.AsReadOnly(features), info.GsubStatus, info.GposStatus);
        }
    }
}

internal sealed partial class FaceSelection
{
    /// <summary>Reads an independent snapshot. Call on a worker with a live selection.</summary>
    internal FontDetails ReadDetails()
    {
        bool held = false;
        try
        {
            _handle.DangerousAddRef(ref held);
            return FontDetails.Read(_handle.DangerousGetHandle());
        }
        finally
        {
            if (held)
                _handle.DangerousRelease();
        }
    }

    /// <summary>Holds the current selection before scheduling work, even if the caller switches fonts immediately.</summary>
    internal async Task<FontDetails> ReadDetailsAsync()
    {
        using var activity = FontReadActivity.Begin();
        bool held = false;
        try
        {
            _handle.DangerousAddRef(ref held);
            var selection = _handle.DangerousGetHandle();
            return await Task.Run(() => FontDetails.Read(selection)).ConfigureAwait(false);
        }
        finally
        {
            if (held)
                _handle.DangerousRelease();
        }
    }
}

internal sealed class FontDetailsHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.fb_font_details_close(handle);
        return true;
    }
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FontDetailsInfo
    {
        public IntPtr Path, Family, FullName, Postscript, StyleName, Version, Manufacturer, Designer,
            Copyright, License, LicenseUrl;
        public uint FaceIndex, GlyphCount, UnitsPerEm, Ascent, Descent, Weight, Stretch, Style, Simulations, FaceType;
        public FontOutline Outline;
        public uint AxisCount, FeatureCount;
        public FontLayoutStatus GsubStatus, GposStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FontFeatureEntry
    {
        public uint Tag;
        public FontFeatureTables Tables;
    }

    // Raw selection is only passed while FaceSelection holds a DangerousAddRef.
    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int fb_font_details_open(IntPtr selection, out FontDetailsHandle details);
    [DllImport(Dll, ExactSpelling = true)]
    internal static extern void fb_font_details_close(IntPtr details);
    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int fb_font_details_describe(FontDetailsHandle details, out FontDetailsInfo info);
    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int fb_font_details_axis_at(FontDetailsHandle details, uint index, out Axis axis);
    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int fb_font_details_feature_at(FontDetailsHandle details, uint index, out FontFeatureEntry feature);
}
