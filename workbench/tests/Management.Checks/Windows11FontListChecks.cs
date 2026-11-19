using System.Security.Cryptography;
using System.Text;
using FontBook.Management;

internal static class Windows11FontListChecks
{
    internal static void Run()
    {
        CheckSnapshot();
        CheckExactMatches();
        Console.WriteLine("PASS: Windows 11 font snapshot completeness, aliases, and exact offline classification");
    }

    private static void CheckSnapshot()
    {
        var snapshot = ReadResource("Windows11Fonts.tsv");
        int header = Array.IndexOf(snapshot, "section\tfamily\tfont_name\tfile_names\tversion");
        Require(header >= 0, "missing snapshot header");
        Require(snapshot.Contains("# ms.date: 2022-04-21T00:00:00Z") &&
                snapshot.Contains("# updated_at: 2022-06-16T19:06:00Z"), "snapshot dates changed");

        var sourceRows = snapshot[(header + 1)..];
        // Pinned against all rows of the en-US Learn page, including non-indexed face names and versions.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', sourceRows) + "\n")));
        Require(digest == "D0C59F025AE2115C3F17CC05D993C50B5549F752B8E85F9F6741D7B5F1BFD30F", "source rows changed");
        Require(snapshot.Contains("# rows_sha256_utf8_lf: " + digest.ToLowerInvariant()), "snapshot digest header is stale");

        var rows = sourceRows.Select(row => row.Split('\t')).ToArray();
        var sections = rows.GroupBy(row => row[0]).ToDictionary(group => group.Key, group => group.Count());
        Require(rows.Length == 504 && sections.Count == 25 && sections["system"] == 233 &&
                sections.Where(pair => pair.Key != "system").All(pair => pair.Key.EndsWith("-supplemental-fonts", StringComparison.Ordinal)),
                "expected one 233-row system table and 24 supplemental tables");
        Require(snapshot.Contains("# section_rows: " + string.Join("; ", sections.Select(pair => $"{pair.Key}={pair.Value}"))),
                "per-table source counts do not match their rows");

        var families = rows.Select(row => row[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = rows.SelectMany(row => row[3].Split(", ", StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(families.Count == 141 && files.Count == 362, "source family/file count changed");
        foreach (string family in families)
            Require(Windows11FontList.MatchesFamily(family.ToUpperInvariant()), $"missed source family: {family}");
        foreach (string file in files)
            Require(Windows11FontList.MatchesFile(file.ToUpperInvariant()), $"missed source file: {file}");

        var aliases = ReadResource("Windows11FontAliases.tsv");
        int aliasHeader = Array.IndexOf(aliases, "kind\tlisted_family\talias\tevidence");
        Require(aliasHeader >= 0, "missing alias header");
        var familyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in aliases[(aliasHeader + 1)..])
        {
            var row = line.Split('\t');
            Require(row.Length == 4 && families.Contains(row[1]) && row[3].Length != 0, $"invalid alias row: {line}");
            Require(row[0] switch
            {
                "family" => !families.Contains(row[2]) && familyAliases.Add(row[2]) && Windows11FontList.MatchesFamily(row[2]),
                "file" => !files.Contains(row[2]) && fileAliases.Add(row[2]) && Windows11FontList.MatchesFile(row[2]),
                _ => false,
            }, $"duplicate alias or not indexed in its declared kind: {row[2]}");
        }
        Require(familyAliases.Count == 121 && fileAliases.Count == 2, "reviewed alias counts changed");
    }

    private static void CheckExactMatches()
    {
        string[] exactFamilies = [
            "aRiAl", "Calibri Light", "Aharoni", "Aharoni Bold", "Microsoft YaHei UI", "\u5fae\u8f6f\u96c5\u9ed1",
            "\u5fae\u8edf\u6b63\u9ed1\u9ad4", "\u65b0\u5b8b\u4f53", "\u65b0\u7d30\u660e\u9ad4", "pmingliu-extb", "MS PGothic",
            "\uff2d\uff33 \u30b4\u30b7\u30c3\u30af", "Yu Gothic UI", "BIZ UDPMincho", "UD Digi Kyokasho NK", "\ub9d1\uc740 \uace0\ub515",
            "DotumChe", "Sitka Small", "Segoe UI Variable Text", "LilyUPC",
        ];
        foreach (string family in exactFamilies)
            Require(Windows11FontList.MatchesFamily(family), $"expected family: {family}");

        string?[] notFamilies = [
            null, "", " ", " Arial", "Arial ", "Arial Bold", "Arial Narrow", "Aptos", "Cambria  Math", "Times New Roman Pro",
            "Arial, Calibri", "Segoe UI Variable Display Bold", "Calibri\u00a0Light", "MS \u30b4\u30b7\u30c3\u30af", "\u5b8b\u4f53 Plus",
            "Cascadia Code PL", "SimSun-ExtG", "Wingdings 2", "Arial.ttf", "BIZ UD",
        ];
        foreach (string? family in notFamilies)
            Require(!Windows11FontList.MatchesFamily(family), $"unexpected family: {family ?? "<null>"}");

        string[] exactFiles = [
            "CAMBRIA.TTC", "SegUIVar.ttf", @"C:\DoesNotExist\Other\aRiAl.TTF", "C:/Assumed/Fonts/simsun.ttc",
            @"\\FixtureServer\Share\Browalia.ttc", "CascadiaMono Italic.ttf", "CascadiaCode Italic.ttf", "CascadiaMonoItalic.ttf",
            "CascadiaCodeItalic.ttf", "MSJHBD.TTC", "UPCLBI.TTF",
        ];
        foreach (string file in exactFiles)
            Require(Windows11FontList.MatchesFile(file), $"expected file: {file}");

        string?[] notFiles = [
            null, "", " ", "Arial", " Arial.ttf", "Arial.ttf ", "Arial.otf", "myarial.ttf", "arial.ttf.bak", "Arial.ttf:stream",
            @"C:\Font\Arial.ttf\", @"C:\Arial.ttf\custom.ttf", "Arial.ttf, Calibri.ttf", "*arial.ttf", "Browallia.ttc",
            "Aptos.ttf", "arialn.ttf", "CascadiaCodePL.ttf", "simsunextg.ttf", "wingdng2.ttf",
        ];
        foreach (string? file in notFiles)
            Require(!Windows11FontList.MatchesFile(file), $"unexpected file: {file ?? "<null>"}");
    }

    private static string[] ReadResource(string name)
    {
        using var stream = typeof(Windows11FontList).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing expected Windows 11 font resource: {name}");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    private static void Require(bool value, string detail)
    {
        if (!value)
            throw new InvalidOperationException("Windows 11 font list: " + detail);
    }
}
