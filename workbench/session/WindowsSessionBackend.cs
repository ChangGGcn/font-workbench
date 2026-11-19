using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace FontBook.Workbench.Session;

[SupportedOSPlatform("windows")]
public sealed class WindowsSessionBackend : ISessionFontBackend
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc", ".otc" };
    public IDisposable Pin(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('|') || path.IndexOf(':', 2) >= 0 ||
            !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) || !Extensions.Contains(Path.GetExtension(path)) ||
            path.Split('\\').Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new ArgumentException("只允许明确选择的本地标准字体路径。");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        if (string.IsNullOrEmpty(windows) || Under(path, windows) || Under(path, userFonts) || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new InvalidOperationException("不会为 Windows 目录、已安装字体目录或非本地磁盘创建试用引用。");
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new InvalidOperationException("试用不接受重解析路径或脱机文件。");
        var locks = new PathPins();
        try
        {
            // GDI accepts a path, not a handle. Also hold the parent chain against renames.
            var parents = new Stack<string>();
            for (var parent = new System.IO.FileInfo(path).Directory; parent?.Parent is not null; parent = parent.Parent)
            {
                if (parents.Count == 64)
                    throw new IOException("目录层级超过试用上限。");
                parents.Push(parent.FullName);
            }
            foreach (var parent in parents)
            {
                var dir = CreateFileW(parent, 0x80 /* READ_ATTRIBUTES */, 3 /* share read/write, not delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
                locks.Keep(dir);
                if (dir.IsInvalid || !GetFileInformationByHandle(dir, out var data) || (data.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw new IOException("无法固定字体文件的上级目录。");
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            locks.Keep(stream);
            // Keep this handle pinned without share-write/delete until the matching Remove call succeeds.
            var final = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(stream.SafeFileHandle, final, (uint)final.Capacity, 0);
            if (length == 0 || length >= final.Capacity || !GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                throw new IOException("无法确认当前字体文件的真实位置。", new Win32Exception());
            if (!string.Equals(final.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase) || info.Links != 1 || stream.Length is < 12 or > (128L << 20))
                throw new InvalidOperationException("试用只接受无别名、单硬链接、大小不超过 128 MiB 的本地字体。");
            Span<byte> head = stackalloc byte[4];
            stream.ReadExactly(head);
            if (!(head.SequenceEqual("OTTO"u8) || head.SequenceEqual("ttcf"u8) || (head[0] == 0 && head[1] == 1 && head[2] == 0 && head[3] == 0)))
                throw new InvalidDataException("文件不是支持的 OpenType/TrueType 字体。");
            return locks;
        }
        catch { locks.Dispose(); throw; }
    }
    private sealed class PathPins : IDisposable
    {
        private readonly List<IDisposable> _handles = new(65);
        public void Keep(IDisposable handle) => _handles.Add(handle);
        public void Dispose()
        {
            for (int i = _handles.Count - 1; i >= 0; i--)
                _handles[i].Dispose();
            _handles.Clear();
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    private static bool Under(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public int Add(string path) => AddFontResourceExW(path, 0, IntPtr.Zero);
    public bool Remove(string path) => RemoveFontResourceExW(path, 0, IntPtr.Zero);
    public void Notify() => SendNotifyMessageW(new IntPtr(0xffff), 0x001D /* WM_FONTCHANGE */, UIntPtr.Zero, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Attributes; public FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder result, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int AddFontResourceExW(string file, uint flags, IntPtr reserved);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveFontResourceExW(string file, uint flags, IntPtr reserved);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SendNotifyMessageW(IntPtr hwnd, uint message, UIntPtr w, IntPtr l);
}
