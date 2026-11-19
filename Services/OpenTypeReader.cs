using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FontBook.Services;

/// <summary>A validated face from a TrueType/OpenType font or collection.</summary>
public sealed class FaceInfo
{
    public int Index { get; init; }
    public string PsName { get; init; } = "";
    /// <summary>Effective family: name ID 16, falling back to ID 1.</summary>
    public string FamilyEn { get; init; } = "";
    public string FamilyZh { get; init; } = "";
    /// <summary>Effective subfamily: name ID 17, falling back to ID 2.</summary>
    public string SubFamilyEn { get; init; } = "";
    public string SubFamilyZh { get; init; } = "";
    /// <summary>Typographic family name ID 16 only.</summary>
    public string TypoFamilyEn { get; init; } = "";
    public string TypoFamilyZh { get; init; } = "";
    public string TypoSubFamilyEn { get; init; } = "";
    public string TypoSubFamilyZh { get; init; } = "";
    public int WeightClass { get; init; } = 400;
    public int WidthClass { get; init; } = 5;
    public bool IsItalic { get; init; }
    public int NumGlyphs { get; init; }
    /// <summary>All decoded family names (legacy and typographic, every supported language).</summary>
    public List<string> Aliases { get; init; } = new();
    /// <summary>All decoded subfamily names for search/disambiguation.</summary>
    public List<string> SubFamilyAliases { get; init; } = new();
    /// <summary>Resolved names keyed by BCP-47 language tag (empty for neutral).</summary>
    public Dictionary<string, string> FamilyNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LegacyFamilyNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TypoFamilyNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SubFamilyNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VarAxis> Axes { get; init; } = new();
    public bool IsVariable => Axes.Count > 0;
    public string DisplayFamily => PickDisplay(FamilyNames, FamilyZh, FamilyEn);
    public string DisplaySub => PickDisplay(SubFamilyNames, SubFamilyZh, SubFamilyEn);

    private static string PickDisplay(IReadOnlyDictionary<string, string> names, string zh, string en)
    {
        if (!string.IsNullOrWhiteSpace(zh)) return zh;
        if (!string.IsNullOrWhiteSpace(en)) return en;
        return names.OrderBy(p => p.Key.Length == 0 ? 0 : 1).Select(p => p.Value).FirstOrDefault() ?? "";
    }
}

public sealed record VarAxis(string Tag, float Min, float Default, float Max);

/// <summary>
/// Defensive sfnt/TTC metadata reader. All sfnt/TTC integers are big-endian;
/// table offsets in a TTC are absolute file offsets. Corrupt input is reported,
/// never converted into a successful empty parse.
/// </summary>
public static class OpenTypeReader
{
    private const int MaxFaces = 4096, MaxTables = 4096, MaxNameRecords = 8192, MaxNameBytes = 16 * 1024 * 1024, MaxAxes = 256;
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    private const int NameIdFamily = 1, NameIdSubFamily = 2, NameIdPostScript = 6;
    private const int NameIdTypographicFamily = 16, NameIdTypographicSubFamily = 17;
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, false, true);
    private static readonly Encoding MacRoman = CreateMacRomanEncoding();
    private static Encoding CreateMacRomanEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(10000, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static List<FaceInfo> ReadFaces(string path) => ReadFaces(path, CancellationToken.None);

    public static List<FaceInfo> ReadFaces(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length < 12 || stream.Length > MaxFileBytes) throw new InvalidDataException("Font file length is outside the accepted bounds.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        // A standalone TrueType signature is binary 0x00010000, not a printable table tag.
        var signature = ReadU32(reader);
        stream.Position = 0;
        var offsets = new List<uint>();
        if (signature == 0x74746366)
        {
            _ = ReadU32(reader);
            var version = ReadU32(reader);
            if (version is not (0x00010000 or 0x00020000)) throw new InvalidDataException("Unsupported TTC version.");
            var count = ReadU32(reader);
            if (count is 0 or > MaxFaces) throw new InvalidDataException("Invalid TTC face count.");
            RequireRange(stream, 12, checked((long)count * 4));
            for (var i = 0; i < count; i++) offsets.Add(ReadU32(reader));
            if (version == 0x00020000)
            {
                RequireRange(stream, stream.Position, 12);
                var tag = ReadU32(reader); var length = ReadU32(reader); var offset = ReadU32(reader);
                if (length == 0)
                {
                    if (tag != 0 || offset != 0) throw new InvalidDataException("Invalid empty TTC DSIG descriptor.");
                }
                else
                {
                    if (tag != 0x44534947) throw new InvalidDataException("Invalid TTC DSIG tag.");
                    RequireRange(stream, offset, length);
                }
            }
        }
        else offsets.Add(0);

        var faces = new List<FaceInfo>(offsets.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            faces.Add(ReadOneFace(reader, offsets[i], i));
        }
        return faces;
    }

    private static FaceInfo ReadOneFace(BinaryReader br, uint faceOffset, int index)
    {
        var fs = br.BaseStream;
        RequireRange(fs, faceOffset, 12);
        fs.Position = faceOffset;
        var sfnt = ReadU32(br);
        if (sfnt is not (0x00010000 or 0x4F54544F or 0x74727565 or 0x74797031))
            throw new InvalidDataException($"Invalid sfnt signature for face {index}.");
        var tableCount = ReadU16(br);
        _ = ReadU16(br); _ = ReadU16(br); _ = ReadU16(br);
        if (tableCount == 0 || tableCount > MaxTables) throw new InvalidDataException("Invalid sfnt table count.");
        RequireRange(fs, fs.Position, checked((long)tableCount * 16));
        var tables = new Dictionary<string, (uint Offset, uint Length)>(StringComparer.Ordinal);
        for (var t = 0; t < tableCount; t++)
        {
            var tag = ReadTag(br); _ = ReadU32(br);
            var off = ReadU32(br); var len = ReadU32(br);
            RequireRange(fs, off, len);
            if (!tables.TryAdd(tag, (off, len))) throw new InvalidDataException($"Duplicate table '{tag}'.");
        }

        var names = ReadNames(br, tables);
        string Pick(int preferred, int fallback, string lang)
        {
            string Find(int id)
            {
                if (names.TryGetValue((lang, id), out var exact) && exact.Length > 0) return exact;
                var prefix = lang + "-";
                return names.Where(p => p.Key.Id == id && p.Key.Lang.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Key.Lang, StringComparer.OrdinalIgnoreCase).Select(p => p.Value).FirstOrDefault(v => v.Length > 0) ?? "";
            }
            var preferredValue = Find(preferred);
            return preferredValue.Length > 0 ? preferredValue : Find(fallback);
        }
        var legacyFamilyNames = names.Where(p => p.Key.Id == NameIdFamily).ToDictionary(p => p.Key.Lang, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var familyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var typoFamilyNames = names.Where(p => p.Key.Id == NameIdTypographicFamily).ToDictionary(p => p.Key.Lang, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var subNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in names.Keys.Select(k => k.Lang).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var family = Pick(NameIdTypographicFamily, NameIdFamily, lang);
            var sub = Pick(NameIdTypographicSubFamily, NameIdSubFamily, lang);
            if (family.Length > 0) familyNames[lang] = family;
            if (sub.Length > 0) subNames[lang] = sub;
        }
        var familyAliases = names.Where(p => p.Key.Id is NameIdFamily or NameIdTypographicFamily)
            .Select(p => p.Value).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var subAliases = names.Where(p => p.Key.Id is NameIdSubFamily or NameIdTypographicSubFamily)
            .Select(p => p.Value).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string ByLanguage(int id, string language)
        {
            if (names.TryGetValue((language, id), out var exact)) return exact;
            return names.Where(p => p.Key.Id == id && p.Key.Lang.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Key.Lang.Length).ThenBy(p => p.Key.Lang, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Value).FirstOrDefault() ?? "";
        }
        var typoEn = ByLanguage(NameIdTypographicFamily, "en");
        var typoZh = ByLanguage(NameIdTypographicFamily, "zh-Hans");
        if (typoZh.Length == 0) typoZh = ByLanguage(NameIdTypographicFamily, "zh-Hant");
        var typoSubEn = ByLanguage(NameIdTypographicSubFamily, "en");
        var typoSubZh = ByLanguage(NameIdTypographicSubFamily, "zh-Hans");
        if (typoSubZh.Length == 0) typoSubZh = ByLanguage(NameIdTypographicSubFamily, "zh-Hant");
        var familyEn = Pick(NameIdTypographicFamily, NameIdFamily, "en");
        var familyZh = Pick(NameIdTypographicFamily, NameIdFamily, "zh-Hans");
        if (familyZh.Length == 0) familyZh = Pick(NameIdTypographicFamily, NameIdFamily, "zh-Hant");
        var subEn = Pick(NameIdTypographicSubFamily, NameIdSubFamily, "en");
        var subZh = Pick(NameIdTypographicSubFamily, NameIdSubFamily, "zh-Hans");
        if (subZh.Length == 0) subZh = Pick(NameIdTypographicSubFamily, NameIdSubFamily, "zh-Hant");
        var ps = names.GetValueOrDefault(("en", NameIdPostScript), "");
        if (ps.Length == 0) ps = names.Where(p => p.Key.Id == NameIdPostScript).Select(p => p.Value).FirstOrDefault() ?? "";

        var weight = 400; var width = 5; var italic = false;
        if (tables.TryGetValue("OS/2", out var os2))
        {
            if (os2.Length < 64) throw new InvalidDataException("Truncated OS/2 table.");
            fs.Position = os2.Offset + 4; weight = ReadU16(br); width = ReadU16(br);
            fs.Position = os2.Offset + 62; var selection = ReadU16(br); italic = (selection & 1) != 0;
            if (weight is < 1 or > 1000) weight = 400;
            if (width is < 1 or > 9) width = 5;
        }
        var glyphs = 0;
        if (tables.TryGetValue("maxp", out var maxp))
        {
            if (maxp.Length < 6) throw new InvalidDataException("Truncated maxp table.");
            fs.Position = maxp.Offset + 4; glyphs = ReadU16(br);
        }
        var axes = new List<VarAxis>();
        if (tables.TryGetValue("fvar", out var fvar))
        {
            if (fvar.Length < 16) throw new InvalidDataException("Truncated fvar header.");
            fs.Position = fvar.Offset;
            var major = ReadU16(br); var minor = ReadU16(br);
            if (major != 1) throw new InvalidDataException("Unsupported fvar version.");
            var axesOffset = ReadU16(br); var reserved = ReadU16(br); var axisCount = ReadU16(br); var axisSize = ReadU16(br);
            var instanceCount = ReadU16(br); var instanceSize = ReadU16(br);
            if (reserved != 2 || axisCount > MaxAxes || (axisCount > 0 && axisSize < 20) || (instanceCount > 0 && instanceSize < 4 + axisCount * 4))
                throw new InvalidDataException("Invalid fvar array bounds.");
            var axisBytes = checked((long)axisCount * axisSize);
            var instanceStart = checked((long)axesOffset + axisBytes);
            var instanceBytes = checked((long)instanceCount * instanceSize);
            if (axesOffset < 16 || instanceStart > fvar.Length || instanceBytes > fvar.Length - instanceStart)
                throw new InvalidDataException("fvar axis/instance arrays exceed table bounds.");
            RequireRange(fs, fvar.Offset + axesOffset, axisBytes);
            RequireRange(fs, fvar.Offset + instanceStart, instanceBytes);
            fs.Position = fvar.Offset + axesOffset;
            for (var a = 0; a < axisCount; a++)
            {
                var axisStart = fs.Position;
                var tag = ReadTag(br); var min = ReadFixed(br); var def = ReadFixed(br); var max = ReadFixed(br);
                _ = ReadU16(br); _ = ReadU16(br);
                if (!float.IsFinite(min) || !float.IsFinite(def) || !float.IsFinite(max) || min > def || def > max)
                    throw new InvalidDataException("Invalid fvar axis coordinates.");
                axes.Add(new VarAxis(tag, min, def, max));
                fs.Position = checked(axisStart + axisSize);
            }
            _ = minor;
        }
        return new FaceInfo
        {
            Index = index, PsName = ps, FamilyEn = familyEn, FamilyZh = familyZh, SubFamilyEn = subEn, SubFamilyZh = subZh,
            TypoFamilyEn = typoEn, TypoFamilyZh = typoZh, TypoSubFamilyEn = typoSubEn, TypoSubFamilyZh = typoSubZh,
            WeightClass = weight, WidthClass = width, IsItalic = italic, NumGlyphs = glyphs,
            Aliases = familyAliases, SubFamilyAliases = subAliases, FamilyNames = familyNames,
            LegacyFamilyNames = legacyFamilyNames, TypoFamilyNames = typoFamilyNames, SubFamilyNames = subNames, Axes = axes
        };
    }

    private static Dictionary<(string Lang, int Id), string> ReadNames(BinaryReader br, Dictionary<string, (uint Offset, uint Length)> tables)
    {
        var result = new Dictionary<(string, int), string>();
        if (!tables.TryGetValue("name", out var table)) return result;
        if (table.Length < 6) throw new InvalidDataException("Truncated name table.");
        var fs = br.BaseStream; fs.Position = table.Offset;
        var format = ReadU16(br); var count = ReadU16(br); var stringOffset = ReadU16(br);
        if (format > 1 || count > MaxNameRecords || 6L + (long)count * 12 > table.Length)
            throw new InvalidDataException("Invalid name table record array.");
        var recordsEnd = 6L + (long)count * 12;
        if (stringOffset < recordsEnd || stringOffset > table.Length) throw new InvalidDataException("Invalid name string-storage offset.");
        var records = new List<(ushort Platform, ushort Encoding, ushort Language, ushort Id, ushort Length, ushort Offset)>();
        for (var i = 0; i < count; i++) records.Add((ReadU16(br), ReadU16(br), ReadU16(br), ReadU16(br), ReadU16(br), ReadU16(br)));
        long totalStringBytes = 0;
        var tagLanguages = new List<string>();
        if (format == 1)
        {
            if (recordsEnd + 2 > stringOffset) throw new InvalidDataException("Truncated name language-tag count.");
            var langCount = ReadU16(br);
            var languageRecordsEnd = 6L + (long)count * 12 + 2L + (long)langCount * 4;
            if (langCount > MaxNameRecords || languageRecordsEnd > table.Length || languageRecordsEnd > stringOffset)
                throw new InvalidDataException("Invalid name language-tag array.");
            for (var i = 0; i < langCount; i++)
            {
                var len = ReadU16(br); var off = ReadU16(br);
                totalStringBytes += len;
                if (totalStringBytes > MaxNameBytes) throw new InvalidDataException("Name strings exceed the safety limit.");
                var nextRecord = fs.Position;
                tagLanguages.Add(DecodeNameString(fs, table, stringOffset, off, len, 0, 0));
                fs.Position = nextRecord;
            }
        }
        foreach (var r in records)
        {
            if (r.Id is not (NameIdFamily or NameIdSubFamily or NameIdTypographicFamily or NameIdTypographicSubFamily or NameIdPostScript)) continue;
            totalStringBytes += r.Length;
            if (totalStringBytes > MaxNameBytes) throw new InvalidDataException("Name table strings exceed the safety limit.");
            var lang = r.Platform == 3 ? WindowsLanguage(r.Language) : r.Platform == 0 ? "" : r.Platform == 1 ? MacLanguage(r.Language) : "";
            if (r.Platform is 0 or 3 && format == 1 && r.Language >= 0x8000)
            {
                var tagIndex = r.Language - 0x8000;
                if (tagIndex >= tagLanguages.Count) throw new InvalidDataException("Name record refers to a missing language tag.");
                lang = tagLanguages[tagIndex];
            }
            var text = DecodeNameString(fs, table, stringOffset, r.Offset, r.Length, r.Platform, r.Encoding).Trim('\0', ' ', '\t', '\r', '\n');
            if (text.Length == 0) continue;
            if (!result.ContainsKey((lang, r.Id)) || (lang.Length > 0 && r.Platform == 3)) result[(lang, r.Id)] = text;
        }
        return result;
    }

    private static string DecodeNameString(Stream fs, (uint Offset, uint Length) table, ushort storageOffset, ushort offset, ushort length, ushort platform, ushort encoding)
    {
        if ((long)storageOffset + offset + length > table.Length) throw new InvalidDataException("Name string lies outside its table.");
        if (length == 0) return "";
        if (platform == 0 || (platform == 3 && encoding is 0 or 1 or 10))
        {
            if ((length & 1) != 0) throw new InvalidDataException("Odd byte length in UTF-16 name record.");
            var bytes = new byte[length]; fs.Position = table.Offset + storageOffset + offset; ReadExactly(fs, bytes);
            return StrictUtf16Be.GetString(bytes);
        }
        var codePage = platform switch
        {
            1 => encoding switch { 0 => 10000, 1 => 10001, 2 => 10002, 3 => 10003, 4 => 10004,
                5 => 10005, 6 => 10006, 7 => 10007, 21 => 10021, 25 => 10008, _ => 0 },
            3 => encoding switch { 2 => 932, 3 => 936, 4 => 950, 5 => 949, 6 => 1361, _ => 0 },
            _ => 0
        };
        if (codePage != 0)
        {
            var bytes = new byte[length]; fs.Position = table.Offset + storageOffset + offset; ReadExactly(fs, bytes);
            try
            {
                var decoder = codePage == 10000 ? MacRoman : Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return decoder.GetString(bytes);
            }
            catch (ArgumentException) { return ""; }
        }
        return "";
    }

    private static string WindowsLanguage(ushort lcid)
    {
        if (lcid is 0x0804 or 0x1004) return "zh-Hans";
        if (lcid is 0x0404 or 0x0c04 or 0x1404) return "zh-Hant";
        try { return CultureInfo.GetCultureInfo(lcid).Name; }
        catch (CultureNotFoundException) { return "x-lcid-" + lcid.ToString("X4", CultureInfo.InvariantCulture); }
    }

    private static string MacLanguage(ushort id) => id switch
    {
        0 => "en", 1 => "fr", 2 => "de", 3 => "it", 4 => "nl", 5 => "sv", 6 => "es",
        11 => "ja", 19 => "zh-Hant", 33 => "zh-Hans", 23 => "ko",
        _ => "x-mac-" + id.ToString("X4", CultureInfo.InvariantCulture)
    };
    private static string ReadTag(BinaryReader br)
    {
        var bytes = new byte[4]; ReadExactly(br.BaseStream, bytes);
        if (bytes.Any(b => b is < 0x20 or > 0x7e)) throw new InvalidDataException("Invalid sfnt tag.");
        return Encoding.ASCII.GetString(bytes);
    }
    private static ushort ReadU16(BinaryReader br) { Span<byte> b = stackalloc byte[2]; ReadExactly(br.BaseStream, b); return BinaryPrimitives.ReadUInt16BigEndian(b); }
    private static uint ReadU32(BinaryReader br) { Span<byte> b = stackalloc byte[4]; ReadExactly(br.BaseStream, b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    private static float ReadFixed(BinaryReader br) => unchecked((int)ReadU32(br)) / 65536f;
    private static void RequireRange(Stream s, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > s.Length || length > s.Length - offset) throw new InvalidDataException("Font table offset/length is out of file bounds.");
    }
    private static void ReadExactly(Stream s, Span<byte> bytes)
    {
        var read = 0;
        while (read < bytes.Length) { var n = s.Read(bytes[read..]); if (n == 0) throw new EndOfStreamException(); read += n; }
    }
}
