using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static FamilyRow LatinFamily(WorkbenchUi ui) => new[] { "Segoe UI", "Arial", "Tahoma", "Verdana", "Calibri" }
        .Select(name => ui.Rows.FirstOrDefault(row =>
            string.Equals(row.Key, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)))
        .FirstOrDefault(row => row is not null)
        ?? throw new InvalidOperationException("已安装目录中未找到可验证拉丁 cmap 的 Windows 系统家族。");

    private static async Task CheckOpenTypeAsync(WorkbenchUi ui)
    {
        var family = ui.Families.SelectedItem as FamilyRow ?? throw new InvalidOperationException("未选择测试家族。");
        var style = ui.Styles.SelectedItem as StyleRow ?? throw new InvalidOperationException("未选择测试字面。");
        var launcher = Find<Button>(ui.Window, "OpenTypeButton");
        Window? dialog = null;
        try
        {
            async Task<ComboBox> OpenFeatureAsync()
            {
                dialog = await OpenOwnedWindowAsync(ui, launcher, "OpenTypeWindow");
                Require(!ComponentDispatcher.IsThreadModal, "OpenType 面板应保持非模态。");
                var heading = Find<TextBlock>(dialog, "FeatureFace");
                var list = Find<ListBox>(dialog, "FeatureList");
                await ui.WaitAsync("真实字面加载 OpenType 特性行", () => list.Items.Count > 0 &&
                    heading.Text.StartsWith($"{family.Name} · {style.Name} · Face ", StringComparison.Ordinal));
                list.ScrollIntoView(list.Items[0]);
                dialog.UpdateLayout();
                var picker = VisualDescendants<ComboBox>(list).FirstOrDefault(control => control.IsVisible &&
                    control.Items.Count == 3 && AutomationProperties.GetName(control).Length == 4)
                    ?? throw new InvalidOperationException("OpenType 行没有可交互的三态控件。");
                Require(picker.Items.OfType<string>().SequenceEqual(new[] { "字体默认", "启用", "关闭" }),
                    "OpenType 特性未提供默认、启用和关闭。");
                return picker;
            }

            var choice = await OpenFeatureAsync();
            Require(choice.SelectedIndex == 0, "首次打开时 OpenType 特性不是字体默认状态。");
            await ApplyAsync(choice, 1, "启用 OpenType 特性");
            await ApplyAsync(choice, 2, "关闭 OpenType 特性");
            Click(Find<Button>(dialog!, "CloseFeatures"));
            await ui.WaitAsync("关闭 OpenType 面板", () => dialog?.IsVisible == false);

            choice = await OpenFeatureAsync();
            Require(choice.SelectedIndex == 2, "同一字面的 OpenType 界面未记住关闭状态。");
            var beforeReset = ui.Specimen.Source;
            Click(Find<Button>(dialog!, "ResetFeatures"));
            await ui.WaitAsync("恢复 OpenType 字体默认", () => choice.SelectedIndex == 0 &&
                GlyphUi.Property(choice.DataContext, "Mode") is 0);
            await ui.PreviewAsync("恢复默认后重新请求标本", beforeReset);
            Pass("所选字面的 OpenType 三态设置双向更新、关闭重开保留、可恢复默认，并重新请求标本；不判断 native 特性是否改变字形。");
        }
        finally
        {
            if (dialog is { IsVisible: true })
                dialog.Close();
        }

        async Task ApplyAsync(ComboBox picker, int index, string phase)
        {
            var prior = ui.Specimen.Source;
            picker.SelectedIndex = index;
            await ui.WaitAsync(phase, () => picker.SelectedIndex == index &&
                GlyphUi.Property(picker.DataContext, "Mode") is int mode && mode == index);
            await ui.PreviewAsync($"{phase}后重新请求标本", prior);
        }
    }

    private static async Task CheckFontInfoAsync(WorkbenchUi ui, string output)
    {
        var family = (FamilyRow)ui.Families.SelectedItem;
        var style = (StyleRow)ui.Styles.SelectedItem;
        var face = ui.FaceDetail.Text.Split('·', StringSplitOptions.TrimEntries);
        var file = Find<TextBlock>(ui.Window, "PathText").Text;
        Require(face.Length == 3 && face[1].StartsWith("Face ", StringComparison.Ordinal) &&
            face[2].EndsWith(" 字形", StringComparison.Ordinal) && File.Exists(file),
            "未取得所选著名字体的本地文件、字面编号或字形数。");

        var info = await OpenOwnedWindowAsync(ui, Find<Button>(ui.Window, "FontInfoButton"), "FontDetailsWindow");
        try
        {
            var caption = Find<TextBlock>(info, "SnapshotCaption");
            var details = Find<TextBox>(info, "DetailsText");
            var status = Find<TextBlock>(info, "ReadStatus");
            Require(details.IsReadOnly && !ComponentDispatcher.IsThreadModal,
                "字体详情应是非模态、只读可选择的快照。");
            await ui.WaitAsync("读取所选字体的真实详情", () =>
            {
                Require(!status.Text.Contains("不可用", StringComparison.Ordinal), "字体详情未能读取真实字面。");
                return details.Text.Contains("UPEM：", StringComparison.Ordinal) && status.Text.Contains("快照", StringComparison.Ordinal);
            });
            Require(caption.Text == $"{family.Name} / {style.Name}  ·  {face[1]}" &&
                Field("字体文件") == file && Field("Face index（从 0 开始）") == face[1][5..] &&
                Field("PostScript 名称") == face[0] && Field("字形数") == face[2][..^" 字形".Length] &&
                Field("Windows 11 清单") == (family.IsWindowsSystemFont ? "系统字体" : "未列入"),
                "字体详情的文件、Face index、PostScript 名称或字形数与当前字面不一致。");
            Require(Provided("家族") && Provided("完整名称") && Provided("版本") &&
                Provided("版权") && !Field("格式").StartsWith("未识别", StringComparison.Ordinal) &&
                Positive("UPEM") && Positive("Ascender（上升量）") && Positive("Descender（下降量，正值）") &&
                Provided("GSUB") && Provided("GPOS"), "真实字面缺少可读的名称、版本、度量或 OpenType 字段。");
            CaptureRedactedWindow(info, Path.Combine(output, "info.png"));
            Pass("真实字体详情显示精确字面身份、名称、格式、度量及 OpenType 字段；截图遮蔽本机路径。");

            bool Provided(string label) => Field(label) is { Length: > 0 } value && value != "未提供或无法读取";
            bool Positive(string label) => uint.TryParse(Field(label), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value > 0;
            string Field(string label)
            {
                var prefix = label + '：';
                return details.Text.Split('\n').FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
                    ?[prefix.Length..].Trim() ?? throw new InvalidOperationException($"字体详情缺少 {label} 字段。");
            }
        }
        finally { info.Close(); }
    }

    private static async Task<Window> OpenOwnedWindowAsync(WorkbenchUi ui, Button launcher, string type,
        int timeout = InteractionTimeoutSeconds)
    {
        await ui.WaitAsync($"可打开 {type}", () => launcher.IsEnabled);
        var before = Application.Current.Windows.OfType<Window>().ToHashSet();
        Window? child = null;
        // Queue the click so a modal child can be inspected (and closed) from its dispatcher frame.
        var invocation = ui.Window.Dispatcher.InvokeAsync(() => Click(launcher));
        try
        {
            await ui.WaitAsync($"打开 {type}", () =>
            {
                if (invocation.Task.IsCompleted)
                    invocation.Task.GetAwaiter().GetResult();
                child = Application.Current.Windows.OfType<Window>().SingleOrDefault(window =>
                    !before.Contains(window) && ReferenceEquals(window.Owner, ui.Window) && window.IsVisible);
                return child is not null;
            }, timeout);
            Require(child!.GetType().Name == type, $"打开了意外的窗口（预期 {type}）。");
            RequireOffscreen(child);
            return child;
        }
        catch
        {
            child?.Close();
            throw;
        }
        finally
        {
            invocation.Abort();
        }
    }
}
