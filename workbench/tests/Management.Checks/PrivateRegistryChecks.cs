using Microsoft.Win32;
using FontBook.Management;
using FontBook.TestSupport;

internal static class PrivateRegistryChecks
{
    // A private registry file, NOT a subtree in HKCU/HKLM or a process-wide registry override.
    // The only TTF is an original two-glyph test fixture, never installed into Windows.
    public static void Run(string testRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(testRoot, "win32-roundtrip")).FullName;
        using (var lab = new PrivateFontHive(root))
        {
            var backend = lab.Backend;
            var manager = lab.Service;
            var ops = lab.Store;
            var userFonts = lab.UserFonts;
            var machineFonts = lab.MachineFonts;
            var bytes = PrivateFontHive.FontBytes();
            string name = "Synthetic [QA] (TrueType)", path = Path.Combine(root, "wrapped[1].ttf");
            var sibling = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "other")).FullName, Path.GetFileName(path));
            File.WriteAllBytes(path, bytes);
            File.WriteAllBytes(sibling, bytes);
            userFonts.SetValue("sdk_init_timestamp", 123456789, RegistryValueKind.DWord);
            machineFonts.SetValue("opaque-counter", 1234567890123L, RegistryValueKind.QWord);
            machineFonts.SetValue(name, sibling, RegistryValueKind.String);
            userFonts.SetValue(name, path, RegistryValueKind.ExpandString);
            var inventory = new ManagementInventory([new ManagementFamily(0, "FontWorkbench QA Hive", "FontWorkbench QA Hive", [new(path, 0)])]);
            ManagementPlan Plan() => manager.Preflight(ManagementAction.Uninstall, inventory, [0]);
            // A family can be blocked before paths are returned (ambiguous/shared/unreadable).
            // An exact English key or a known TTC basename must still produce a warning, never eligibility.
            var incomplete = new ManagementInventory([
                new(0, "本地化名称", "Segoe UI", []),
                new(1, "Fixture TTC", "Fixture TTC", [new(Path.Combine(root, "msyh.ttc"), 0)]),
                new(2, "Unlisted fixture", "Unlisted fixture", [new(path, 0)])], "fixture incomplete");
            var rejected = manager.Preflight(ManagementAction.Uninstall, incomplete, [0, 1, 2]);
            Require(rejected.Items.Select(r => r.IsWindowsSystemFont).SequenceEqual(new[] { true, true, false }) &&
                rejected.Items.All(r => !r.CanExecute && !r.CanRequestElevation) && !Directory.Exists(ops),
                "Windows list warning survives an early refusal by family key or TTC file, without overriding protection");
            userFonts.SetValue("binary-with-unknown-semantics", new byte[] { 1, 2 }, RegistryValueKind.Binary);
            var bad = Plan().Items.Single();
            Require(!bad.CanExecute && bad.Message.Contains("Binary") && bad.Message.Contains("binary-with-unknown-semantics"), "unclassified type blocks with exact hive/value/kind");
            userFonts.DeleteValue("binary-with-unknown-semantics");
            var items = backend.FindRegistryEntries([path]);
            Require(items.Count == 1 && items[0].Hive == "HKCU" && items[0].ValueName == name && items[0].ValueKind == "ExpandString", "real registry enumeration selects exact path despite literal brackets, same name and numeric metadata");
            using (var lockFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var blocked = Plan().Items.Single();
                Require(!blocked.CanExecute && blocked.Message.Contains("Win32 32"), "real sharing violation is Win32 32, not a UAC request");
                Require(!blocked.CanRequestElevation && !Directory.Exists(ops), "locked file left registered, no journal or UAC");
            }
            var plan = Plan();
            Require(plan.Items.Single().CanExecute && !Directory.Exists(ops), "private-hive preflight succeeds without writes");
            var result = manager.Execute(plan, true).Single();
            Require(result.Ok && result.OperationId != null, "real Win32 unregister and physical unlink: " + result.Message);
            Require(!File.Exists(path) && userFonts.GetValue(name) == null, "both exact registry slot and physical file really absent");
            Require(File.Exists(sibling) && (string?)machineFonts.GetValue(name) == sibling, "same filename and registration name in another scope untouched");
            var history = manager.LoadHistory().Single();
            Require(history.CanRestore, "authenticated backup can be restored");
            var undone = manager.Execute(manager.PreflightRestore([history.Key]), true).Single();
            Require(undone.Ok, "real Win32 file and registry restore: " + undone.Message);
            Require(File.ReadAllBytes(path).SequenceEqual(bytes) && userFonts.GetValueKind(name) == RegistryValueKind.ExpandString &&
                (string?)userFonts.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == path, "restored bytes, registry data and registry type match original");
            Require((int?)userFonts.GetValue("sdk_init_timestamp") == 123456789 && (long?)machineFonts.GetValue("opaque-counter") == 1234567890123L, "numeric metadata survives uninstall and recovery unchanged");
        }
    }
    private static void Require(bool ok, string label)
    {
        if (!ok)
            throw new InvalidOperationException("private hive: " + label);
        Console.WriteLine("PASS: private hive: " + label);
    }
}
