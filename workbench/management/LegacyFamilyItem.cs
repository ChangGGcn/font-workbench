namespace FontBook.Models;

// Only the fields used by the linked transaction code. The legacy WPF FamilyItem
// (and its renderer) are deliberately not referenced by the management library.
public sealed class FamilyItem
{
    public string Name { get; init; } = "";
    public string NameEn { get; init; } = "";
    public string NameZh { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyList<string> Aliases { get; init; } = [];
}
