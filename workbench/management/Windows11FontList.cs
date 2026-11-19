using System.Collections.Frozen;
using System.Text;

namespace FontBook.Management;

/// <summary>Exact classification against the bundled Microsoft Windows 11 font list, not a deletion or installation check.</summary>
public static class Windows11FontList
{
    private static readonly (FrozenSet<string> Families, FrozenSet<string> Files) Index = Load();

    /// <summary>Matches a documented family or an explicitly verified family-name alias, ignoring case only.</summary>
    public static bool MatchesFamily(string? familyOrEnglishKey) =>
        familyOrEnglishKey is not null && Index.Families.Contains(familyOrEnglishKey);

    /// <summary>Matches only the basename of a Windows path or filename, without probing the filesystem.</summary>
    /// <remarks>A match does not establish the file's contents, location, or origin.</remarks>
    public static bool MatchesFile(string? pathOrFileName) =>
        pathOrFileName is not null && Index.Files.GetAlternateLookup<ReadOnlySpan<char>>()
            .Contains(Path.GetFileName(pathOrFileName.AsSpan()));

    private static (FrozenSet<string> Families, FrozenSet<string> Files) Load()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadRows("Windows11Fonts.tsv", "section\tfamily\tfont_name\tfile_names\tversion", 5))
        {
            families.Add(row[1]);
            foreach (string file in row[3].Split(", ", StringSplitOptions.RemoveEmptyEntries))
                files.Add(file);
        }

        var aliases = new HashSet<string>(families, families.Comparer);
        foreach (var row in ReadRows("Windows11FontAliases.tsv", "kind\tlisted_family\talias\tevidence", 4))
        {
            if (!families.Contains(row[1]) || row[2].Length == 0 || row[3].Length == 0)
                throw new InvalidDataException($"Unanchored Windows 11 font alias: {row[2]}");
            if (row[0] == "family")
                aliases.Add(row[2]);
            else if (row[0] == "file")
                files.Add(row[2]);
            else
                throw new InvalidDataException($"Unrecognized Windows 11 font alias kind: {row[0]}");
        }

        return (aliases.ToFrozenSet(aliases.Comparer), files.ToFrozenSet(files.Comparer));
    }

    private static IEnumerable<string[]> ReadRows(string resourceName, string header, int columns)
    {
        using var stream = typeof(Windows11FontList).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Missing Windows 11 font resource: {resourceName}");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        string? line;
        do { line = reader.ReadLine(); } while (line is not null && line.StartsWith('#'));
        if (line != header)
            throw new InvalidDataException($"Unexpected Windows 11 font resource header: {resourceName}");

        while ((line = reader.ReadLine()) is not null)
        {
            var fields = line.Split('\t');
            if (fields.Length != columns || fields[0].Length == 0 || fields[1].Length == 0)
                throw new InvalidDataException($"Invalid Windows 11 font resource row in {resourceName}: {line}");
            yield return fields;
        }
    }
}
