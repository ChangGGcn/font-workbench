using System.Text;
using System.Windows;
using System.Windows.Input;

namespace FontBook.Workbench;

internal partial class FontDetailsWindow : Window
{
    private const string Missing = "未提供或无法读取";
    private bool _closed;

    private FontDetailsWindow(Window owner, string family, string style, string path, uint index)
    {
        InitializeComponent();
        Owner = owner;
        if (!owner.ShowActivated)
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left;
            Top = owner.Top;
        }
        SnapshotCaption.Text = $"{family} / {style}  ·  Face {index}";
        SnapshotCaption.ToolTip = SnapshotCaption.Text;
        DetailsText.Text = $"字体文件：{(string.IsNullOrEmpty(path) ? Missing : path)}\r\nFace index（从 0 开始）：{index}";
    }

    internal static void ShowFor(Window owner, FaceSelection face, FamilyRow family, string style)
    {
        owner.Dispatcher.VerifyAccess();
        var window = new FontDetailsWindow(owner, family.Name, style, face.Path, face.FaceIndex);
        // Pin this selection before showing the modeless window. Later selections cannot change its contents.
        _ = window.PopulateAsync(face.ReadDetailsAsync(), family.IsWindowsSystemFont);
        window.Show();
    }

    private async Task PopulateAsync(Task<FontDetails> request, bool systemFont)
    {
        string? text = null;
        string status;
        try
        {
            text = Format(await request.ConfigureAwait(false), systemFont);
            status = "此快照不会随所选字体改变。可选择并复制内容。";
        }
        catch (Exception ex)
        {
            status = ex is InvalidOperationException
                ? $"字体详情不可用：{ex.Message}"
                : $"字体详情不可用（0x{ex.HResult:X8}）。";
        }
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed)
                    return;
                if (text is not null)
                    DetailsText.Text = text;
                ReadStatus.Text = status;
            });
        }
        catch (OperationCanceledException) when (Dispatcher.HasShutdownStarted) { }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private void Close_Executed(object sender, ExecutedRoutedEventArgs e) => Close();

    private static string Format(FontDetails info, bool systemFont)
    {
        var text = new StringBuilder();
        void Row(string label, string? value)
        {
            text.Append(label).Append('：');
            // Keep line breaks in legal notices, without presenting a continuation as a field label.
            var lines = (value ?? Missing).ReplaceLineEndings("\n").Split('\n');
            text.AppendLine(lines[0]);
            for (int i = 1; i < lines.Length; i++)
                text.Append("    ").AppendLine(lines[i]);
        }
        void Section(string title) => text.AppendLine().AppendLine(title);

        text.AppendLine("字面标识");
        Row("Windows 11 清单", systemFont ? "系统字体" : "未列入");
        Row("字体文件", info.Path);
        Row("Face index（从 0 开始）", $"{info.FaceIndex}");
        Row("家族", info.Names.Family);
        Row("完整名称", info.Names.FullName);
        Row("PostScript 名称", info.Names.Postscript);
        Row("样式名称", info.Names.Style);
        Row("版本", info.Names.Version);
        Row("制造商", info.Names.Manufacturer);
        Row("设计师", info.Names.Designer);

        Section("格式与度量");
        var format = info.Outline switch
        {
            FontOutline.TrueType => "TrueType (glyf)",
            FontOutline.Cff => "OpenType (CFF)",
            FontOutline.Cff2 => "OpenType (CFF2)",
            FontOutline.Type1 => "PostScript Type 1",
            FontOutline.Vector => "矢量 .FON",
            FontOutline.Bitmap => "位图 .FON",
            FontOutline.RawCff => "原始 CFF",
            _ => "未识别轮廓"
        };
        Row("格式", info.FaceType == 2 ? $"{format} · OpenType 字体集" : format);
        Row("字形数", $"{info.Metrics.GlyphCount:N0}");
        Row("UPEM", $"{info.Metrics.UnitsPerEm}");
        Row("Ascender（上升量）", $"{info.Metrics.Ascent}");
        Row("Descender（下降量，正值）", $"{info.Metrics.Descent}");
        Row("Weight（字重）", $"{info.Weight}");
        Row("Stretch（字宽）", $"{info.Stretch}");
        var slope = info.Style switch
        {
            0 => "直立",
            1 => "倾斜",
            2 => "斜体",
            _ => "未知"
        };
        Row("Style（字形倾斜）", $"{slope} ({info.Style})");
        if (info.Simulations != 0)
            Row("DirectWrite 模拟样式", $"0x{info.Simulations:X}");

        Section("可变轴（最小 / 所选实例 / 最大）");
        if (info.Axes.Count == 0)
            text.AppendLine("无可变轴");
        foreach (var axis in info.Axes)
            Row(axis.Label, FormattableString.Invariant($"{axis.Minimum:G} / {axis.Initial:G} / {axis.Maximum:G}"));

        Section("OpenType 功能（因脚本和语言而异）");
        Row("GSUB", FeatureList(info, info.GsubStatus, FontFeatureTables.Gsub));
        Row("GPOS", FeatureList(info, info.GposStatus, FontFeatureTables.Gpos));

        Section("版权与许可（字体内嵌文本）");
        Row("版权", info.Names.Copyright);
        Row("许可", info.Names.License);
        Row("许可网址", info.Names.LicenseUrl);
        return text.ToString();
    }

    private static string FeatureList(FontDetails info, FontLayoutStatus status, FontFeatureTables table)
    {
        if (status == FontLayoutStatus.Absent)
            return "无此表";
        if (status == FontLayoutStatus.Unavailable)
            return "无法读取、不受支持或超过读取上限";
        var tags = string.Join(" · ", info.Features.Where(feature => (feature.Tables & table) != 0).Select(feature => feature.Tag));
        return tags.Length == 0 ? "无功能标签" : tags;
    }
}
