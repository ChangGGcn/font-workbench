using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace FontBook.Management;

// A navigation bookmark, NOT a signed authorization. It can only bring up a fresh read-only
// review. No paths, registry entries, serialized work items or executable commands cross UAC.
public sealed class AdminReviewSelection
{
    public const string Switch = "--review-as-admin";
    public const int MaximumFamilies = 100_000;
    private const int MaxMask = (MaximumFamilies + 7) / 8;
    private readonly uint[] _ids;
    private readonly byte[] _digest;
    public string Mode
    {
        get;
    }
    public string UserSid
    {
        get;
    }
    public IReadOnlyList<uint> Ids
    {
        get;
    }
    private AdminReviewSelection(string mode, string sid, uint[] ids, byte[] digest)
    {
        Mode = mode;
        UserSid = sid;
        _ids = ids;
        Ids = Array.AsReadOnly(ids);
        _digest = digest;
    }

    public static AdminReviewSelection Create(string mode, string sid, ManagementInventory? inventory, IReadOnlyCollection<uint> ids)
    {
        if (!ValidMode(mode) || !ValidSid(sid) || ids.Count > MaximumFamilies)
            throw new ArgumentException("复核范围无效。");
        var selected = ids.Distinct().Order().ToArray();
        if ((mode == "history") != (selected.Length == 0) || selected.Any(i => i >= MaximumFamilies))
            throw new ArgumentException("复核选区无效。");
        return new(mode, sid, selected, Digest(mode, sid, inventory, selected));
    }
    public string[] Arguments()
    {
        string encoded = "-";
        if (_ids.Length > 0)
        {
            var mask = new byte[(_ids[^1] / 8) + 1];
            foreach (var id in _ids)
                mask[id / 8] |= (byte)(1 << (int)(id % 8));
            encoded = Convert.ToBase64String(mask);
        }
        return [Switch, Mode, UserSid, encoded, Convert.ToHexString(_digest)];
    }
    public static bool TryParse(string[] args, out AdminReviewSelection? selection)
    {
        selection = null;
        if (args.Length != 5 || args[0] != Switch || !ValidMode(args[1]) || !ValidSid(args[2]) || args[4].Length != 64 || !args[4].All(char.IsAsciiHexDigit))
            return false;
        var ids = new List<uint>();
        if (args[1] == "history")
        {
            if (args[3] != "-")
                return false;
        }
        else
        {
            if (args[3].Length is 0 or > ((MaxMask + 2) / 3 * 4))
                return false;
            var mask = new byte[MaxMask];
            if (!Convert.TryFromBase64String(args[3], mask, out int n) || n == 0 || mask[n - 1] == 0 || Convert.ToBase64String(mask, 0, n) != args[3])
                return false;
            for (int i = 0; i < n; i++)
            for (int bit = 0; bit < 8; bit++)
            if ((mask[i] & (1 << bit)) != 0)
                ids.Add((uint)(i * 8 + bit));
        }
        selection = new(args[1], args[2], ids.ToArray(), Convert.FromHexString(args[4]));
        return true;
    }
    public bool Matches(string currentSid, ManagementInventory? inventory)
    {
        if (UserSid != currentSid)
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(_digest, Digest(Mode, UserSid, inventory, _ids));
        }
        catch (ArgumentException) { return false; }
    }
    private static bool ValidMode(string mode) => mode is "uninstall" or "disable" or "history";
    private static bool ValidSid(string sid)
    {
        if (sid.Length is 0 or > 184)
            return false;
        try
        {
            return new SecurityIdentifier(sid).Value == sid;
        }
        catch (ArgumentException) { return false; }
    }
    private static byte[] Digest(string mode, string sid, ManagementInventory? inventory, uint[] ids)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Number(uint n)
        {
            Span<byte> v = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(v, n);
            hash.AppendData(v);
        }
        void Text(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Number((uint)bytes.Length);
            hash.AppendData(bytes);
        }
        Text("FontWorkbench/admin-review/1");
        Text(mode);
        Text(sid);
        Number((uint)ids.Length);
        if (ids.Length != 0)
        {
            if (inventory == null)
                throw new ArgumentException("未读取目录。");
            var families = inventory.Families.ToDictionary(f => f.Id);
            foreach (uint id in ids)
            {
                if (!families.TryGetValue(id, out var f))
                    throw new ArgumentException("原选区已不在当前目录。");
                Number(id);
                Text(f.Key);
                Text(f.Name);
                var faces = f.Faces.Select(x => (Path: x.Path.ToUpperInvariant(), x.FaceIndex)).Distinct().OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.FaceIndex).ToArray();
                Number((uint)faces.Length);
                foreach (var item in faces)
                {
                    Text(item.Path);
                    Number(item.FaceIndex);
                }
            }
        }
        return hash.GetHashAndReset();
    }
}
