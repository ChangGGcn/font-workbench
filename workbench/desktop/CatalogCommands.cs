using System.Diagnostics;
using System.Text.Json;

namespace FontBook.Workbench;

internal static class CatalogCommands
{
    // No CLI switch can modify fonts, registrations, shelves or existing recovery records.
    public static int Run(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("--check-uninstall <exact-family-name> (read-only) | --stats | --source-stats | --bench | --find <family-or-alias> (top 30) | --inspect <local-font-file> | --coverage <local-file> <physical-face-index> <sample> | --details <local-file> <physical-face-index>");
            return 0;
        }
        if (args is not (["--stats"] or ["--source-stats"] or ["--bench"] or ["--check-uninstall", _] or ["--find", _] or ["--inspect", _] or ["--coverage", _, _, _] or ["--details", _, _]))
        {
            Console.Error.WriteLine("Invalid arguments; use --help.");
            return 2;
        }
        try
        {
            if (args[0] == "--details")
                return InspectFace(args[1], args[2], null);
            if (args[0] == "--coverage")
                return InspectFace(args[1], args[2], args[3]);
            using var c = FontCatalog.Open(CancellationToken.None, args[0] == "--inspect" ? [System.IO.Path.GetFullPath(args[1])] : null);
            if (args[0] == "--check-uninstall")
                return CheckUninstall(c, args[1]);
            var s = c.Stats;
            if (args[0] == "--stats")
                Write(new
                {
                    s.Families,
                    s.Styles,
                    s.Skipped,
                    scan_ms = c.ElapsedMs
                });
            else if (args[0] == "--source-stats")
            {
                var timer = Stopwatch.StartNew();
                int unavailable = 0, refs = 0;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in c.Rows)
                    try
                    {
                        foreach (var source in c.GetSources(row.Id))
                        {
                            refs++;
                            paths.Add(source.Path);
                        }
                    }
                    catch (InvalidOperationException) { unavailable++; }
                Write(new
                {
                    families = s.Families,
                    unavailable,
                    source_references = refs,
                    distinct_files = paths.Count,
                    ms = timer.ElapsedMilliseconds
                });
                return unavailable == 0 ? 0 : 1;
            }
            else if (args[0] is "--find" or "--inspect")
            {
                var rows = c.Search(args[0] == "--inspect" ? string.Empty : args[1], null, CancellationToken.None);
                Write(new
                {
                    count = rows.Length,
                    results = rows.Take(30).Select(r => new { r.Name, r.Styles, windows11SystemFont = r.IsWindowsSystemFont })
                });
            }
            else
            {
                if (c.Rows.Length == 0)
                    throw new InvalidOperationException("No installed families to measure.");
                // Warm up interop & JIT independently of the measured sample. Do not publish names.
                _ = c.Search(c.Rows[0].Name, null, CancellationToken.None);
                double[] samples = new double[128];
                int hits = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    var row = c.Rows[(int)((long)i * c.Rows.Length / samples.Length)];
                    string q = row.Name[..Math.Min(4, row.Name.Length)];
                    long start = Stopwatch.GetTimestamp();
                    var found = c.Search(q, null, CancellationToken.None);
                    samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (found.Any(r => r.Id == row.Id))
                        hits++;
                }
                Array.Sort(samples);
                Write(new
                {
                    s.Families,
                    s.Styles,
                    s.Skipped,
                    scan_ms = c.ElapsedMs,
                    queries = samples.Length,
                    hits,
                    query_p50_ms = Math.Round(samples[63], 3),
                    query_p95_ms = Math.Round(samples[121], 3),
                    working_set_mib = Math.Round(Environment.WorkingSet / 1048576.0, 1),
                    scope = "local installed catalog + managed result materialization; no UI, no cache flushing"
                });
                if (hits != samples.Length)
                    return 1;
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    // Nothing on this path launches a process, signs a journal, or executes a plan.
    private static int CheckUninstall(FontCatalog catalog, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1024)
        {
            Console.Error.WriteLine("Expected exact family name.");
            return 2;
        }
        var rows = catalog.Rows.Where(r => string.Equals(r.Key, query.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Name, query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rows.Length != 1)
        {
            Write(new
            {
                status = rows.Length == 0 ? "not_found" : "ambiguous",
                matches = rows.Length
            });
            return 2;
        }
        var inventory = catalog.ReadManagementInventory(CancellationToken.None);
        var plan = FontBook.Management.ManagementService.CreateWindows().Preflight(FontBook.Management.ManagementAction.Uninstall, inventory, [rows[0].Id]);
        var check = plan.Items.Single();
        Write(new
        {
            family = rows[0].Name,
            windows11SystemFont = check.IsWindowsSystemFont,
            status = check.CanExecute ? "ready" : check.CanRequestElevation ? "requires_elevation" : "blocked",
            fileCount = check.Files.Count,
            permission = check.Permission,
            reason = check.Message
        });
        return check.CanExecute ? 0 : check.CanRequestElevation ? 3 : 4;
    }
    private static int InspectFace(string path, string index, string? sample)
    {
        if (!uint.TryParse(index, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint wanted) || sample?.Length > 2048)
        {
            Console.Error.WriteLine("Expected nonnegative physical face index and sample of at most 2048 UTF-16 units.");
            return 2;
        }
        string full = System.IO.Path.GetFullPath(path);
        using var catalog = FontCatalog.Open(CancellationToken.None, [full]);
        foreach (var family in catalog.Rows)
            foreach (var style in catalog.GetStyles(family.Id))
            {
                using var face = catalog.Select(family.Id, style.Index);
                if (face.FaceIndex != wanted || !string.Equals(face.Path, full, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (sample is null)
                {
                    var d = face.ReadDetails();
                    Write(new
                    {
                        face = d.FaceIndex,
                        names = d.Names,
                        outline = d.Outline.ToString(),
                        metrics = d.Metrics,
                        weight = d.Weight,
                        stretch = d.Stretch,
                        style = d.Style,
                        axes = d.Axes.Select(a => new { tag = a.Label, minimum = a.Minimum, initial = a.Initial, maximum = a.Maximum }),
                        features = d.Features.Select(feature => new { tag = feature.Tag, tables = feature.Tables.ToString() })
                    });
                    return 0;
                }
                var result = face.AnalyzeSample(sample);
                Write(new
                {
                    family = family.Name,
                    face = wanted,
                    unique = result.Unique,
                    mapped = result.Mapped,
                    occurrences = result.Occurrences,
                    ignored = result.Ignored,
                    invalid = result.Invalid,
                    missing = result.Missing.Select(m => new { code = m.Code, count = m.Occurrences })
                });
                return 0;
            }
        Console.Error.WriteLine("Physical face index not found in this file.");
        return 1;
    }
    private static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) }));
}
