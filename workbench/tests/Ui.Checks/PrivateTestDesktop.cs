using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FontBook.Workbench.Ui.Checks;

internal static class PrivateTestDesktop
{
    // Allocate the UI thread on an unshown desktop so actual keyboard/mouse/focus changes
    // cannot interfere with the test's thread-local synthetic modifier state. Never SwitchDesktop.
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktopW(string name, nint device, nint devmode, uint flags, uint access, nint attributes);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup
    {
        public uint Size; public string? Reserved, Desktop, Title; public uint X, Y, Width, Height, Cols, Rows, Attr, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process, Thread; public uint Id, ThreadId;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string app, StringBuilder args, nint processAttrs, nint threadAttrs,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref Startup start, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint process, out int exit);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint exit);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    internal static int Run(string output)
    {
        string name = "FontWorkbench-UiChecks-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktopW(name, 0, 0, 0, 0x10000000 /* GENERIC_ALL on this private desktop */, 0);
        if (desktop == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建私有测试桌面失败。");
        try
        {
            string binary = Path.Combine(AppContext.BaseDirectory, "Ui.Checks.exe");
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
            if (folder.Contains('"'))
                throw new ArgumentException("Invalid output directory.");
            var start = new Startup { Size = (uint)Marshal.SizeOf<Startup>(), Desktop = name, Flags = 0x100, Input = GetStdHandle(-10), Output = GetStdHandle(-11), Error = GetStdHandle(-12) };
            var args = new StringBuilder($"\"{binary}\" --private-ui-worker \"{folder}\"");
            if (!CreateProcessW(binary, args, 0, 0, true, 0x08000000, 0, AppContext.BaseDirectory, ref start, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.Thread);
            try
            {
                if (WaitForSingleObject(info.Process, 360000) != 0)
                {
                    TerminateProcess(info.Process, 1);
                    WaitForSingleObject(info.Process, 10000);
                    throw new TimeoutException("私有 UI 回归进程超时。 ");
                }
                return GetExitCodeProcess(info.Process, out int exit) ? exit : 1;
            }
            finally { CloseHandle(info.Process); }
        }
        finally { CloseDesktop(desktop); }
    }
}
