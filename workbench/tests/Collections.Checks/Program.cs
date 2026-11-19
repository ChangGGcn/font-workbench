using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FontBook.Workbench.Collections;

namespace FontBook.Workbench.Collections.Checks;

internal static class Program
{
    private const int MaxShelfNameLength = 128;
    private const int MaxFamilyKeyLength = 512;
    private const int MaxShelfCount = 1_000;
    private const int MaxMembersPerShelf = 20_000;
    private const long MaxFileBytes = 8L * 1024 * 1024;

    private const int ConflictExitCode = 42;

    private static int _checks;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            return RunChildMode(args);
        }

        var root = Path.Combine(
            AppContext.BaseDirectory,
            "FontBook-Collections-Checks-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var path = Path.Combine(root, "basic", "state.json");
            var directory = Path.GetDirectoryName(path)!;
            var store = new ShelfStore(path);

            var snapshot = store.Read();
            Assert(
                snapshot.Revision == 0 &&
                snapshot.Shelves.Count == 0 &&
                !Directory.Exists(directory),
                "missing state reads as revision 0 without creating a directory");

            snapshot = store.Create(" Favorites ", 0);
            Assert(
                snapshot.Revision == 1 &&
                snapshot.Shelves.Count == 1 &&
                snapshot.Shelves.ContainsKey("favorites"),
                "Create trims the shelf name and stores it case-insensitively");

            snapshot = store.Create("FAVORITES", snapshot.Revision);
            Assert(
                snapshot.Revision == 1 &&
                snapshot.Shelves.Count == 1,
                "repeated Create is idempotent");

            snapshot = store.SetMember(
                "favorites",
                "Family:Alpha",
                present: true,
                snapshot.Revision);
            Assert(
                snapshot.Revision == 2 &&
                snapshot.Shelves["Favorites"].Count == 1,
                "SetMember adds a member and advances the revision");

            snapshot = store.SetMember(
                "FAVORITES",
                "family:alpha",
                present: true,
                snapshot.Revision);
            Assert(
                snapshot.Revision == 2 &&
                snapshot.Shelves["favorites"].Count == 1,
                "SetMember deduplicates family keys case-insensitively");

            snapshot = store.SetMember(
                "favorites",
                "FAMILY:ALPHA",
                present: false,
                snapshot.Revision);
            Assert(
                snapshot.Revision == 3 &&
                snapshot.Shelves["Favorites"].Count == 0,
                "SetMember removes a member using case-insensitive identity");

            snapshot = store.SetMember(
                "favorites",
                "Family:Alpha",
                present: false,
                snapshot.Revision);
            Assert(
                snapshot.Revision == 3,
                "removing an absent member is idempotent");

            snapshot = store.Rename(
                " favoriteS ",
                "Reading List",
                snapshot.Revision);
            Assert(
                snapshot.Revision == 4 &&
                snapshot.Shelves.ContainsKey("Reading List") &&
                !snapshot.Shelves.ContainsKey("Favorites"),
                "Rename moves the shelf to its trimmed new name");

            snapshot = store.Rename(
                "reading list",
                "READING LIST",
                snapshot.Revision);
            Assert(
                snapshot.Revision == 5 &&
                snapshot.Shelves.ContainsKey("READING LIST"),
                "Rename permits a case-only display-name change");

            snapshot = store.Rename(
                "READING LIST",
                "READING LIST",
                snapshot.Revision);
            Assert(
                snapshot.Revision == 5,
                "renaming to the same display name is idempotent");

            snapshot = store.Delete("reading list", snapshot.Revision);
            var afterDeleteRevision = snapshot.Revision;
            snapshot = store.Delete("READING LIST", snapshot.Revision);
            Assert(
                afterDeleteRevision == 6 &&
                snapshot.Revision == afterDeleteRevision &&
                snapshot.Shelves.Count == 0,
                "Delete removes a shelf and deleting it again is idempotent");

            snapshot = store.Create("Serif", snapshot.Revision);
            snapshot = store.Create("CJK", snapshot.Revision);
            Assert(
                snapshot.Revision == 8 &&
                snapshot.Shelves.Count == 2,
                "two independent shelf groups can coexist");

            snapshot = store.SetMember(
                "serif",
                "Source Serif 4",
                present: true,
                snapshot.Revision);
            snapshot = store.SetMember(
                "CJK",
                "Noto Serif CJK SC",
                present: true,
                snapshot.Revision);
            snapshot = store.SetMember(
                "SERIF",
                "Kaiti SC",
                present: true,
                snapshot.Revision);
            snapshot = store.SetMember(
                "cjk",
                "Noto Sans CJK JP",
                present: true,
                snapshot.Revision);
            Assert(
                snapshot.Shelves["Serif"].Count == 2 &&
                snapshot.Shelves["CJK"].Count == 2,
                "member updates remain isolated between two shelves");

            snapshot = store.Rename(
                "serif",
                "Editorial",
                snapshot.Revision);
            snapshot = store.SetMember(
                "Editorial",
                "Kaiti SC",
                present: false,
                snapshot.Revision);
            snapshot = store.Rename(
                "CJK",
                "CJK 字体",
                snapshot.Revision);
            snapshot = store.Delete("editorial", snapshot.Revision);

            var reloaded = new ShelfStore(path).Read();
            Assert(
                reloaded.Revision == snapshot.Revision &&
                reloaded.Shelves.Count == 1 &&
                reloaded.Shelves.ContainsKey("cjk 字体") &&
                reloaded.Shelves["CJK 字体"].Count == 2,
                "two-group add/rename/delete sequence reloads exactly");

            Throws<NotSupportedException>(
                () =>
                {
                    var shelves =
                        (IDictionary<string, IReadOnlyList<string>>)
                        reloaded.Shelves;
                    shelves.Add("Injected", Array.Empty<string>());
                },
                "snapshot shelf dictionary is read-only");

            Throws<NotSupportedException>(
                () =>
                {
                    var members =
                        (IList<string>)reloaded.Shelves["CJK 字体"];
                    members.Add("Injected");
                },
                "snapshot member lists are read-only");

            RunUnicodeAndValidation(root);
            RunRevisionConflict(root);
            await RunCrossProcessConflict(root);
            RunCorruptionAndLimits(root);
            var batch = new ShelfStore(Path.Combine(root, "batch", "state.json"));
            var added = batch.SetMembers("Project", ["A", "a", "B"], true, 0, createIfMissing: true);
            Assert(added.Revision == 1 && added.Shelves["Project"].Count == 2, "bulk creates a shelf and deduplicates members in one revision");
            var invalid = false;
            try
            {
                batch.SetMembers("Project", ["C", "\0"], true, 1);
            }
            catch (ArgumentException) { invalid = true; }
            Assert(invalid && batch.Read().Revision == 1 && !batch.Read().Shelves["Project"].Contains("C"), "bulk validation failure cannot commit a prefix");
            var removed = batch.SetMembers("Project", ["A", "B"], false, 1);
            Assert(removed.Revision == 2 && removed.Shelves["Project"].Count == 0, "bulk remove is a single commit");
            await RunAtomicWriteChecks(root);

            Throws<ArgumentException>(
                () => _ = new ShelfStore("   "),
                "blank state-file path is rejected");
            Throws<ArgumentException>(
                () => _ = new ShelfStore(
                    Path.Combine(root, "directory-path") +
                    Path.DirectorySeparatorChar),
                "directory-only state-file path is rejected");
            Throws<InvalidDataException>(
                () => _ = new ShelfStore(root).Read(),
                "reading an existing directory as state fails explicitly");

            Console.WriteLine($"{_checks} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void RunUnicodeAndValidation(string root)
    {
        var path = Path.Combine(root, "unicode", "state.json");
        var store = new ShelfStore(path);

        var snapshot = store.Create(" 日本語 📚 ", 0);
        snapshot = store.SetMember(
            "日本語 📚",
            "排版字体·café",
            present: true,
            snapshot.Revision);
        snapshot = store.SetMember(
            "日本語 📚",
            "Family:Combining-e\u0301",
            present: true,
            snapshot.Revision);

        var text = File.ReadAllText(path, new UTF8Encoding(false));
        Assert(
            snapshot.Shelves.ContainsKey("日本語 📚") &&
            snapshot.Shelves["日本語 📚"].Contains(
                "排版字体·café",
                StringComparer.Ordinal) &&
            text.Contains("日本語", StringComparison.Ordinal) &&
            text.Contains("排版字体·café", StringComparison.Ordinal) &&
            !text.Contains('\uFFFD'),
            "Unicode shelf names and family keys round-trip through UTF-8");

        var before = File.ReadAllBytes(path);
        var revision = snapshot.Revision;

        Throws<ArgumentException>(
            () => store.Create("", revision),
            "empty shelf name is rejected");
        Throws<ArgumentException>(
            () => store.Create(" \t ", revision),
            "control-only shelf name is rejected");
        Throws<ArgumentException>(
            () => store.Create("Bad\nName", revision),
            "embedded control character in shelf name is rejected");
        Throws<ArgumentException>(
            () => store.Create(new string('x', MaxShelfNameLength + 1), revision),
            "overlong shelf name is rejected");
        Throws<ArgumentException>(
            () => store.Create("\uD800", revision),
            "unpaired surrogate in shelf name is rejected");

        Throws<ArgumentException>(
            () => store.SetMember("日本語 📚", "", true, revision),
            "empty family key is rejected");
        Throws<ArgumentException>(
            () => store.SetMember("日本語 📚", " ", true, revision),
            "whitespace-only family key is rejected");
        Throws<ArgumentException>(
            () => store.SetMember("日本語 📚", "bad\u0001key", true, revision),
            "control character in family key is rejected");
        Throws<ArgumentException>(
            () => store.SetMember(
                "日本語 📚",
                new string('k', MaxFamilyKeyLength + 1),
                true,
                revision),
            "overlong family key is rejected");

        Assert(
            store.Read().Revision == revision &&
            File.ReadAllBytes(path).SequenceEqual(before),
            "invalid names do not modify the stored revision or bytes");

        var boundaryName = new string('名', MaxShelfNameLength);
        var boundaryKey = new string('字', MaxFamilyKeyLength);
        snapshot = store.Create(boundaryName, revision);
        snapshot = store.SetMember(
            boundaryName,
            boundaryKey,
            present: true,
            snapshot.Revision);
        Assert(
            snapshot.Shelves.ContainsKey(boundaryName) &&
            snapshot.Shelves[boundaryName].Contains(
                boundaryKey,
                StringComparer.Ordinal),
            "exact shelf-name and family-key length boundaries are accepted");
    }

    private static void RunRevisionConflict(string root)
    {
        var path = Path.Combine(root, "revision", "state.json");
        var first = new ShelfStore(path);
        var second = new ShelfStore(path);

        var firstSnapshot = first.Create("Decks", 0);
        var staleRevision = second.Read().Revision;
        firstSnapshot = first.SetMember(
            "Decks",
            "family-alpha",
            present: true,
            firstSnapshot.Revision);

        Throws<InvalidOperationException>(
            () => second.Create("Other", staleRevision),
            "stale revision rejects Create");
        Throws<InvalidOperationException>(
            () => second.Delete("Decks", staleRevision),
            "stale revision rejects Delete");
        Throws<InvalidOperationException>(
            () => second.Create("Decks", staleRevision),
            "stale revision is checked even when the operation would be idempotent");
        Throws<ArgumentOutOfRangeException>(
            () => first.Create("Negative", -1),
            "negative requested revision is rejected");

        var final = second.Read();
        Assert(
            final.Revision == firstSnapshot.Revision &&
            final.Shelves.Count == 1 &&
            final.Shelves["Decks"].Count == 1,
            "revision conflicts leave the latest committed state intact");
    }

    private static async Task RunCrossProcessConflict(string root)
    {
        var path = Path.Combine(root, "process-conflict", "state.json");
        var store = new ShelfStore(path);
        var initial = store.Create("Seed", 0);
        var revision = initial.Revision.ToString(
            CultureInfo.InvariantCulture);

        var attempts = new[]
        {
            RunChildAsync("--attempt-create", path, revision, "ProcessA"),
            RunChildAsync("--attempt-create", path, revision, "ProcessB")
        };
        var results = await Task.WhenAll(attempts);

        Assert(
            results.Count(result => result.ExitCode == 0) == 1 &&
            results.Count(result => result.ExitCode == ConflictExitCode) == 1,
            "two processes using one revision produce one success and one conflict");

        var final = store.Read();
        Assert(
            final.Revision == initial.Revision + 1 &&
            final.Shelves.Count == 2 &&
            (final.Shelves.ContainsKey("ProcessA") ^
             final.Shelves.ContainsKey("ProcessB")),
            "cross-process loser cannot overwrite the committed winner");
    }

    private static void RunCorruptionAndLimits(string root)
    {
        var corruptDirectory = Path.Combine(root, "corrupt");
        Directory.CreateDirectory(corruptDirectory);

        AssertInvalidStateDoesNotOverwrite(
            Path.Combine(corruptDirectory, "bad-json.json"),
            Encoding.UTF8.GetBytes(
                """{"schema":1,"revision":2,"shelves":["""),
            "malformed JSON");

        AssertInvalidStateDoesNotOverwrite(
            Path.Combine(corruptDirectory, "future.json"),
            Encoding.UTF8.GetBytes(
                """{"schema":2,"revision":7,"shelves":[]}"""),
            "future schema");

        AssertInvalidStateDoesNotOverwrite(
            Path.Combine(corruptDirectory, "unknown-property.json"),
            Encoding.UTF8.GetBytes(
                """{"schema":1,"revision":0,"shelves":[],"extra":true}"""),
            "unknown JSON property");

        AssertInvalidStateDoesNotOverwrite(
            Path.Combine(corruptDirectory, "duplicate-shelf.json"),
            Encoding.UTF8.GetBytes(
                """{"schema":1,"revision":1,"shelves":[{"name":"A","members":[]},{"name":"a","members":[]}]}"""),
            "case-insensitive duplicate shelf");

        var oversizedPath = Path.Combine(
            corruptDirectory,
            "oversized.json");
        using (var stream = new FileStream(
                   oversizedPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(MaxFileBytes + 1);
        }

        var oversizedStore = new ShelfStore(oversizedPath);
        Throws<InvalidDataException>(
            () => oversizedStore.Read(),
            "over-limit state file is rejected");
        Throws<InvalidDataException>(
            () => oversizedStore.Create("Nope", 0),
            "over-limit state file cannot be overwritten");
        Assert(
            new FileInfo(oversizedPath).Length == MaxFileBytes + 1,
            "failed over-limit writes preserve the original file size");

        RunShelfCountLimitChecks(corruptDirectory);
        RunMemberCountLimitChecks(corruptDirectory);
        RunFileLimitWriteCheck(corruptDirectory);
    }

    private static void RunShelfCountLimitChecks(string directory)
    {
        var fullPath = Path.Combine(directory, "full-shelves.json");
        var fullShelves = Enumerable.Range(0, MaxShelfCount)
            .Select(index => new
            {
                name = "Shelf-" + index.ToString(
                    "D4",
                    CultureInfo.InvariantCulture),
                members = Array.Empty<string>()
            })
            .ToArray();
        WriteJson(
            fullPath,
            new
            {
                schema = 1,
                revision = 9,
                shelves = fullShelves
            });

        var store = new ShelfStore(fullPath);
        var snapshot = store.Read();
        var before = File.ReadAllBytes(fullPath);
        Throws<InvalidOperationException>(
            () => store.Create("Overflow", snapshot.Revision),
            "shelf count limit rejects an additional shelf");
        Assert(
            File.ReadAllBytes(fullPath).SequenceEqual(before) &&
            store.Read().Shelves.Count == MaxShelfCount,
            "shelf count limit failure preserves the full state file");

        var tooManyPath = Path.Combine(
            directory,
            "too-many-shelves.json");
        var tooManyShelves = Enumerable.Range(0, MaxShelfCount + 1)
            .Select(index => new
            {
                name = "S" + index.ToString(
                    CultureInfo.InvariantCulture),
                members = Array.Empty<string>()
            })
            .ToArray();
        WriteJson(
            tooManyPath,
            new
            {
                schema = 1,
                revision = 1,
                shelves = tooManyShelves
            });
        Throws<InvalidDataException>(
            () => new ShelfStore(tooManyPath).Read(),
            "stored shelf count above the limit is rejected");
    }

    private static void RunMemberCountLimitChecks(string directory)
    {
        var fullPath = Path.Combine(directory, "full-members.json");
        var fullMembers = Enumerable.Range(0, MaxMembersPerShelf)
            .Select(index => "member-" + index.ToString(
                "D5",
                CultureInfo.InvariantCulture))
            .ToArray();
        WriteJson(
            fullPath,
            new
            {
                schema = 1,
                revision = 4,
                shelves = new[]
                {
                    new
                    {
                        name = "Full",
                        members = fullMembers
                    }
                }
            });

        var store = new ShelfStore(fullPath);
        var snapshot = store.Read();
        var before = File.ReadAllBytes(fullPath);
        Throws<InvalidOperationException>(
            () => store.SetMember(
                "Full",
                "one-more",
                present: true,
                snapshot.Revision),
            "member count limit rejects an additional family key");
        Assert(
            File.ReadAllBytes(fullPath).SequenceEqual(before) &&
            store.Read().Shelves["Full"].Count == MaxMembersPerShelf,
            "member count limit failure preserves the full state file");

        var tooManyPath = Path.Combine(
            directory,
            "too-many-members.json");
        var tooManyMembers = fullMembers
            .Append("one-more")
            .ToArray();
        WriteJson(
            tooManyPath,
            new
            {
                schema = 1,
                revision = 1,
                shelves = new[]
                {
                    new
                    {
                        name = "Full",
                        members = tooManyMembers
                    }
                }
            });
        Throws<InvalidDataException>(
            () => new ShelfStore(tooManyPath).Read(),
            "stored member count above the limit is rejected");
    }

    private static void RunFileLimitWriteCheck(string directory)
    {
        var path = Path.Combine(directory, "near-file-limit.json");
        const int revision = 7;
        var emptyMembers = Array.Empty<string>();
        var emptyContent = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schema = 1,
                revision,
                shelves = new[]
                {
                    new
                    {
                        name = "Large",
                        members = emptyMembers
                    }
                }
            });

        const int encodedMemberWidth = MaxFamilyKeyLength + 3;
        var memberCount = checked((int)(
            (MaxFileBytes - emptyContent.LongLength + 1) /
            encodedMemberWidth));
        Check(
            memberCount > 0 &&
            memberCount < MaxMembersPerShelf,
            "near-limit state member count is inside the member limit");

        var members = Enumerable.Range(0, memberCount)
            .Select(CreateFixedWidthFamilyKey)
            .ToArray();
        var content = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schema = 1,
                revision,
                shelves = new[]
                {
                    new
                    {
                        name = "Large",
                        members
                    }
                }
            });
        Check(
            content.LongLength <= MaxFileBytes,
            "near-limit fixture is accepted by the file-size limit");
        Check(
            content.LongLength >
            MaxFileBytes - 2L * encodedMemberWidth,
            "near-limit fixture is within two fixed-width members of the limit");

        File.WriteAllBytes(path, content);
        var store = new ShelfStore(path);
        var snapshot = store.Read();
        Check(
            snapshot.Revision == revision &&
            snapshot.Shelves["Large"].Count == memberCount,
            "near-limit state loads completely");

        var rejected = false;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var before = File.ReadAllBytes(path);
            try
            {
                snapshot = store.SetMember(
                    "Large",
                    "family-over-file-limit-" +
                    attempt.ToString(CultureInfo.InvariantCulture),
                    present: true,
                    snapshot.Revision);
            }
            catch (InvalidDataException)
            {
                rejected = true;
                Assert(
                    File.ReadAllBytes(path).SequenceEqual(before),
                    "file-size limit failure preserves the preceding revision");
                break;
            }
        }

        Assert(
            rejected,
            "write that would exceed the file-size limit is rejected");
    }

    private static async Task RunAtomicWriteChecks(string root)
    {
        var directory = Path.Combine(root, "atomic");
        var path = Path.Combine(directory, "state.json");
        var store = new ShelfStore(path);
        var snapshot = store.Create("Atomic", 0);
        var revisionBeforeFailure = snapshot.Revision;

        using (var blocker = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var failure = Throws<Exception>(
                () => store.SetMember(
                    "Atomic",
                    "blocked-member",
                    present: true,
                    revisionBeforeFailure),
                "replacement failure is surfaced to the caller");
            Assert(
                failure is IOException or UnauthorizedAccessException,
                "replacement failure is an I/O or access error");
        }

        var afterFailure = store.Read();
        Assert(
            afterFailure.Revision == revisionBeforeFailure &&
            afterFailure.Shelves["Atomic"].Count == 0 &&
            !Directory.EnumerateFiles(
                    directory,
                    ".fontbook-collections-*.tmp")
                .Any(),
            "failed replacement leaves the old complete state and no temp file");

        const int memberCount = 40;
        var startRevision = afterFailure.Revision;
        using var writer = StartChild(
            "--append-members",
            path,
            startRevision.ToString(CultureInfo.InvariantCulture),
            memberCount.ToString(CultureInfo.InvariantCulture),
            "Atomic");
        var stdoutTask = writer.StandardOutput.ReadToEndAsync();
        var stderrTask = writer.StandardError.ReadToEndAsync();

        var lastRevision = startRevision;
        var lastCount = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!writer.HasExited)
        {
            if (DateTime.UtcNow > deadline)
            {
                writer.Kill(entireProcessTree: true);
                throw new CheckFailedException(
                    "ASSERT: concurrent writer exceeded 20 seconds");
            }

            var observed = store.Read();
            var observedCount = observed.Shelves["Atomic"].Count;
            Check(
                observed.Revision >= lastRevision &&
                observed.Revision <= startRevision + memberCount &&
                observedCount >= lastCount &&
                observedCount <= memberCount,
                "concurrent reads observe only monotonic, complete snapshots");
            lastRevision = observed.Revision;
            lastCount = observedCount;
            Thread.Sleep(2);
        }

        await writer.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert(
            writer.ExitCode == 0,
            "concurrent writer completed successfully " +
            $"(stdout: {stdout.Trim()}, stderr: {stderr.Trim()})");

        var final = store.Read();
        Assert(
            final.Revision == startRevision + memberCount &&
            final.Shelves["Atomic"].Count == memberCount &&
            !Directory.EnumerateFiles(
                    directory,
                    ".fontbook-collections-*.tmp")
                .Any(),
            "all atomic writes are visible as one complete final revision");
    }

    private static void AssertInvalidStateDoesNotOverwrite(
        string path,
        byte[] content,
        string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        var store = new ShelfStore(path);

        Throws<InvalidDataException>(
            () => store.Read(),
            $"{description} is rejected on read");
        Throws<InvalidDataException>(
            () => store.Create("MustNotOverwrite", 0),
            $"{description} is rejected before a write");
        Assert(
            File.ReadAllBytes(path).SequenceEqual(content),
            $"{description} leaves the original bytes untouched");
    }

    private static string CreateFixedWidthFamilyKey(int index)
    {
        var prefix = index.ToString(
            "D5",
            CultureInfo.InvariantCulture);
        return prefix +
               new string(
                   'x',
                   MaxFamilyKeyLength - prefix.Length);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(value));
    }

    private static int RunChildMode(string[] args)
    {
        try
        {
            return args[0] switch
            {
                "--attempt-create" => AttemptCreate(args),
                "--append-members" => AppendMembers(args),
                _ => throw new ArgumentException(
                    $"Unknown child mode '{args[0]}'.")
            };
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine(
                "CONFLICT: " + exception.Message);
            return ConflictExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int AttemptCreate(string[] args)
    {
        if (args.Length != 4)
        {
            throw new ArgumentException(
                "Expected path, revision, and shelf name.");
        }

        var revision = long.Parse(
            args[2],
            CultureInfo.InvariantCulture);
        var result = new ShelfStore(args[1])
            .Create(args[3], revision);
        Console.WriteLine(
            "CREATED revision " +
            result.Revision.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static int AppendMembers(string[] args)
    {
        if (args.Length != 5)
        {
            throw new ArgumentException(
                "Expected path, revision, count, and shelf name.");
        }

        var revision = long.Parse(
            args[2],
            CultureInfo.InvariantCulture);
        var count = int.Parse(
            args[3],
            CultureInfo.InvariantCulture);
        var store = new ShelfStore(args[1]);

        for (var index = 0; index < count; index++)
        {
            var member = "Concurrent-" + index.ToString(
                "D3",
                CultureInfo.InvariantCulture);
            revision = store.SetMember(
                args[4],
                member,
                present: true,
                revision).Revision;
        }

        Console.WriteLine(
            "APPENDED revision " +
            revision.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static async Task<ChildResult> RunChildAsync(
        params string[] args)
    {
        using var process = StartChild(args);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ChildResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static Process StartChild(params string[] args)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The current process path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(
                Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start the checks child process.");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new CheckFailedException(
                "ASSERT: " + description);
        }

        _checks++;
        Console.WriteLine("PASS: " + description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new CheckFailedException(
                "ASSERT: " + description);
        }

        _checks++;
    }

    private static TException Throws<TException>(
        Action action,
        string description)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            _checks++;
            Console.WriteLine("PASS: " + description);
            return exception;
        }
        catch (Exception exception)
        {
            throw new CheckFailedException(
                $"ASSERT: {description}. Expected {typeof(TException).Name}, " +
                $"but got {exception.GetType().Name}: {exception.Message}");
        }

        throw new CheckFailedException(
            $"ASSERT: {description}. Expected {typeof(TException).Name}, " +
            "but no exception was thrown.");
    }

    private static void Cleanup(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var testOutputRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory)) +
            Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullRoot.StartsWith(testOutputRoot, comparison) ||
            !Path.GetFileName(fullRoot).StartsWith(
                "FontBook-Collections-Checks-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to clean an unexpected test path: {fullRoot}");
        }

        try
        {
            Directory.Delete(fullRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed record ChildResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private sealed class CheckFailedException(string message)
        : Exception(message);
}
