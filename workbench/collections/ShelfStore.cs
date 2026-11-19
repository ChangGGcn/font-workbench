using System.Security.Cryptography;
using System.Text;

namespace FontBook.Workbench.Collections;

/// <summary>
/// Stores named font-family collections in a single versioned JSON file.
/// </summary>
public sealed class ShelfStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly string _mutexName;

    public ShelfStore(string path)
    {
        _path = NormalizePath(path);
        _mutexName = CreateMutexName(_path);
    }

    public ShelfSnapshot Read() =>
        WithLock(() =>
        {
            var state = LoadState(_path);
            return ShelfSnapshotFactory.Create(
                state.Revision,
                state.Shelves);
        });

    public ShelfSnapshot Create(string name, long revision)
    {
        var normalizedName = ShelfStateCodec.NormalizeShelfName(name);
        ValidateRequestedRevision(revision);

        return WithLock(() =>
        {
            var state = LoadState(_path);
            EnsureRevision(revision, state.Revision);

            if (state.Shelves.ContainsKey(normalizedName))
            {
                return ShelfSnapshotFactory.Create(
                    state.Revision,
                    state.Shelves);
            }

            if (state.Shelves.Count >= ShelfStateCodec.MaxShelfCount)
            {
                throw new InvalidOperationException(
                    $"Collection state already contains the maximum " +
                    $"{ShelfStateCodec.MaxShelfCount} shelves.");
            }

            state.Shelves.Add(normalizedName, []);
            return Commit(state);
        });
    }

    public ShelfSnapshot Rename(
        string name,
        string newName,
        long revision)
    {
        var normalizedName = ShelfStateCodec.NormalizeShelfName(name);
        var normalizedNewName =
            ShelfStateCodec.NormalizeShelfName(newName);
        ValidateRequestedRevision(revision);

        return WithLock(() =>
        {
            var state = LoadState(_path);
            EnsureRevision(revision, state.Revision);

            if (!state.Shelves.TryGetValue(
                    normalizedName,
                    out var members))
            {
                throw new KeyNotFoundException(
                    $"Shelf '{normalizedName}' does not exist.");
            }

            var existingName = state.Shelves.Keys.First(
                key => StringComparer.OrdinalIgnoreCase.Equals(
                    key,
                    normalizedName));
            if (string.Equals(
                    existingName,
                    normalizedNewName,
                    StringComparison.Ordinal))
            {
                return ShelfSnapshotFactory.Create(
                    state.Revision,
                    state.Shelves);
            }

            if (state.Shelves.TryGetValue(
                    normalizedNewName,
                    out _) &&
                !StringComparer.OrdinalIgnoreCase.Equals(
                    existingName,
                    normalizedNewName))
            {
                throw new InvalidOperationException(
                    $"Shelf '{normalizedNewName}' already exists.");
            }

            state.Shelves.Remove(existingName);
            state.Shelves.Add(normalizedNewName, members);
            return Commit(state);
        });
    }

    public ShelfSnapshot Delete(string name, long revision)
    {
        var normalizedName = ShelfStateCodec.NormalizeShelfName(name);
        ValidateRequestedRevision(revision);

        return WithLock(() =>
        {
            var state = LoadState(_path);
            EnsureRevision(revision, state.Revision);

            if (!state.Shelves.ContainsKey(normalizedName))
            {
                return ShelfSnapshotFactory.Create(
                    state.Revision,
                    state.Shelves);
            }

            state.Shelves.Remove(normalizedName);
            return Commit(state);
        });
    }

    public ShelfSnapshot SetMember(string name, string familyKey, bool present, long revision) =>
        SetMembers(name, [familyKey], present, revision);

    // A multi-selection is one revision, one lock and one atomic file replacement.
    public ShelfSnapshot SetMembers(string name, IReadOnlyCollection<string> familyKeys, bool present, long revision, bool createIfMissing = false)
    {
        var normalized = ShelfStateCodec.NormalizeShelfName(name);
        ArgumentNullException.ThrowIfNull(familyKeys);
        if (familyKeys.Count > ShelfStateCodec.MaxMembersPerShelf)
            throw new ArgumentOutOfRangeException(nameof(familyKeys));
        var requested = familyKeys.Select(ShelfStateCodec.NormalizeFamilyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateRequestedRevision(revision);
        return WithLock(() =>
        {
            var state = LoadState(_path);
            EnsureRevision(revision, state.Revision);
            bool created = !state.Shelves.TryGetValue(normalized, out var members);
            if (created)
            {
                if (!present || !createIfMissing)
                    throw new KeyNotFoundException($"Shelf '{normalized}' does not exist.");
                if (state.Shelves.Count >= ShelfStateCodec.MaxShelfCount)
                    throw new InvalidOperationException("已达到清单数量上限。");
                state.Shelves.Add(normalized, members = []);
            }
            var current = members!.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (present)
                current.UnionWith(requested);
            else
                current.ExceptWith(requested);
            if (current.Count > ShelfStateCodec.MaxMembersPerShelf)
                throw new InvalidOperationException("清单将超过家族数量上限。");
            if (!created && members!.Count == current.Count)
                return ShelfSnapshotFactory.Create(state.Revision, state.Shelves);
            state.Shelves[normalized] = current.ToList();
            return Commit(state);
        });
    }
    private ShelfSnapshot Commit(ParsedShelfState state)
    {
        if (state.Revision == long.MaxValue)
        {
            throw new InvalidOperationException(
                "Collection revision cannot be advanced further.");
        }

        var nextRevision = checked(state.Revision + 1);
        var nextState = new ParsedShelfState(nextRevision, state.Shelves);
        var content = ShelfStateCodec.Encode(nextState);
        WriteAtomically(_path, content);

        return ShelfSnapshotFactory.Create(
            nextRevision,
            nextState.Shelves);
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        var acquired = false;

        try
        {
            try
            {
                acquired = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException(
                    $"Timed out waiting for the collection store lock " +
                    $"for '{_path}'.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static ParsedShelfState LoadState(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                $"Collection state path is a directory: '{path}'.");
        }

        if (!File.Exists(path))
        {
            return new ParsedShelfState(
                0,
                new Dictionary<string, List<string>>(
                    StringComparer.OrdinalIgnoreCase));
        }

        byte[] content;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);

            if (stream.Length > ShelfStateCodec.MaxFileBytes)
            {
                throw new InvalidDataException(
                    $"Collection state exceeds the " +
                    $"{ShelfStateCodec.MaxFileBytes}-byte file limit.");
            }

            var length = checked((int)stream.Length);
            content = new byte[length];
            stream.ReadExactly(content);

            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    "Collection state changed while it was being read.");
            }
        }
        catch (FileNotFoundException)
        {
            return new ParsedShelfState(
                0,
                new Dictionary<string, List<string>>(
                    StringComparer.OrdinalIgnoreCase));
        }
        catch (DirectoryNotFoundException)
        {
            return new ParsedShelfState(
                0,
                new Dictionary<string, List<string>>(
                    StringComparer.OrdinalIgnoreCase));
        }

        return ShelfStateCodec.Decode(content);
    }

    private static void WriteAtomically(
        string path,
        ReadOnlySpan<byte> content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Collection state path must have a parent directory.");

        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".fontbook-collections-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(
                    temporaryPath,
                    path,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    path,
                    overwrite: false);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the original read/write failure. Orphaned temp files
                // are never treated as collection state by LoadState.
            }
        }
    }

    private static void EnsureRevision(
        long requestedRevision,
        long currentRevision)
    {
        if (requestedRevision != currentRevision)
        {
            throw new InvalidOperationException(
                $"Revision conflict: requested {requestedRevision}, " +
                $"but the current revision is {currentRevision}.");
        }
    }

    private static void ValidateRequestedRevision(long revision)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "Revision must be non-negative.");
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)))
        {
            throw new ArgumentException(
                "Collection state path must name a file.",
                nameof(path));
        }

        return fullPath;
    }

    private static string CreateMutexName(string path)
    {
        var identity = OperatingSystem.IsWindows()
            ? path.ToUpperInvariant()
            : path;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "FontBook.Workbench.Collections." +
               Convert.ToHexString(hash);
    }
}
