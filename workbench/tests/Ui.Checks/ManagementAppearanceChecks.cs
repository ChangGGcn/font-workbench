using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FontBook.Management;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static async Task CheckManagementAppearanceAsync(Window owner, string output)
    {
        Require(!SystemParameters.HighContrast, "管理弹窗深浅色检验需要系统未启用高对比度。");
        var picker = Find<ComboBox>(owner, "ThemePicker");
        var originalChoice = picker.SelectedIndex;

        // Display-only fixtures. The filenames are labels, never paths passed to a service.
        const string sans = "示例黑体", serif = "示例宋体";
        const string demo = "仅为界面示例，未执行字体变更。", time = "2025-01-15 09:00";
        string[] sansFiles = ["PreviewSans-Regular.ttf"], serifFiles = ["PreviewSerif-Regular.ttf"];
        var plan = new ManagementPlan([
            new(ManagementAction.Disable, sans, sansFiles, true, "用户范围（示例）", demo),
            new(ManagementAction.Disable, serif, serifFiles, false, "只读（示例）", "此示例项目未通过预检。")]);
        ManagementResult[] results = [
            new(ManagementAction.Disable, sans, true, demo, "example-01", sansFiles),
            new(ManagementAction.Disable, serif, false, "此示例项目未执行。", Files: serifFiles)];
        ManagementHistoryItem[] history = [
            new(new(ManagementHistorySource.Workbench, "example-01"), ManagementAction.Disable,
                sans, time, sansFiles, "已停用（示例）", true, demo),
            new(new(ManagementHistorySource.Workbench, "example-02"), ManagementAction.Uninstall,
                serif, time, serifFiles, "已备份（示例）", true, demo)];

        try
        {
            foreach (var (darkMode, choice, label, fileSuffix) in new[]
            {
                (true, FindChoice(picker, "Dark", "深色"), "深色", "dark"),
                (false, FindChoice(picker, "Light", "浅色"), "浅色", "light")
            })
            {
                picker.SelectedIndex = choice;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                // Call the presenters directly: no service, write handlers, or elevation request.
                await CheckManagementDialogAsync(owner, $"{label}预检", darkMode, () =>
                    Require(ManagementDialog.Confirm(owner, plan, allowAdminReview: false) == ReviewDecision.Cancel,
                        "模拟预检未以取消退出。"), screenshot: Path.Combine(output, $"management-preflight-{fileSuffix}.png"));
                await CheckManagementDialogAsync(owner, $"{label}结果", darkMode, () => ManagementDialog.ShowResults(owner, results));
                await CheckManagementDialogAsync(owner, $"{label}历史", darkMode, () =>
                    Require(ManagementDialog.ReviewHistory(owner, history, canPlan: true).Count == 0,
                        "模拟历史意外提交了记录。"), extended: true);
            }
            Pass("管理弹窗深浅色的预检、结果和历史列表底色及逐字段文字像素通过检查，覆盖选中、未选中和历史多选。");
        }
        finally
        {
            picker.SelectedIndex = originalChoice;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    private static async Task CheckManagementDialogAsync(Window owner, string context, bool darkMode,
        Action show, bool extended = false, string? screenshot = null)
    {
        var existing = Application.Current.Windows.OfType<ManagementDialog>().ToHashSet();
        // The real presenter pumps this inspection from its ShowDialog dispatcher frame.
        var inspection = owner.Dispatcher.InvokeAsync(() =>
        {
            var opened = Application.Current.Windows.OfType<ManagementDialog>()
                .Where(dialog => !existing.Contains(dialog) && dialog.IsVisible).ToArray();
            try
            {
                Require(opened.Length == 1 && ReferenceEquals(opened[0].Owner, owner), $"{context}未打开预期的管理弹窗。");
                var dialog = opened[0];
                RequireOffscreen(dialog);
                var list = Find<ListBox>(dialog, extended ? "ManagementHistoryList" : "ManagementItemsList");
                Require(list.IsVisible && list.Items.Count == 2 &&
                    list.SelectionMode == (extended ? SelectionMode.Extended : SelectionMode.Single),
                    $"{context}列表的可见性、行数或选择模式不正确。");

                // Complementary selections measure every fixture row in both states.
                int[][] selections = extended ? [[0, 1], []] : [[0], [1]];
                foreach (var selected in selections)
                {
                    if (extended)
                    {
                        list.UnselectAll();
                        foreach (var i in selected)
                            list.SelectedItems.Add(list.Items[i]);
                    }
                    else
                        list.SelectedIndex = selected[0];
                    Require(list.SelectedItems.Count == selected.Length, $"{context}未建立预期选区。");
                    if (screenshot is not null && selected.Contains(0))
                        CaptureWindow(dialog, screenshot);
                    CheckManagementPixels(dialog, list, context, darkMode, selected);
                }
            }
            finally
            {
                foreach (var dialog in opened)
                    dialog.Close();
            }
        }, DispatcherPriority.ApplicationIdle);
        try { show(); }
        finally { inspection.Abort(); }
        Require(!inspection.Task.IsCanceled, $"{context}未进入管理弹窗的模态消息循环。");
        await inspection.Task;
    }

    private static void CheckManagementPixels(Window dialog, ListBox list, string context, bool darkMode, int[] selectedRows)
    {
        dialog.UpdateLayout();
        var root = VisualTreeHelper.GetChild(dialog, 0) as FrameworkElement
            ?? throw new InvalidOperationException($"{context}未生成窗口客户区。");
        var dpi = VisualTreeHelper.GetDpi(root);
        var width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var viewport = Bounds(VisualDescendants<ScrollContentPresenter>(list).FirstOrDefault(v => v.IsVisible)
            ?? throw new InvalidOperationException($"{context}未生成列表视口。"));
        var regions = new List<(TextBlock Text, Int32Rect Area, bool Selected, string Label)>();
        var lastRowBottom = viewport.Y;
        for (var i = 0; i < list.Items.Count; i++)
        {
            var selected = selectedRows.Contains(i);
            var rowLabel = $"{context}第 {i + 1} 行（{(selected ? "选中" : "未选中")}）";
            var row = list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem
                ?? throw new InvalidOperationException($"{rowLabel}未生成容器。");
            Require(row.IsSelected == selected, $"{rowLabel}的选中状态不正确。");
            var rowArea = Bounds(row);
            lastRowBottom = Math.Max(lastRowBottom, rowArea.Y + rowArea.Height);

            foreach (var field in new[] { "Title", "Status", "Files", "Detail" })
            {
                var label = $"{rowLabel} {field}";
                var text = VisualDescendants<TextBlock>(row).SingleOrDefault(block =>
                    block.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == field)
                    ?? throw new InvalidOperationException($"{label}未生成文本块。");
                var area = Bounds(text);
                Require(area.X >= viewport.X && area.Y >= viewport.Y && area.X + area.Width <= viewport.X + viewport.Width &&
                    area.Y + area.Height <= viewport.Y + viewport.Height, $"{label} 被列表视口裁切。");
                regions.Add((text, area, selected, label));
            }
        }

        var rendered = Render();
        var blankHeight = viewport.Y + viewport.Height - lastRowBottom;
        Require(blankHeight >= 8, $"{context}列表没有足够的空白区域用于采样底色。");
        CheckSurface(rendered, viewport.X + viewport.Width / 2, lastRowBottom + blankHeight / 2, $"{context}列表");

        // Hide the text visuals without reflow. The paired pixels exclude selection chrome and
        // other non-text drawing, while keeping the actual composited backdrop under each glyph.
        var opacity = regions.Select(region => region.Text.Opacity).ToArray();
        byte[] background;
        try
        {
            foreach (var region in regions)
                region.Text.SetCurrentValue(UIElement.OpacityProperty, 0d);
            background = Render();
        }
        finally
        {
            for (var i = 0; i < regions.Count; i++)
                regions[i].Text.SetCurrentValue(UIElement.OpacityProperty, opacity[i]);
        }

        foreach (var (_, area, selected, label) in regions)
        {
            // The short fixture strings leave this point empty; hiding the visual must not hide a fill.
            var x = area.X + area.Width * 3 / 4;
            var y = area.Y + area.Height / 2;
            var sample = (y * width + x) * 4;
            Require(rendered.AsSpan(sample, 4).SequenceEqual(background.AsSpan(sample, 4)), $"{label} 的空白底色在两次渲染中不一致。");
            if (!selected)
                CheckSurface(rendered, x, y, label);
            CheckManagementInk(rendered, background, width, area, label);
        }

        Int32Rect Bounds(FrameworkElement element)
        {
            Require(element.IsVisible && root.IsAncestorOf(element), $"{context}存在未显示的列表元素。");
            var rect = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            var x = (int)Math.Ceiling(rect.Left * dpi.DpiScaleX);
            var y = (int)Math.Ceiling(rect.Top * dpi.DpiScaleY);
            var right = (int)Math.Floor(rect.Right * dpi.DpiScaleX);
            var bottom = (int)Math.Floor(rect.Bottom * dpi.DpiScaleY);
            Require(x >= 0 && y >= 0 && right <= width && bottom <= height && right > x && bottom > y,
                $"{context}列表元素未完整布局在窗口客户区内。");
            return new Int32Rect(x, y, right - x, bottom - y);
        }

        byte[] Render()
        {
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            return ReadVisiblePixels(bitmap, context);
        }

        void CheckSurface(byte[] pixels, int x, int y, string label)
        {
            var i = (y * width + x) * 4;
            var color = Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
            Require(color.A == 255 && (darkMode ? Luma(color) < 96 : Luma(color) > 160),
                $"{label}实际底色不符合主题：{color}。");
        }
    }

    private static void CheckManagementInk(byte[] rendered, byte[] background, int width, Int32Rect area, string label)
    {
        const double minimumContrast = 4.5;
        int changed = 0, readable = 0;
        double peak = 0;
        for (var y = area.Y; y < area.Y + area.Height; y++)
        for (var x = area.X; x < area.X + area.Width; x++)
        {
            var i = (y * width + x) * 4;
            if (rendered.AsSpan(i, 4).SequenceEqual(background.AsSpan(i, 4)))
                continue;
            changed++;
            if (rendered[i + 3] != 255 || background[i + 3] != 255)
                continue;
            var ink = Luminance(rendered, i);
            var ground = Luminance(background, i);
            var contrast = (Math.Max(ink, ground) + 0.05) / (Math.Min(ink, ground) + 0.05);
            peak = Math.Max(peak, contrast);
            if (contrast >= minimumContrast)
                readable++;
        }

        // Antialiased edges can have low contrast; require a core, not just a few outlier pixels.
        Require(readable >= 12 && readable * 10 >= changed, FormattableString.Invariant(
            $"{label}缺少可读文字像素：≥{minimumContrast}:1 的像素 {readable}/{changed}，峰值 {peak:F2}:1。"));

        static double Luminance(byte[] pixels, int i) =>
            0.2126 * Linear(pixels[i + 2]) + 0.7152 * Linear(pixels[i + 1]) + 0.0722 * Linear(pixels[i]);

        static double Linear(byte channel)
        {
            var srgb = channel / 255d;
            return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }
    }
}
