using FontBook.Management;

internal static class AdminReviewChecks
{
    internal static void Run()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var a = new ManagementFamily(4, "甲", "A", [new(@"C:\PrivateFixture\a.ttc", 1), new(@"C:\PrivateFixture\a.ttc", 0)]);
        var b = new ManagementFamily(31, "乙", "B", [new(@"C:\PrivateFixture\b.ttf", 0)]);
        var inventory = new ManagementInventory([a, b]);
        var request = AdminReviewSelection.Create("uninstall", sid, inventory, [31, 4, 4]);
        var args = request.Arguments();
        Require(AdminReviewSelection.TryParse(args, out var copy) && copy!.Ids.SequenceEqual(new uint[] { 4, 31 }) && copy.Matches(sid, inventory), "multi-selection round trip");
        Require(!args.Any(arg => arg.Contains("PrivateFixture") || arg.Contains(".ttc")), "no executable, path or serialised plan crosses the consent boundary");
        Require(!copy!.Matches("S-1-5-21-1-2-3-1002", inventory), "different user rejected");
        Require(!copy.Matches(sid, new([a, b with { Key = "Different" }])) &&
                !copy.Matches(sid, new([a, b with { Faces = [new(@"C:\PrivateFixture\c.ttf", 0)] }])) &&
                !copy.Matches(sid, new([a, b with { Faces = [new(@"C:\PrivateFixture\b.ttf", 1)] }])), "changed family, physical path or TTC face cannot be replayed");
        var modified = (string[])args.Clone();
        modified[1] = "disable";
        Require(AdminReviewSelection.TryParse(modified, out var different) && !different!.Matches(sid, inventory), "digest includes action");
        foreach (var mask in new[] { "", "-", "AA==", "!", new string('A', 20000) })
        {
            var bad = (string[])args.Clone();
            bad[3] = mask;
            Require(!AdminReviewSelection.TryParse(bad, out _), "invalid mask rejected");
        }
        var history = AdminReviewSelection.Create("history", sid, null, []);
        Require(AdminReviewSelection.TryParse(history.Arguments(), out var h) && h!.Ids.Count == 0 && h.Matches(sid, null), "history bookmark contains no undo IDs");
    }
    private static void Require(bool ok, string label)
    {
        if (!ok)
            throw new Exception(label);
        Console.WriteLine("PASS: UAC review bookmark: " + label);
    }
}
