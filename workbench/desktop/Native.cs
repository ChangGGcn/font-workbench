using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
namespace FontBook.Workbench;

internal sealed class CatalogHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.fb_close(handle);
        return true;
    }
}
internal sealed class StylesHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.fb_styles_close(handle);
        return true;
    }
}
internal sealed class FaceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.fb_selection_close(handle);
        return true;
    }
}

internal static partial class Native
{
    private const string Dll = "FontBookCore.dll";
    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public uint Families, Styles, Skipped;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Family
    {
        public IntPtr Name, Key; public uint Styles;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Style
    {
        public IntPtr Name; public uint Index, Weight;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Detail
    {
        public IntPtr Path, Postscript; public uint Index, Glyphs, Axes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Axis(uint Tag, float Minimum, float Initial, float Maximum)
    {
        public string Label => new(new[] { (char)(Tag & 255), (char)((Tag >> 8) & 255), (char)((Tag >> 16) & 255), (char)(Tag >> 24) });
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Cancel(IntPtr context);
    [DllImport(Dll)] internal static extern uint fb_version();
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int fb_open(string locale, Cancel stop, IntPtr context, out CatalogHandle result);
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int fb_open_files(string locale, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] paths, uint count, Cancel stop, IntPtr context, out CatalogHandle result);
    [DllImport(Dll)] internal static extern void fb_close(IntPtr handle);
    [DllImport(Dll)] internal static extern int fb_statistics(CatalogHandle handle, out Stats result);
    [DllImport(Dll)] internal static extern int fb_family(CatalogHandle handle, uint id, out Family result);
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int fb_query(CatalogHandle handle, string query, [Out] uint[] ids, uint count, out uint actual);
    [DllImport(Dll)] internal static extern int fb_list_styles(CatalogHandle handle, uint id, out StylesHandle styles);
    [DllImport(Dll)] internal static extern void fb_styles_close(IntPtr handle);
    [DllImport(Dll)] internal static extern uint fb_styles_count(StylesHandle handle);
    [DllImport(Dll)] internal static extern int fb_style(StylesHandle handle, uint id, out Style result);
    [DllImport(Dll)] internal static extern int fb_select(CatalogHandle handle, uint family, uint style, out FaceHandle selection);
    [DllImport(Dll)] internal static extern void fb_selection_close(IntPtr handle);
    [DllImport(Dll)] internal static extern int fb_describe(FaceHandle handle, out Detail result);
    [DllImport(Dll)] internal static extern int fb_axis_at(FaceHandle handle, uint index, out Axis result);
    [DllImport(Dll)] internal static extern int fb_map(FaceHandle handle, [In] uint[] scalars, uint count, [Out] ushort[] glyphs);
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int fb_render(FaceHandle handle, string text, float size, float scale, uint width, uint height, [In] float[] axes, uint count, [Out] byte[] pixels, uint bytes);

    internal static void Check(int hr, string action)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{action}失败（0x{hr:X8}）");
    }
    internal static string Copy(IntPtr p) => Marshal.PtrToStringUni(p) ?? string.Empty;
}
