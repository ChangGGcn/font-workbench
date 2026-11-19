using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using FontBook.Models;
using Microsoft.Win32;

namespace FontBook.Services;

public sealed record OperationResult(bool Ok, string Message, string? OperationId = null);

internal sealed record InspectionResult(bool Ok, string Message, List<string>? Paths = null);

/// <summary>Injectable I/O boundary for isolated tests; production uses exact physical file/registry operations.</summary>
public interface IFontOperationBackend
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    bool HasReparsePoint(string path);
    /// <summary>Checks delete access without modifying the file; uncertain backends fail closed.</summary>
    bool CanDeleteFile(string path, out string reason) { reason = "删除权限 backend 未实现。"; return false; }
    /// <summary>Checks whether a Windows-directory font is a safe direct child of Windows\\Fonts.</summary>
    bool IsSafeWindowsFontFile(string path, out string reason) { reason = "Windows 字体安全判定 backend 未实现。"; return false; }
    void CreateDirectory(string path);
    string Sha256(string path);
    void CopyFileExclusive(string source, string destination);
    void RestoreFileNoOverwrite(string backup, string destination, string expectedHash);
    bool DeleteFile(string path, string expectedSha256);
    IReadOnlyList<RegistryEntry> FindRegistryEntries(IReadOnlyCollection<string> exactFontPaths);
    bool TryReadRegistry(RegistryEntry entry, out string? data, out string? valueKind);
    bool RemoveRegistryEntry(RegistryEntry entry);
    bool RestoreRegistryEntry(RegistryEntry entry);
}

/// <summary>Filesystem + Windows registry backend. Never changes ACLs and never schedules restart deletion.</summary>
public sealed class WindowsFontOperationBackend : IFontOperationBackend
{
    private const string FontsRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    private readonly RegistryKey _machineRoot, _userRoot;
    public WindowsFontOperationBackend() : this(Registry.LocalMachine, Registry.CurrentUser) { }

    // The private-hive integration check supplies two handle-scoped roots. Never override
    // process-wide predefined keys or redirect unrelated Windows API calls during a test.
    internal WindowsFontOperationBackend(RegistryKey machineRoot, RegistryKey userRoot)
    {
        _machineRoot = machineRoot ?? throw new ArgumentNullException(nameof(machineRoot));
        _userRoot = userRoot ?? throw new ArgumentNullException(nameof(userRoot));
    }

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool CanDeleteFile(string path, out string reason)
    {
        reason = "";
        try
        {
            using var handle = CreateFileW(path, GenericRead | DeleteAccess, FileShareRead,
                IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
                throw FileAccessError("打开删除句柄", Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
                (info.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.ReadOnly)) != 0)
                throw new IOException("删除目标不是可写普通文件或无法确认其属性。");
            var normalized = Path.GetFullPath(path);
            if (!GetFinalPath(handle).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件句柄的最终路径与已校验路径不一致。");
            return true;
        }
        catch (Exception ex) { reason = "无法确认当前删除权限：" + ex.Message; return false; }
    }

    public bool IsSafeWindowsFontFile(string path, out string reason)
    {
        reason = "";
        try
        {
            var windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var fontsRoot = Path.GetFullPath(Path.Combine(windowsRoot, "Fonts"));
            if (!FontOperationSafety.IsPathWithin(path, windowsRoot)) return true;
            if (!Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(fontsRoot, StringComparison.OrdinalIgnoreCase))
            { reason = "Windows 目录中仅对 Windows\\Fonts 的直接子文件进行候选判定；其它位置一律保护。"; return false; }
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 || HasReparsePoint(path))
            { reason = "Windows\\Fonts 目标不是普通文件或路径含重解析点。"; return false; }

            // SFC errors, an unavailable API, or unreadable security metadata are fail-closed.
            if (SfcIsFileProtected(IntPtr.Zero, path))
            { reason = "该字体受 Windows Resource Protection 保护。"; return false; }

            var owner = new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null) { reason = "无法确定 Windows 字体文件属主。"; return false; }
            if (owner.Value.Equals(TrustedInstallerSid, StringComparison.OrdinalIgnoreCase))
            { reason = "该字体由 TrustedInstaller 所有。"; return false; }

            // WRP/owner are identity checks. Uninstallation tests the actual DELETE access separately; neither operation edits font contents.
            return true;
        }
        catch (Exception ex)
        {
            reason = "无法可靠判定 Windows\\Fonts 字体是否可安全操作：" + ex.Message;
            return false;
        }
    }

    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    [DllImport("sfc.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SfcIsFileProtected(IntPtr rpcHandle, string fileName);

    public bool HasReparsePoint(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                }
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            return false;
        }
        catch { return true; }
    }

    public string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void CopyFileExclusive(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    public void RestoreFileNoOverwrite(string backup, string destination, string expectedHash)
    {
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("目标文件没有父目录。");
        var temp = Path.Combine(parent, ".fontbook-restore-" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
        try
        {
            CopyFileExclusive(backup, temp);
            if (!FontOperationSafety.IsValidHash(Sha256(temp)) || !Sha256(temp).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("临时恢复文件哈希不匹配。");
            File.Move(temp, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public bool DeleteFile(string path, string expectedSha256)
    {
        // Open the same file object that is being checked; deny concurrent writes and renames,
        // then mark that handle for deletion rather than unlinking a re-resolved path.
        using var handle = CreateFileW(path, GenericRead | DeleteAccess, FileShareRead,
            IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw FileAccessError("打开已确认字体", Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
            (info.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.ReadOnly)) != 0)
            throw new IOException("待删除字体不是可写普通文件或无法确认其属性。");
        var finalPath = GetFinalPath(handle);
        var normalized = Path.GetFullPath(path);
        if (!finalPath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            throw new IOException("待删除文件句柄的最终路径与已校验路径不一致。");
        if (FontOperationSafety.IsPathWithin(normalized, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
        {
            var windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var fontsRoot = Path.GetFullPath(Path.Combine(windowsRoot, "Fonts"));
            if (!Path.GetDirectoryName(normalized)!.Equals(fontsRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("仅可删除 Windows\\Fonts 直接子目录内通过检查的字体。");
            if (SfcIsFileProtected(IntPtr.Zero, normalized)) throw new IOException("文件句柄指向受 Windows Resource Protection 保护的字体。");
            var owner = new FileInfo(normalized).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || owner.Value.Equals(TrustedInstallerSid, StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件句柄对应字体属主未知或为 TrustedInstaller。");
        }
        using (var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!FontOperationSafety.IsValidHash(expectedSha256) || !actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("删除前文件句柄的内容哈希不匹配。");
            var disposition = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(stream.SafeFileHandle, FileDispositionInfoClass, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>()))
                throw FileAccessError("提交文件删除", Marshal.GetLastWin32Error());
        }
        handle.Dispose(); // Complete delete-pending before checking the path; otherwise File.Exists can report a false failure.
        if (GetFileAttributesW(path) != uint.MaxValue) return false;
        int lastError = Marshal.GetLastWin32Error();
        if (lastError is 2 or 3) return true; // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
        throw FileAccessError("核对删除后的文件状态", lastError);
    }

    private static IOException FileAccessError(string stage, int code)
    {
        var native = new System.ComponentModel.Win32Exception(code);
        string reason = code switch {
            5 => "Windows 拒绝访问；请检查当前账户权限，并关闭仍映射此字体的应用。该错误码不能单独区分这两种情况。",
            32 or 33 or 1224 => "文件正被使用或映射。关闭使用该字体的应用、释放会话试用后重试；也可单独预检停用（不删文件）。",
            2 or 3 => "文件已不存在；刷新字体目录后重试。",
            _ => native.Message
        };
        return new IOException($"{stage}：{reason}（Win32 {code}）", native);
    }

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileDispositionInfoClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    private static extern uint GetFileAttributesW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, int fileInformationClass,
        out FileAttributeTagInfo fileInformation, uint bufferSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int fileInformationClass,
        ref FileDispositionInfo fileInformation, uint bufferSize);

    private static string GetFinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new System.Text.StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new IOException("无法确定待删除字体句柄的最终路径。");
            if (length < buffer.Capacity)
            {
                var path = buffer.ToString();
                if (!path.StartsWith("\\\\?\\", StringComparison.Ordinal)) throw new IOException("待删除字体不是可识别的本地 DOS 路径。");
                return path[4..];
            }
            capacity = checked((int)length + 1);
            if (capacity > 32768) throw new IOException("待删除字体最终路径长度超限。");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path,
        uint filePathSize, uint flags);

    public IReadOnlyList<RegistryEntry> FindRegistryEntries(IReadOnlyCollection<string> exactFontPaths)
    {
        var targets = exactFontPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<RegistryEntry>();
        foreach (var hive in new[] { "HKLM", "HKCU" })
        {
            try
            {
                using var key = OpenRegistry(hive, writable: false);
                if (key == null) continue; // A missing per-user Fonts key is an empty scope; access errors throw and fail closed.
                foreach (var name in key.GetValueNames())
                {
                    RegistryValueKind kind;
                    object? raw;
                    try
                    {
                        kind = key.GetValueKind(name);
                        // DWORD/QWORD cannot encode a Windows font-file path. Some font managers
                        // keep counters/timestamps here. Leave these values completely untouched.
                        if (kind is RegistryValueKind.DWord or RegistryValueKind.QWord) continue;
                        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                            throw new InvalidDataException($"数据类型 {kind} 不能证明是否引用目标字体；保守拒绝。");
                        raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (raw is not string || key.GetValueKind(name) != kind)
                            throw new InvalidDataException("值在读取时已变化或无法读取。");
                    }
                    catch (Exception ex) { throw new IOException($"{hive} 字体项「{name}」：{ex.Message}", ex); }
                    string value = (string)raw;
                    if (value.Length == 0 || value.IndexOf('\0') >= 0)
                        throw new InvalidDataException($"{hive} 字体项「{name}」含空或不完整的路径。");
                    var resolved = ResolveRegisteredPath(hive, value, kind);
                    if (resolved == null || !targets.Contains(resolved)) continue;
                    result.Add(new RegistryEntry
                    {
                        Hive = hive, RegistryPath = FontsRegistryPath, ValueName = name,
                        ValueData = value, ValueKind = kind.ToString()
                    });
                }
            }
            catch (Exception ex) { throw new IOException($"无法枚举 {hive} 字体注册项：{ex.Message}", ex); }
        }
        return result;
    }

    public bool TryReadRegistry(RegistryEntry entry, out string? data, out string? valueKind)
    {
        data = null;
        valueKind = null;
        try
        {
            using var key = OpenRegistry(entry.Hive, writable: false);
            if (key == null || !key.GetValueNames().Contains(entry.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
            var kind = key.GetValueKind(entry.ValueName);
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) return false;
            if (key.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value) return false;
            data = value;
            valueKind = kind.ToString();
            return true;
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (IOException) { throw; }
        catch (Exception ex) { throw new IOException($"无法安全读取 {entry.Hive} 字体注册项 {entry.ValueName}。", ex); }
    }

    public bool RemoveRegistryEntry(RegistryEntry entry)
    {
        try
        {
            using var key = OpenRegistry(entry.Hive, writable: true);
            if (key == null || !key.GetValueNames().Contains(entry.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
            var kind = key.GetValueKind(entry.ValueName);
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) return false;
            var value = key.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (value != entry.ValueData || kind.ToString() != entry.ValueKind) return false;
            // Use one key handle for the last comparison and deletion. Registry has no
            // compare-and-delete primitive, so external concurrent writers remain a race.
            key.DeleteValue(entry.ValueName, throwOnMissingValue: true);
            return !key.GetValueNames().Contains(entry.ValueName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public bool RestoreRegistryEntry(RegistryEntry entry)
    {
        try
        {
            var valueKind = ParseKind(entry.ValueKind);
            using var key = OpenRegistry(entry.Hive, writable: true, create: true);
            if (key == null) return false;
            if (key.GetValueNames().Contains(entry.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
            key.SetValue(entry.ValueName, entry.ValueData, valueKind);
            return TryReadRegistry(entry, out var data, out var kind) && data == entry.ValueData && kind == entry.ValueKind;
        }
        catch { return false; }
    }

    private RegistryKey? OpenRegistry(string hive, bool writable, bool create = false)
    {
        if (hive == "HKLM")
            return create ? _machineRoot.CreateSubKey(FontsRegistryPath, writable) : _machineRoot.OpenSubKey(FontsRegistryPath, writable);
        if (hive == "HKCU")
            return create ? _userRoot.CreateSubKey(FontsRegistryPath, writable) : _userRoot.OpenSubKey(FontsRegistryPath, writable);
        return null;
    }

    private static RegistryValueKind ParseKind(string kind) => kind switch
    {
        "String" => RegistryValueKind.String,
        "ExpandString" => RegistryValueKind.ExpandString,
        _ => throw new InvalidDataException("不支持的注册表类型。")
    };

    internal static bool TryResolveRegisteredPath(string hive, string value, string valueKind, out string resolved)
    {
        resolved = "";
        if (hive is not ("HKLM" or "HKCU") || valueKind is not ("String" or "ExpandString")) return false;
        var path = ResolveRegisteredPath(hive, value, valueKind == "ExpandString" ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        if (path == null) return false;
        resolved = path;
        return true;
    }

    private static string? ResolveRegisteredPath(string hive, string value, RegistryValueKind kind)
    {
        try
        {
            var expanded = kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(value) : value;
            if (expanded.StartsWith("\\\\", StringComparison.Ordinal) || expanded.StartsWith("\\\\?\\", StringComparison.Ordinal)) return null;
            string full;
            if (Path.IsPathRooted(expanded)) full = Path.GetFullPath(expanded);
            else
            {
                if (expanded.Contains('/') || expanded.Contains('\\') || expanded is "." or "..") return null;
                var fonts = hive == "HKLM"
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
                full = Path.GetFullPath(Path.Combine(fonts, expanded));
            }
            return full;
        }
        catch { return null; }
    }
}

/// <summary>Journal authenticator enables isolated transaction tests while production protects a secret with Windows DPAPI.</summary>
public interface IFontOperationAuthenticator
{
    string Sign(FontOperation operation);
    bool Verify(FontOperation operation);
}

/// <summary>HMAC journal integrity backed by a user-scoped DPAPI-protected local secret.</summary>
public sealed class DpapiFontOperationAuthenticator : IFontOperationAuthenticator
{
    private const int ProtectUiForbidden = 0x1;
    private readonly string _keyPath;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public DpapiFontOperationAuthenticator(string opsDir) => _keyPath = Path.Combine(opsDir, ".journal-key");

    public string Sign(FontOperation operation) => Sign(operation, createKey: true);

    private string Sign(FontOperation operation, bool createKey)
    {
        var old = operation.Integrity;
        try
        {
            operation.Integrity = "";
            using var hmac = new HMACSHA256(GetKey(createKey));
            return Convert.ToHexString(hmac.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(operation, Options)));
        }
        finally { operation.Integrity = old; }
    }

    public bool Verify(FontOperation operation)
    {
        if (!FontOperationSafety.IsValidHash(operation.Integrity)) return false;
        try
        {
            var actual = Convert.FromHexString(Sign(operation, createKey: false));
            var expected = Convert.FromHexString(operation.Integrity);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private byte[] GetKey(bool create)
    {
        if (File.Exists(_keyPath))
        {
            if ((File.GetAttributes(_keyPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("操作日志密钥是重解析路径。");
            var unprotected = Unprotect(File.ReadAllBytes(_keyPath));
            if (unprotected.Length != 32) throw new CryptographicException("操作日志密钥长度无效。");
            return unprotected;
        }
        if (!create) throw new CryptographicException("操作日志密钥缺失，历史记录仅可只读查看。");
        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = Protect(key);
        var tempPath = Path.Combine(Path.GetDirectoryName(_keyPath)!, ".journal-key." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(protectedKey);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(tempPath, _keyPath, overwrite: false); }
            catch (IOException) when (File.Exists(_keyPath))
            {
                var recovered = Unprotect(File.ReadAllBytes(_keyPath));
                if (recovered.Length != 32) throw new CryptographicException("操作日志密钥长度无效。");
                return recovered;
            }
            return key;
        }
        finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
    }

    private static byte[] Protect(byte[] input) => Transform(input, protect: true);
    private static byte[] Unprotect(byte[] input) => Transform(input, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob { Length = input.Length, Data = inputPointer };
            DataBlob outputBlob;
            var success = protect
                ? CryptProtectData(ref inputBlob, "FontBook operation journal key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ProtectUiForbidden, out outputBlob);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally { _ = LocalFree(outputBlob.Data); }
        }
        finally { Marshal.FreeHGlobal(inputPointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>可独立注入隔离目录与假注册表的事务协调器。</summary>
public sealed class FontOperationService
{
    private const string FontsRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const int CurrentFormatVersion = 4;
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly string[] ProtectedPrefixes =
    {
        "arial", "tahoma", "times", "verdana", "courier", "calibri", "cambria", "corbel", "candara", "consola", "constantia", "sitka", "bahnschrift",
        "segoe", "segoeui", "segoeuii", "segoeuil", "seguisb", "segoeuib", "seguiemj", "seguisym", "marlett", "symbol", "wingding", "webding",
        "msyh", "msyhl", "msyhbd", "simsun", "simhei", "simkai", "simfang", "deng", "fangzheng", "kaiti", "mingliu", "pmingliu", "msjh",
        "malgun", "gulim", "batang", "meiryo", "yugoth", "yu gothic", "yu gothic ui", "msgothic", "noto sans cjk", "noto serif cjk"
    };
    private static readonly object ThreadGate = new();
    private static readonly Mutex ProcessMutex = new(false, @"Global\FontBook.FontOperations.v2");
    private readonly IFontOperationBackend _backend;
    private readonly IFontOperationAuthenticator _authenticator;
    private readonly string _opsDir;
    private readonly string _vaultDir;
    private readonly string _trustedRoot;

    public FontOperationService(string opsDir, string vaultDir, IFontOperationBackend? backend = null, IFontOperationAuthenticator? authenticator = null)
    {
        _opsDir = Path.GetFullPath(opsDir);
        _vaultDir = Path.GetFullPath(vaultDir);
        _backend = backend ?? new WindowsFontOperationBackend();
        _authenticator = authenticator ?? new DpapiFontOperationAuthenticator(_opsDir);
        var common = Path.GetDirectoryName(_opsDir);
        if (common == null || !FontOperationSafety.IsPathWithin(_vaultDir, common))
            throw new ArgumentException("ops 和 vault 必须位于同一显式数据根目录之下。");
        _trustedRoot = common;
    }

    public string OpsPath => _opsDir;
    public string VaultPath => _vaultDir;

    /// <summary>纯路径校验入口；不访问磁盘或注册表。</summary>
    public static bool TryNormalizeFontPath(string? path, out string normalized, out string reason) =>
        FontOperationSafety.TryNormalizeFontPath(path, out normalized, out reason);

    /// <summary>只读预检。不会修改字体、注册表、操作日志或保险库。</summary>
    public OperationResult Preflight(FamilyItem family, string kind) => WithLock(() => PreflightLocked(family, kind));

    private OperationResult PreflightLocked(FamilyItem family, string kind)
    {
        if (kind is not ("deactivate" or "uninstall")) return new(false, "操作类型无效。");
        var check = InspectFamily(family, kind);
        if (!check.Ok) return new(false, check.Message);
        try
        {
            var entries = _backend.FindRegistryEntries(check.Paths!);
            if (entries.Count > 4096 || entries.Any(e => !IsValidRegistryEntryForPaths(e, check.Paths!)))
                return new(false, "注册表返回了超限或不安全/不相关的值；已拒绝执行。");
            if (kind == "deactivate" && entries.Count == 0)
                return new(false, "没有精确路径匹配的字体注册记录；未执行停用。");
            return new(true, "预检通过：完整路径、共享字体影响、文件可访问性与数据根目录均通过检查。");
        }
        catch (Exception ex) { return new(false, "无法完整检查字体注册表作用域；已拒绝操作：" + ex.Message); }
    }

    public string? GetProtectionReason(FamilyItem family) => WithLock(() =>
    {
        var result = InspectFamily(family, "uninstall");
        return result.Ok ? null : result.Message;
    });

    public IReadOnlyList<FontOperation> LoadHistory()
    {
        return WithLock(() => ReadLogs().Where(x => x.Operation != null).Select(x => x.Operation!).OrderByDescending(x => x.Time, StringComparer.Ordinal).ToList());
    }

    /// <summary>详细只读历史；旧格式、未知版本、损坏或不安全记录均保留可见并明确禁止执行。</summary>
    public IReadOnlyList<FontOperationHistoryEntry> LoadHistoryDetailed()
    {
        return WithLock(() => ReadLogs().OrderByDescending(x => x.Operation?.Time ?? "", StringComparer.Ordinal).ToList());
    }

    public OperationResult Deactivate(FamilyItem family) => WithLock(() => DeactivateLocked(family));
    public OperationResult Uninstall(FamilyItem family) => WithLock(() => UninstallLocked(family));
    public OperationResult UndoLast() => WithLock(() =>
    {
        var record = ReadLogs().Where(x => x.CanUndo && x.Operation is { Undone: false })
            .OrderByDescending(x => x.Operation!.Time, StringComparer.Ordinal).FirstOrDefault();
        return record?.Operation == null ? new(false, "没有安全且可撤销的操作记录。") : UndoLocked(record.Operation.Id);
    });
    public OperationResult Undo(string opId) => WithLock(() => UndoLocked(opId));

    private OperationResult DeactivateLocked(FamilyItem family)
    {
        var preflight = InspectFamily(family, "deactivate");
        if (!preflight.Ok) return new(false, preflight.Message);
        var paths = preflight.Paths!;
        var entries = _backend.FindRegistryEntries(paths).ToList();
        if (entries.Count > 4096 || entries.Any(e => !IsValidRegistryEntryForPaths(e, paths))) return new(false, "注册表返回了超限或不安全/不相关的值；已拒绝执行。");
        if (entries.Count == 0) return new(false, "没有精确路径匹配的字体注册记录；未执行停用。");

        var op = NewOperation("deactivate", family.Name, paths);
        op.Registry = entries;
        try
        {
            op.FileRecords = paths.Select(p => new FontOperationFile { Path = p, Sha256 = _backend.Sha256(p) }).ToList();
            if (op.FileRecords.Any(f => !FontOperationSafety.IsValidHash(f.Sha256)))
                return new(false, "停用前无法记录原字体哈希；未修改注册表。");
        }
        catch (Exception ex) { return new(false, "停用前字体校验失败；未修改注册表：" + ex.Message); }
        op.Status = "Prepared";
        if (!TrySave(op, out var error, initial: true)) return new(false, "无法原子写入恢复日志，未修改注册表：" + error);

        var failures = new List<string>();
        foreach (var entry in op.Registry)
        {
            entry.RemovalAttempted = true;
            op.Status = "RemovingRegistry";
            if (!TrySave(op, out error)) { failures.Add("操作日志无法持久化，停止后续步骤：" + error); break; }
            if (!SafeReadRegistry(entry, out var current, out var kind) || current != entry.ValueData || kind != entry.ValueKind)
            { failures.Add($"注册项 {entry.Hive}\\{entry.ValueName} 已变化、不可读或权限不足，未覆盖。"); continue; }
            if (SafeRemoveRegistry(entry))
            {
                entry.Removed = true;
                op.Status = "RegistryRemoved";
                if (!TrySave(op, out error)) { failures.Add("注册表已变更但进度日志写入失败；请勿清理操作记录。"); break; }
            }
            else failures.Add($"注册项 {entry.Hive}\\{entry.ValueName} 删除失败或已变化。");
        }
        try
        {
            var remaining = _backend.FindRegistryEntries(paths);
            if (remaining.Count > 4096 || remaining.Any(e => !IsValidRegistryEntryForPaths(e, paths)))
                failures.Add("停用后注册表复核返回了超限或不安全条目。");
            else if (remaining.Count > 0)
                failures.Add($"停用期间字体出现 {remaining.Count} 条仍然有效的注册记录；未报告完整成功。");
        }
        catch (Exception ex) { failures.Add("停用后无法完整复核注册表：" + ex.Message); }
        op.Status = failures.Count == 0 ? "Completed" : "Partial";
        op.LastError = string.Join("；", failures);
        var journalSaved = TrySave(op, out error);
        if (!journalSaved) failures.Add("最终状态日志写入失败：" + error);
        if (op.Registry.Any(e => e.Removed)) BroadcastFontChange();
        if (failures.Count > 0)
            return new(false, $"停用未完整完成：已删除 {op.Registry.Count(e => e.Removed)}/{op.Registry.Count} 项。{string.Join("；", failures)} 可尝试撤销记录 {op.Id}。", op.Id);
        return new(true, $"已停用「{family.Name}」：移除 {op.Registry.Count} 条精确注册项，可撤销（{op.Id}）。", op.Id);
    }

    private OperationResult UninstallLocked(FamilyItem family)
    {
        var preflight = InspectFamily(family, "uninstall");
        if (!preflight.Ok) return new(false, preflight.Message);
        var paths = preflight.Paths!;
        var entries = _backend.FindRegistryEntries(paths).ToList();
        if (entries.Count > 4096 || entries.Any(e => !IsValidRegistryEntryForPaths(e, paths))) return new(false, "注册表返回了超限或不安全/不相关的值；已拒绝执行。");

        var op = NewOperation("uninstall", family.Name, paths);
        op.BackupDir = Path.Combine(_vaultDir, op.Id);
        op.Registry = entries;
        op.FileRecords = paths.Select((p, i) => new FontOperationFile
        {
            Path = p,
            BackupPath = Path.Combine(op.BackupDir, $"{i:D4}-{Path.GetFileName(p)}")
        }).ToList();
        op.Status = "Prepared";
        if (!TrySave(op, out var error, initial: true)) return new(false, "无法原子写入恢复日志，未更改字体：" + error);

        try
        {
            EnsureDataRoots(create: true);
            if (_backend.DirectoryExists(op.BackupDir))
                return FailBeforeMutation(op, "操作专属备份目录已存在；为避免覆盖，拒绝继续。");
            _backend.CreateDirectory(op.BackupDir);
            if (_backend.HasReparsePoint(op.BackupDir)) return FailBeforeMutation(op, "备份目录包含重解析点；拒绝继续。");
        }
        catch (Exception ex) { return FailBeforeMutation(op, "无法安全创建备份目录：" + ex.Message); }

        foreach (var file in op.FileRecords)
        {
            try
            {
                if (!_backend.FileExists(file.Path) || _backend.HasReparsePoint(file.Path))
                    throw new IOException("源文件不存在或路径含重解析点。");
                var beforeHash = _backend.Sha256(file.Path);
                if (!FontOperationSafety.IsValidHash(beforeHash)) throw new IOException("源文件哈希不可用。");
                op.Status = "BackingUp";
                if (!TrySave(op, out error)) throw new IOException("无法持久化备份前日志：" + error);
                _backend.CopyFileExclusive(file.Path, file.BackupPath);
                var backupHash = _backend.Sha256(file.BackupPath);
                var afterHash = _backend.Sha256(file.Path);
                if (!beforeHash.Equals(backupHash, StringComparison.OrdinalIgnoreCase) || !beforeHash.Equals(afterHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("备份哈希校验失败或源文件在复制时发生变化。");
                file.Sha256 = beforeHash;
                if (!TrySave(op, out error)) throw new IOException("备份完成但无法写入其哈希记录：" + error);
            }
            catch (Exception ex)
            {
                op.Status = "PartialBackup";
                op.LastError = ex.Message;
                TrySave(op, out _);
                return new(false, $"未执行注册表/字体删除：文件「{Path.GetFileName(file.Path)}」备份失败；保留日志 {op.Id} 与已生成备份。原因：{ex.Message}", op.Id);
            }
        }

        op.Status = "BackedUp";
        if (!TrySave(op, out error)) return new(false, "已备份但日志无法持久化；未修改注册表或源文件。操作记录 " + op.Id + "：" + error, op.Id);

        var failures = new List<string>();
        foreach (var entry in op.Registry)
        {
            entry.RemovalAttempted = true;
            op.Status = "RemovingRegistry";
            if (!TrySave(op, out error)) { failures.Add("无法持久化注册表删除前日志，停止：" + error); break; }
            if (!SafeReadRegistry(entry, out var current, out var kind) || current != entry.ValueData || kind != entry.ValueKind)
            { failures.Add($"注册项 {entry.Hive}\\{entry.ValueName} 已变化、不可读或权限不足。"); continue; }
            if (SafeRemoveRegistry(entry))
            {
                entry.Removed = true;
                op.Status = "RegistryRemoved";
                if (!TrySave(op, out error)) { failures.Add("注册表已变更但进度日志无法持久化；停止删除字体文件。"); break; }
            }
            else failures.Add($"注册项 {entry.Hive}\\{entry.ValueName} 删除失败或已变化。");
        }
        if (failures.Count == 0)
        {
            foreach (var file in op.FileRecords)
            {
                try
                {
                    // A different process can add a Fonts registration after our initial snapshot.
                    // Never unlink a file while any exact-path registration still points at it.
                    EnsureNoCurrentFontRegistrations(file.Path);
                    if (!_backend.FileExists(file.Path) || _backend.HasReparsePoint(file.Path) ||
                        !_backend.Sha256(file.Path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("源文件已消失、路径改变或内容哈希改变；拒绝删除。");
                    file.DeleteAttempted = true;
                    op.Status = "DeletingFile";
                    if (!TrySave(op, out error)) throw new IOException("无法持久化删除前日志：" + error);
                    if (!_backend.DeleteFile(file.Path, file.Sha256)) throw new IOException("文件删除未完成。");
                    file.Deleted = true;
                    op.DeletedFiles.Add(file.Path);
                    // Recheck after unlink. If another process registered it during the race window,
                    // restore the verified backup before returning a partial failure.
                    EnsureNoCurrentFontRegistrations(file.Path);
                    op.Status = "FileDeleted";
                    if (!TrySave(op, out error)) throw new IOException("文件已删除但进度日志无法持久化；保留此前日志及备份。");
                }
                catch (Exception ex)
                {
                    var failureMessage = ex.Message;
                    try
                    {
                        if (file.DeleteAttempted && !_backend.FileExists(file.Path) && _backend.FileExists(file.BackupPath) &&
                            FontOperationSafety.IsValidHash(_backend.Sha256(file.BackupPath)) &&
                            _backend.Sha256(file.BackupPath).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            EnsureSafeTargetParent(file.Path);
                            _backend.RestoreFileNoOverwrite(file.BackupPath, file.Path, file.Sha256);
                            if (!_backend.FileExists(file.Path) || !_backend.Sha256(file.Path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("回滚竞态删除时恢复的字体文件校验失败。");
                            file.Restored = true;
                        }
                    }
                    catch (Exception restoreEx) { failureMessage += "；删除后保护性恢复失败：" + restoreEx.Message; }
                    failures.Add($"文件「{Path.GetFileName(file.Path)}」：{failureMessage}");
                }
            }
        }
        else failures.Add("至少一个注册项删除未能确认；为免留下悬空注册，不删除任何字体文件。");

        op.Status = failures.Count == 0 ? "Completed" : "Partial";
        op.LastError = string.Join("；", failures);
        var finalSaved = TrySave(op, out error);
        if (!finalSaved) failures.Add("最终状态日志写入失败：" + error);
        if (op.Registry.Any(e => e.Removed) || op.FileRecords.Any(f => f.Deleted)) BroadcastFontChange();
        var message = $"卸载「{family.Name}」：已删除 {op.FileRecords.Count(f => f.Deleted)}/{op.FileRecords.Count} 个文件、移除 {op.Registry.Count(e => e.Removed)}/{op.Registry.Count} 条注册项。备份位于 {op.BackupDir}。";
        if (failures.Count > 0) message += " 部分失败：" + string.Join("；", failures) + $"；可尝试撤销 {op.Id}。";
        return new(failures.Count == 0, message, op.Id);
    }

    private OperationResult UndoLocked(string opId)
    {
        if (!FontOperationSafety.IsValidId(opId)) return new(false, "操作编号格式无效。");
        var item = ReadLogs().FirstOrDefault(x => Path.GetFileNameWithoutExtension(x.LogPath).Equals(opId, StringComparison.Ordinal));
        if (item == null) return new(false, "找不到该操作记录。");
        if (!item.CanUndo || item.Operation == null) return new(false, "该日志只读拒绝执行：" + item.SafetyMessage);
        var op = item.Operation;
        if (op.Undone) return new(false, "该操作已经完整撤销。");
        var issues = new List<string>();
        var restoredFiles = 0;
        var restoredKeys = 0;

        if (op.Kind == "uninstall")
        {
            foreach (var file in op.FileRecords.Where(f => f.DeleteAttempted))
            {
                try
                {
                    if (_backend.FileExists(file.Path))
                    {
                        if (_backend.HasReparsePoint(file.Path)) throw new IOException("目标路径出现重解析点。");
                        if (!_backend.Sha256(file.Path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("原路径已有不同内容，拒绝覆盖。");
                        file.Restored = true;
                        restoredFiles++;
                    }
                    else
                    {
                        EnsureSafeTargetParent(file.Path);
                        if (!_backend.FileExists(file.BackupPath) || _backend.HasReparsePoint(file.BackupPath) ||
                            !_backend.Sha256(file.BackupPath).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("保险库备份缺失、越界或哈希不匹配。");
                        op.Status = "RestoringFile";
                        if (!TrySave(op, out var saveError)) throw new IOException("恢复前日志无法持久化：" + saveError);
                        _backend.RestoreFileNoOverwrite(file.BackupPath, file.Path, file.Sha256);
                        if (!_backend.FileExists(file.Path) || !_backend.Sha256(file.Path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("恢复后文件校验失败。");
                        file.Restored = true;
                        restoredFiles++;
                    }
                    op.Status = "FileRestored";
                    if (!TrySave(op, out var error)) throw new IOException("恢复已完成但日志更新失败：" + error);
                }
                catch (Exception ex) { issues.Add($"文件「{Path.GetFileName(file.Path)}」未恢复：{ex.Message}"); }
            }
        }

        foreach (var entry in op.Registry.Where(e => e.RemovalAttempted))
        {
            try
            {
                if (op.Kind is "uninstall" or "deactivate")
                {
                    if (!WindowsFontOperationBackend.TryResolveRegisteredPath(entry.Hive, entry.ValueData, entry.ValueKind, out var registeredPath))
                        throw new IOException("无法将原注册表值安全解析为完整字体路径。");
                    var relatedFile = op.FileRecords.FirstOrDefault(f => f.Path.Equals(registeredPath, StringComparison.OrdinalIgnoreCase));
                    if (relatedFile == null || !_backend.FileExists(relatedFile.Path) || _backend.HasReparsePoint(relatedFile.Path) ||
                        !_backend.Sha256(relatedFile.Path).Equals(relatedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("关联字体文件未恢复或内容已变化；为避免重新启用错误字体，不恢复此注册表项。");
                }
                if (SafeReadRegistry(entry, out var current, out var kind))
                {
                    if (current != entry.ValueData || kind != entry.ValueKind)
                        throw new IOException("注册表项已有不同值/类型，拒绝覆盖。");
                    entry.Restored = true;
                    restoredKeys++;
                }
                else
                {
                    op.Status = "RestoringRegistry";
                    if (!TrySave(op, out var saveError)) throw new IOException("恢复前日志无法持久化：" + saveError);
                    if (!SafeRestoreRegistry(entry)) throw new IOException("无法安全恢复注册表值。");
                    entry.Restored = true;
                    restoredKeys++;
                }
                op.Status = "RegistryRestored";
                if (!TrySave(op, out var error)) throw new IOException("注册表已恢复但日志更新失败：" + error);
            }
            catch (Exception ex) { issues.Add($"注册项 {entry.Hive}\\{entry.ValueName} 未恢复：{ex.Message}"); }
        }

        op.Undone = issues.Count == 0;
        op.Status = op.Undone ? "Undone" : "UndoPartial";
        op.LastError = string.Join("；", issues);
        if (!TrySave(op, out var finalError)) issues.Add("撤销进度日志持久化失败：" + finalError);
        if (restoredFiles > 0 || restoredKeys > 0) BroadcastFontChange();
        if (issues.Count > 0)
            return new(false, $"撤销部分完成：恢复文件 {restoredFiles} 个、注册项 {restoredKeys} 条。{string.Join("；", issues)}；可在冲突修复后重试 {op.Id}。", op.Id);
        return new(true, $"已撤销 {op.Summary}（恢复文件 {restoredFiles} 个、注册项 {restoredKeys} 条）。", op.Id);
    }

    internal OperationResult PreflightIdentity(FamilyItem family, string kind) => WithLock(() =>
    {
        var result = InspectFamily(family, kind, checkDeleteAccess: false);
        return new OperationResult(result.Ok, result.Message);
    });

    private InspectionResult InspectFamily(FamilyItem family, string kind, bool checkDeleteAccess = true)
    {
        if (family == null) return new(false, "字体家族为空。");
        var source = family.Files.Count > 0 ? family.Files : new[] { family.FilePath };
        if (source.Count == 0 || source.Count > 128 || source.Any(string.IsNullOrWhiteSpace)) return new(false, "字体文件路径数量为空、超限或不完整。");
        var paths = new List<string>();
        foreach (var path in source)
        {
            if (!FontOperationSafety.TryNormalizeFontPath(path, out var full, out var reason)) return new(false, "字体路径不安全：" + reason);
            if (!paths.Contains(full, StringComparer.OrdinalIgnoreCase)) paths.Add(full);
        }
        try { EnsureDataRoots(create: false); }
        catch (Exception ex) { return new(false, "操作/备份数据根目录不安全：" + ex.Message); }
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var names = new[] { family.Name, family.NameEn, family.NameZh }.Concat(family.Aliases).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        foreach (var path in paths)
        {
            if (!_backend.FileExists(path)) return new(false, $"字体文件不存在：{path}");
            if (_backend.HasReparsePoint(path)) return new(false, $"拒绝操作含重解析点的字体路径：{path}");
            if (FontOperationSafety.IsPathWithin(path, windowsRoot))
            {
                if (!_backend.IsSafeWindowsFontFile(path, out var windowsFontReason))
                    return new(false, $"Windows 系统字体保护：{windowsFontReason}");
            }
            var basename = Path.GetFileNameWithoutExtension(path);
            if (ProtectedPrefixes.Any(p => basename.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return new(false, $"字体「{basename}」匹配系统关键字体保护规则。");
            if (kind == "uninstall" && checkDeleteAccess)
            {
                if (!_backend.CanDeleteFile(path, out var deleteReason))
                    return new(false, $"字体当前不能删除：{Path.GetFileName(path)}。{deleteReason}");

            }
            var extension = Path.GetExtension(path);
            if (extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase) || extension.Equals(".otc", StringComparison.OrdinalIgnoreCase))
            {
                var faces = OpenTypeReader.ReadFaces(path);
                if (faces.Count == 0) return new(false, $"无法完整解析集合字体 {Path.GetFileName(path)}；为避免误删共享 face，拒绝操作。");
                var familyNames = names.Select(NormalizeFamilyName).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var unmatched = faces.Where(face => !face.Aliases.Append(face.FamilyEn).Append(face.FamilyZh).Append(face.TypoFamilyEn).Append(face.TypoFamilyZh)
                    .Select(NormalizeFamilyName).Any(familyNames.Contains)).ToList();
                if (unmatched.Count > 0)
                    return new(false, $"共享 TTC/OTC 文件含有 {unmatched.Count} 个不属于「{family.Name}」的 face；为防止连带影响其他家族，拒绝卸载/停用此家族单独操作该文件。");
            }
        }
        return new(true, "", paths);
    }

    private static string NormalizeFamilyName(string? value) => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private OperationResult FailBeforeMutation(FontOperation op, string reason)
    {
        op.Status = "PreparationFailed";
        op.LastError = reason;
        TrySave(op, out _);
        return new(false, reason + $" 无字体/注册表变更；记录 {op.Id}。", op.Id);
    }

    private FontOperation NewOperation(string kind, string family, IEnumerable<string> files) => new()
    {
        Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N"),
        Kind = kind,
        Family = family ?? "",
        Time = DateTime.Now.ToString("s"),
        Files = files.ToList(),
        FormatVersion = CurrentFormatVersion
    };

    private bool EnsureDataRoots(bool create)
    {
        if (!FontOperationSafety.IsPathWithin(_opsDir, _trustedRoot) || !FontOperationSafety.IsPathWithin(_vaultDir, _trustedRoot))
            throw new IOException("操作日志和保险库必须在当前配置的数据根目录下。");
        if (_opsDir.Equals(_vaultDir, StringComparison.OrdinalIgnoreCase) || FontOperationSafety.IsPathWithin(_opsDir, _vaultDir) || FontOperationSafety.IsPathWithin(_vaultDir, _opsDir))
            throw new IOException("操作目录与保险库目录必须相互独立。");
        if (_backend.HasReparsePoint(_trustedRoot)) throw new IOException("数据根路径含重解析点。");
        if (create)
        {
            CreatePathSafely(_trustedRoot);
            CreatePathSafely(Path.GetDirectoryName(_opsDir)!);
            CreatePathSafely(_opsDir);
            CreatePathSafely(Path.GetDirectoryName(_vaultDir)!);
            CreatePathSafely(_vaultDir);
        }
        foreach (var dir in new[] { _trustedRoot, _opsDir, _vaultDir })
            if (_backend.DirectoryExists(dir) && _backend.HasReparsePoint(dir)) throw new IOException("操作目录或保险库目录含重解析点。");
        return true;
    }

    private void CreatePathSafely(string path)
    {
        if (!_backend.DirectoryExists(path)) _backend.CreateDirectory(path);
        if (_backend.HasReparsePoint(path)) throw new IOException("创建目录后检测到重解析点：" + path);
    }

    private void EnsureSafeTargetParent(string filePath)
    {
        if (!FontOperationSafety.TryNormalizeFontPath(filePath, out var normalized, out var reason) || normalized != filePath)
            throw new IOException("目标字体路径无效：" + reason);
        var parent = Path.GetDirectoryName(normalized) ?? throw new IOException("字体目标没有父目录。");
        if (!_backend.DirectoryExists(parent) || _backend.HasReparsePoint(parent)) throw new IOException("原文件父目录不存在或包含重解析点。");
        if (_backend.FileExists(normalized)) throw new IOException("恢复目标文件已存在；不会覆盖原文件。");
    }

    private void EnsureNoCurrentFontRegistrations(string path)
    {
        var entries = _backend.FindRegistryEntries(new[] { path });
        if (entries.Count > 4096 || entries.Any(e => !IsValidRegistryEntryForPaths(e, new[] { path })))
            throw new IOException("删除字体前的注册表复核返回超限或不安全条目；拒绝继续。");
        if (entries.Count > 0)
            throw new IOException($"删除字体前发现 {entries.Count} 条当前注册记录；为避免悬空字体注册，拒绝删除。");
    }

    private bool IsValidRegistryEntryForPaths(RegistryEntry entry, IReadOnlyCollection<string> paths) =>
        IsValidRegistryEntry(entry) && WindowsFontOperationBackend.TryResolveRegisteredPath(entry.Hive, entry.ValueData, entry.ValueKind, out var resolved) &&
        paths.Contains(resolved, StringComparer.OrdinalIgnoreCase);

    private bool IsValidRegistryEntry(RegistryEntry entry) =>
        entry != null && entry.Hive is "HKLM" or "HKCU" && entry.RegistryPath == FontsRegistryPath &&
        !string.IsNullOrEmpty(entry.ValueName) && entry.ValueName.IndexOf('\0') < 0 &&
        entry.ValueData != null && entry.ValueData.IndexOf('\0') < 0 && entry.ValueKind is "String" or "ExpandString";

    private bool TrySave(FontOperation op, out string error, bool initial = false)
    {
        error = "";
        try
        {
            EnsureDataRoots(create: true);
            if (!FontOperationSafety.IsValidId(op.Id)) throw new InvalidDataException("无效操作编号。");
            var path = Path.Combine(_opsDir, op.Id + ".json");
            if (_backend.HasReparsePoint(path)) throw new IOException("操作日志路径包含重解析点。");
            op.Integrity = _authenticator.Sign(op);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(op, JsonOptions);
            if (bytes.Length == 0 || bytes.Length > MaxLogBytes)
                throw new InvalidDataException($"操作日志超过安全上限 {MaxLogBytes} 字节，拒绝持久化。");
            var temp = Path.Combine(_opsDir, "." + op.Id + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                if (_backend.FileExists(path))
                {
                    if (initial) throw new IOException("同名操作日志已存在，拒绝覆盖。");
                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                }
                else File.Move(temp, path, overwrite: false);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private List<FontOperationHistoryEntry> ReadLogs()
    {
        var result = new List<FontOperationHistoryEntry>();
        try
        {
            EnsureDataRoots(create: false);
            if (!_backend.DirectoryExists(_opsDir)) return result;
            foreach (var path in Directory.EnumerateFiles(_opsDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                FontOperation? op = null;
                var reason = "";
                var canUndo = false;
                try
                {
                    if (_backend.HasReparsePoint(path)) throw new InvalidDataException("日志文件含重解析点。");
                    var info = new FileInfo(path);
                    if (info.Length <= 0 || info.Length > MaxLogBytes) throw new InvalidDataException("日志为空或超过安全大小限制。");
                    op = JsonSerializer.Deserialize<FontOperation>(File.ReadAllBytes(path), JsonOptions);
            if (op == null) throw new InvalidDataException("日志内容为空。");
                    if (Path.GetFileNameWithoutExtension(path) != op.Id) reason = "日志文件名与操作编号不一致。";
                    else if (op.FormatVersion != CurrentFormatVersion) reason = op.FormatVersion == 0
                        ? "旧版兼容日志只读；缺少逐步持久化与哈希证据，禁止执行撤销。"
                        : $"未知日志版本 {op.FormatVersion}；为防止误恢复，保持只读。";
                    else if (!_authenticator.Verify(op)) reason = "操作日志完整性校验失败；疑似被篡改，只读拒绝执行。";
                    else if (!ValidateOperation(op, out reason)) { }
                    else if (op.Undone) reason = "操作已撤销。";
                    else { canUndo = true; reason = "日志通过安全校验，可安全尝试撤销。"; }
                }
                catch (Exception ex) { reason = "日志只读拒绝执行：" + ex.Message; }
                op ??= new FontOperation { Id = Path.GetFileNameWithoutExtension(path), Status = "Invalid" };
                result.Add(new FontOperationHistoryEntry { Operation = op, CanUndo = canUndo, SafetyMessage = reason, LogPath = path });
            }
        }
        catch (Exception ex)
        {
            result.Add(new FontOperationHistoryEntry { SafetyMessage = "无法读取操作历史：" + ex.Message });
        }
        return result;
    }

    private bool ValidateOperation(FontOperation op, out string reason)
    {
        reason = "";
        if (!FontOperationSafety.IsValidId(op.Id) || op.Kind is not ("deactivate" or "uninstall") || op.Files == null || op.Registry == null || op.FileRecords == null)
        { reason = "日志结构或操作编号无效。"; return false; }
        if (op.Files.Count == 0 || op.Files.Count > 128 || op.Registry.Count > 4096 || op.FileRecords.Count != op.Files.Count)
        { reason = "日志文件/注册表项数量无效。"; return false; }
        var normalized = new List<string>();
        foreach (var file in op.Files)
        {
            if (!FontOperationSafety.TryNormalizeFontPath(file, out var full, out _) || full != file || _backend.HasReparsePoint(file))
            { reason = "日志包含非规范化、遍历或重解析字体路径。"; return false; }
            normalized.Add(full);
        }
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Count)
        { reason = "日志包含重复路径。"; return false; }
        for (var i = 0; i < op.FileRecords.Count; i++)
        {
            var file = op.FileRecords[i];
            if (file.Path != op.Files[i] || file.Path.IndexOf('\0') >= 0 || file.DeleteAttempted && op.Kind != "uninstall" || file.Deleted && !file.DeleteAttempted)
            { reason = "日志文件记录与操作路径不一致。"; return false; }
            if (op.Kind == "uninstall")
            {
                var expectedDir = Path.GetFullPath(Path.Combine(_vaultDir, op.Id));
                var expectedBackup = Path.Combine(expectedDir, $"{i:D4}-{Path.GetFileName(file.Path)}");
                if (file.BackupPath != expectedBackup || !FontOperationSafety.IsPathWithin(file.BackupPath, expectedDir) || !FontOperationSafety.IsValidHash(file.Sha256))
                { reason = "备份路径越界或其 SHA-256 不合法。"; return false; }
            }
            else if (file.BackupPath.Length != 0 || !FontOperationSafety.IsValidHash(file.Sha256))
            { reason = "停用日志应保留原件 SHA-256，且不应声明备份文件。"; return false; }
        }
        foreach (var entry in op.Registry)
        {
            if (!IsValidRegistryEntry(entry) || entry.Removed && !entry.RemovalAttempted ||
                !WindowsFontOperationBackend.TryResolveRegisteredPath(entry.Hive, entry.ValueData, entry.ValueKind, out var resolved) ||
                !normalized.Contains(resolved, StringComparer.OrdinalIgnoreCase))
            { reason = "日志注册表路径、作用域、值类型、字体关联或阶段字段不安全。"; return false; }
        }
        if (op.Kind == "deactivate" && op.Registry.Count == 0)
        { reason = "停用日志缺少注册表恢复依据。"; return false; }
        if (op.Kind == "uninstall" && op.BackupDir != Path.GetFullPath(Path.Combine(_vaultDir, op.Id)))
        { reason = "日志声明的保险库路径不在其合法根目录。"; return false; }
        if (op.Kind == "deactivate" && op.BackupDir.Length != 0)
        { reason = "停用日志声明了非预期备份路径。"; return false; }
        if (_backend.HasReparsePoint(_vaultDir) || op.Kind == "uninstall" && _backend.HasReparsePoint(op.BackupDir))
        { reason = "保险库或备份目录含重解析点。"; return false; }
        return true;
    }

    private bool SafeReadRegistry(RegistryEntry entry, out string? data, out string? kind)
    {
        try { return _backend.TryReadRegistry(entry, out data, out kind); }
        catch { data = null; kind = null; return false; }
    }

    private bool SafeRemoveRegistry(RegistryEntry entry)
    {
        try { return _backend.RemoveRegistryEntry(entry); }
        catch { return false; }
    }

    private bool SafeRestoreRegistry(RegistryEntry entry)
    {
        try { return _backend.RestoreRegistryEntry(entry); }
        catch { return false; }
    }

    private T WithLock<T>(Func<T> action)
    {
        lock (ThreadGate)
        {
            var acquired = false;
            try
            {
                try { acquired = ProcessMutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new TimeoutException("字体操作跨进程锁等待超时。");
                return action();
            }
            catch (Exception ex)
            {
                if (typeof(T) == typeof(OperationResult)) return (T)(object)new OperationResult(false, "字体事务安全停止：" + ex.Message);
                if (typeof(T) == typeof(IReadOnlyList<FontOperation>)) return (T)(object)Array.Empty<FontOperation>();
                if (typeof(T) == typeof(IReadOnlyList<FontOperationHistoryEntry>)) return (T)(object)Array.Empty<FontOperationHistoryEntry>();
                throw;
            }
            finally { if (acquired) ProcessMutex.ReleaseMutex(); }
        }
    }

    private void BroadcastFontChange()
    {
        // Injected backends are isolated tests: never broadcast into real design applications.
        if (_backend is not WindowsFontOperationBackend) return;
        try { SendMessageTimeout(new IntPtr(0xFFFF), 0x001D, IntPtr.Zero, IntPtr.Zero, 2, 250, out _); }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}

/// <summary>兼容既有公共调用点；所有危险操作由可注入事务服务实现。</summary>
public static class FontOperations
{
    private static readonly string LocalRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly FontOperationService Default = new(
        Path.Combine(LocalRoot, "FontBook", "ops"), Path.Combine(LocalRoot, "FontBook", "vault"));

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
    public static string OpsPath => Default.OpsPath;
    public static string? GetProtectionReason(FamilyItem family) => Default.GetProtectionReason(family);
    public static OperationResult Preflight(FamilyItem family, string kind) => Default.Preflight(family, kind);
    public static bool TryNormalizeFontPath(string? path, out string normalized, out string reason) => FontOperationService.TryNormalizeFontPath(path, out normalized, out reason);
    public static IReadOnlyList<FontOperation> LoadHistory() => Default.LoadHistory();
    public static IReadOnlyList<FontOperationHistoryEntry> LoadHistoryDetailed() => Default.LoadHistoryDetailed();
    public static OperationResult Deactivate(FamilyItem family) => Default.Deactivate(family);
    public static OperationResult Uninstall(FamilyItem family) => Default.Uninstall(family);
    public static OperationResult UndoLast() => Default.UndoLast();
    public static OperationResult Undo(string opId) => Default.Undo(opId);
}
