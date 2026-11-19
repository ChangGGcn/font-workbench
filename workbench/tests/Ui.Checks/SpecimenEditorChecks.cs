using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static async Task CheckSpecimenEditorAsync(WorkbenchUi ui, string output)
    {
        var sample = Find<TextBox>(ui.Window, "SampleBox");
        var viewport = Find<FrameworkElement>(ui.Window, "SpecimenViewport");
        var overlay = Find<Canvas>(ui.Window, "EditOverlay");
        var original = sample.Text;
        var originalSize = ui.Size.Value;
        const string fixture = "HHHH";
        const string replacement = "Ag";
        var pointer = typeof(MainWindow).GetProperty("EditorPointerForChecks", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("缺少离屏标本点击坐标钩子。");
        Require(viewport.IsAncestorOf(sample) && viewport.IsAncestorOf(ui.Specimen) && viewport.IsAncestorOf(overlay) &&
            sample.Foreground is SolidColorBrush { Color.A: 0 } && !overlay.IsHitTestVisible,
            "标本视口应包含透明 SampleBox、真实标本和不截获点击的 EditOverlay。");
        try
        {
            var previous = ui.Specimen.Source;
            ui.Size.Value = 36;
            sample.Text = fixture; // Establish the fixture only. Subsequent edits go through WPF input.
            sample.Select(fixture.Length, 0);
            var baseline = await ui.PreviewAsync("载入内联编辑的参考排版", previous);
            var originalPixels = ReadVisiblePixels(baseline);
            var ink = InkBounds(baseline, originalPixels);
            var y = (ink.Y + ink.Height / 2.0) * 96 / baseline.DpiY;
            var leading = new Point(Math.Max(1, ink.X * 96 / baseline.DpiX - 4), y);
            var middle = new Point((ink.X + ink.Width / 2.0) * 96 / baseline.DpiX, y);
            var trailing = new Point(Math.Min(overlay.ActualWidth - 1, (ink.X + ink.Width) * 96 / baseline.DpiX + 8), y);

            ClickSpecimen(leading, ModifierKeys.None);
            await ui.WaitAsync("点击视口字首定位光标", () => sample.CaretIndex == 0 && sample.SelectionLength == 0 &&
                OverlayRects("Caret").Length == 1 && OverlayRects("Selection").Length == 0);
            var startCaret = Canvas.GetLeft(OverlayRects("Caret").Single());

            // Leave unselected glyphs to check their AA independently of the visible highlight.
            RequireOffscreen(ui.Window);
            ClickSpecimen(middle, ModifierKeys.Shift);
            await ui.WaitAsync("Shift 点击选中前两个字形", () => sample.SelectionStart == 0 &&
                sample.SelectionLength == 2 && OverlayRects("Selection").Length > 0);
            var partial = CheckRenderedSpecimen(ui, baseline, "Shift 半选标本后");
            Require(partial.SelectionPixels >= 16, "选区没有可见的实际高亮像素。");

            ClickSpecimen(trailing, ModifierKeys.Shift);
            await ui.WaitAsync("Shift 点击选择标本并绘制覆盖层", () => sample.SelectionStart == 0 &&
                sample.SelectionLength == fixture.Length && OverlayRects("Selection").Length > 0 &&
                OverlayRects("Caret").Length == 1);
            var caret = OverlayRects("Caret").Single();
            var highlight = OverlayRects("Selection");
            Require(Canvas.GetLeft(caret) > startCaret + 8 && caret.Height > 1 && caret.Opacity > 0 &&
                highlight.Sum(rect => rect.Width) > 12 && highlight.All(rect => !rect.IsHitTestVisible),
                "标本选区没有在字形范围内展开，或插入点未跟随选择端点。");
            RequireOffscreen(ui.Window);
            ui.Capture(Path.Combine(output, "editor.png"));

            var composition = new TextComposition(InputManager.Current, sample, replacement, TextCompositionAutoComplete.On);
            TextCompositionManager.StartComposition(composition);
            await ui.WaitAsync("WPF TextInput 替换标本选区", () => sample.Text == replacement &&
                sample.SelectionLength == 0 && sample.CaretIndex == replacement.Length && sample.CanUndo);
            var edited = await ui.PreviewAsync("输入后更新 DirectWrite 标本", baseline);
            Require(!originalPixels.AsSpan().SequenceEqual(ReadVisiblePixels(edited)), "文本输入后标本像素没有改变。");
            Require(OverlayRects("Selection").Length == 0 && OverlayRects("Caret").Length == 1,
                "输入替换选区后应只保留插入点。");
            CheckRenderedSpecimen(ui, edited, "WPF TextInput 替换标本后");

            WpfKey(sample, Key.Z, ModifierKeys.Control);
            await ui.WaitAsync("Ctrl+Z 撤销内联编辑", () => sample.Text == fixture);
            var undone = await ui.PreviewAsync("撤销后恢复 DirectWrite 标本", edited);
            Require(originalPixels.AsSpan().SequenceEqual(ReadVisiblePixels(undone)), "撤销文本后没有恢复参考排版的像素。");
            Pass("离屏标本响应点击、Shift 选区、WPF TextInput 和 Ctrl+Z；插入点、选区及真实位图均更新。");
        }
        finally
        {
            FocusInWpf(null);
            sample.Text = original;
            ui.Size.Value = originalSize;
        }

        Rectangle[] OverlayRects(string kind) => overlay.Children.OfType<Rectangle>()
            .Where(rect => Equals(rect.Tag, kind) && rect.Width > 0 && rect.Height > 0 && (kind == "Caret" || rect.Opacity > 0))
            .ToArray();

        void ClickSpecimen(Point position, ModifierKeys modifiers)
        {
            var hit = viewport.InputHitTest(overlay.TranslatePoint(position, viewport)) as UIElement;
            if (hit is null || (!ReferenceEquals(hit, sample) && !sample.IsAncestorOf(hit)))
                throw new InvalidOperationException("标本区域内的鼠标命中没有到达透明 SampleBox。");
            FocusInWpf(sample);
            // The desktop test hook substitutes coordinates, not the editor's routed event handling.
            pointer.SetValue(ui.Window, (Func<MouseEventArgs, Point>)(_ => position));
            try
            {
                WpfMouseClick(hit, modifiers);
            }
            finally { pointer.SetValue(ui.Window, null); }
        }
    }

    private static Int32Rect InkBounds(BitmapSource bitmap, byte[] pixels)
    {
        int left = bitmap.PixelWidth, top = bitmap.PixelHeight, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                if (pixels[(y * bitmap.PixelWidth + x) * 4 + 3] < 32)
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        Require(right >= left && bottom >= top, "标本缺少可用于定位鼠标的墨色像素。");
        return new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }
}
