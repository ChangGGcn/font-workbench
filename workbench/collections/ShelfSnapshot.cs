using System.Collections.ObjectModel;

namespace FontBook.Workbench.Collections;

/// <summary>
/// An immutable view of the collection state at one revision.
/// </summary>
public record ShelfSnapshot(
    long Revision,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Shelves);

internal static class ShelfSnapshotFactory
{
    public static ShelfSnapshot Create(
        long revision,
        IReadOnlyDictionary<string, List<string>> shelves)
    {
        var immutableShelves = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var shelf in shelves.OrderBy(
                     pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            var members = shelf.Value
                .OrderBy(member => member, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            immutableShelves.Add(
                shelf.Key,
                new ReadOnlyCollection<string>(members));
        }

        return new ShelfSnapshot(
            revision,
            new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                immutableShelves));
    }
}
