using FontBook.Services;

namespace FontBook.Management;

public enum ManagementAction
{
    Disable, Uninstall, Enable, Restore
}
public enum ManagementHistorySource
{
    Workbench, Legacy
}

public sealed record ManagementFace(string Path, uint FaceIndex);
public sealed record ManagementFamily(uint Id, string Name, string Key, IReadOnlyList<ManagementFace> Faces,
    string? BlockReason = null);
public sealed record ManagementHistoryKey(ManagementHistorySource Source, string Id);
public sealed record ManagementHistoryItem(ManagementHistoryKey Key, ManagementAction? OriginalAction,
    string Family, string Time, IReadOnlyList<string> Files, string Status, bool CanRestore, string Detail);
public sealed record ManagementCheck(ManagementAction Action, string Family, IReadOnlyList<string> Files,
    bool CanExecute, string Permission, string Message)
{
    // Advisory only: membership of the Windows 11 list never grants permission to operate.
    public bool IsWindowsSystemFont { get; init; }

    // A navigation affordance only, not an approved operation or a promise of writable access.
    public bool CanRequestElevation
    {
        get; init;
    }
}
public sealed record ManagementPlan(IReadOnlyList<ManagementCheck> Items);
public sealed record ManagementResult(ManagementAction Action, string Family, bool Ok, string Message,
    string? OperationId = null, IReadOnlyList<string>? Files = null);

/// <summary>Read-only preparation followed by one explicitly approved, single-use plan. Calls may run on a worker.</summary>
public interface IManagementService
{
    ManagementPlan Preflight(ManagementAction action, ManagementInventory inventory, IReadOnlyList<uint> familyIds);
    IReadOnlyList<ManagementHistoryItem> LoadHistory();
    ManagementPlan PreflightRestore(IReadOnlyList<ManagementHistoryKey> keys);
    IReadOnlyList<ManagementResult> Execute(ManagementPlan plan, bool confirmed);
}

/// <summary>One installed catalogue snapshot. Path ownership is indexed once, including unselected rows.</summary>
public sealed class ManagementInventory
{
    private readonly Dictionary<uint, ManagementFamily> _families = [];
    private readonly Dictionary<string, uint> _firstOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shared = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _blockReason;

    public ManagementInventory(IEnumerable<ManagementFamily> families, string? blockReason = null)
    {
        _blockReason = blockReason;
        foreach (var family in families)
        {
            var saved = family with
            {
                Faces = Array.AsReadOnly(family.Faces.ToArray())
            };
            if (family.BlockReason is not null)
                _blockReason ??= $"无法排除「{family.Name}」与其它家族共享文件。";
            if (!_families.TryAdd(saved.Id, saved))
                _blockReason ??= "目录出现重复家族编号，无法证明文件归属。";
            foreach (var face in saved.Faces)
            {
                if (!FontOperationSafety.TryNormalizeFontPath(face.Path, out var path, out _))
                    continue;
                if (!_firstOwner.TryAdd(path, saved.Id) && _firstOwner[path] != saved.Id)
                    _shared.Add(path);
            }
        }
    }

    public IReadOnlyCollection<ManagementFamily> Families => _families.Values;

    internal string? Describe(uint id, out ManagementFamily? family, out Dictionary<string, HashSet<uint>> files)
    {
        files = new(StringComparer.OrdinalIgnoreCase);
        if (!_families.TryGetValue(id, out family))
            return "选中的家族不在本次已安装字体目录。";
        var reason = family.BlockReason ?? _blockReason;
        if (string.IsNullOrWhiteSpace(family.Key))
            reason ??= "家族身份不唯一，拒绝操作。";
        foreach (var face in family.Faces)
        {
            if (!FontOperationSafety.TryNormalizeFontPath(face.Path, out var path, out var pathError))
            {
                reason ??= "目录中的字面没有安全的完整路径：" + pathError;
                continue;
            }
            if (!files.TryGetValue(path, out var indices))
                files.Add(path, indices = []);
            indices.Add(face.FaceIndex); // Named instances/simulations can re-use a physical face.
        }
        if (files.Count is 0 or > 128)
            reason ??= "家族文件为空或超出安全上限。";
        if (files.Keys.FirstOrDefault(_shared.Contains) is { } shared)
            reason = $"文件由不同家族共享，不可停用或卸载：{shared}";
        return reason;
    }
}
