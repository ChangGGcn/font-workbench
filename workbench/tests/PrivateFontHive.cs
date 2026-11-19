using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using FontBook.Management;
using FontBook.Services;

namespace FontBook.TestSupport;

// No predefined registry key is changed, and the synthetic font is never registered with GDI.
// Both Windows key handles exist only inside this process-private, temporary registry file.
internal sealed class PrivateFontHive : IDisposable
{
    internal const string FontKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private readonly RegistryKey _hive, _machine, _user;
    private readonly SafeRegistryHandle _handle;
    public RegistryKey MachineFonts
    {
        get;
    }
    public RegistryKey UserFonts
    {
        get;
    }
    public WindowsFontOperationBackend Backend
    {
        get;
    }
    public ManagementService Service
    {
        get;
    }
    public string Store
    {
        get;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKeyW(string path, out SafeRegistryHandle handle, int access, int options, int reserved);

    public PrivateFontHive(string root)
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "private.dat");
        if (File.Exists(file))
            throw new IOException("Private hive test requires a fresh directory.");
        var err = RegLoadAppKeyW(file, out _handle, 0x2001F /* KEY_READ | KEY_WRITE */, 1 /* REG_PROCESS_APPKEY */, 0);
        if (err != 0)
            throw new System.ComponentModel.Win32Exception(err);
        _hive = RegistryKey.FromHandle(_handle, RegistryView.Registry64);
        _machine = _hive.CreateSubKey("Machine");
        _user = _hive.CreateSubKey("User");
        MachineFonts = _machine.CreateSubKey(FontKey);
        UserFonts = _user.CreateSubKey(FontKey);
        Backend = new WindowsFontOperationBackend(_machine, _user);
        Store = Path.Combine(root, "recovery");
        Service = new ManagementService(Store, Backend, new DpapiFontOperationAuthenticator(Path.Combine(Store, "ops")));
    }
    public static byte[] FontBytes()
    {
        using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("hive-fixture.b64")!);
        return Convert.FromBase64String(reader.ReadToEnd());
    }
    public void Dispose()
    {
        UserFonts.Dispose();
        MachineFonts.Dispose();
        _user.Dispose();
        _machine.Dispose();
        _hive.Dispose();
        _handle.Dispose();
    }
}
