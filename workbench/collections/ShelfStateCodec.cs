using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace FontBook.Workbench.Collections;

internal sealed record ParsedShelfState(
    long Revision,
    Dictionary<string, List<string>> Shelves);

internal static class ShelfStateCodec
{
    internal const int SchemaVersion = 1;
    internal const int MaxShelfNameLength = 128;
    internal const int MaxFamilyKeyLength = 512;
    internal const int MaxShelfCount = 1_000;
    internal const int MaxMembersPerShelf = 20_000;
    internal const long MaxFileBytes = 8L * 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Indented = false
    };

    public static byte[] Encode(ParsedShelfState state)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", SchemaVersion);
            writer.WriteNumber("revision", state.Revision);
            writer.WriteStartArray("shelves");

            foreach (var shelf in state.Shelves.OrderBy(
                         pair => pair.Key,
                         StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("name", shelf.Key);
                writer.WriteStartArray("members");

                foreach (var member in shelf.Value.OrderBy(
                             member => member,
                             StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteStringValue(member);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (stream.Length > MaxFileBytes)
        {
            throw new InvalidDataException(
                $"Collection state exceeds the {MaxFileBytes}-byte file limit.");
        }

        return stream.ToArray();
    }

    public static ParsedShelfState Decode(ReadOnlyMemory<byte> content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });

            var root = document.RootElement;
            var properties = GetExactProperties(
                root,
                "root",
                "schema",
                "revision",
                "shelves");

            if (!properties["schema"].TryGetInt32(out var schema))
            {
                throw Invalid("The schema value must be an integer.");
            }

            if (schema > SchemaVersion)
            {
                throw Invalid(
                    $"Future collection schema {schema} is not supported by this build.");
            }

            if (schema != SchemaVersion)
            {
                throw Invalid(
                    $"Unsupported collection schema {schema}; expected {SchemaVersion}.");
            }

            if (!properties["revision"].TryGetInt64(out var revision) ||
                revision < 0)
            {
                throw Invalid("The revision must be a non-negative integer.");
            }

            var shelvesElement = properties["shelves"];
            if (shelvesElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("The shelves value must be an array.");
            }

            if (shelvesElement.GetArrayLength() > MaxShelfCount)
            {
                throw Invalid(
                    $"Collection state exceeds the {MaxShelfCount}-shelf limit.");
            }

            var shelves = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var shelfElement in shelvesElement.EnumerateArray())
            {
                var shelfProperties = GetExactProperties(
                    shelfElement,
                    "shelf",
                    "name",
                    "members");

                var storedName = RequireString(
                    shelfProperties["name"],
                    "Shelf name");
                var name = NormalizeShelfName(storedName);
                if (!string.Equals(storedName, name, StringComparison.Ordinal))
                {
                    throw Invalid(
                        "Stored shelf names must already be trimmed.");
                }

                if (shelves.ContainsKey(name))
                {
                    throw Invalid(
                        $"Duplicate shelf name '{name}' (names are case-insensitive).");
                }

                var membersElement = shelfProperties["members"];
                if (membersElement.ValueKind != JsonValueKind.Array)
                {
                    throw Invalid(
                        $"The members for shelf '{name}' must be an array.");
                }

                if (membersElement.GetArrayLength() > MaxMembersPerShelf)
                {
                    throw Invalid(
                        $"Shelf '{name}' exceeds the {MaxMembersPerShelf}-member limit.");
                }

                var members = new List<string>(
                    membersElement.GetArrayLength());
                var memberKeys = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var memberElement in membersElement.EnumerateArray())
                {
                    var member = NormalizeFamilyKey(
                        RequireString(memberElement, "Family key"));
                    if (!memberKeys.Add(member))
                    {
                        throw Invalid(
                            $"Shelf '{name}' contains duplicate family key '{member}'.");
                    }

                    members.Add(member);
                }

                shelves.Add(name, members);
            }

            return new ParsedShelfState(revision, shelves);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Collection state contains invalid JSON.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Collection state contains an invalid shelf name or family key.",
                exception);
        }
    }

    public static string NormalizeShelfName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ValidateCharacters(name, "Shelf name");

        var normalized = name.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "Shelf name must not be empty.",
                nameof(name));
        }

        if (normalized.Length > MaxShelfNameLength)
        {
            throw new ArgumentException(
                $"Shelf name exceeds the {MaxShelfNameLength}-character limit.",
                nameof(name));
        }

        return normalized;
    }

    public static string NormalizeFamilyKey(string familyKey)
    {
        ArgumentNullException.ThrowIfNull(familyKey);
        ValidateCharacters(familyKey, "Family key");

        if (string.IsNullOrWhiteSpace(familyKey))
        {
            throw new ArgumentException(
                "Family key must not be empty.",
                nameof(familyKey));
        }

        if (familyKey.Length > MaxFamilyKeyLength)
        {
            throw new ArgumentException(
                $"Family key exceeds the {MaxFamilyKeyLength}-character limit.",
                nameof(familyKey));
        }

        return familyKey;
    }

    private static Dictionary<string, JsonElement> GetExactProperties(
        JsonElement element,
        string context,
        params string[] expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"The {context} value must be an object.");
        }

        var properties = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!expectedNames.Contains(
                    property.Name,
                    StringComparer.Ordinal))
            {
                throw Invalid(
                    $"Unexpected property '{property.Name}' in {context}.");
            }

            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw Invalid(
                    $"Duplicate property '{property.Name}' in {context}.");
            }
        }

        foreach (var expectedName in expectedNames)
        {
            if (!properties.ContainsKey(expectedName))
            {
                throw Invalid(
                    $"Missing property '{expectedName}' in {context}.");
            }
        }

        return properties;
    }

    private static string RequireString(
        JsonElement element,
        string description)
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw Invalid($"{description} must be a string.");
    }

    private static void ValidateCharacters(
        string value,
        string description)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length ||
                    !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new ArgumentException(
                        $"{description} contains an unpaired surrogate.",
                        description);
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character) ||
                char.IsControl(character))
            {
                throw new ArgumentException(
                    $"{description} contains a control character or unpaired surrogate.",
                    description);
            }
        }
    }

    private static InvalidDataException Invalid(string message) =>
        new(message);
}
