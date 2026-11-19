using FontBook.Models;
using FontBook.Services;

namespace FontBook.Management;

// Bridge the confirmation boundary through the original engine's mutex. The first
// registry read is its last read-only decision point before creating the journal.
internal sealed class TransactionBackend(IFontOperationBackend inner,
    Action<IReadOnlyCollection<string>, IReadOnlyList<RegistryEntry>>? inspect = null,
    IReadOnlyDictionary<string, string>? approved = null, Action? changed = null,
    Func<IReadOnlyCollection<string>, IReadOnlyList<RegistryEntry>>? preflightRead = null) : IFontOperationBackend
{
    private bool _inspected, _changed;

    public IReadOnlyList<RegistryEntry> FindRegistryEntries(IReadOnlyCollection<string> paths)
    {
        var entries = preflightRead is null ? inner.FindRegistryEntries(paths) : preflightRead(paths);
        if (!_inspected)
        {
            inspect?.Invoke(paths, entries);
            _inspected = true;
        }
        return entries;
    }

    public string Sha256(string path)
    {
        var hash = inner.Sha256(path);
        if (approved?.TryGetValue(path, out var expected) == true)
            ManagementService.RequireHash(hash, expected, path);
        return hash;
    }

    public bool FileExists(string path) => inner.FileExists(path);
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);
    public bool HasReparsePoint(string path) => inner.HasReparsePoint(path);
    public bool CanDeleteFile(string path, out string reason) => inner.CanDeleteFile(path, out reason);
    public bool IsSafeWindowsFontFile(string path, out string reason) => inner.IsSafeWindowsFontFile(path, out reason);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);
    public void CopyFileExclusive(string source, string destination) => inner.CopyFileExclusive(source, destination);
    public bool TryReadRegistry(RegistryEntry entry, out string? data, out string? valueKind) => inner.TryReadRegistry(entry, out data, out valueKind);
    public bool RemoveRegistryEntry(RegistryEntry entry)
    {
        var removed = inner.RemoveRegistryEntry(entry);
        _changed |= removed;
        return removed;
    }
    public bool RestoreRegistryEntry(RegistryEntry entry)
    {
        var restored = inner.RestoreRegistryEntry(entry);
        _changed |= restored;
        return restored;
    }
    public bool DeleteFile(string path, string expectedSha256)
    {
        var deleted = inner.DeleteFile(path, expectedSha256);
        _changed |= deleted;
        return deleted;
    }
    public void RestoreFileNoOverwrite(string backup, string destination, string expectedHash)
    {
        inner.RestoreFileNoOverwrite(backup, destination, expectedHash);
        _changed = true;
    }
    internal void Notify()
    {
        if (_changed)
            changed?.Invoke();
    }
}

// Only the exact signed journal shown at confirmation may enter Undo. Checking
// live conflicts here runs under the original mutex and before its first write.
internal sealed class RestoreAuthenticator(IFontOperationAuthenticator inner, string id, string integrity,
    Action<FontOperation> check) : IFontOperationAuthenticator
{
    internal string? Refusal
    {
        get; private set;
    }
    public string Sign(FontOperation operation) => inner.Sign(operation);
    public bool Verify(FontOperation operation)
    {
        if (operation.Id != id)
            return false;
        if (!inner.Verify(operation) || operation.Integrity != integrity)
        {
            Refusal = "操作日志在预检后改变或验签失败；拒绝恢复。";
            return false;
        }
        try
        {
            check(operation);
            return true;
        }
        catch (Exception ex) { Refusal = "恢复条件已改变，未执行：" + ex.Message; return false; }
    }
}

// Domain-separate new journals even if a complete old ops directory (including its
// per-user DPAPI key) is copied. The engine still writes and verifies the unmodified
// v4 schema; only the input to HMAC contains the additional Workbench domain.
internal sealed class WorkbenchJournalAuthenticator(IFontOperationAuthenticator inner) : IFontOperationAuthenticator
{
    public string Sign(FontOperation operation) => InDomain(operation, inner.Sign);
    public bool Verify(FontOperation operation) => InDomain(operation, inner.Verify);

    private static T InDomain<T>(FontOperation operation, Func<FontOperation, T> authenticate)
    {
        var kind = operation.Kind;
        try
        {
            operation.Kind = "FontBook.Workbench.Management/journal-v1:" + kind;
            return authenticate(operation);
        }
        finally { operation.Kind = kind; }
    }
}
