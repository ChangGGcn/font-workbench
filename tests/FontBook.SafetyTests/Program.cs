using System.Security.Cryptography;
using System.Text.Json;
using FontBook.Models;
using FontBook.Services;

static void Assert(bool ok, string message)
{
    if (!ok) throw new Exception("ASSERT: " + message);
    Console.WriteLine("PASS: " + message);
}

var root = Path.Combine(Path.GetTempPath(), "FontBook-safety-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Assert(FontOperationService.TryNormalizeFontPath(Path.Combine(root, "safe.ttf"), out var safe, out _) && safe.EndsWith("safe.ttf"), "canonical absolute font path accepted");
    Assert(!FontOperationService.TryNormalizeFontPath(Path.Combine(root, "..", "escape.ttf"), out _, out _), "traversal path refused");
    Assert(!FontOperationService.TryNormalizeFontPath("relative.ttf", out _, out _), "relative path refused");
    Assert(!FontOperationService.TryNormalizeFontPath("C:drive-relative.ttf", out _, out _), "drive-relative path refused");
    Assert(!FontOperationService.TryNormalizeFontPath(Path.Combine(root, "wrong.exe"), out _, out _), "non-font extension refused");
    Assert(!FontOperationService.TryNormalizeFontPath(Path.Combine(root, "stream.ttf:payload"), out _, out _), "alternate data stream path refused");
    Assert(!FontOperationService.TryNormalizeFontPath(" " + Path.Combine(root, "safe.ttf"), out _, out _), "leading whitespace path refused");

    var fake = new FakeBackend();
    var windowsFontsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "ThirdParty-Regular.ttf");
    fake.WindowsFontAllowed = true;
    Assert(fake.IsSafeWindowsFontFile(windowsFontsPath, out _), "fake backend can allow eligible Windows Fonts file");
    fake.WindowsFontAllowed = false;
    Assert(!fake.IsSafeWindowsFontFile(windowsFontsPath, out _), "fake backend can deny protected Windows Fonts file");
    var ops = Path.Combine(root, "FontBook", "ops");
    var vault = Path.Combine(root, "FontBook", "vault");
    var service = new FontOperationService(ops, vault, fake, new TestAuthenticator());

    // Exercise the service's actual Windows-directory gate entirely through the fake backend.
    fake.VirtualFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "ThirdParty-Regular.ttf");
    fake.AddFontRegistry("HKCU", "Third party font", fake.VirtualFilePath);
    var windowsFamily = new FamilyItem { Name = "Third Party Typeface", FileName = "ThirdParty-Regular.ttf", FilePath = fake.VirtualFilePath, Files = new[] { fake.VirtualFilePath } };
    fake.WindowsFontAllowed = true;
    Assert(service.Preflight(windowsFamily, "deactivate").Ok, "eligible direct-child Windows font passes injected safety gate");
    fake.WindowsFontAllowed = false;
    Assert(!service.Preflight(windowsFamily, "deactivate").Ok, "protected Windows font is rejected by injected safety gate");
    fake.VirtualFilePath = null;
    fake.ClearRegistry();

    var reparseFont = Path.Combine(root, "reparse.ttf");
    File.WriteAllBytes(reparseFont, new byte[] { 1, 3, 5 });
    fake.AddFontRegistry("HKCU", "Reparse test", reparseFont);
    fake.ReparsePath = reparseFont;
    Assert(!service.Preflight(new FamilyItem { Name = "Reparse Typeface", FileName = "reparse.ttf", FilePath = reparseFont, Files = new[] { reparseFont } }, "deactivate").Ok,
        "reparse font path is rejected before registry mutation");
    fake.ClearRegistry();
    fake.ReparsePath = null;

    var unknownTypeFont = Path.Combine(root, "unknown-type.ttf");
    File.WriteAllBytes(unknownTypeFont, new byte[] { 2, 4, 6 });
    fake.AddFontRegistry("HKCU", "Unknown type test", unknownTypeFont, "Binary");
    Assert(!service.Preflight(new FamilyItem { Name = "Unknown Type Typeface", FileName = "unknown-type.ttf", FilePath = unknownTypeFont, Files = new[] { unknownTypeFont } }, "deactivate").Ok,
        "unsupported registry value type is rejected before mutation");
    fake.ClearRegistry();

    var racedFont = Path.Combine(root, "registry-race.ttf");
    File.WriteAllBytes(racedFont, new byte[] { 5, 6, 7 });
    fake.AddFontRegistry("HKCU", "Race original", racedFont);
    fake.OnRemove = _ => fake.AddFontRegistry("HKCU", "Concurrent registration", racedFont);
    var racedUninstall = service.Uninstall(new FamilyItem { Name = "Registry Race Typeface", FileName = "registry-race.ttf", FilePath = racedFont, Files = new[] { racedFont } });
    Assert(!racedUninstall.Ok && File.Exists(racedFont), "new exact-path registration observed before delete prevents font unlink");
    fake.OnRemove = null;
    fake.ClearRegistry();

    var oversizedFont = Path.Combine(root, "oversized-journal.ttf");
    File.WriteAllBytes(oversizedFont, new byte[] { 3, 2, 1 });
    var oversizedRegistryCount = 100;
    for (var i = 0; i < oversizedRegistryCount; i++)
        fake.AddFontRegistry("HKCU", "oversized-" + i.ToString("D3") + new string('X', 12_000), oversizedFont);
    var oversizedResult = service.Deactivate(new FamilyItem { Name = "Oversized Log Typeface", FileName = "oversized-journal.ttf", FilePath = oversizedFont, Files = new[] { oversizedFont } });
    Assert(!oversizedResult.Ok && oversizedResult.OperationId == null && fake.RegistryCount == oversizedRegistryCount,
        "oversized journal is rejected before any registry mutation");
    fake.ClearRegistry();

    var aclFont = Path.Combine(root, "delete-access.ttf");
    File.WriteAllBytes(aclFont, new byte[] { 8, 6, 4 });
    fake.AddFontRegistry("HKCU", "Delete access test", aclFont);
    fake.FailOpenForDeletePath = aclFont;
    var deniedPreflight = service.Preflight(new FamilyItem { Name = "Delete ACL Typeface", FileName = "delete-access.ttf", FilePath = aclFont, Files = new[] { aclFont } }, "uninstall");
    Assert(!deniedPreflight.Ok && File.Exists(aclFont) && fake.RegistryCount == 1, "missing delete ACL denies preflight before any journal or registry mutation");
    fake.ClearRegistry();
    fake.FailOpenForDeletePath = null;

    var deactFont = Path.Combine(root, "deactivate.ttf");
    File.WriteAllBytes(deactFont, new byte[] { 1, 2, 3, 4 });
    fake.AddFontRegistry("HKCU", "User font", deactFont);
    fake.AddFontRegistry("HKLM", "Another font", deactFont);
    fake.FailRemoveHive = "HKLM";
    var deactivate = service.Deactivate(new FamilyItem { Name = "Test User Typeface", FileName = "deactivate.ttf", FilePath = deactFont, Files = new[] { deactFont } });
    Assert(!deactivate.Ok && deactivate.OperationId != null, "partial deactivation reported honestly with recovery id");
    var history = service.LoadHistoryDetailed().Single(h => h.Operation.Id == deactivate.OperationId);
    Assert(history.CanUndo && history.Operation.Status == "Partial", "partial deactivation journal remains undoable");
    fake.FailRemoveHive = null;
    var deactivationUndo = service.Undo(deactivate.OperationId!);
    Assert(deactivationUndo.Ok && fake.RegistryCount == 2, "partial deactivation restores exact fake registry values");

    var first = Path.Combine(root, "first.ttf");
    var second = Path.Combine(root, "second.ttf");
    File.WriteAllBytes(first, new byte[] { 10, 20, 30 });
    File.WriteAllBytes(second, new byte[] { 40, 50, 60 });
    fake.AddFontRegistry("HKCU", "First font", first);
    fake.AddFontRegistry("HKCU", "Second font", second);
    fake.FailDeletePath = second;
    var family = new FamilyItem { Name = "Two Safe Test Fonts", FileName = "first.ttf", FilePath = first, Files = new[] { first, second } };
    var preflight = service.Preflight(family, "uninstall");
    Assert(preflight.Ok, "uninstall preflight accepts isolated non-system font paths");
    var uninstall = service.Uninstall(family);
    Assert(!uninstall.Ok && uninstall.OperationId != null && !File.Exists(first) && File.Exists(second), "partial uninstall stops safely and reports exact file state");
    var uninstallHistory = service.LoadHistoryDetailed().Single(h => h.Operation.Id == uninstall.OperationId);
    Assert(uninstallHistory.CanUndo, "backed-up partial uninstall retains validated undo record");
    fake.FailDeletePath = null;
    var undo = service.Undo(uninstall.OperationId!);
    Assert(undo.Ok && File.Exists(first) && File.Exists(second), "partial uninstall rollback restores without overwriting existing file");
    Assert(File.ReadAllBytes(first).SequenceEqual(new byte[] { 10, 20, 30 }), "restored file bytes match pre-delete source");
    Assert(fake.RegistryCount == 4, "rollback restores fake registry entries without disturbing earlier history");

    var conflictPath = Path.Combine(root, "undo-conflict.otf");
    File.WriteAllBytes(conflictPath, new byte[] { 7, 8, 9 });
    fake.AddFontRegistry("HKCU", "Conflict font", conflictPath);
    var conflictFamily = new FamilyItem { Name = "Conflict Test Typeface", FileName = "undo-conflict.otf", FilePath = conflictPath, Files = new[] { conflictPath } };
    var conflictUninstall = service.Uninstall(conflictFamily);
    Assert(conflictUninstall.Ok && !File.Exists(conflictPath), "isolated uninstall reaches committed state");
    File.WriteAllBytes(conflictPath, new byte[] { 99, 98, 97 });
    var conflictUndo = service.Undo(conflictUninstall.OperationId!);
    Assert(!conflictUndo.Ok && File.ReadAllBytes(conflictPath).SequenceEqual(new byte[] { 99, 98, 97 }), "undo refuses to overwrite externally changed original file");
    Assert(fake.RegistryCount == 4, "undo does not re-register a font whose original file changed");

    var wrongScope = Path.Combine(root, "scope.ttf");
    File.WriteAllBytes(wrongScope, new byte[] { 1 });
    fake.AddFontRegistry("HKCU", "basename collision", Path.Combine(root, "elsewhere", "scope.ttf"));
    var collision = service.Deactivate(new FamilyItem { Name = "Scope Test Typeface", FileName = "scope.ttf", FilePath = wrongScope, Files = new[] { wrongScope } });
    Assert(!collision.Ok && collision.OperationId == null, "same-basename registry collision is not treated as a matching registration");

    var legacy = new FontOperation { Id = "legacy-operation-identifier", Kind = "deactivate", Family = "Legacy", Time = DateTime.Now.ToString("s") };
    Directory.CreateDirectory(ops);
    File.WriteAllText(Path.Combine(ops, legacy.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(legacy));
    var legacyEntry = service.LoadHistoryDetailed().Single(h => h.Operation.Id == legacy.Id);
    Assert(!legacyEntry.CanUndo && legacyEntry.SafetyMessage.Contains("旧版兼容日志"), "legacy logs remain visible but read-only");

    var malformedPath = Path.Combine(ops, "malformed.json");
    File.WriteAllText(malformedPath, "{ not valid json");
    var malformedEntry = service.LoadHistoryDetailed().Single(h => h.Operation.Id == "malformed");
    Assert(!malformedEntry.CanUndo && malformedEntry.SafetyMessage.StartsWith("日志只读拒绝执行："), "malformed log remains visible and read-only");

    var tamperFont = Path.Combine(root, "tamper.ttf");
    File.WriteAllBytes(tamperFont, new byte[] { 2, 3, 4 });
    fake.AddFontRegistry("HKCU", "Tamper font", tamperFont);
    var tamperResult = service.Deactivate(new FamilyItem { Name = "Tamper Test Typeface", FileName = "tamper.ttf", FilePath = tamperFont, Files = new[] { tamperFont } });
    var tamperLogPath = Path.Combine(ops, tamperResult.OperationId + ".json");
    using (var log = JsonDocument.Parse(File.ReadAllText(tamperLogPath)))
    {
        var tampered = JsonSerializer.Deserialize<FontOperation>(log.RootElement.GetRawText())!;
        tampered.Family = "Modified by log editor";
        File.WriteAllText(tamperLogPath, JsonSerializer.Serialize(tampered));
    }
    var tamperedEntry = service.LoadHistoryDetailed().Single(h => h.Operation.Id == tamperResult.OperationId);
    Assert(!tamperedEntry.CanUndo && tamperedEntry.SafetyMessage.Contains("完整性校验失败"), "modified journal is rejected by integrity check");

    var changedPath = Path.Combine(root, "deactivate-changed.ttf");
    File.WriteAllBytes(changedPath, new byte[] { 3, 7, 9 });
    fake.AddFontRegistry("HKCU", "Changed after deactivation", changedPath);
    var changedResult = service.Deactivate(new FamilyItem { Name = "Changed Typeface", FilePath = changedPath, FileName = "deactivate-changed.ttf", Files = new[] { changedPath } });
    Assert(changedResult.Ok, "isolated deactivation records original file hash");
    var registryBeforeUndo = fake.RegistryCount;
    File.WriteAllBytes(changedPath, new byte[] { 9, 9, 9 });
    var changedUndo = service.Undo(changedResult.OperationId!);
    Assert(!changedUndo.Ok && fake.RegistryCount == registryBeforeUndo, "deactivation undo refuses to register changed file content");
    Assert(File.ReadAllBytes(changedPath).SequenceEqual(new byte[] { 9, 9, 9 }), "deactivation undo preserves externally modified file");

}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

sealed class TestAuthenticator : IFontOperationAuthenticator
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    public string Sign(FontOperation operation)
    {
        var previous = operation.Integrity;
        operation.Integrity = "";
        try { return Convert.ToHexString(HMACSHA256.HashData(Key, JsonSerializer.SerializeToUtf8Bytes(operation))); }
        finally { operation.Integrity = previous; }
    }
    public bool Verify(FontOperation operation) => operation.Integrity == Sign(operation);
}

sealed class FakeBackend : IFontOperationBackend
{
    private readonly Dictionary<(string Hive, string Name), RegistryEntry> _registry = new();
    public string? FailRemoveHive { get; set; }
    public string? FailDeletePath { get; set; }
    public string? VirtualFilePath { get; set; }
    public string? ReparsePath { get; set; }
    public bool WindowsFontAllowed { get; set; }
    public string? FailOpenForDeletePath { get; set; }
    public Action<RegistryEntry>? OnRemove { get; set; }
    public int RegistryCount => _registry.Count;
    public void ClearRegistry() => _registry.Clear();

    public bool CanDeleteFile(string path, out string reason)
    {
        reason = "fake delete access";
        return !string.Equals(path, FailOpenForDeletePath, StringComparison.OrdinalIgnoreCase);
    }
    public bool IsSafeWindowsFontFile(string path, out string reason)
    {
        reason = WindowsFontAllowed ? "fake eligible Windows font" : "fake protected Windows font";
        return WindowsFontAllowed;
    }

    public void AddFontRegistry(string hive, string name, string path, string kind = "String") => _registry[(hive, name)] = new RegistryEntry
    { Hive = hive, RegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", ValueName = name, ValueData = path, ValueKind = kind };
    public bool FileExists(string path) => string.Equals(path, VirtualFilePath, StringComparison.OrdinalIgnoreCase) || File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool HasReparsePoint(string path) => string.Equals(path, ReparsePath, StringComparison.OrdinalIgnoreCase);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public void CopyFileExclusive(string source, string destination) => File.Copy(source, destination, overwrite: false);
    public void RestoreFileNoOverwrite(string backup, string destination, string expectedHash)
    {
        if (!Sha256(backup).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("bad backup hash");
        File.Move(backup, destination, overwrite: false);
    }
    public bool DeleteFile(string path, string expectedSha256)
    {
        if (string.Equals(path, FailDeletePath, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Sha256(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("unexpected file hash");
        File.Delete(path);
        return !File.Exists(path);
    }
    public IReadOnlyList<RegistryEntry> FindRegistryEntries(IReadOnlyCollection<string> exactFontPaths) =>
        _registry.Values.Where(e => exactFontPaths.Contains(e.ValueData, StringComparer.OrdinalIgnoreCase)).Select(Clone).ToArray();
    public bool TryReadRegistry(RegistryEntry entry, out string? data, out string? valueKind)
    {
        if (_registry.TryGetValue((entry.Hive, entry.ValueName), out var current))
        { data = current.ValueData; valueKind = current.ValueKind; return true; }
        data = null; valueKind = null; return false;
    }
    public bool RemoveRegistryEntry(RegistryEntry entry)
    {
        if (entry.Hive == FailRemoveHive || !TryReadRegistry(entry, out var data, out var kind) || data != entry.ValueData || kind != entry.ValueKind) return false;
        var removed = _registry.Remove((entry.Hive, entry.ValueName));
        var callback = OnRemove;
        OnRemove = null;
        callback?.Invoke(entry);
        return removed;
    }
    public bool RestoreRegistryEntry(RegistryEntry entry)
    {
        if (_registry.ContainsKey((entry.Hive, entry.ValueName))) return false;
        _registry[(entry.Hive, entry.ValueName)] = Clone(entry);
        return true;
    }
    private static RegistryEntry Clone(RegistryEntry entry) => new()
    { Hive = entry.Hive, RegistryPath = entry.RegistryPath, ValueName = entry.ValueName, ValueData = entry.ValueData, ValueKind = entry.ValueKind,
      Removed = entry.Removed, RemovalAttempted = entry.RemovalAttempted, Restored = entry.Restored };
}
