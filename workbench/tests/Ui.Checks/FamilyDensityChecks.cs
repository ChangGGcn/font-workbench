using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static async Task<IReadOnlyList<string>> CheckFamilyDensityAsync(WorkbenchUi ui)
    {
        var list = ui.Families;
        Require(VirtualizingPanel.GetIsVirtualizing(list) &&
                VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling &&
                ScrollViewer.GetCanContentScroll(list), "家族列表没有启用回收虚拟化。");

        // Alpha.1 (d3e144c) was measured at 144 DPI, with the unfiltered list scrolled to its top.
        var regressions = new List<string>();
        foreach (var (width, height, alpha1Rows) in new[] { (1240, 800, 12), (900, 600, 8) })
        {
            await ui.ResizeAsync(width, height);
            ui.Window.UpdateLayout();
            var viewer = VisualDescendants<ScrollViewer>(list).FirstOrDefault()
                ?? throw new InvalidOperationException("家族列表没有 ScrollViewer。");
            var viewport = VisualDescendants<ScrollContentPresenter>(viewer).FirstOrDefault()
                ?? throw new InvalidOperationException("家族列表没有 ScrollContentPresenter。");
            viewer.ScrollToVerticalOffset(0);
            FamilyRowGeometry? geometry = null;
            await ui.WaitAsync($"{width}×{height} 家族视口回到顶部并完成排版", () =>
            {
                ui.Window.UpdateLayout();
                if (viewer.VerticalOffset != 0)
                    viewer.ScrollToVerticalOffset(0); // Initial selection may request ScrollIntoView after layout.
                if (viewer.VerticalOffset != 0 || viewer.HorizontalOffset != 0 ||
                    viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0)
                {
                    geometry = null;
                    return false;
                }

                var next = MeasureFamilyRows(list, viewport);
                var same = next == geometry;
                geometry = next;
                return next.StartsAtFirstRow && same;
            }, stableMilliseconds: 250);

            var rows = geometry!;
            var dpi = VisualTreeHelper.GetDpi(viewport);
            var minimum = (alpha1Rows * 14 + 9) / 10;
            var gain = (double)rows.FullyVisible / alpha1Rows - 1;
            Console.WriteLine(FormattableString.Invariant(
                $"DENSITY: {width}×{height} DIP @ {dpi.PixelsPerInchX:0.#} DPI，视口={rows.Viewport.Width:0.###}×{rows.Viewport.Height:0.###} DIP，完整={rows.FullyVisible}，部分={rows.Partial}，已生成={rows.Realized}/{list.Items.Count}，alpha.1={alpha1Rows}，增幅={gain:P1}，门限={minimum}。"));
            if (rows.FullyVisible < minimum)
                regressions.Add($"{width}×{height} 的完整行数 {rows.FullyVisible} 低于 {minimum}");
            if (list.Items.Count >= 50 && rows.Realized > Math.Min(list.Items.Count / 2, 128))
                regressions.Add($"{width}×{height} 的虚拟化未显著缩减容器数：{rows.Realized}/{list.Items.Count}");
        }
        if (regressions.Count == 0)
            Pass("两种外窗的完整可见家族行数均比 alpha.1 增加至少 40%，列表仍使用回收虚拟化。");
        else
            Console.WriteLine($"DENSITY REVIEW: {string.Join("；", regressions)}。原门槛保留，其他回归继续采集结果。");
        return regressions;
    }

    private static FamilyRowGeometry MeasureFamilyRows(ListBox list, ScrollContentPresenter viewport)
    {
        const double edgeTolerance = 0.01; // DIP, for transform precision only.
        var clip = new Rect(viewport.RenderSize);
        int realized = 0, fullyVisible = 0, partial = 0;
        var startsAtFirstRow = false;
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row)
                continue;
            realized++;
            if (!row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0 || !viewport.IsAncestorOf(row))
                continue;

            var box = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            var contained = box.Left >= clip.Left - edgeTolerance && box.Top >= clip.Top - edgeTolerance &&
                box.Right <= clip.Right + edgeTolerance && box.Bottom <= clip.Bottom + edgeTolerance;
            if (index == 0)
                startsAtFirstRow = contained;
            if (contained)
                fullyVisible++;
            else
            {
                box.Intersect(clip);
                if (!box.IsEmpty && box.Width > 0 && box.Height > 0)
                    partial++;
            }
        }
        return new FamilyRowGeometry(fullyVisible, partial, realized, clip.Size, startsAtFirstRow);
    }

    private sealed record FamilyRowGeometry(int FullyVisible, int Partial, int Realized, Size Viewport,
        bool StartsAtFirstRow);
}
