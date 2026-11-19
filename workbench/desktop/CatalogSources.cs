using FontBook.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FontBook.Workbench;

internal sealed record CatalogSource(uint StyleIndex, string Path, uint FaceIndex);
internal sealed class SourcesHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.fb_sources_close(handle);
        return true;
    }
}
internal static partial class Native
{
    [DllImport(Dll)] internal static extern int fb_sources_open(CatalogHandle catalog, uint id, out SourcesHandle sources);
    [DllImport(Dll)] internal static extern void fb_sources_close(IntPtr sources);
    [DllImport(Dll)] internal static extern uint fb_sources_count(SourcesHandle sources);
    [DllImport(Dll)] internal static extern int fb_source_at(SourcesHandle sources, uint ordinal, out IntPtr path, out uint index);
}
internal sealed partial class FontCatalog
{
    // Some listed TTC faces have a different typographic family name. Inspect DirectWrite's
    // local file references once per catalogue, not per visible row; do not open font faces.
    private static bool HasWindowsFontFile(CatalogHandle catalog, uint id, CancellationToken ct)
    {
        int hr = Native.fb_sources_open(catalog, id, out var sources);
        using (sources)
        {
            if (hr < 0)
            {
                if (hr == unchecked((int)0x8007000E))
                    throw new OutOfMemoryException();
                return false; // Missing file metadata must not hide a browsable family.
            }
            uint count = Native.fb_sources_count(sources);
            for (uint i = 0; i < count; i++)
            {
                if (i % 64 == 0)
                    ct.ThrowIfCancellationRequested();
                Native.Check(Native.fb_source_at(sources, i, out var path, out _), "读取目录文件名");
                if (Windows11FontList.MatchesFile(Native.Copy(path)))
                    return true;
            }
            return false;
        }
    }

    public CatalogSource[] GetSources(uint familyId)
    {
        int hr = Native.fb_sources_open(_handle, familyId, out var source);
        using (source)
        {
            Native.Check(hr, "读取家族的物理文件关联");
            var rows = new CatalogSource[Native.fb_sources_count(source)];
            for (uint i = 0; i < rows.Length; i++)
            {
                Native.Check(Native.fb_source_at(source, i, out var path, out uint index), "读取字面文件位置");
                rows[i] = new(i, Native.Copy(path), index);
            }
            return rows;
        }
    }

    internal ManagementInventory ReadManagementInventory(CancellationToken ct)
    {
        var families = new List<ManagementFamily>(Rows.Length);
        string? incomplete = Stats.Skipped == 0 ? null :
            $"目录跳过了 {Stats.Skipped} 个家族，无法排除共享文件；本目录仅可浏览。";
        foreach (var row in Rows)
        {
            ct.ThrowIfCancellationRequested();
            var faces = new List<ManagementFace>();
            string? issue = null;
            try
            {
                var sources = GetSources(row.Id);
                var indices = new HashSet<uint>();
                if (sources.Length != row.Styles)
                    issue = "该家族没有可验证的完整本地样式集合。";
                foreach (var source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    if (source.StyleIndex >= row.Styles || !indices.Add(source.StyleIndex))
                        issue ??= "样式序号重复或越界，不能确认完整字面集合。";
                    if (string.IsNullOrWhiteSpace(source.Path))
                        issue ??= "该家族包含无法定位文件的本地字面。";
                    else
                        faces.Add(new(source.Path, source.FaceIndex));
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                issue = "无法完整核对目录字面：" + ex.Message;
            }
            if (issue is not null)
                incomplete ??= $"无法排除「{row.Name}」与其它家族共享文件；本目录仅可浏览。";
            families.Add(new(row.Id, row.Name, row.Key, faces, issue));
        }
        return new(families, incomplete);
    }
}
