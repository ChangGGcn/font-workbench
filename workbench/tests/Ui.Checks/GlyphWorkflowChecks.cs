using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static async Task CheckGlyphWorkflowAsync(WorkbenchUi ui, string output)
    {
        var allKeys = ui.Rows.Select(row => row.Key).ToArray();
        var family = LatinFamily(ui);
        ui.Search.Text = family.Name;
        await ui.WaitAsync("筛选字形测试家族", () => ui.ShowsCount(ui.Families.Items.Count, search: true) &&
            ui.Rows.Any(row => row.Key == family.Key));
        await ui.SelectPreviewAsync(ui.Rows.First(row => row.Key == family.Key));
        var originalStyle = (StyleRow)ui.Styles.SelectedItem;
        var alternativeStyle = ui.Styles.Items.OfType<StyleRow>()
            .Where(style => style.Index != originalStyle.Index && style.Name != originalStyle.Name)
            .OrderByDescending(style => Math.Abs((long)style.Weight - originalStyle.Weight))
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{family.Name} 没有可切换的第二种样式。");
        Pass($"使用 Windows 系统字体 {family.Name}，样式 {originalStyle.Name} → {alternativeStyle.Name}。");

        await CheckCoverageAsync(ui);
        var button = Find<Button>(ui.Window, "GlyphBrowserButton");
        await ui.WaitAsync("可打开当前字面的字形浏览器", () => button.IsEnabled);
        RequireOffscreen(ui.Window);
        Window? child = null;
        try
        {
            // A modal regression enters a nested dispatcher frame. Queue the click so the wait
            // can detect that state and the finally block can close the child.
            var click = ui.Window.Dispatcher.InvokeAsync(() => Click(button));
            try
            {
                await ui.WaitAsync("打开非模态 GlyphWindow", () =>
                {
                    child = Application.Current.Windows.OfType<Window>()
                        .SingleOrDefault(window => window.GetType().Name == "GlyphWindow");
                    Require(!ComponentDispatcher.IsThreadModal, "GlyphBrowserButton 打开了模态窗口。");
                    if (!click.Task.IsCompleted)
                        return false;
                    click.Task.GetAwaiter().GetResult();
                    return child is { IsVisible: true };
                });
            }
            finally
            {
                click.Abort();
            }

            var glyphs = new GlyphUi(child!, ui);
            glyphs.CheckPlacement();
            await glyphs.WaitAsync("初始页已加载 GID 0 的字形详情", () =>
                glyphs.Page is { Index: 1, Total: > 1 } && glyphs.FirstGid == 0 &&
                glyphs.SelectedGid == 0 && glyphs.Scalars.ItemsSource is not null);
            Require(!glyphs.Copy.IsEnabled, "GID 0 不允许复制。");
            var firstPageSize = glyphs.List.Items.Count;
            var glyphCountText = ui.FaceDetail.Text.Split('·')[^1].Trim();
            const string glyphCountSuffix = " 字形";
            Require(glyphCountText.EndsWith(glyphCountSuffix, StringComparison.Ordinal) &&
                int.TryParse(glyphCountText[..^glyphCountSuffix.Length], NumberStyles.Number,
                    CultureInfo.CurrentCulture, out var glyphCount) &&
                glyphs.Page == (1, (glyphCount + firstPageSize - 1) / firstPageSize),
                "GlyphPageText 的总页数不符合字面字形数与首屏 atlas 容量。");
            Pass($"GlyphBrowserButton 打开离屏非模态子窗，GID 0 禁止复制，页数 {glyphs.PageLabel.Text}。");

            var originalPixels = await JumpToLatinAAsync(glyphs);
            CaptureWindow(glyphs.Window, Path.Combine(output, "glyphs.png"));

            var (page, total) = glyphs.Page!.Value;
            var first = glyphs.FirstGid!.Value;
            var count = glyphs.List.Items.Count;
            var oldPage = glyphs.List.ItemsSource;
            Click(glyphs.Next);
            await glyphs.WaitAsync("下一页替换 atlas 并更新页码", () => glyphs.Page == (page + 1, total) &&
                glyphs.FirstGid == first + (uint)count && !ReferenceEquals(glyphs.List.ItemsSource, oldPage));
            Pass($"下一页 {glyphs.PageLabel.Text}，首项 GID {glyphs.FirstGid}。");

            oldPage = glyphs.List.ItemsSource;
            var originalSpecimen = await ui.PreviewAsync("切换字面前的真实标本");
            var originalSpecimenPixels = ReadVisiblePixels(originalSpecimen);
            ui.Styles.SelectedItem = alternativeStyle;
            Require(glyphs.List.Items.Count == 0 && glyphs.Zoom.Source is null && !glyphs.Copy.IsEnabled,
                "切换样式后仍显示旧字面的 atlas、放大图或复制状态。");
            await glyphs.WaitAsync("字形窗跟随新样式并从第一页重建", () =>
                Equals(ui.Styles.SelectedItem, alternativeStyle) && glyphs.Follows(family, alternativeStyle) &&
                glyphs.FreshFirstPage(oldPage));
            var alternativePixels = await JumpToLatinAAsync(glyphs);
            var alternativeSpecimen = await ui.PreviewAsync("字面切换后更新真实标本", originalSpecimen);
            Require(!originalSpecimenPixels.AsSpan().SequenceEqual(ReadVisiblePixels(alternativeSpecimen)),
                "字面切换后标本像素没有改变。");
            Require(!originalPixels.AsSpan().SequenceEqual(alternativePixels), "切换样式后仍显示上一字面的字形像素。");

            // A query change can retain the selected face if it is still in the results.
            // Pass through an empty result so clearing the query must load a fresh selection.
            oldPage = glyphs.List.ItemsSource;
            ui.Search.Text = $"fontbook-glyph-check-{Guid.NewGuid():N}";
            await glyphs.WaitAsync("搜索无匹配时清除旧字面", () =>
                ui.Result.Text.StartsWith(NoMatchText, StringComparison.Ordinal) &&
                ui.Families.SelectedItem is null && ui.Styles.Items.Count == 0 && !button.IsEnabled &&
                glyphs.List.Items.Count == 0 && glyphs.Page is null && glyphs.Zoom.Source is null && !glyphs.Copy.IsEnabled);
            ui.Search.Clear();
            await glyphs.WaitAsync("清空搜索后恢复全目录及当前字面 atlas", () =>
                ui.ShowsCount(allKeys.Length) && ui.Rows.Select(row => row.Key).SequenceEqual(allKeys) &&
                ui.Families.SelectedItem is FamilyRow current && ui.Styles.SelectedItem is StyleRow style &&
                glyphs.Follows(current, style) && glyphs.FreshFirstPage(oldPage) && !glyphs.Copy.IsEnabled &&
                glyphs.JumpBox.Text.Length == 0);
            await ui.SelectPreviewAsync(family);
            if (!Equals(ui.Styles.SelectedItem, originalStyle))
                ui.Styles.SelectedItem = originalStyle;
            await glyphs.WaitAsync("返回系统测试字面", () =>
                Equals(ui.Styles.SelectedItem, originalStyle) && glyphs.Follows(family, originalStyle) &&
                glyphs.FreshFirstPage(oldPage));
            var restoredPixels = await JumpToLatinAAsync(glyphs);
            Require(originalPixels.AsSpan().SequenceEqual(restoredPixels), "返回原始字面后未恢复对应的字形像素。");
            Pass("切换样式、搜索无匹配及清空搜索时，atlas 和复制状态跟随主窗口，放大像素与所选字面一致。");

            // Segoe UI maps CR to a nonzero GID. Start from copyable A before each control jump.
            await CheckUncopyableControlAsync(glyphs, 0x000D);
            await JumpToLatinAAsync(glyphs);
            await CheckUncopyableControlAsync(glyphs, 0x0000);
        }
        finally
        {
            if (child is { IsVisible: true })
                child.Close();
        }
    }

    private static async Task<byte[]> JumpToLatinAAsync(GlyphUi glyphs)
    {
        const uint scalar = 0x41;
        var oldPage = glyphs.List.ItemsSource;
        glyphs.Jump("U+0041");
        await glyphs.WaitAsync("U+0041 定位到实际 atlas 项并显示放大图", () =>
            !ReferenceEquals(glyphs.List.ItemsSource, oldPage) && glyphs.SelectedGid is > 0 &&
            GlyphUi.Property(glyphs.List.SelectedItem, "MappingCount") is uint and > 0 &&
            glyphs.SelectedScalar == scalar && glyphs.Zoom.Source is BitmapSource && glyphs.Copy.IsEnabled);
        var selected = glyphs.List.SelectedItem;
        Require(glyphs.List.SelectedIndex >= 0 &&
            ReferenceEquals(glyphs.List.Items[glyphs.List.SelectedIndex], selected),
            "U+0041 的所选项不在当前 GlyphList atlas 中。");
        var thumbnail = GlyphUi.Property(selected, "Image") as BitmapSource
            ?? throw new InvalidOperationException("所选 GID 缺少 atlas 缩略图。");
        var enlarged = (BitmapSource)glyphs.Zoom.Source;
        glyphs.Window.UpdateLayout();
        Require(glyphs.Zoom.IsVisible && glyphs.Zoom.ActualWidth > 0 && glyphs.Zoom.ActualHeight > 0 &&
            enlarged.PixelWidth > thumbnail.PixelWidth && enlarged.PixelHeight > thumbnail.PixelHeight,
            "GlyphSelectedImage 尚未呈现比 atlas 缩略图更大的位图。");
        ReadVisiblePixels(thumbnail, "atlas 缩略图");
        var pixels = ReadVisiblePixels(enlarged, "字形放大图");
        // Check availability only. Raising either copy button would mutate the real Clipboard.
        Pass($"U+0041 → GID {glyphs.SelectedGid}，当前页有真实缩略图和 {enlarged.PixelWidth}×{enlarged.PixelHeight} 放大图，复制可用。");
        return pixels;
    }

    private static async Task CheckUncopyableControlAsync(GlyphUi glyphs, uint scalar)
    {
        var code = $"U+{scalar:X4}";
        Require(glyphs.SelectedScalar == 0x0041 && glyphs.Copy.IsEnabled, "检查控制字符前应有可复制的 A。");
        var oldStatus = glyphs.Status.Text;
        glyphs.Jump(code);
        await glyphs.WaitAsync($"检查控制码点 {code} 的复制状态", () => glyphs.SelectedScalar == scalar ||
            glyphs.Status.Text != oldStatus && glyphs.Status.Text.Contains(code, StringComparison.Ordinal) &&
            (glyphs.Status.Text.Contains("没有直接映射", StringComparison.Ordinal) ||
             glyphs.Status.Text.Contains("控制字符", StringComparison.Ordinal)));
        Require(!glyphs.Copy.IsEnabled, $"控制码点 {code}（GID {glyphs.SelectedGid}）错误地允许复制。");
        Pass($"控制码点 {code} 禁止复制（{(glyphs.SelectedScalar == scalar ? $"GID {glyphs.SelectedGid}" : "无可复制映射")}）。");
    }

    private static async Task CheckCoverageAsync(WorkbenchUi ui)
    {
        const string missing = "U+10FFFF";
        var sample = Find<TextBox>(ui.Window, "SampleBox");
        var coverage = Find<TextBlock>(ui.Window, "CoverageText");
        var copy = Find<Button>(ui.Window, "CopyMissingButton");
        var original = sample.Text;
        var previous = ui.Specimen.Source;
        try
        {
            sample.Text = "A" + char.ConvertFromUtf32(0x10FFFF);
            await ui.WaitAsync("标本覆盖报告包含 U+10FFFF，且可复制缺字报告", () =>
                coverage.Text.Contains(missing, StringComparison.Ordinal) && copy.IsEnabled &&
                ui.Specimen.Source is BitmapSource bitmap && !ReferenceEquals(previous, bitmap));
            Require(!coverage.Text.Contains("U+0041", StringComparison.Ordinal), "系统拉丁字面的 A 被误列为缺字。");
            ReadVisiblePixels((BitmapSource)ui.Specimen.Source);
            sample.Clear();
            await ui.WaitAsync("清空标本后禁用缺字复制且清空旧覆盖状态", () =>
                !copy.IsEnabled && !coverage.Text.Contains(missing, StringComparison.Ordinal) && ui.Specimen.Source is null,
                stableMilliseconds: 250);
            Pass("标本 A + U+10FFFF 报告缺字，清空后禁用缺字复制，未调用复制。");
        }
        finally
        {
            sample.Text = original;
        }
    }

    private static void RequireOffscreen(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        const uint defaultToNull = 0;
        Require(handle != 0 && MonitorFromWindow(handle, defaultToNull) == 0,
            $"{window.GetType().Name} 已显示在显示器内，外窗原点为 ({window.Left:0.##}, {window.Top:0.##}) DIP。");
        Require(!window.ShowActivated && !window.IsActive, $"{window.GetType().Name} 获得或请求了激活。");
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    private static void Click(Button button)
    {
        Require(button.IsEnabled, $"{button.Name} 未启用。");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private sealed class GlyphUi(Window window, WorkbenchUi owner)
    {
        public Window Window { get; } = window;
        public ListBox List { get; } = Find<ListBox>(window, "GlyphList");
        public ComboBox Scalars { get; } = Find<ComboBox>(window, "GlyphScalarBox");
        public TextBox JumpBox { get; } = Find<TextBox>(window, "GlyphJumpBox");
        public Button Copy { get; } = Find<Button>(window, "GlyphCopyButton");
        public Button Next { get; } = Find<Button>(window, "GlyphNextButton");
        public Image Zoom { get; } = Find<Image>(window, "GlyphSelectedImage");
        public TextBlock PageLabel { get; } = Find<TextBlock>(window, "GlyphPageText");
        public TextBlock Status { get; } = Find<TextBlock>(window, "GlyphStatus");
        private Button JumpButton { get; } = Find<Button>(window, "GlyphJumpButton");
        private TextBlock Title { get; } = Find<TextBlock>(window, "GlyphTitle");
        public uint? FirstGid => List.Items.Count > 0 ? Property(List.Items[0], "Gid") as uint? : null;
        public uint? SelectedGid => Property(List.SelectedItem, "Gid") as uint?;
        public uint? SelectedScalar => Property(Scalars.SelectedItem, "Value") as uint?;

        public (int Index, int Total)? Page
        {
            get
            {
                var fields = PageLabel.Text.Split('/', StringSplitOptions.TrimEntries);
                return fields.Length == 2 &&
                    int.TryParse(fields[0], NumberStyles.Number, CultureInfo.CurrentCulture, out var index) &&
                    int.TryParse(fields[1], NumberStyles.Number, CultureInfo.CurrentCulture, out var total) &&
                    index > 0 && index <= total ? (index, total) : null;
            }
        }

        public bool Follows(FamilyRow family, StyleRow style) =>
            Title.Text.StartsWith($"{family.Name} · {style.Name} · Face ", StringComparison.Ordinal);

        public bool FreshFirstPage(object? oldPage) => Page is { Index: 1 } && FirstGid == 0 && SelectedGid == 0 &&
            !ReferenceEquals(List.ItemsSource, oldPage);

        public void Jump(string value)
        {
            JumpBox.Text = value;
            Click(JumpButton);
        }

        public void CheckPlacement()
        {
            Require(Window.IsVisible && ReferenceEquals(Window.Owner, owner.Window) &&
                !ComponentDispatcher.IsThreadModal, "字形子窗未保持可交互的非模态所有权。");
            RequireOffscreen(Window);
            RequireOffscreen(owner.Window);
        }

        public async Task WaitAsync(string phase, Func<bool> ready)
        {
            try
            {
                await owner.WaitAsync(phase, () =>
                {
                    CheckPlacement();
                    return ready();
                });
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"{error.Message} 字形页={PageLabel.Text}，项数={List.Items.Count}，所选 GID={SelectedGid}，状态={Status.Text}", error);
            }
        }

        // Read the data items' public binding properties, keeping the app's internal types private.
        public static object? Property(object? item, string name) =>
            item?.GetType().GetProperty(name)?.GetValue(item);
    }
}
