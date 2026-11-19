using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static async Task CheckSpecimenAaAsync(WorkbenchUi ui, string output, ComboBox picker, int light, int dark)
    {
        var sample = Find<TextBox>(ui.Window, "SampleBox");
        var originalDpi = VisualTreeHelper.GetDpi(ui.Window);
        var originalSize = ui.Size.Value;
        var originalText = sample.Text;
        var originalTheme = picker.SelectedIndex;
        var originalFamily = (FamilyRow)ui.Families.SelectedItem;
        var originalStyle = ui.Styles.SelectedIndex;
        var originalFocus = Keyboard.FocusedElement as UIElement;
        RequireOffscreen(ui.Window);
        try
        {
            await CheckFixturesAsync("aa", "AVOS", [12, 48]);
            Pass("拉丁标本 100% / 150%、12 pt / 48 pt、深浅色实际合成像素吻合预乘墨色，聚焦只增加插入点。");

            string[] cjkNames = ["Microsoft YaHei", "微软雅黑", "Microsoft YaHei UI", "DengXian", "等线", "SimSun", "宋体", "Noto Sans CJK SC", "Source Han Sans SC"];
            var cjk = cjkNames.Select(name => ui.Rows.FirstOrDefault(row =>
                name.Equals(row.Key, StringComparison.OrdinalIgnoreCase) || name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(row => row is not null);
            if (cjk is null)
            {
                Console.WriteLine("SKIP: 本机未找到用于小字号标本回归的可选中文家族。");
                return;
            }
            await ui.SelectPreviewAsync(cjk);
            if (ui.Styles.Items.OfType<StyleRow>().FirstOrDefault(style => style.Weight == 400) is { } regular)
                ui.Styles.SelectedItem = regular;
            await CheckFixturesAsync("aa-cjk", "永字 体态清晰", [12]);
            Pass($"{cjk.Name} {ui.Styles.SelectedItem} 的 12 pt 汉字无缺字，并通过 100% / 150% 深浅色合成和焦点检查。");
        }
        finally
        {
            FocusInWpf(originalFocus);
            SetDpi(originalDpi);
            ui.Size.Value = originalSize;
            sample.Text = originalText;
            picker.SelectedIndex = originalTheme;
            if (!Equals(ui.Families.SelectedItem, originalFamily))
            {
                await ui.SelectPreviewAsync(originalFamily);
                ui.Styles.SelectedIndex = originalStyle;
            }
        }

        async Task CheckFixturesAsync(string prefix, string text, int[] sizes)
        {
            foreach (int percent in new[] { 100, 150 })
            {
                // Change only the private WPF tree, never the monitor or the user's settings.
                SetDpi(new DpiScale(percent / 100d, percent / 100d));
                foreach (var (choice, mode) in new[] { (light, "light"), (dark, "dark") })
                foreach (int pt in sizes)
                {
                    FocusInWpf(ui.Search);
                    var prior = ui.Specimen.Source;
                    picker.SelectedIndex = choice;
                    ui.Size.Value = pt;
                    sample.Text = text;
                    sample.Select(sample.Text.Length, 0);
                    string fixture = $"{prefix}-{percent}-{mode}-{pt}pt";
                    var bitmap = await ui.PreviewAsync(fixture, prior);
                    Require(Math.Abs(bitmap.DpiX - 96 * percent / 100d) < .01 && !sample.IsKeyboardFocusWithin &&
                        !Find<Button>(ui.Window, "CopyMissingButton").IsEnabled,
                        $"{fixture} 没有以指定 DPI 绘制失焦标本，或选中字面缺字。");
                    var unfocused = CheckRenderedSpecimen(ui, bitmap, fixture, Path.Combine(output, fixture + "-blur.png"));

                    FocusInWpf(sample);
                    Require(sample.IsKeyboardFocused && sample.SelectionLength == 0 && ReferenceEquals(bitmap, ui.Specimen.Source),
                        $"{fixture} 聚焦时不应切换到另一份标本或选中文字。");
                    var focused = CheckRenderedSpecimen(ui, bitmap, fixture, Path.Combine(output, fixture + "-focus.png"));
                    Require(focused.CaretPixels > 0 && focused.SelectionPixels == 0 && unfocused == (0, 0),
                        $"{fixture} 聚焦后应仅多出可见的插入点。");
                }
            }
        }

        void SetDpi(DpiScale dpi)
        {
            VisualTreeHelper.SetRootDpi(ui.Window, dpi);
            ui.Window.InvalidateMeasure();
            ui.Window.UpdateLayout();
        }
    }

    private static (int CaretPixels, int SelectionPixels) CheckRenderedSpecimen(
        WorkbenchUi ui, BitmapSource source, string phase, string? file = null)
    {
        ui.Window.UpdateLayout();
        var viewport = Find<Border>(ui.Window, "SpecimenViewport");
        var overlay = Find<Canvas>(ui.Window, "EditOverlay");
        var client = (FrameworkElement)VisualTreeHelper.GetChild(ui.Window, 0);
        var dpi = VisualTreeHelper.GetDpi(viewport);
        int width = (int)Math.Round(viewport.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(viewport.ActualHeight * dpi.DpiScaleY);
        var origin = viewport.TranslatePoint(new Point(), client);
        Require(Aligned(origin.X * dpi.DpiScaleX) && Aligned(origin.Y * dpi.DpiScaleY) &&
            Math.Abs(source.DpiX - dpi.PixelsPerInchX) < .01 && Math.Abs(source.DpiY - dpi.PixelsPerInchY) < .01 &&
            ui.Specimen.TranslatePoint(new Point(), viewport) == new Point() &&
            source.PixelWidth <= width && source.PixelHeight <= height,
            $"{phase} 标本未以设备像素对齐，或位图被缩放/裁切。");
        Require(viewport.Background is SolidColorBrush { Color.A: 255, Opacity: 1 }, $"{phase} 缺少不透明标本底色。");
        var ground = ((SolidColorBrush)viewport.Background).Color;
        var screen = new RenderTargetBitmap((int)Math.Ceiling(client.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(client.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Render the real client tree, then crop in physical pixels (a nested Visual keeps its offset).
        screen.Render(client);
        var rendered = new CroppedBitmap(screen, new Int32Rect((int)Math.Round(origin.X * dpi.DpiScaleX),
            (int)Math.Round(origin.Y * dpi.DpiScaleY), width, height));
        var expected = ReadVisiblePixels(source, phase);
        byte[] actual = new byte[width * height * 4];
        rendered.CopyPixels(actual, width * 4, 0);
        var decorations = overlay.Children.OfType<Rectangle>().Where(r => r.Opacity > 0)
            .Select(r => (r.Tag, Bounds: r.TransformToAncestor(viewport).TransformBounds(new Rect(r.RenderSize)))).ToArray();

        int edgePixels = 0, mismatches = 0, caretPixels = 0, selectionPixels = 0, maxError = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (actual[i + 3] != 255)
                    mismatches++;
                int j = x < source.PixelWidth && y < source.PixelHeight ? (y * source.PixelWidth + x) * 4 : -1;
                int a = j < 0 ? 0 : expected[j + 3];
                int error = Math.Max(Math.Abs(actual[i] - Composite(0, ground.B)), Math.Max(
                    Math.Abs(actual[i + 1] - Composite(1, ground.G)), Math.Abs(actual[i + 2] - Composite(2, ground.R))));
                if (error > 1 && InDecoration("Caret", x, y))
                {
                    caretPixels++;
                    continue;
                }
                if (error > 1 && InDecoration("Selection", x, y))
                {
                    selectionPixels++;
                    continue;
                }
                edgePixels += a is >= 4 and <= 251 ? 1 : 0;
                maxError = Math.Max(maxError, error);
                if (error > 2)
                    mismatches++;

                int Composite(int channel, byte background) => (j < 0 ? 0 : expected[j + channel]) + (background * (255 - a) + 127) / 255;
            }
        }
        if (file is not null)
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(rendered));
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            png.Save(stream);
        }
        Require(edgePixels >= 16 && mismatches == 0,
            $"{phase} WPF 合成像素与原生覆盖不一致（软边 {edgePixels}，不匹配 {mismatches}，最大通道误差 {maxError}）。");
        return (caretPixels, selectionPixels);

        bool InDecoration(string tag, int x, int y) => decorations.Any(d => Equals(d.Tag, tag) &&
            x >= Math.Floor(d.Bounds.Left * dpi.DpiScaleX) && x < Math.Ceiling(d.Bounds.Right * dpi.DpiScaleX) &&
            y >= Math.Floor(d.Bounds.Top * dpi.DpiScaleY) && y < Math.Ceiling(d.Bounds.Bottom * dpi.DpiScaleY));
        static bool Aligned(double deviceCoordinate) => Math.Abs(deviceCoordinate - Math.Round(deviceCoordinate)) < .01;
    }
}
