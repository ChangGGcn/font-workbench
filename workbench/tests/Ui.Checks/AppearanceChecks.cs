using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static async Task CheckThemeAsync(WorkbenchUi ui, string output)
    {
        Require(!SystemParameters.HighContrast, "深浅色检验需要系统未启用高对比度；测试不会修改系统设置。");
        var picker = Find<ComboBox>(ui.Window, "ThemePicker");
        var viewport = Find<Border>(ui.Window, "SpecimenViewport");
        var sample = Find<TextBox>(ui.Window, "SampleBox");
        var originalChoice = picker.SelectedIndex;
        var originalText = sample.Text;
        var light = FindChoice(picker, "Light", "浅色");
        var dark = FindChoice(picker, "Dark", "深色");
        var system = FindChoice(picker, "System", "跟随系统", "系统");
        try
        {
            var prior = ui.Specimen.Source;
            sample.Text = "WAVY Ag 0123";
            picker.SelectedIndex = light;
            var lightBitmap = await ui.PreviewAsync("浅色模式及标本", prior);
            var lightBackground = SurfaceColor();
            CheckInk(lightBitmap, lightBackground, darkMode: false);
            var lightChrome = ChromeColor();
            ui.Capture(Path.Combine(output, "light.png"));

            picker.SelectedIndex = dark;
            var darkBitmap = await ui.PreviewAsync("深色模式及标本", lightBitmap);
            var darkBackground = SurfaceColor();
            CheckInk(darkBitmap, darkBackground, darkMode: true);
            Require(Luma(lightChrome) > Luma(ChromeColor()) + 100,
                "切换深色时没有更新应用界面的纸面颜色。");
            ui.Capture(Path.Combine(output, "dark.png"));

            // Read the current Windows preference; don't change it. Enter Follow System from
            // the opposite app theme to prove that selecting it takes effect.
            using var personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            var windowsDark = personalize?.GetValue("AppsUseLightTheme") is 0;
            if (windowsDark)
            {
                picker.SelectedIndex = light;
                lightBitmap = await ui.PreviewAsync("跟随系统之前先切成浅色", darkBitmap);
            }
            picker.SelectedIndex = system;
            var follow = await ui.PreviewAsync("应用跟随当前 Windows 主题", windowsDark ? lightBitmap : darkBitmap);
            var followBackground = SurfaceColor();
            Require(followBackground == (windowsDark ? darkBackground : lightBackground),
                "跟随系统没有采用 Windows 当前的应用主题。");
            CheckInk(follow, followBackground, windowsDark);
            Pass("应用浅色、深色及跟随 Windows 主题均生效；真实标本有对比度足够的黑/白墨色与中间 alpha 边缘。");
            await CheckSpecimenAaAsync(ui, output, picker, light, dark);
        }
        finally
        {
            picker.SelectedIndex = originalChoice;
            sample.Text = originalText;
        }

        Color SurfaceColor() => viewport.Background is SolidColorBrush brush ? brush.Color
            : throw new InvalidOperationException("标本缺少用于对比度检查的底色。");
        Color ChromeColor() => ui.Window.Background is SolidColorBrush brush ? brush.Color
            : throw new InvalidOperationException("应用缺少纸面主题画刷。");
    }

    private static void CheckInk(BitmapSource source, Color background, bool darkMode)
    {
        var bytes = ReadVisiblePixels(source);
        var core = 0;
        var correctCore = 0;
        var correctEdge = 0;
        var mode = darkMode ? "深色" : "浅色";
        var ground = Luma(background);
        Require(background.A == 255 && (darkMode ? ground < 90 : ground > 175),
            $"{mode}模式的标本底色不符合预期。");
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var a = bytes[i + 3];
            var b = bytes[i];
            var g = bytes[i + 1];
            var r = bytes[i + 2];
            if (a == 0)
                continue;
            Require(b <= a + 1 && g <= a + 1 && r <= a + 1, $"{mode}标本含错误的预乘 alpha 像素。");
            var ink = (r * 54 + g * 183 + b * 19) * 255 / (256 * a);
            var correct = darkMode ? ink >= 204 : ink <= 64;
            if (a >= 240)
            {
                core++;
                if (correct && Math.Abs(ink - ground) >= 145)
                    correctCore++;
            }
            else if (a is >= 32 and <= 223 && correct)
            {
                correctEdge++;
            }
        }
        Require(core > 32 && correctCore * 10 >= core * 9 && correctEdge > 16,
            $"{mode}标本不具备可读墨色或抗锯齿边缘（核心={correctCore}/{core}，软边={correctEdge}）。");
    }

    private static int Luma(Color c) => (c.R * 54 + c.G * 183 + c.B * 19) / 256;

    private static int FindChoice(ComboBox picker, params string[] names)
    {
        for (var i = 0; i < picker.Items.Count; i++)
        {
            var item = picker.Items[i];
            if (item is ComboBoxItem container && (Matches(container.Tag) || Matches(container.Content)) || Matches(item))
                return i;
        }
        throw new InvalidOperationException($"{picker.Name} 缺少选项 {string.Join(" / ", names)}。");

        bool Matches(object? value) => value?.ToString() is { } text && names.Contains(text, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task CheckSidebarAndSplitterAsync(WorkbenchUi ui)
    {
        var nav = Find<ColumnDefinition>(ui.Window, "NavColumn");
        var library = Find<ColumnDefinition>(ui.Window, "LibraryColumn");
        var inspector = Find<ColumnDefinition>(ui.Window, "InspectorColumn");
        var toggle = Find<Button>(ui.Window, "NavToggleButton");
        var splitter = Find<GridSplitter>(ui.Window, "InspectorSplitter");
        ui.Window.UpdateLayout();
        var expanded = nav.ActualWidth;
        Require(expanded >= 100, "侧栏测试需要初始可用的展开栏。");
        Click(toggle);
        await ui.WaitAsync("收合侧栏并重新布局", () => nav.ActualWidth <= expanded - 48 &&
            library.ActualWidth > 0 && inspector.ActualWidth > 0 && toggle.IsVisible);
        Click(toggle);
        await ui.WaitAsync("侧栏复原到原宽度", () => Math.Abs(nav.ActualWidth - expanded) <= 2 &&
            ui.Shelves.IsVisible && splitter.IsVisible);

        var originalLibrary = library.ActualWidth;
        var originalInspector = inspector.ActualWidth;
        var prior = await ui.PreviewAsync("调整检查器之前的标本尺寸");
        Require(splitter.ActualWidth >= 3 && splitter.ActualHeight > 80 && splitter.IsEnabled,
            "检查器分隔条没有可拖动的可见区域。");
        var delta = originalInspector - inspector.MinWidth >= 56 ? 52 : -52;
        Drag(delta);
        await ui.WaitAsync("拖动分隔条改变库区和检查器宽度", () =>
            Math.Abs(inspector.ActualWidth - originalInspector) >= 24 &&
            Math.Abs(library.ActualWidth - originalLibrary) >= 24 &&
            Math.Abs(inspector.ActualWidth + library.ActualWidth - originalInspector - originalLibrary) <= 2);
        Require(inspector.ActualWidth >= inspector.MinWidth - 1 && library.ActualWidth >= library.MinWidth - 1,
            "分隔条拖动后打破了列的最小宽度。");
        var resized = await ui.PreviewAsync("检查器改宽后重新排版标本", prior);
        Require(Math.Abs(resized.PixelWidth - prior.PixelWidth) >= 16, "调整分隔条没有改变标本实际位图宽度。");
        Drag(-delta);
        await ui.WaitAsync("反向拖动恢复列宽", () => Math.Abs(inspector.ActualWidth - originalInspector) <= 3 &&
            Math.Abs(library.ActualWidth - originalLibrary) <= 3);
        var restored = await ui.PreviewAsync("检查器复原后重新排版标本", resized);
        Require(Math.Abs(restored.PixelWidth - prior.PixelWidth) <= 4, "检查器复原后标本位图宽度没有复原。");
        Pass("侧栏可收合并恢复，分隔条重新分配库区/检查器宽度，标本随检查器尺寸重绘。");

        void Drag(double horizontal)
        {
            splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            splitter.RaiseEvent(new DragDeltaEventArgs(horizontal, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            splitter.RaiseEvent(new DragCompletedEventArgs(horizontal, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        }
    }
}
