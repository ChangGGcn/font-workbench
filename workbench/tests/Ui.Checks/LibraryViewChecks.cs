using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static async Task CheckLibraryViewsAsync(WorkbenchUi ui, string output)
    {
        var list = ui.Families;
        var picker = Find<ComboBox>(ui.Window, "ViewPicker");
        var compact = FindChoice(picker, "紧凑列表");
        var grid = FindChoice(picker, "网格卡片");
        var samples = FindChoice(picker, "样本列表");
        picker.SelectedIndex = compact;
        Require(list.SelectionMode == SelectionMode.Extended && list.Items.Count >= 6,
            "需要至少六个真实家族和 WPF Extended 选择模式。");
        list.UnselectAll(); // Establish an empty fixture, not the gestures under test.
        await ui.WaitAsync("未选择家族时禁用批量停用与卸载", () =>
            !Find<Button>(ui.Window, "BatchDisableButton").IsEnabled &&
            !Find<Button>(ui.Window, "BatchUninstallButton").IsEnabled && Find<Button>(ui.Window, "BatchEnableButton").IsEnabled);

        var rows = ui.Rows.Take(6).ToArray();
        try
        {
            RowClick(rows[1]);
            CheckSelected([rows[1]], "普通点击");
            RowClick(rows[3], ModifierKeys.Control);
            CheckSelected([rows[1], rows[3]], "Ctrl 增选");
            RowClick(rows[3], ModifierKeys.Control);
            CheckSelected([rows[1]], "Ctrl 取消单个选择");

            RowClick(rows[1]); // Re-establish the WPF selection anchor after toggling another row.
            var rangeEnd = RowClick(rows[4], ModifierKeys.Shift);
            CheckSelected(rows[1..5], "Shift 连续选择");
            WpfKey(rangeEnd, Key.A, ModifierKeys.Control);
            Require(list.SelectedItems.Count == list.Items.Count && SelectedIds().SetEquals(ui.Rows.Select(row => row.Id)),
                "Ctrl+A 没有走 WPF Extended 全选的命令路径。");

            RowClick(rows[1]);
            RowClick(rows[3], ModifierKeys.Control);
        }
        finally { FocusInWpf(null); }
        CheckSelected([rows[1], rows[3]], "全选后重新生成非连续的多选");
        RequireOffscreen(ui.Window);
        Pass("FamilyList 的 WPF Extended 普通点击、Ctrl 切换、Shift 范围和 Ctrl+A 全选均使用实际选择事件。");

        var primary = list.SelectedItem as FamilyRow ?? throw new InvalidOperationException("多选后没有主要家族。");
        var selection = SelectedIds();
        var title = Find<TextBlock>(ui.Window, "FamilyTitle");
        await ui.WaitAsync("多选的主要家族已载入检查器", () => title.Text == primary.Name && ui.Styles.SelectedItem is StyleRow);
        var face = (StyleRow)ui.Styles.SelectedItem;
        try
        {
            foreach (var (mode, cards) in new[] { (grid, true), (samples, false) })
            {
                picker.SelectedIndex = mode;
                var name = (string)picker.Items[mode];
                var first = await WaitForViewportAsync(name, cards, replaced: null);
                CheckViewSelection(name);
                if (cards)
                    ui.Capture(Path.Combine(output, "grid.png"));
                var viewer = VisualDescendants<ScrollViewer>(list).First();
                Require(viewer.ScrollableHeight > viewer.ViewportHeight,
                    $"{name} 测试需要超过两屏家族才能检验延迟加载。");
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * 0.55);
                var next = await WaitForViewportAsync(name, cards, first.VisibleIds);
                CheckViewSelection(name);
                Require(!next.VisibleIds.Overlaps(first.VisibleIds), $"{name} 大幅滚动后仍显示旧可视家族。");
                Pass($"{name} 只请求可视区域预览，滚动更新：已生成容器 {first.Realized} → {next.Realized} / {list.Items.Count}，可视预览 {first.VisibleIds.Count} → {next.VisibleIds.Count}。");
            }
        }
        finally { picker.SelectedIndex = compact; }
        await ui.WaitAsync("还原紧凑列表的模板与选择", () => !VisualDescendants<WorkbenchFamilyPreview>(list).Any(), stableMilliseconds: 150);
        CheckViewSelection("紧凑列表");
        Pass("紧凑列表、网格和样本视图来回切换保留主要家族、当前字面及多选集合。");

        ListBoxItem RowClick(FamilyRow row, ModifierKeys modifiers = ModifierKeys.None)
        {
            list.ScrollIntoView(row);
            ui.Window.UpdateLayout();
            var item = list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem
                ?? throw new InvalidOperationException("离屏测试未生成目标家族行。");
            // Reset WPF focus before a mouse gesture; otherwise ListBox may treat moving focus
            // between its rows as an additional *keyboard* selection.
            FocusInWpf(null);
            FocusInWpf(item);
            WpfMouseClick(item, modifiers);
            return item;
        }

        HashSet<uint> SelectedIds() => list.SelectedItems.OfType<FamilyRow>().Select(row => row.Id).ToHashSet();

        void CheckSelected(IEnumerable<FamilyRow> expected, string step) =>
            Require(SelectedIds().SetEquals(expected.Select(row => row.Id)), $"{step} 的 WPF 选择集合不正确。");

        void CheckViewSelection(string name) => Require(list.SelectedItem is FamilyRow selected &&
            selected.Id == primary.Id && selected.Key == primary.Key && SelectedIds().SetEquals(selection) &&
            title.Text == primary.Name && Equals(ui.Styles.SelectedItem, face),
            $"{name} 丢失了主要家族、字面或多选集合。");

        async Task<PreviewViewport> WaitForViewportAsync(string name, bool cards, HashSet<uint>? replaced)
        {
            PreviewViewport? measured = null;
            var distinctColumns = 0;
            await ui.WaitAsync($"{name} 完成可视家族预览加载", () =>
            {
                var viewport = VisualDescendants<ScrollContentPresenter>(list).FirstOrDefault();
                if (viewport is null || viewport.ActualHeight <= 0)
                    return false;
                var controls = VisualDescendants<WorkbenchFamilyPreview>(list)
                    .Where(control => control.IsLoaded && control.IsCard == cards && control.DataContext is FamilyRow).ToArray();
                var visible = controls.Where(control => IntersectsViewport(control, viewport)).ToHashSet();
                var active = controls.Where(control => control.PreviewStatus is not null || control.PreviewImage is not null).ToArray();
                var ids = visible.Select(control => ((FamilyRow)control.DataContext).Id).ToHashSet();
                if (ids.Count == 0 || replaced?.Overlaps(ids) == true ||
                    !visible.Any(control => control.PreviewImage is not null) ||
                    visible.Any(control => control is { PreviewImage: null, PreviewStatus: null or "预览中…" }))
                    return false;
                Require(active.All(visible.Contains), $"{name} 为视口外的已生成容器加载了预览。");
                Require(visible.Count == ids.Count, $"{name} 的可视卡片出现重复的家族绑定。");
                distinctColumns = visible.Select(control =>
                    Math.Round(control.TransformToAncestor(viewport).Transform(new Point()).X / 8)).Distinct().Count();
                measured = new PreviewViewport(ids, VisualDescendants<ListBoxItem>(list).Count(),
                    visible.Where(control => control.PreviewImage is not null).ToArray());
                return true;
            }, LoadTimeoutSeconds, stableMilliseconds: 250);
            var ready = measured!;
            var limit = Math.Max(32, ready.VisibleIds.Count * 4 + 12);
            Require(ready.Realized <= limit && ready.Realized < list.Items.Count,
                $"{name} 生成的容器过多：{ready.Realized}/{list.Items.Count}，当前上限 {limit}。");
            Require(cards ? distinctColumns >= 2 : distinctColumns == 1,
                $"{name} 未实际使用预期网格或单列样本布局。");
            foreach (var control in ready.Rendered)
            {
                var bitmap = control.PreviewImage!;
                Require(VisualDescendants<Image>(control).Any(image => image.IsVisible && ReferenceEquals(image.Source, bitmap)),
                    $"{name} 没有在实际模板中显示加载出的位图。");
                var ink = InkBounds(bitmap, ReadVisiblePixels(bitmap, name));
                Require(ink.X > 0 && ink.Y > 0 && ink.X + ink.Width < bitmap.PixelWidth &&
                    ink.Y + ink.Height < bitmap.PixelHeight &&
                    bitmap.PixelWidth * 96 / bitmap.DpiX <= control.ActualWidth + 1 &&
                    bitmap.PixelHeight * 96 / bitmap.DpiY <= control.ActualHeight + 1,
                    $"{name} 的预览墨色触及位图边缘或位图超出卡片画布，可能截断字形。");
            }
            return ready;
        }
    }

    private static bool IntersectsViewport(FrameworkElement element, ScrollContentPresenter viewport)
    {
        if (!element.IsVisible || !viewport.IsAncestorOf(element))
            return false;
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        bounds.Intersect(new Rect(viewport.RenderSize));
        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
    }

    private sealed record PreviewViewport(HashSet<uint> VisibleIds, int Realized, WorkbenchFamilyPreview[] Rendered);
}
