using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FontBook.Services;
using Microsoft.Win32.SafeHandles;

namespace FontBook.Management;

internal static class FontFileEvidence
{
    // Pin the exact local file while parsing it and hashing it. A hard link or
    // alternate final path makes the catalogue's path-based ownership ambiguous.
    internal static string Read(string path, IFontOperationBackend backend, HashSet<uint>? expectedFaces = null)
    {
        return Inspect(path, backend, expectedFaces, hash: true);
    }

    internal static void CheckFaces(string path, IFontOperationBackend backend, HashSet<uint> expectedFaces) =>
        Inspect(path, backend, expectedFaces, hash: false);

    private static string Inspect(string path, IFontOperationBackend backend, HashSet<uint>? expectedFaces, bool hash)
    {
        if (!backend.FileExists(path) || backend.HasReparsePoint(path))
            throw new IOException($"字体文件不存在或路径含重解析点：{path}");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var handle = stream.SafeFileHandle;
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (((FileAttributes)info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1)
            throw new IOException($"不能确认目标是唯一的普通文件（例如存在硬链接）：{path}");
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity || !buffer.ToString().Equals(@"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"文件句柄的最终路径与目录路径不一致：{path}");
        if (expectedFaces is not null)
        {
            var faces = OpenTypeReader.ReadFaces(path);
            if (!expectedFaces.SetEquals(faces.Select(f => (uint)f.Index)))
                throw new InvalidDataException($"目录未证明此文件中的全部物理字面都属于所选家族：{path}");
        }
        return hash ? Convert.ToHexString(SHA256.HashData(stream)) : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation info);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
