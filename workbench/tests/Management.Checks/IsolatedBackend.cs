using System.Security.Cryptography;
using FontBook.Models;
using FontBook.Services;

internal sealed class IsolatedBackend(string root) : IFontOperationBackend
{
    private readonly Dictionary<(string Hive, string Name), string> _registry = [];
    internal int Mutations
    {
        get; private set;
    }
    internal string? FailedRegistration
    {
        get; set;
    }
    internal void Register(string hive, string name, string path) => _registry[(hive, name)] = Checked(path);
    internal void Unregister(string hive, string name) => _registry.Remove((hive, name));
    internal bool Has(string hive, string name) => _registry.ContainsKey((hive, name));
    private string Checked(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !FontOperationSafety.IsPathWithin(path, root))
            throw new InvalidOperationException("Fake backend refused an out-of-test path: " + path);
        return Path.GetFullPath(path);
    }
    public bool FileExists(string path) => File.Exists(Checked(path));
    public bool DirectoryExists(string path) => Directory.Exists(Checked(path));
    public bool HasReparsePoint(string path) => false;
    public bool CanDeleteFile(string path, out string reason)
    {
        reason = "";
        return FileExists(path);
    }
    public void CreateDirectory(string path) => Directory.CreateDirectory(Checked(path));
    public string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Checked(path))));
    public void CopyFileExclusive(string source, string destination) => File.Copy(Checked(source), Checked(destination), overwrite: false);
    public void RestoreFileNoOverwrite(string backup, string destination, string expectedHash)
    {
        if (Sha256(backup) != expectedHash)
            throw new IOException("Unexpected backup bytes");
        File.Copy(Checked(backup), Checked(destination), overwrite: false);
        ++Mutations;
    }
    public bool DeleteFile(string path, string expectedSha256)
    {
        if (!FileExists(path) || Sha256(path) != expectedSha256)
            return false;
        File.Delete(Checked(path));
        ++Mutations;
        return true;
    }
    internal int RegistryScans
    {
        get; private set;
    }
    public IReadOnlyList<RegistryEntry> FindRegistryEntries(IReadOnlyCollection<string> exactFontPaths)
    {
        RegistryScans++;
        foreach (var path in exactFontPaths)
            Checked(path);
        return _registry.Where(x => exactFontPaths.Contains(x.Value, StringComparer.OrdinalIgnoreCase))
            .Select(x => new RegistryEntry { Hive = x.Key.Hive, ValueName = x.Key.Name, ValueData = x.Value }).ToArray();
    }
    public bool TryReadRegistry(RegistryEntry entry, out string? data, out string? valueKind)
    {
        bool exists = _registry.TryGetValue((entry.Hive, entry.ValueName), out data);
        valueKind = exists ? "String" : null;
        return exists;
    }
    public bool RemoveRegistryEntry(RegistryEntry entry)
    {
        if (entry.ValueName == FailedRegistration || !TryReadRegistry(entry, out var value, out var kind) || value != entry.ValueData || kind != entry.ValueKind)
            return false;
        _registry.Remove((entry.Hive, entry.ValueName));
        ++Mutations;
        return true;
    }
    public bool RestoreRegistryEntry(RegistryEntry entry)
    {
        if (!_registry.TryAdd((entry.Hive, entry.ValueName), Checked(entry.ValueData)))
            return false;
        ++Mutations;
        return true;
    }
}
