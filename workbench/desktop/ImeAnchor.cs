using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace FontBook.Workbench;

// WPF/TSF remains the text owner. For IMEs that use the IMM compatibility window,
// place the candidate list at the actual DirectWrite caret rather than the transparent TextBox glyphs.
internal static class ImeAnchor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateForm
    {
        public uint Index, Style; public int X, Y, Left, Top, Right, Bottom;
    }
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("imm32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmGetOpenStatus(nint context);
    [DllImport("imm32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmSetCandidateWindow(nint context, ref CandidateForm form);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(nint window, ref WinPoint point);
    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X, Y;
    }
    internal static void Follow(TextBox host, Point caret)
    {
        if (!host.IsKeyboardFocused || PresentationSource.FromVisual(host) is not HwndSource source)
            return;
        nint ctx = ImmGetContext(source.Handle);
        if (ctx == 0)
            return;
        try
        {
            if (!ImmGetOpenStatus(ctx))
                return;
            var screen = host.PointToScreen(caret);
            var p = new WinPoint { X = (int)screen.X, Y = (int)screen.Y };
            if (ScreenToClient(source.Handle, ref p))
            {
                var form = new CandidateForm { Style = 0x40 /* CFS_CANDIDATEPOS */, X = p.X, Y = p.Y };
                ImmSetCandidateWindow(ctx, ref form);
            }
        }
        finally { ImmReleaseContext(source.Handle, ctx); }
    }
}
