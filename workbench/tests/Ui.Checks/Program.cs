using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FontBook.Workbench.Collections;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private const int LoadTimeoutSeconds = 90;
    private const int InteractionTimeoutSeconds = 30;
    private const string ShelfName = "Ui Checks Shelf";
    private const string NoMatchText = "未找到匹配字体";
    private const string LocalPathPattern = @"(?i)(?:\b[A-Z]:[\\/]|\\\\|file:/+)[^\r\n]*";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--private-ui-worker", var output])
            return RunOnDesktop([output]);
        if (args.Length != 1)
            return 2;
        try
        {
            return PrivateTestDesktop.Run(args[0]);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int RunOnDesktop(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("用法：Ui.Checks.exe <ignored artifacts/ 下的新输出目录>");
            return 1;
        }

        try
        {
            var output = CreateOutputDirectory(args[0]);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var diagnostics = new WpfDiagnostics(app);

            // Run from the WPF dispatcher so control events capture its synchronization context.
            app.Startup += async (_, _) =>
            {
                var exitCode = 1;
                try
                {
                    await RunChecksAsync(output, diagnostics);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    diagnostics.Check();
                    Pass("离屏 UI 回归完成，无 WPF BindingError。");
                    exitCode = 0;
                }
                catch (Exception ex) { ReportFailure(ex); }
                finally { app.Shutdown(exitCode); }
            };
            return app.Run();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return 1;
        }
    }

    private static async Task RunChecksAsync(string output, WpfDiagnostics diagnostics)
    {
        var temporary = Directory.CreateTempSubdirectory("FontBook-UiChecks-");
        var shelfPath = Path.Combine(temporary.FullName, "shelves.json");
        var failures = new List<string>();
        try
        {
            FamilyRow shelfFamily;
            IReadOnlyList<string> densityRegressions;
            using (var ui = WorkbenchUi.Create(shelfPath, diagnostics))
            {
                await ui.ShowAsync("初始窗口");
                shelfFamily = LatinFamily(ui);
                densityRegressions = await CheckFamilyDensityAsync(ui);
                await ui.ResizeAsync(1240, 800);
                await CheckStageAsync("多选与视图", () => CheckLibraryViewsAsync(ui, output));
                await CheckStageAsync("批量预检和取消", () => CheckManagementAsync(ui));
                await ui.SelectPreviewAsync(shelfFamily, switchStyle: true);
                Pass("选中真实家族及样式，标本含不透明像素。");
                await CheckSearchAsync(ui, shelfFamily);
                await ui.SelectPreviewAsync(shelfFamily);
                await CheckStageAsync("主题与墨色", () => CheckThemeAsync(ui, output));
                await CheckStageAsync("管理弹窗深浅色可读性", () => CheckManagementAppearanceAsync(ui.Window, output));
                await ui.PreviewAsync("恢复主题和标本内容");
                await CheckStageAsync("导航栏与分隔条", () => CheckSidebarAndSplitterAsync(ui));
                await CheckStageAsync("标本直接编辑", () => CheckSpecimenEditorAsync(ui, output));
                await ui.PreviewAsync("恢复内联编辑前的标本");
                await CheckStageAsync("OpenType 设置", () => CheckOpenTypeAsync(ui));
                await CheckStageAsync("字体详情", () => CheckFontInfoAsync(ui, output));

                var fullPreview = await ui.SelectPreviewAsync(shelfFamily);
                ui.Capture(Path.Combine(output, "workbench.png"));
                await ui.ResizeAsync(900, 600);
                await ui.PreviewAsync("紧凑布局重新渲染标本", fullPreview);
                ui.Capture(Path.Combine(output, "compact.png"));
            }

            var store = new ShelfStore(shelfPath);
            var created = store.Create(ShelfName, store.Read().Revision);
            store.SetMember(ShelfName, shelfFamily.Key, present: true, created.Revision);

            using var reopened = WorkbenchUi.Create(shelfPath, diagnostics);
            await reopened.ShowAsync("清单窗口");
            var installedFamilyCount = reopened.Families.Items.Count;
            await reopened.WaitAsync("读取预置清单", () => reopened.Shelves.Items.Contains(ShelfName));
            reopened.Shelves.SelectedItem = ShelfName;
            await reopened.WaitAsync("清单筛选及状态更新", () =>
                Equals(reopened.Shelves.SelectedItem, ShelfName) &&
                reopened.Scope.Text == ShelfName && reopened.ShowsCount(1) &&
                reopened.Rows.Single().Key == shelfFamily.Key &&
                reopened.ShelfHint.Text.StartsWith("清单记录 1 个家族", StringComparison.Ordinal),
                stableMilliseconds: 500);
            Pass("临时清单仅显示预置家族，范围、结果数和清单提示一致。");

            var before = await reopened.SelectPreviewAsync(reopened.Rows.Single());
            var pixels = ReadVisiblePixels(before);
            var newSize = reopened.Size.Value < 64 ? 64 : 38;
            reopened.Size.Value = newSize;
            var after = await reopened.PreviewAsync("调整字号后重新渲染标本", before);
            Require(reopened.SizeLabel.Text == $"{newSize} pt", "字号标签没有更新。");
            Require(!pixels.AsSpan().SequenceEqual(ReadVisiblePixels(after)), "字号变化后标本像素未改变。");
            Pass($"字号改为 {newSize} pt 后标本像素改变，且仍非透明。");

            Click(Find<Button>(reopened.Window, "AllButton"));
            await reopened.WaitAsync("离开清单并恢复安装目录", () =>
                reopened.ShowsCount(installedFamilyCount) && reopened.Scope.Text == "已安装字体");
            await CheckStageAsync("字形浏览与标本", () => CheckGlyphWorkflowAsync(reopened, output));
            await CheckStageAsync("主窗口私有字体卸载与恢复", () => CheckPrivateUninstallAsync(diagnostics, output));
            failures.AddRange(densityRegressions);
            Require(failures.Count == 0, string.Join("；", failures));
        }
        finally
        {
            File.Delete(shelfPath);
            temporary.Delete();
        }

        async Task CheckStageAsync(string stage, Func<Task> run)
        {
            Console.WriteLine($"STAGE: {stage}");
            try
            {
                await run();
            }
            catch (Exception ex) { Record(ex); }

            // Binding problems fail this stage and the suite. Collect them after the actual UI
            // assertions so one bad binding cannot mask other regressions in subsequent stages.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                diagnostics.Check();
            }
            catch (Exception ex) { Record(ex); }

            void Record(Exception ex)
            {
                if (!failures.Contains(stage))
                    failures.Add(stage);
                ReportFailure(new InvalidOperationException($"{stage}：{ex.Message}", ex));
            }
        }
    }

    private static async Task CheckSearchAsync(WorkbenchUi ui, FamilyRow target)
    {
        var allKeys = ui.Rows.Select(row => row.Key).ToArray();
        var query = target.Name.Trim();
        var alternate = ui.Rows.Select(row => row.Name).FirstOrDefault(name => name != target.Name);
        var absent = $"fontbook-ui-check-{Guid.NewGuid():N}";

        ui.Search.Text = query;
        await ui.WaitAsync("取得已知家族的参照搜索结果", () =>
            ui.ShowsCount(ui.Families.Items.Count, search: true) && ui.Rows.Any(row => row.Key == target.Key));
        var expectedKeys = ui.Rows.Select(row => row.Key).ToArray();

        ui.Search.Text = absent;
        await ui.WaitAsync("无匹配状态（包括清空旧家族、样式及标本）", () =>
            ui.Families.ItemsSource is not null && ui.Families.Items.Count == 0 &&
            ui.Families.SelectedItem is null && ui.Styles.Items.Count == 0 &&
            ui.Specimen.Source is null && ui.Result.Text.StartsWith(NoMatchText, StringComparison.Ordinal),
            stableMilliseconds: 250);
        Pass("无匹配搜索清空了列表、选择和标本。");

        // Let one edit cross the app's debounce, then supersede it with a burst of edits.
        ui.Search.Text = alternate ?? absent + "-alternate";
        await Task.Delay(90);
        for (var i = 0; i < 6; i++)
        {
            ui.Search.Text = (i % 2 != 0 ? alternate : null) ?? $"{absent}-{i}";
            await Task.Delay(15);
        }
        ui.Search.Text = query;
        await ui.WaitAsync("快速输入后，仅最终查询的结果和顺序可见", () =>
            ui.Search.Text == query && ui.ShowsCount(expectedKeys.Length, search: true) &&
            ui.Rows.Select(row => row.Key).SequenceEqual(expectedKeys), stableMilliseconds: 600);
        Pass("快速连续输入后，最终查询的完整结果和顺序与独立查询一致。");

        ui.Search.Clear();
        await ui.WaitAsync("清空搜索并恢复完整目录和状态", () =>
            ui.Search.Text.Length == 0 && ui.ShowsCount(allKeys.Length) &&
            ui.Rows.Select(row => row.Key).SequenceEqual(allKeys), stableMilliseconds: 250);
        Pass("清空搜索后恢复全目录，结果状态不陈旧。");
    }

    private sealed class WorkbenchUi(MainWindow window, WpfDiagnostics diagnostics, NoWriteManagement management) : IDisposable
    {
        public MainWindow Window { get; } = window;
        public NoWriteManagement Management { get; } = management;
        public ListBox Families { get; } = Find<ListBox>(window, "FamilyList");
        public ListBox Shelves { get; } = Find<ListBox>(window, "ShelfList");
        public ComboBox Styles { get; } = Find<ComboBox>(window, "FacePicker");
        public TextBox Search { get; } = Find<TextBox>(window, "SearchBox");
        public Image Specimen { get; } = Find<Image>(window, "SpecimenImage");
        public Slider Size { get; } = Find<Slider>(window, "SizeSlider");
        public TextBlock SizeLabel { get; } = Find<TextBlock>(window, "SizeText");
        public TextBlock Scope { get; } = Find<TextBlock>(window, "ScopeTitle");
        public TextBlock Result { get; } = Find<TextBlock>(window, "ResultText");
        public TextBlock ShelfHint { get; } = Find<TextBlock>(window, "ShelfHint");
        private TextBlock FamilyTitle { get; } = Find<TextBlock>(window, "FamilyTitle");
        public TextBlock FaceDetail { get; } = Find<TextBlock>(window, "FaceDetail");
        private TextBlock Status { get; } = Find<TextBlock>(window, "StatusText");
        private TextBlock PreviewNote { get; } = Find<TextBlock>(window, "PreviewNote");
        private Button Retry { get; } = Find<Button>(window, "RetryButton");

        public IEnumerable<FamilyRow> Rows => Families.Items.Cast<FamilyRow>();

        public static WorkbenchUi Create(string shelfPath, WpfDiagnostics diagnostics)
        {
            var management = new NoWriteManagement();
            return new WorkbenchUi(new MainWindow(shelfPath, management)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 1500,
                Top = SystemParameters.VirtualScreenTop - 1000,
                Width = 1240,
                Height = 800,
                ShowInTaskbar = false,
                ShowActivated = false
            }, diagnostics, management);
        }

        public async Task ShowAsync(string context)
        {
            Console.WriteLine($"STAGE: {context}：离屏加载实际目录（上限 {LoadTimeoutSeconds}s）。");
            Window.Show();
            RequireOffscreen(Window);
            await WaitAsync($"{context}：读取真实目录", () => Families.ItemsSource is not null &&
                Families.Items.Count > 0 && ShowsCount(Families.Items.Count), LoadTimeoutSeconds, 250);
            Pass($"{context}：真实目录 {Families.Items.Count} 个家族。");
        }

        public bool ShowsCount(int count, bool search = false) => Families.Items.Count == count &&
            Result.Text == $"{count:N0} 个家族{(search ? "　·　搜索结果" : "")}";

        public bool HasOuterSize(int width, int height) =>
            Math.Abs(Window.ActualWidth - width) < 0.5 && Math.Abs(Window.ActualHeight - height) < 0.5;

        public async Task ResizeAsync(int width, int height)
        {
            Window.Width = width;
            Window.Height = height;
            await WaitAsync($"应用 {width}×{height} DIP 外窗", () => HasOuterSize(width, height));
        }

        public async Task<BitmapSource> SelectPreviewAsync(FamilyRow family, bool switchStyle = false)
        {
            var previous = Equals(Families.SelectedItem, family) ? null : Specimen.Source;
            Families.SelectedItem = family;
            Families.ScrollIntoView(family);
            await WaitAsync("读取选中家族及样式", () => Equals(Families.SelectedItem, family) &&
                FamilyTitle.Text == family.Name && Styles.SelectedItem is StyleRow);
            if (switchStyle && Styles.Items.Count > 1)
            {
                previous = Specimen.Source;
                Styles.SelectedIndex = (Styles.SelectedIndex + 1) % Styles.Items.Count;
            }
            return await PreviewAsync("所选家族及样式的真实标本", previous);
        }

        public async Task<BitmapSource> PreviewAsync(string phase, ImageSource? previous = null)
        {
            await WaitAsync(phase, () => Styles.SelectedItem is StyleRow &&
                Specimen.Source is BitmapSource bitmap && !ReferenceEquals(previous, bitmap),
                LoadTimeoutSeconds, stableMilliseconds: 200);
            Window.UpdateLayout();
            Require(Specimen.ActualWidth > 0 && Specimen.ActualHeight > 0, $"{phase}：标本未布局。");
            var source = (BitmapSource)Specimen.Source;
            ReadVisiblePixels(source);
            return source;
        }

        public async Task WaitAsync(string phase, Func<bool> ready, int timeout = InteractionTimeoutSeconds,
            int stableMilliseconds = 0)
        {
            var clock = Stopwatch.StartNew();
            long? readySince = null;
            while (clock.Elapsed.TotalSeconds < timeout)
            {
                diagnostics.CheckAsyncErrors();
                Require(Window.IsVisible, $"{phase}：测试窗口提前关闭。");
                if (Retry.IsVisible || Result.Text.Contains("失败", StringComparison.Ordinal) ||
                    FaceDetail.Text is "样式不可用" or "无法读取此字面，不使用替代字体。" ||
                    PreviewNote.IsVisible && PreviewNote.Text.Contains("失败", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{phase}：应用报告错误。{DescribeState()}");
                }

                if (ready())
                {
                    readySince ??= clock.ElapsedMilliseconds;
                    if (clock.ElapsedMilliseconds - readySince >= stableMilliseconds)
                        return;
                }
                else
                {
                    readySince = null;
                }

                await Task.Delay(40);
            }

            throw new TimeoutException($"{phase}，等待超过 {timeout}s。{DescribeState()}");
        }

        private string DescribeState() =>
            $" 家族={Families.Items.Count}，样式={Styles.Items.Count}；" +
            $"结果={Result.Text}；状态={Status.Text}；清单={ShelfHint.Text}；预览={PreviewNote.Text}";

        public void Capture(string file) => CaptureRedactedWindow(Window, file);

        public void Dispose() => Window.Close();
    }

    private static void CaptureRedactedWindow(Window window, string file)
    {
        window.UpdateLayout();
        var replacements = new List<(FrameworkElement Element, DependencyProperty Property, string Text)>();
        try
        {
            foreach (var element in VisualDescendants<FrameworkElement>(window).Where(element => element.IsVisible).ToArray())
            {
                var property = element switch
                {
                    TextBlock => TextBlock.TextProperty,
                    TextBox => TextBox.TextProperty,
                    ContentControl => ContentControl.ContentProperty,
                    _ => null
                };
                if (property is null || element.GetValue(property) is not string text || !Regex.IsMatch(text, LocalPathPattern))
                    continue;
                replacements.Add((element, property, text));
                element.SetCurrentValue(property, Regex.Replace(text, LocalPathPattern, "（本机路径已隐藏）"));
            }
            CaptureWindow(window, file);
        }
        finally
        {
            foreach (var (element, property, text) in replacements.AsEnumerable().Reverse())
                element.SetCurrentValue(property, text);
        }
    }

    private static void CaptureWindow(Window window, string file)
    {
        window.UpdateLayout();
        // The window template owns the client background and the margins around its content.
        var root = VisualTreeHelper.GetChildrenCount(window) > 0 &&
            VisualTreeHelper.GetChild(window, 0) is FrameworkElement client
            ? client
            : throw new InvalidOperationException("无法截取窗口：没有可视客户区。");
        var containsPath = VisualDescendants<FrameworkElement>(root).Any(element => element.IsVisible &&
            Regex.IsMatch(element switch
            {
                TextBlock text => text.Text,
                TextBox text => text.Text,
                ContentControl { Content: string text } => text,
                _ => ""
            }, LocalPathPattern));
        Require(!containsPath, "截图客户区包含未遮蔽的本机路径。");
        var dpi = VisualTreeHelper.GetDpi(root);
        var width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        Require(width > 0 && height > 0, "截图内容尚未布局。");
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        png.Save(stream);
        Require(stream.Length > 0, "截图编码结果为空。");
        Pass($"{Path.GetFileName(file)}：{width}×{height} px（仅窗口客户区）。");
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in VisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private static byte[] ReadVisiblePixels(BitmapSource source, string context = "标本")
    {
        var bitmap = source.Format == PixelFormats.Pbgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return pixels;
        }
        throw new InvalidOperationException($"{context}位图完全透明。");
    }

    private sealed class WpfDiagnostics : IDisposable
    {
        private readonly Application _app;
        private readonly TraceSource _binding;
        private readonly StringWriter _messages = new(CultureInfo.InvariantCulture);
        private readonly TextWriterTraceListener _listener;
        private readonly SourceLevels _previousLevel;
        private Exception? _unhandled;

        public WpfDiagnostics(Application app)
        {
            _app = app;
            PresentationTraceSources.Refresh();
            _binding = PresentationTraceSources.DataBindingSource;
            _previousLevel = _binding.Switch.Level;
            _listener = new TextWriterTraceListener(_messages);
            _binding.Listeners.Add(_listener);
            _binding.Switch.Level = SourceLevels.Error;
            _app.DispatcherUnhandledException += OnDispatcherError;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        }

        public void CheckAsyncErrors()
        {
            if (Interlocked.Exchange(ref _unhandled, null) is { } error)
                throw new InvalidOperationException($"WPF 异步异常：{error.GetBaseException().Message}", error);
        }

        public void Check()
        {
            CheckAsyncErrors();
            var trace = _messages.ToString();
            _messages.GetStringBuilder().Clear();
            if (trace.Length == 0)
                return;
            var lines = trace.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            throw new InvalidOperationException($"WPF BindingError（{lines.Length} 条）：" +
                string.Join("；", lines.Distinct(StringComparer.Ordinal).Take(4)));
        }

        private void OnDispatcherError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Interlocked.CompareExchange(ref _unhandled, e.Exception, null);
            e.Handled = true; // The check fails with the original error instead of an unhandled-exception dialog.
        }

        private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Interlocked.CompareExchange(ref _unhandled, e.Exception, null);
            e.SetObserved();
        }

        public void Dispose()
        {
            _app.DispatcherUnhandledException -= OnDispatcherError;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
            _binding.Listeners.Remove(_listener);
            _binding.Switch.Level = _previousLevel;
            _listener.Dispose();
            _messages.Dispose();
        }
    }

    private static string CreateOutputDirectory(string requested)
    {
        var output = new DirectoryInfo(Path.GetFullPath(requested));
        Require(!Path.Exists(output.FullName), "截图输出目录必须是新的，不能覆盖现有文件或目录。");
        var insideArtifacts = false;
        for (var parent = output.Parent; parent is not null; parent = parent.Parent)
        {
            Require(!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) == 0,
                "截图输出路径不能经过目录链接。");
            if (Path.Exists(Path.Combine(parent.FullName, ".git")))
            {
                Require(insideArtifacts, "截图必须留在仓库忽略的 artifacts/ 下。");
                output.Create();
                return output.FullName;
            }
            insideArtifacts |= parent.Name.Equals("artifacts", StringComparison.OrdinalIgnoreCase);
        }
        throw new InvalidOperationException("截图输出目录不在 Git 工作树的 artifacts/ 下。");
    }

    private static T Find<T>(Window window, string name) where T : DependencyObject =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"缺少控件 {name}（{typeof(T).Name}）。");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Pass(string message) => Console.WriteLine($"PASS: {message}");

    private static void ReportFailure(Exception error)
    {
        Console.Error.WriteLine($"FAIL: {error.GetType().Name}: " +
            Regex.Replace(error.Message, LocalPathPattern, "<local-path>").ReplaceLineEndings(" "));
        if (error.GetBaseException().StackTrace is { Length: > 0 } stack)
            Console.Error.WriteLine(Regex.Replace(stack, LocalPathPattern, "<local-path>"));
    }
}
