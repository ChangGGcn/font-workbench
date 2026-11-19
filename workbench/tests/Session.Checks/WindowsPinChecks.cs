using System.Runtime.Versioning;
using FontBook.Workbench.Session;

namespace FontBook.Workbench.SessionTests;

[SupportedOSPlatform("windows")]
internal static class WindowsPinChecks
{
    // Strictly preflight: no AddFontResource/RemoveFontResource/WM_FONTCHANGE or installed files.
    internal static void Run()
    {
        var b = new WindowsSessionBackend();
        var dir = Directory.CreateTempSubdirectory("FontBook.PinChecks-");
        string file=Path.Combine(dir.FullName,"fixture.ttf");
        try
        {
            byte[] bytes = new byte[16]; bytes[1]=1;File.WriteAllBytes(file,bytes);
            using(b.Pin(file))
            {
                Denies<IOException>(()=>{using var ignored=File.Open(file,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);});
                // The pin grants concurrent readers access to the same signed bytes.
                if(!File.ReadAllBytes(file).SequenceEqual(bytes))throw new Exception("Pinned file changed.");
            }
            File.WriteAllBytes(file,new byte[16]); Denies<InvalidDataException>(()=>b.Pin(file).Dispose());
            Denies<ArgumentException>(()=>b.Pin("relative.ttf").Dispose());
            Denies<ArgumentException>(()=>b.Pin(file+":stream.ttf").Dispose());
            Denies<ArgumentException>(()=>b.Pin(Path.Combine(dir.FullName,"..",dir.Name,"fixture.ttf")).Dispose());
            Denies<InvalidOperationException>(()=>b.Pin(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Fonts","never-open.ttf")).Dispose());
            Console.WriteLine("PASS: read-only Windows pin, release, path and header checks (no GDI writes)");
        }
        finally {File.Delete(file);dir.Delete(recursive:false);}
    }
    private static void Denies<T>(Action action) where T:Exception
    { try{action();}catch(T){return;}throw new Exception($"Expected {typeof(T).Name}"); }
}
