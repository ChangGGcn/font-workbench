using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using FontBook.Management;
using FontBook.Models;
using FontBook.Services;

Windows11FontListChecks.Run();
if (args is ["--windows11-font-list-only"])
    return;

// The original safety suite runs in-process against the same linked engine, never the old EXE.
var entry = Assembly.Load("Management.LegacyChecks").EntryPoint ?? throw new InvalidOperationException("Missing linked legacy safety suite");
try
{
    entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
}
catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
{
    ExceptionDispatchInfo.Capture(inner).Throw();
}

var root = Directory.CreateTempSubdirectory("FontBook-ManagementChecks-").FullName;
try
{
    AdminReviewChecks.Run();
    PrivateRegistryChecks.Run(root);
    var workbench = Path.Combine(root, "workbench");
    var oldRoot = Path.Combine(root, "legacy");
    var ops = Path.Combine(workbench, "ops");
    var oldOps = Path.Combine(oldRoot, "ops");
    var backend = new IsolatedBackend(root);
    var auth = new DpapiFontOperationAuthenticator(ops);
    var oldAuth = new DpapiFontOperationAuthenticator(oldOps);
    var manager = new ManagementService(workbench, backend, auth, isElevated: true, legacyRoot: oldRoot, legacyAuthenticator: oldAuth);
    var a = Path.Combine(root, "a.ttf");
    var aItalic = Path.Combine(root, "a-italic.otf");
    var b = Path.Combine(root, "b.ttf");
    WriteFont(a, "First");
    WriteFont(aItalic, "First");
    WriteFont(b, "Second");
    var original = new[] { a, aItalic, b }.ToDictionary(p => p, File.ReadAllBytes);
    backend.Register("HKCU", "First", a);
    backend.Register("HKCU", "First Extra Name", a); // Registry matching uses paths, not the displayed names.
    backend.Register("HKCU", "First Italic", aItalic);
    backend.Register("HKLM", "Second wrong display name", b);
    var first = Family(1, "First", new(a, 0), new(a, 0), new(aItalic, 0));
    var second = Family(2, "Second", new ManagementFace(b, 0));
    const string win = @"C:\Windows", local = @"C:\Users\Test\AppData\Local";
    Check(ManagementService.IsInstalledCopy(win + @"\Fonts\A.ttf", win, local) &&
          ManagementService.IsInstalledCopy(local + @"\Microsoft\Windows\Fonts\B.otf", win, local), "only direct Windows and per-user installed copies are candidates");
    Check(!ManagementService.IsInstalledCopy(@"C:\Design\Fonts\A.ttf", win, local) &&
          !ManagementService.IsInstalledCopy(win + @"\Fonts\Folder\A.ttf", win, local) &&
          !ManagementService.IsInstalledCopy(win + @"\FontsExtra\A.ttf", win, local), "external originals and prefix lookalikes cannot be uninstalled");
    var catalogue = new ManagementInventory([first, second]);
    using (var stop = new CancellationTokenSource())
    {
        bool canceled = false;
        try
        {
            manager.Preflight(ManagementAction.Disable, catalogue, [1, 2], stop.Token, new StopAfterFirst(stop));
        }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && backend.Mutations == 0 && !Directory.Exists(workbench), "cancel during bulk preflight cannot publish or mutate");
    }

    int previousScans = backend.RegistryScans;
    var initial = manager.Preflight(ManagementAction.Disable, catalogue, [1, 1, 2]);
    Check(backend.RegistryScans == previousScans + 1, "batch read-only preflight enumerates both registry scopes once irrespective of family count");
    Check(initial.Items.Count == 2 && initial.Items.All(x => x.CanExecute) && initial.Items[0].Files.Count == 2, "two eligible families; duplicate selection and named-instance face do not repeat writes");
    Check(initial.Items[0].Permission.StartsWith("不需管理员") && initial.Items[1].Permission.StartsWith("需管理员"), "read-only preflight reports per-item permission scope");
    Check(backend.Mutations == 0 && !Directory.Exists(workbench), "preflight and empty history create no files, journal key, ops or vault");
    Check(manager.LoadHistory().Count == 0 && !Directory.Exists(workbench) && !Directory.Exists(oldRoot), "reading both absent stores creates nothing");
    Check(manager.Execute(initial, false).All(x => !x.Ok) && manager.Execute(initial, true).All(x => !x.Ok), "batch cancel makes the plan unusable");
    var singleCancel = manager.Preflight(ManagementAction.Uninstall, catalogue, [1]);
    Check(manager.Execute(singleCancel, false).Single().Ok == false && backend.Mutations == 0 && !Directory.Exists(workbench), "single uninstall cancel touches nothing");
    var fabricated = initial with
    {
    };
    Check(manager.Execute(fabricated, true).All(x => !x.Ok) && backend.Mutations == 0, "fabricated display plan is not a capability");

    var duplicateFile = new ManagementInventory([first, second, Family(3, "Different family", new ManagementFace(a, 0))]);
    var shared = manager.Preflight(ManagementAction.Disable, duplicateFile, [1, 3]);
    Check(shared.Items.All(x => !x.CanExecute && x.Message.Contains("共享")), "same physical face assigned to different families is blocked, not deduplicated silently");
    Check(!manager.Preflight(ManagementAction.Uninstall, duplicateFile, [1]).Items.Single().CanExecute, "unselected owner also blocks a shared file");
    Check(!manager.Preflight(ManagementAction.Disable, new([first, second], "catalogue incomplete"), [1]).Items.Single().CanExecute, "unknown catalogue ownership fails closed");
    var notElevated = new ManagementService(workbench, backend, auth);
    var reviewOnly = notElevated.Preflight(ManagementAction.Uninstall, catalogue, [2]);
    Check(reviewOnly.Items.Single() is { CanRequestElevation: true, CanExecute: false } && !notElevated.Execute(reviewOnly, true).Single().Ok && backend.Mutations == 0,
        "scope needs UAC review but rejected plans cannot execute even when confirmed");
    Check(!notElevated.Preflight(ManagementAction.Disable, catalogue, [2]).Items.Single().CanExecute && backend.Mutations == 0, "HKLM requires elevation and no implicit restart or write");

    var ttc = Path.Combine(root, "collection.ttc");
    WriteFont(ttc, "Collection", "Collection");
    backend.Register("HKCU", "Collection registry", ttc);
    var complete = Family(4, "Collection", new(ttc, 0), new(ttc, 1));
    Check(manager.Preflight(ManagementAction.Disable, new([complete]), [4]).Items.Single().CanExecute, "single-family TTC enumerates every physical face");
    var split = new ManagementInventory([Family(4, "Collection", new ManagementFace(ttc, 0))]);
    Check(!manager.Preflight(ManagementAction.Disable, split, [4]).Items.Single().CanExecute, "physical face not present in selected row blocks TTC even when names match");
    var disguised = Path.Combine(root, "renamed.ttf");
    WriteFont(disguised, "A", "B");
    backend.Register("HKCU", "Renamed collection", disguised);
    Check(!manager.Preflight(ManagementAction.Uninstall, new([Family(4, "A", new ManagementFace(disguised, 0))]), [4]).Items.Single().CanExecute,
        "misleading extension does not hide additional physical faces");
    var protectedFont = Path.Combine(root, "SegoeUI-protected.ttf");
    WriteFont(protectedFont, "Protected");
    backend.Register("HKCU", "Protected", protectedFont);
    var protectedCheck = manager.Preflight(ManagementAction.Disable, new([Family(5, "Protected", new ManagementFace(protectedFont, 0))]), [5]).Items.Single();
    Check(!protectedCheck.CanExecute && protectedCheck.Message.Contains("保护"), "old protected system font prefix is not bypassed");
    Check(backend.Mutations == 0 && !Directory.Exists(workbench), "shared/protected/incomplete preflights remain read only");

    var changedRegistration = manager.Preflight(ManagementAction.Disable, catalogue, [1]);
    backend.Register("HKLM", "New registry name", a);
    Check(!manager.Execute(changedRegistration, true).Single().Ok && backend.Mutations == 0 && !Directory.Exists(workbench), "late scope change is refused before a journal or mutation");
    backend.Unregister("HKLM", "New registry name");
    var changedContent = manager.Preflight(ManagementAction.Uninstall, catalogue, [1]);
    WriteFont(a, "Replaced");
    Check(!manager.Execute(changedContent, true).Single().Ok && backend.Mutations == 0 && !Directory.Exists(workbench), "late file replacement cannot cross the confirmation boundary");
    File.WriteAllBytes(a, original[a]);

    var disabled = Run(ManagementAction.Disable);
    Check(disabled.All(x => x.Ok && x.OperationId is not null) && original.Keys.All(File.Exists) && !backend.Has("HKCU", "First") && !backend.Has("HKLM", "Second wrong display name"),
        "batch disable preserves both families' exact files and removes only enrolled registrations");
    var disabledKeys = Keys(disabled);
    var enable = manager.PreflightRestore([disabledKeys[0], disabledKeys[1], disabledKeys[0]]);
    Check(enable.Items.Count == 2 && enable.Items.All(x => x.Action == ManagementAction.Enable && x.CanExecute), "two signed disable records are eligible for one batch re-enable, with duplicate deduplication");
    int afterDisable = backend.Mutations;
    var disabledJournal = File.ReadAllBytes(Path.Combine(ops, disabledKeys[0].Id + ".json"));
    Check(!manager.Execute(enable, false).Any(x => x.Ok) && backend.Mutations == afterDisable && File.ReadAllBytes(Path.Combine(ops, disabledKeys[0].Id + ".json")).SequenceEqual(disabledJournal), "restore cancellation does not rewrite a signed journal");
    Check(manager.Execute(manager.PreflightRestore(disabledKeys), true).All(x => x.Ok) && backend.Has("HKCU", "First Extra Name") && backend.Has("HKLM", "Second wrong display name"), "batch re-enable restores both exact HKCU/HKLM values");

    var uninstalled = Run(ManagementAction.Uninstall);
    Check(uninstalled.All(x => x.Ok && x.OperationId is not null) && original.Keys.All(p => !File.Exists(p)), "batch uninstall durably backs up and removes each family's files");
    var uninstallKeys = Keys(uninstalled);
    var restore = manager.PreflightRestore(uninstallKeys);
    Check(restore.Items.All(x => x.Action == ManagementAction.Restore && x.CanExecute), "two authenticated uninstall records offer batch restore");
    Check(manager.Execute(restore, true).All(x => x.Ok) && original.All(e => File.ReadAllBytes(e.Key).SequenceEqual(e.Value)), "restore both families byte-for-byte without replacing an existing file");

    backend.FailedRegistration = "First Italic";
    var partial = Run(ManagementAction.Uninstall);
    Check(!partial[0].Ok && partial[0].OperationId is not null && original.Keys.Take(2).All(File.Exists) && partial[1].Ok && !File.Exists(b),
        "partial failure preserves its journal, refuses unlink, and does not report the other family as failed");
    backend.FailedRegistration = null;
    var partialKeys = Keys(partial);
    var beforeConflict = manager.PreflightRestore(partialKeys);
    WriteFont(b, "Other file at destination");
    var duringRestore = manager.Execute(beforeConflict, true);
    Check(duringRestore[0].Ok && !duringRestore[1].Ok && OpenTypeReader.ReadFaces(b)[0].FamilyEn == "Other file at destination" && !backend.Has("HKLM", "Second wrong display name"),
        "late restore conflict keeps displaced bytes and registration untouched while another item can recover");
    Check(!manager.PreflightRestore([partialKeys[1]]).Items.Single().CanExecute, "existing file conflict is also visible in read-only preflight");
    File.Delete(b);
    backend.Register("HKLM", "Second wrong display name", a); // Do not create a file before refusing a registry conflict.
    Check(!manager.PreflightRestore([partialKeys[1]]).Items.Single().CanExecute && !File.Exists(b), "conflicting registry value blocks restoring the file up front");
    backend.Unregister("HKLM", "Second wrong display name");
    Check(manager.Execute(manager.PreflightRestore([partialKeys[1]]), true).Single().Ok && File.ReadAllBytes(b).SequenceEqual(original[b]), "clearing conflicts allows signed retry with the preserved backup");

    var oldEngine = new FontOperationService(oldOps, Path.Combine(oldRoot, "vault"), backend, oldAuth);
    var oldOperation = oldEngine.Deactivate(new FamilyItem { Name = "Legacy protected by DPAPI", Files = [ttc], Aliases = ["Collection"] });
    Check(oldOperation.Ok && oldOperation.OperationId is not null, "linked legacy engine creates an authenticated v4 journal in isolated root");
    var oldLog = Path.Combine(oldOps, oldOperation.OperationId + ".json");
    var oldBytes = File.ReadAllBytes(oldLog);
    var oldKey = new ManagementHistoryKey(ManagementHistorySource.Legacy, oldOperation.OperationId!);
    const string formatZeroId = "legacy-format-zero-000000001";
    File.WriteAllText(Path.Combine(oldOps, formatZeroId + ".json"), JsonSerializer.Serialize(new FontOperation { Id = formatZeroId, Family = "Legacy format zero", Kind = "uninstall" }));
    var copiedLog = Path.Combine(ops, oldOperation.OperationId + ".json");
    File.Copy(oldLog, copiedLog);
    var history = manager.LoadHistory();
    var legacySigned = history.Single(h => h.Key == oldKey);
    Check(!legacySigned.CanRestore && legacySigned.Detail.Contains("通过安全校验") && history.Any(h => h.Key.Id == formatZeroId && h.Detail.Contains("旧版")),
        "signed legacy vs old format are distinguishable but both read-only");
    var copied = history.Single(h => h.Key.Source == ManagementHistorySource.Workbench && h.Key.Id == oldKey.Id);
    Check(!copied.CanRestore && copied.Detail.Contains("完整性校验失败"), "same id copied from legacy cannot become a trusted workbench record");
    Check(!manager.Execute(manager.PreflightRestore([oldKey, copied.Key]), true).Any(r => r.Ok) && File.ReadAllBytes(oldLog).SequenceEqual(oldBytes),
        "old roots cannot be selected for writes; different origins never alias even with the same id");

    var relocated = Path.Combine(root, "relocated");
    var relocatedOps = Directory.CreateDirectory(Path.Combine(relocated, "ops")).FullName;
    File.Copy(Path.Combine(oldOps, ".journal-key"), Path.Combine(relocatedOps, ".journal-key"));
    File.Copy(oldLog, Path.Combine(relocatedOps, oldKey.Id + ".json"));
    var relocatedManager = new ManagementService(relocated, backend, new DpapiFontOperationAuthenticator(relocatedOps));
    Check(!relocatedManager.LoadHistory().Single().CanRestore, "copying the entire old store including the DPAPI key cannot authorize a legacy journal in the new domain");

    var latest = Run(ManagementAction.Disable);
    var key = Keys(latest)[0];
    var approved = manager.PreflightRestore([key]);
    Check(manager.Execute(manager.PreflightRestore([key]), true).Single().Ok, "another confirmed restore updates the valid signed journal");
    var beforeTamper = backend.Mutations;
    Check(!manager.Execute(approved, true).Single().Ok && backend.Mutations == beforeTamper, "a newly signed journal cannot reuse an old confirmation");
    key = Keys(latest)[1];
    var log = Path.Combine(ops, key.Id + ".json");
    var logBytes = File.ReadAllBytes(log);
    var changed = JsonSerializer.Deserialize<FontOperation>(logBytes)!;
    changed.Family = "Unsigned tamper";
    File.WriteAllText(log, JsonSerializer.Serialize(changed));
    Check(!manager.PreflightRestore([key]).Items.Single().CanExecute && backend.Mutations == beforeTamper, "tampered new log is visible and not actionable");
    File.WriteAllBytes(log, logBytes);

    var eligibleAndProtected = new ManagementInventory([first, Family(5, "Protected", new ManagementFace(protectedFont, 0))]);
    var mixed = manager.Execute(manager.Preflight(ManagementAction.Disable, eligibleAndProtected, [5, 1]), true);
    Check(!mixed[0].Ok && mixed[0].OperationId is null && backend.Has("HKCU", "Protected") && mixed[1].Ok &&
        mixed[1].Files!.SequenceEqual(new[] { a, aItalic }), "mixed preflight executes only approved rows and returns exact per-item files");

    IReadOnlyList<ManagementResult> Run(ManagementAction action) => manager.Execute(manager.Preflight(action, catalogue, [1, 2]), true);
}
finally
{
    var allowed = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "FontBook-ManagementChecks-");
    if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.GetParent(root)?.FullName != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
        throw new InvalidOperationException("Refusing cleanup outside the test temp directory.");
    Directory.Delete(root, recursive: true);
}

static void Check(bool ok, string message)
{
    if (!ok)
        throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static ManagementFamily Family(uint id, string label, params ManagementFace[] faces) => new(id, label, label, faces);
static ManagementHistoryKey[] Keys(IReadOnlyList<ManagementResult> results) => results.Select(r => new ManagementHistoryKey(ManagementHistorySource.Workbench, r.OperationId!)).ToArray();

// Minimal sfnt name-table fixtures; no application installs or consumes them as system fonts.
static void WriteFont(string path, params string[] names)
{
    var records = names.Select(Encoding.BigEndianUnicode.GetBytes).ToArray();
    bool collection = records.Length > 1;
    int position = collection ? 12 + 4 * records.Length : 0;
    var bytes = new byte[position + records.Sum(x => 46 + x.Length)];
    void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), value);
    void U32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), value);
    if (collection)
    {
        U32(0, 0x74746366);
        U32(4, 0x00010000);
        U32(8, (uint)records.Length);
    }
    for (int i = 0; i < records.Length; ++i)
    {
        if (collection)
            U32(12 + 4 * i, (uint)position);
        var table = position + 28;
        U32(position, 0x00010000);
        U16(position + 4, 1);
        U32(position + 12, 0x6E616D65);
        U32(position + 20, (uint)table);
        U32(position + 24, (uint)(18 + records[i].Length));
        U16(table + 2, 1);
        U16(table + 4, 18);
        U16(table + 6, 3);
        U16(table + 8, 1);
        U16(table + 10, 0x0409);
        U16(table + 12, 1);
        U16(table + 14, (ushort)records[i].Length);
        records[i].CopyTo(bytes, table + 18);
        position = table + 18 + records[i].Length;
    }
    File.WriteAllBytes(path, bytes);
}

sealed class StopAfterFirst(CancellationTokenSource stop) : IProgress<int>
{
    public void Report(int completed)
    {
        if (completed == 1)
            stop.Cancel();
    }
}
