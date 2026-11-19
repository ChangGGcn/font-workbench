using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using FontBook.Models;
using FontBook.Services;
using Microsoft.Win32;

namespace FontBook.Management;

/// <summary>Batch policy around the original journaled transaction engine. Each approved item remains its own recoverable transaction.</summary>
public sealed class ManagementService : IManagementService
{
    private const string UnknownPermission = "未能确认权限";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly string WindowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly IFontOperationBackend _backend;
    private readonly JournalStore _workbench;
    private readonly JournalStore? _legacy;
    private readonly bool _elevated, _isProduction;
    private readonly ConditionalWeakTable<ManagementPlan, Prepared[]> _plans = new();
    private readonly object _planGate = new();

    public ManagementService(string dataRoot, IFontOperationBackend backend, IFontOperationAuthenticator authenticator,
        bool isElevated = false, string? legacyRoot = null, IFontOperationAuthenticator? legacyAuthenticator = null)
        : this(new(dataRoot, authenticator), backend, isElevated,
            legacyRoot is null ? null : new(legacyRoot, legacyAuthenticator ?? throw new ArgumentNullException(nameof(legacyAuthenticator))), false)
    {
    }

    private ManagementService(JournalStore workbench, IFontOperationBackend backend, bool elevated, JournalStore? legacy, bool production)
    {
        if (legacy is not null && (FontOperationSafety.IsPathWithin(workbench.Root, legacy.Root) || FontOperationSafety.IsPathWithin(legacy.Root, workbench.Root)))
            throw new ArgumentException("工作台与旧操作目录必须相互独立。");
        _workbench = workbench.WithWorkbenchAuthentication();
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _legacy = legacy;
        _elevated = elevated;
        _isProduction = production;
    }

    public static ManagementService CreateWindows()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(local))
            throw new IOException("无法确定本机应用数据目录。");
        return new(new(Path.Combine(local, "FontBook.Workbench", "management")), new WindowsFontOperationBackend(),
            IsElevated(), new(Path.Combine(local, "FontBook")), true);
    }

    public ManagementPlan Preflight(ManagementAction action, ManagementInventory inventory, IReadOnlyList<uint> familyIds) =>
        Preflight(action, inventory, familyIds, CancellationToken.None, null);

    public ManagementPlan Preflight(ManagementAction action, ManagementInventory inventory, IReadOnlyList<uint> familyIds,
        CancellationToken token, IProgress<int>? progress)
    {
        if (action is not (ManagementAction.Disable or ManagementAction.Uninstall))
            throw new ArgumentOutOfRangeException(nameof(action));
        var ids = familyIds.Distinct().ToArray();
        var targets = new HashSet<string>(PathComparer);
        foreach (uint id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (inventory.Describe(id, out _, out var files) == null)
                targets.UnionWith(files.Keys);
        }
        // Exactly one registry census per read-only plan, not two full HKLM/HKCU scans for every family.
        // This snapshot is never passed into Execute: the signed transaction uses fresh reads under its mutex.
        var snapshot = new Dictionary<string, List<RegistryEntry>>(PathComparer);
        string? error = null;
        if (targets.Count != 0)
            try
            {
                var entries = _backend.FindRegistryEntries(targets);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry == null || !TryExactRegisteredPath(entry, out string path) || !targets.Contains(path))
                        throw new IOException("注册表返回不相关或无法定位的记录；拒绝继续。");
                    if (!snapshot.TryGetValue(path, out var bucket))
                        snapshot.Add(path, bucket = []);
                    bucket.Add(entry);
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { error = ex.Message; }
        var prepared = new List<Prepared>(ids.Length);
        foreach (uint id in ids)
        {
            token.ThrowIfCancellationRequested();
            prepared.Add(PrepareFamily(action, inventory, id, snapshot, error));
            progress?.Report(prepared.Count);
        }
        token.ThrowIfCancellationRequested();
        return Publish(prepared.ToArray());
    }
    /// Determines only whether an installed *copy* is located directly in a managed font directory.
    /// This does not grant permission: handle identity, WRP, ACL, registration and hash checks follow.
    public static bool IsInstalledCopy(string path, string windows, string localAppData)
    {
        if (!FontOperationSafety.TryNormalizeFontPath(path, out var file, out _) ||
            !Path.IsPathFullyQualified(windows) || !Path.IsPathFullyQualified(localAppData))
            return false;
        var parent = Path.GetDirectoryName(file);
        return PathComparer.Equals(parent, Path.Combine(windows, "Fonts")) ||
               PathComparer.Equals(parent, Path.Combine(localAppData, "Microsoft", "Windows", "Fonts"));
    }

    private Prepared PrepareFamily(ManagementAction action, ManagementInventory inventory, uint id,
        Dictionary<string, List<RegistryEntry>> snapshot, string? enumerationError)
    {
        var refusal = inventory.Describe(id, out var family, out var files);
        var label = family?.Name ?? $"家族 #{id}";
        var paths = Array.AsReadOnly(files.Keys.ToArray());
        var permission = UnknownPermission;
        var listed = family is not null && (Windows11FontList.MatchesFamily(family.Key) ||
            Windows11FontList.MatchesFamily(family.Name) || family.Faces.Any(face => Windows11FontList.MatchesFile(face.Path)));
        ManagementCheck Check(bool allowed, string message) => new(action, label, paths, allowed, permission, message) { IsWindowsSystemFont = listed };
        Prepared Block(string message) => new(Check(false, message));
        if (refusal is not null || family is null)
            return Block(refusal ?? "家族证据缺失。");
        if (_isProduction && action == ManagementAction.Uninstall && paths.Any(path =>
            !IsInstalledCopy(path, WindowsRoot, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))))
            return Block("包含安装目录以外的字体原件，不会从设计资源库删除文件。若要移除系统注册，请使用停用。");
        if (paths.Any(InJournalRoot))
            return Block("字体目标位于操作数据或保险库中，拒绝操作。");
        var kind = action == ManagementAction.Disable ? "deactivate" : "uninstall";
        var target = new FamilyItem { Name = family.Name, Aliases = [family.Key], Files = paths };
        var hashes = new Dictionary<string, string>(PathComparer);
        Dictionary<RegistrySlot, RegistrySnapshot>? registry = null;
        try
        {
            // Calculate scope even if the old service's file ACL preflight subsequently refuses the row.
            // The second read is under the original cross-process mutex, before any write.
            if (enumerationError != null)
                return Block("无法完成注册核对：" + enumerationError);
            var exactEntries = paths.Where(snapshot.ContainsKey).SelectMany(p => snapshot[p]).ToArray();
            registry = CaptureRegistry(paths, exactEntries);
            var requiresAdmin = RequiresAdmin(paths, registry.Keys);
            permission = Permission(requiresAdmin);
            if (registry.Count == 0)
                return Block("未找到精确路径匹配的已安装注册项；不处理未注册/已停用文件。");
            if (requiresAdmin && !_elevated)
            {
                var identity = _workbench.Engine(_backend).PreflightIdentity(target, kind);
                if (!identity.Ok)
                    return Block(identity.Message);
                foreach (var (path, indices) in files)
                    FontFileEvidence.CheckFaces(path, _backend, indices);
                return new(Check(false, "这是机器范围字体。请在管理员窗口重新预检，确认后才会更改；当前窗口未执行。") with
                {
                    CanRequestElevation = true
                });
            }
            var probe = new TransactionBackend(_backend, (request, entries) =>
            {
                VerifyRegistry(request, paths, entries, registry);
                CheckRegistryWriteAccess(registry.Keys);
                foreach (var (path, faces) in files)
                    hashes.Add(path, FontFileEvidence.Read(path, _backend, faces));
            }, preflightRead: _ => exactEntries);
            var ready = _workbench.Engine(probe).Preflight(target, kind);
            if (!ready.Ok)
                return Block(ready.Message);
            if (hashes.Count != paths.Count)
                return Block("未能读取全部物理字面证据。");

            return new(Check(true, ready.Message), () =>
            {
                var guarded = new TransactionBackend(_backend, (request, entries) =>
                {
                    VerifyRegistry(request, paths, entries, registry);
                    CheckRegistryWriteAccess(registry.Keys);
                    foreach (var (path, expected) in hashes)
                        RequireHash(FontFileEvidence.Read(path, _backend), expected, path);
                }, hashes, NotifyIfChanged);
                var engine = _workbench.Engine(guarded);
                try
                {
                    return action == ManagementAction.Disable ? engine.Deactivate(target) : engine.Uninstall(target);
                }
                finally { guarded.Notify(); }
            });
        }
        catch (Exception ex) { return Block("未通过只读预检：" + ex.Message); }
    }

    public IReadOnlyList<ManagementHistoryItem> LoadHistory()
    {
        var current = _workbench.Engine(_backend).LoadHistoryDetailed().Select(x => HistoryView(x, ManagementHistorySource.Workbench));
        var legacy = _legacy?.Engine(_backend).LoadHistoryDetailed().Select(x => HistoryView(x, ManagementHistorySource.Legacy)) ?? [];
        return Array.AsReadOnly(current.Concat(legacy).OrderByDescending(x => x.Time, StringComparer.Ordinal).ToArray());
    }

    public ManagementPlan PreflightRestore(IReadOnlyList<ManagementHistoryKey> keys)
    {
        var current = _workbench.Engine(_backend).LoadHistoryDetailed()
            .Where(x => !string.IsNullOrEmpty(x.LogPath)).ToDictionary(x => LogId(x), StringComparer.Ordinal);
        var prepared = new List<Prepared>();
        foreach (var key in keys.Distinct())
        {
            if (key.Source != ManagementHistorySource.Workbench)
            {
                prepared.Add(new(new(ManagementAction.Restore, key.Id, [], false, UnknownPermission, "旧安全中心记录只读，不会改写旧日志或保险库。")));
                continue;
            }
            if (!current.TryGetValue(key.Id, out var item))
            {
                prepared.Add(new(new(ManagementAction.Restore, key.Id, [], false, UnknownPermission, "未找到对应的工作台日志。")));
                continue;
            }
            prepared.Add(PrepareRestore(item));
        }
        var overlapping = prepared.Where(x => x.Conflicts is not null).SelectMany(x => x.Conflicts!)
            .GroupBy(x => x, PathComparer).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(PathComparer);
        return Publish(prepared.Select(x => x.Conflicts?.Any(overlapping.Contains) == true
            ? new Prepared(x.Check with
            {
                CanExecute = false,
                Message = "选中的记录涉及同一字体文件或注册项；请分开核对，拒绝批量恢复冲突记录。"
            }) : x).ToArray());
    }

    private Prepared PrepareRestore(FontOperationHistoryEntry item)
    {
        var view = HistoryView(item, ManagementHistorySource.Workbench);
        var action = view.OriginalAction == ManagementAction.Disable ? ManagementAction.Enable : ManagementAction.Restore;
        var permission = UnknownPermission;
        Prepared Block(string reason, string[]? conflicts = null) => new(new(action, view.Family, view.Files, false, permission, reason), Conflicts: conflicts);
        if (!view.CanRestore)
            return Block(view.Detail);
        var operation = item.Operation;
        var slots = operation.Registry.Where(e => e.RemovalAttempted).Select(Slot).ToArray();
        var conflicts = operation.Files.Select(path => "file:" + path).Concat(slots.Select(slot => "registry:" + slot.Hive + "\\" + slot.Name)).Distinct(PathComparer).ToArray();
        try
        {
            var requiresAdmin = RequiresAdmin(operation.Files, slots);
            permission = Permission(requiresAdmin);
            if (requiresAdmin && !_elevated)
                return new(new ManagementCheck(action, view.Family, view.Files, false, permission, "需要管理员权限；请在管理员窗口重新选择经签名的恢复记录。") { CanRequestElevation = true }, Conflicts: conflicts);
            CheckRestore(operation);
            var integrity = operation.Integrity;
            var id = operation.Id;
            return new(new(action, view.Family, view.Files, true, permission, "日志已验签，目标文件、关联注册项和所需备份通过只读检查。"), () =>
            {
                var guarded = new TransactionBackend(_backend, changed: NotifyIfChanged);
                var authenticator = new RestoreAuthenticator(_workbench.Authenticator, id, integrity, CheckRestore);
                try
                {
                    var result = _workbench.Engine(guarded, authenticator).Undo(id);
                    return authenticator.Refusal is { } reason ? new(false, reason, id) : result;
                }
                finally { guarded.Notify(); }
            }, conflicts);
        }
        catch (Exception ex) { return Block("恢复预检拒绝：" + ex.Message, conflicts); }
    }

    private void CheckRestore(FontOperation operation)
    {
        if (operation.Files.Any(InJournalRoot))
            throw new IOException("字体恢复目标位于操作数据或保险库中。");
        var related = new HashSet<string>(PathComparer);
        var slots = new HashSet<RegistrySlot>();
        foreach (var entry in operation.Registry.Where(e => e.RemovalAttempted))
        {
            if (!TryExactRegisteredPath(entry, out var path) || !operation.Files.Contains(path, PathComparer) || !slots.Add(Slot(entry)))
                throw new IOException("日志中的恢复注册项重复或缺少精确的路径对应。");
            related.Add(path);
            if (_backend.TryReadRegistry(entry, out var value, out var kind))
            {
                if (value != entry.ValueData || kind != entry.ValueKind)
                    throw new IOException($"注册项 {entry.Hive}\\{entry.ValueName} 已有不同值或类型；不会覆盖。");
            }
            else if (_isProduction && ExistsRegistrySlot(entry))
                throw new IOException($"注册项 {entry.Hive}\\{entry.ValueName} 已被占用或无法读取；不会覆盖。");
        }
        CheckRegistryWriteAccess(slots);
        foreach (var file in operation.FileRecords.Where(f => f.DeleteAttempted || related.Contains(f.Path)))
        {
            if (_backend.HasReparsePoint(file.Path))
                throw new IOException($"目标字体路径含重解析点：{file.Path}");
            if (_backend.FileExists(file.Path))
            {
                if (InWindows(file.Path) && !_backend.IsSafeWindowsFontFile(file.Path, out var reason))
                    throw new IOException(reason);
                RequireHash(FontFileEvidence.Read(file.Path, _backend), file.Sha256, file.Path);
                continue;
            }
            var parent = Path.GetDirectoryName(file.Path);
            if (!file.DeleteAttempted || parent is null || !_backend.DirectoryExists(parent) || _backend.HasReparsePoint(parent) || _backend.DirectoryExists(file.Path))
                throw new IOException($"原字体缺失、恢复目标冲突或父目录不安全：{file.Path}");
            if (InWindows(file.Path) && !PathComparer.Equals(parent, Path.Combine(WindowsRoot, "Fonts")))
                throw new IOException("禁止恢复到 Windows Fonts 目录以外的系统位置。");
            // The original service validates the per-operation backup path and writes without overwrite.
            RequireHash(FontFileEvidence.Read(file.BackupPath, _backend), file.Sha256, file.BackupPath);
        }
    }

    public IReadOnlyList<ManagementResult> Execute(ManagementPlan plan, bool confirmed)
    {
        Prepared[]? items;
        lock (_planGate)
        {
            _plans.TryGetValue(plan, out items);
            _plans.Remove(plan);
        }
        if (!confirmed || items is null)
            return Array.AsReadOnly(plan.Items.Select(x => new ManagementResult(x.Action, x.Family, false,
                !confirmed ? "已取消；没有写入。" : "计划无效或已使用；请重新预检。", Files: x.Files)).ToArray());
        var results = new List<ManagementResult>(items.Length);
        foreach (var item in items)
        {
            var check = item.Check;
            OperationResult result;
            if (item.Run is null)
                result = new(false, "未执行：" + check.Message);
            else
            {
                try
                {
                    result = item.Run();
                }
                catch (Exception ex) { result = new(false, "操作安全停止；如有部分写入，请查看工作台历史：" + ex.Message); }
            }
            results.Add(new(check.Action, check.Family, result.Ok, result.Message, result.OperationId, check.Files));
        }
        return results.AsReadOnly();
    }

    private ManagementPlan Publish(Prepared[] items)
    {
        var plan = new ManagementPlan(Array.AsReadOnly(items.Select(x => x.Check).ToArray()));
        _plans.Add(plan, items);
        return plan;
    }

    private static ManagementHistoryItem HistoryView(FontOperationHistoryEntry item, ManagementHistorySource source)
    {
        var op = item.Operation;
        var action = op.Kind switch
        {
            "deactivate" => (ManagementAction?)ManagementAction.Disable,
            "uninstall" => ManagementAction.Uninstall,
            _ => null
        };
        var attempted = item.CanUndo && (op.Registry.Any(e => e.RemovalAttempted) || op.FileRecords.Any(f => f.DeleteAttempted));
        var detail = source == ManagementHistorySource.Legacy ? "旧安全中心历史，仅供查看。" : "";
        detail += source == ManagementHistorySource.Legacy && item.CanUndo ? "日志通过安全校验；在本工作台仍为只读。" :
            item.CanUndo && !attempted ? "日志通过验签，但无变更尝试可恢复。" : item.SafetyMessage;
        if (!string.IsNullOrWhiteSpace(op.LastError))
            detail += " 上次结果：" + op.LastError;
        var id = LogId(item);
        return new(new(source, id), action, string.IsNullOrWhiteSpace(op.Family) ? (id.Length == 0 ? "历史读取提示" : Display(id)) : Display(op.Family),
            Display(op.Time), Array.AsReadOnly((op.Files ?? []).Take(128).Select(Display).ToArray()), Display(op.Status),
            source == ManagementHistorySource.Workbench && attempted, Display(detail, 4096));
    }


    private static Dictionary<RegistrySlot, RegistrySnapshot> CaptureRegistry(IReadOnlyCollection<string> paths, IReadOnlyList<RegistryEntry> entries)
    {
        if (entries.Count > 4096)
            throw new IOException("字体注册项超出安全上限。");
        var result = new Dictionary<RegistrySlot, RegistrySnapshot>();
        foreach (var entry in entries)
        {
            if (entry is null || entry.RegistryPath != @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts" || string.IsNullOrEmpty(entry.ValueName) || entry.ValueName.Contains('\0') ||
                !TryExactRegisteredPath(entry, out var path) || !paths.Contains(path, PathComparer) ||
                !result.TryAdd(Slot(entry), new(entry.ValueData, entry.ValueKind)))
                throw new IOException("注册表含重复、无关或不安全的字体项；拒绝操作。");
        }
        return result;
    }

    private static bool TryExactRegisteredPath(RegistryEntry entry, out string path)
    {
        if (!WindowsFontOperationBackend.TryResolveRegisteredPath(entry.Hive, entry.ValueData, entry.ValueKind, out path))
            return false;
        var expanded = entry.ValueKind == "ExpandString" ? Environment.ExpandEnvironmentVariables(entry.ValueData) : entry.ValueData;
        var raw = Path.IsPathRooted(expanded) ? expanded : Path.Combine(Path.GetDirectoryName(path)!, expanded);
        return FontOperationSafety.TryNormalizeFontPath(raw, out var full, out _) && PathComparer.Equals(path, full);
    }

    private static void VerifyRegistry(IReadOnlyCollection<string> request, IReadOnlyCollection<string> paths,
        IReadOnlyList<RegistryEntry> entries, Dictionary<RegistrySlot, RegistrySnapshot> expected)
    {
        if (request.Count != paths.Count || paths.Except(request, PathComparer).Any())
            throw new IOException("事务目标路径与确认范围不一致。");
        var snapshot = CaptureRegistry(paths, entries);
        if (snapshot.Count != expected.Count || expected.Any(x => !snapshot.TryGetValue(x.Key, out var current) || current != x.Value))
            throw new IOException("注册项已在预检后变化；请回到列表重新预检并确认。");
    }

    private bool InJournalRoot(string path) => FontOperationSafety.IsPathWithin(path, _workbench.Root) ||
        _legacy is not null && FontOperationSafety.IsPathWithin(path, _legacy.Root);

    private void CheckRegistryWriteAccess(IEnumerable<RegistrySlot> slots)
    {
        if (!_isProduction)
            return;
        const string fontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
        foreach (var hive in slots.Select(x => x.Hive).Distinct(StringComparer.Ordinal))
        {
            var root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.OpenSubKey(fontsKey, writable: true);
            if (key is null)
                throw new IOException($"无法确认当前账户可以写入 {hive} 字体注册表；不尝试创建键。");
        }
    }

    private static bool ExistsRegistrySlot(RegistryEntry entry)
    {
        var root = entry.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        using var key = root.OpenSubKey(entry.RegistryPath, writable: false);
        return key?.GetValueNames().Contains(entry.ValueName, PathComparer) == true;
    }

    private static RegistrySlot Slot(RegistryEntry entry) => new(entry.Hive, entry.ValueName.ToUpperInvariant());
    private static string LogId(FontOperationHistoryEntry item) => Path.GetFileNameWithoutExtension(item.LogPath);
    private static bool InWindows(string path) => WindowsRoot.Length == 0 || FontOperationSafety.IsPathWithin(path, WindowsRoot);
    private static bool RequiresAdmin(IEnumerable<string> paths, IEnumerable<RegistrySlot> slots) => paths.Any(InWindows) || slots.Any(x => x.Hive == "HKLM");
    private string Permission(bool requiresAdmin) => requiresAdmin ? (_elevated ? "需管理员（当前已提升）" : "需管理员（当前未提升）") : "不需管理员（仍受文件及注册表权限限制）";
    internal static void RequireHash(string actual, string expected, string path)
    {
        if (!PathComparer.Equals(actual, expected))
            throw new IOException($"文件与已验证的内容不一致；拒绝操作：{path}");
    }

    private static string Display(string? raw, int limit = 2048) => raw is null ? "" :
        string.Concat(raw.Take(limit).Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? '\uFFFD' : c)) + (raw.Length > limit ? "…" : "");

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void NotifyIfChanged()
    {
        if (!_isProduction)
            return;
        SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001D, IntPtr.Zero, IntPtr.Zero, 2, 250, out _);
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private sealed record Prepared(ManagementCheck Check, Func<OperationResult>? Run = null, string[]? Conflicts = null);
    private sealed record RegistrySlot(string Hive, string Name);
    private sealed record RegistrySnapshot(string Data, string Kind);

    private sealed class JournalStore
    {
        internal string Root
        {
            get;
        }
        internal IFontOperationAuthenticator Authenticator
        {
            get;
        }
        private readonly string _ops, _vault;
        internal JournalStore(string root, IFontOperationAuthenticator? authenticator = null)
        {
            if (!Path.IsPathFullyQualified(root))
                throw new ArgumentException("事务数据根必须是完整路径。", nameof(root));
            Root = Path.GetFullPath(root);
            _ops = Path.Combine(Root, "ops");
            _vault = Path.Combine(Root, "vault");
            Authenticator = authenticator ?? new DpapiFontOperationAuthenticator(_ops);
        }
        internal JournalStore WithWorkbenchAuthentication() => new(Root, new WorkbenchJournalAuthenticator(Authenticator));
        internal FontOperationService Engine(IFontOperationBackend backend, IFontOperationAuthenticator? authenticator = null) =>
            new(_ops, _vault, backend, authenticator ?? Authenticator);
    }
}
