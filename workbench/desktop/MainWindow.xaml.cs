using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FontBook.Workbench.Collections;

namespace FontBook.Workbench;

public partial class MainWindow : Window
{
    private readonly LatestWork _scan = new(), _query = new(), _styles = new(), _choose = new(), _paint = new();
    private readonly ShelfStore _store;
    private ShelfSnapshot _shelves = new(0, new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
    private string? _scope;
    private string[]? _filePaths;
    private FontCatalog? _catalog;
    private FaceSelection? _face;
    private FamilyRow? _shownFamily;
    private float[] _axisValues = [];
    private bool _closed, _syncingShelves, _shelfBusy, _shelvesReady;
    private string? _restoreKey;

    public MainWindow() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FontBook.Workbench", "shelves.v1.json")) { }
    public MainWindow(string shelfStatePath)
    {
        _store = new ShelfStore(shelfStatePath);
        InitializeComponent();
        SyncContrast();
        InitializeEditor();
        InitializeLibraryViews();
        InitializeManagement();
        InitializeSelectionMenu();
        SessionButton.Click += (_, _) => ManageSession();
        Closing += OnClosingSession;
        SystemParameters.StaticPropertyChanged += OnSystemPreferenceChanged;
        UpdateNavigation();
        OpenTypeButton.Click += (_, _) => ShowOpenType();
        FontInfoButton.Click += (_, _) => InspectFont();
        OpenTypeButton.IsEnabled = FontInfoButton.IsEnabled = false;
        GlyphBrowserButton.Click += (_, _) => ShowGlyphBrowser();
        CopyMissingButton.Click += (_, _) => CopyMissing();
        GlyphBrowserButton.IsEnabled = false;
        ClearCoverage();
        SearchBox.MaxLength = 1024;
        AllButton.Click += (_, _) => ShowInstalled(null);
        FilesButton.Click += (_, _) => PreviewFiles();
        RefreshButton.Click += (_, _) => Refresh();
        RetryButton.Click += (_, _) => Refresh();
        SearchBox.TextChanged += (_, _) => Search(65);
        FamilyList.SelectionChanged += (_, _) =>
        {
            if (_applyingReview)
                return;
            if (!ReferenceEquals(_shownFamily, FamilyList.SelectedItem))
            {
                _shownFamily = FamilyList.SelectedItem as FamilyRow;
                LoadStyles();
            }
            UpdateShelfButtons();
            UpdateManagementCommands();
            UpdateSelectionSummary();
        };
        FacePicker.SelectionChanged += (_, _) => LoadFace();
        ShelfList.SelectionChanged += (_, _) => { if (!_syncingShelves && ShelfList.SelectedItem is string name) ShowInstalled(name); };
        SizeSlider.ValueChanged += (_, _) => { SizeText.Text = $"{SizeSlider.Value:0} pt"; Draw(); };
        SpecimenViewport.SizeChanged += (_, _) => Draw();
        DpiChanged += (_, e) => { if (e.OriginalSource == this) Draw(); };

        NewShelfButton.Click += (_, _) => AddShelf();
        RenameShelfButton.Click += (_, _) => RenameShelf();
        DeleteShelfButton.Click += (_, _) => DeleteShelf();
        ToggleMemberButton.Click += (_, _) => ToggleMembership();
        LegacyButton.Click += (_, _) => OpenSafetyCenter();
        SettingsButton.Click += (_, _) => SetSpecimen();
        LegacyButton.ToolTip = "在独立字体册中检查操作历史或停用、卸载、恢复；不会自动执行操作。";
        SettingsButton.Content = "标本设置";
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowGlyphBrowser();
                e.Handled = true;
            }
            else if (e.Key == Key.Right && FamilyList.IsKeyboardFocusWithin && FacePicker.Items.Count > 0)
            {
                FacePicker.Focus();
                FacePicker.IsDropDownOpen = true;
                e.Handled = true;
            }
            else if (e.Key == Key.Left && FacePicker.IsKeyboardFocusWithin && !FacePicker.IsDropDownOpen)
            {
                FamilyList.Focus();
                e.Handled = true;
            }
            else if (SearchBox.IsKeyboardFocusWithin && e.Key is Key.Down or Key.Enter
                && FamilyList.SelectedItem is FamilyRow selected)
            {
                FamilyList.ScrollIntoView(selected);
                FamilyList.UpdateLayout();
                if (FamilyList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
                    item.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                Refresh();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
            {
                SearchBox.Clear();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemPreferenceChanged;
            _closed = true;
            ClearEditor();
            InvalidateLibraryPreviews();
            _scan.Cancel();
            _query.Cancel();
            _styles.Cancel();
            _choose.Cancel();
            _paint.Cancel();
            _face?.Dispose();
            _catalog?.Dispose();
        };
        UpdateShelfButtons();
    }
    private void PreviewFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择本地字体，只预览不安装", Filter = "字体文件 (*.ttf;*.otf;*.ttc;*.otc)|*.ttf;*.otf;*.ttc;*.otc", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
            return;
        if (dialog.FileNames.Length > 128)
        {
            MessageBox.Show(this, "每次最多预览 128 个文件。", "文件预览");
            return;
        }
        SwitchSource(dialog.FileNames, null);
    }
    private void ShowInstalled(string? scope)
    {
        if (_filePaths is not null)
            SwitchSource(null, scope);
        else
            ChangeScope(scope);
    }
    private void SwitchSource(string[]? files, string? scope)
    {
        _scan.Cancel();
        _query.Cancel();
        _restoreKey = null;
        FamilyList.ItemsSource = null;
        ClearDetails();
        _catalog?.Dispose();
        _catalog = null;
        InvalidateLibraryPreviews();
        SearchBox.Clear(); // The query belongs to the previous data source.
        _filePaths = files;
        ChangeScope(scope);
        Refresh();
    }
    private void Refresh()
    {
        if (_managementExecuting)
        {
            _managementRefreshNeeded = true;
            return;
        }
        RetryButton.Visibility = Visibility.Collapsed;
        var paths = _filePaths;
        StatusText.Text = paths is null ? "正在读取 Windows 已安装字体…" : "正在按文件建立私有预览目录…";
        if (_catalog is null)
            ResultText.Text = "正在建立字族索引…";
        var previous = (FamilyList.SelectedItem as FamilyRow)?.Key;
        _ = _scan.Run(ct => paths is null ? _openInstalledCatalog(ct) : FontCatalog.Open(ct, paths), snapshot =>
        {
            _query.Cancel();
            _styles.Cancel();
            _choose.Cancel();
            _paint.Cancel();
            FamilyList.ItemsSource = null;
            _catalog?.Dispose();
            _catalog = snapshot;
            InvalidateLibraryPreviews();
            _restoreKey = previous;
            var s = snapshot.Stats;
            StatusText.Text = (paths is null ? "已安装" : "免安装预览") + $" · {s.Families:N0} 个家族 / {s.Styles:N0} 个样式　·　目录就绪 {snapshot.ElapsedMs:N0} ms" + (s.Skipped > 0 ? $"　·　跳过 {s.Skipped} 个不可读家族" : "");
            Search(0);
        }, ex =>
        {
            StatusText.Text = $"目录读取失败：{ex.Message}";
            RetryButton.Visibility = Visibility.Visible;
            ResultText.Text = _catalog is null ? "字体目录不可用，未显示示例数据。" : "刷新失败，保留上一次成功读取的目录。";
        }, discard: s => s.Dispose());
        _ = UseStore(_ => _store.Read());
    }
    private void UpdateNavigation()
    {
        AllButton.SetResourceReference(BackgroundProperty, _filePaths is null && _scope is null ? "NavSelectionBrush" : "TransparentBrush");
        FilesButton.SetResourceReference(BackgroundProperty, _filePaths is not null ? "NavSelectionBrush" : "TransparentBrush");
    }
    private void ChangeScope(string? name)
    {
        _scope = name;
        UpdateNavigation();
        _syncingShelves = true;
        try
        {
            ShelfList.SelectedItem = name;
        }
        finally { _syncingShelves = false; }
        ScopeTitle.Text = name ?? (_filePaths is null ? "已安装字体" : "文件预览");
        UpdateShelfButtons();
        Search(0);
    }
    private void Search(int debounce)
    {
        var catalog = _catalog;
        if (catalog is null || _closed)
            return;
        var query = SearchBox.Text;
        var previous = _restoreKey ?? (FamilyList.SelectedItem as FamilyRow)?.Key;
        IReadOnlyList<string>? members = null;
        if (_scope is not null && !_shelves.Shelves.TryGetValue(_scope, out members))
            members = [];
        var scopeCount = members?.Count;
        _ = _query.Run(ct => catalog.Search(query, members, ct), rows =>
        {
            _restoreKey = null;
            var show = previous is not null ? rows.FirstOrDefault(r => r.Key == previous) : null;
            // Prefer a legible first specimen, never a made-up font. Subsequent searches pick the best match.
            if (show is null && previous is null && query.Length == 0 && _scope is null)
                show = rows.FirstOrDefault(r => r.Key == "Microsoft YaHei") ?? rows.FirstOrDefault(r => r.Key == "Segoe UI");
            show ??= rows.FirstOrDefault();
            FamilyList.ItemsSource = rows;
            FamilyList.SelectedItem = show;
            if (show != null)
                FamilyList.ScrollIntoView(show);
            ResultText.Text = rows.Length == 0 ? (query.Length > 0 ? "未找到匹配字体，试试家族名称或本地化别名。" : scopeCount.HasValue ? "当前清单没有已安装的匹配字体。" : "Windows 未返回任何本地字体。") : $"{rows.Length:N0} 个家族" + (query.Length > 0 ? "　·　搜索结果" : "");
            if (scopeCount.HasValue)
                ShelfHint.Text = $"清单记录 {scopeCount:N0} 个家族；只显示当前已安装且匹配的项目。";
            PublishIncomingReview(catalog);
            if (show is null)
                ClearDetails();
        }, ex => { FamilyList.ItemsSource = null; ClearDetails(); ResultText.Text = ex.Message; }, debounce);
    }
    private void ClearFace()
    {
        _typeWindow?.Follow(null, string.Empty, _featureOverrides);
        _featureOverrides.Clear();
        OpenTypeButton.IsEnabled = FontInfoButton.IsEnabled = false;
        ClearEditor();
        GlyphBrowserButton.IsEnabled = false;
        _glyphWindow?.Follow(null, string.Empty, []);
        ClearCoverage();
        _choose.Cancel();
        _paint.Cancel();
        _face?.Dispose();
        _face = null;
        _axisValues = [];
        AxisPanel.Children.Clear();
        AxisPanel.Visibility = Visibility.Collapsed;
        SpecimenImage.Source = null;
        PathText.Text = "—";
        PreviewNote.Text = "选择一个本地样式以查看标本。";
        FaceDetail.Text = "尚未载入字面";
    }
    private void ClearDetails()
    {
        _styles.Cancel();
        FacePicker.ItemsSource = null;
        ClearFace();
        FamilyTitle.Text = "尚未选择字体";
        UpdateShelfButtons();
    }
    private void LoadStyles()
    {
        ClearDetails();
        if (_closed || _catalog is not { } catalog || FamilyList.SelectedItem is not FamilyRow row)
            return;
        FamilyTitle.Text = row.Name;
        FaceDetail.Text = "正在读取本家族的样式…";
        UpdateShelfButtons();
        _ = _styles.Run(_ => catalog.GetStyles(row.Id), styles =>
        {
            FacePicker.ItemsSource = styles;
            FacePicker.SelectedItem = styles.FirstOrDefault(s => s.Weight == 400 && (s.Name is "Regular" or "常规" or "Normal")) ?? styles.FirstOrDefault(s => s.Weight == 400) ?? styles.FirstOrDefault();
            if (styles.Length == 0)
                PreviewNote.Text = "此家族没有可用的本地样式。";
        }, ex => { FaceDetail.Text = "样式不可用"; PreviewNote.Text = ex.Message; }, 30);
    }
    private void LoadFace()
    {
        ClearFace();
        if (_closed || _catalog is not { } catalog || FamilyList.SelectedItem is not FamilyRow row || FacePicker.SelectedItem is not StyleRow style)
            return;
        PreviewNote.Text = "正在载入精确字面…";
        _ = _choose.Run(_ => catalog.Select(row.Id, style.Index), face =>
        {
            _face = face;
            OpenTypeButton.IsEnabled = FontInfoButton.IsEnabled = true;
            _typeWindow?.Follow(face, GlyphCaption(), _featureOverrides);
            PathText.Text = string.IsNullOrEmpty(face.Path) ? "由字体提供程序管理（无本地路径）" : face.Path;
            FaceDetail.Text = FaceSummary(face);
            MakeAxes(face);
            GlyphBrowserButton.IsEnabled = face.Glyphs > 0;
            _glyphWindow?.Follow(face, GlyphCaption(), _axisValues);
            Draw(0);
        }, ex => { PreviewNote.Text = ex.Message; FaceDetail.Text = "无法读取此字面，不使用替代字体。"; }, discard: f => f.Dispose());
    }
    private static string FaceSummary(FaceSelection f) => $"{(string.IsNullOrEmpty(f.Postscript) ? "本地字面" : f.Postscript)}　·　Face {f.FaceIndex}　·　{f.Glyphs:N0} 字形";
    private void MakeAxes(FaceSelection face)
    {
        _axisValues = face.Axes.Select(a => a.Initial).ToArray();
        AxisPanel.Children.Clear();
        AxisPanel.Visibility = face.Axes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < face.Axes.Length; i++)
        {
            int slot = i;
            var axis = face.Axes[i];
            var line = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
            var title = new TextBlock { Text = axis.Label, Width = 44 };
            var val = new TextBlock { Text = $"{axis.Initial:G4}", Width = 47, TextAlignment = TextAlignment.Right };
            DockPanel.SetDock(title, Dock.Left);
            DockPanel.SetDock(val, Dock.Right);
            line.Children.Add(title);
            line.Children.Add(val);
            var slider = new Slider { Minimum = axis.Minimum, Maximum = axis.Maximum, Value = axis.Initial, SmallChange = (axis.Maximum - axis.Minimum) / 100.0, ToolTip = $"{axis.Label}：{axis.Minimum:G4} – {axis.Maximum:G4}" };
            AutomationProperties.SetName(slider, $"可变轴 {axis.Label}");
            slider.ValueChanged += (_, _) => { _axisValues[slot] = (float)slider.Value; val.Text = $"{slider.Value:G4}"; _glyphWindow?.SetAxes(_axisValues); Draw(); };
            line.Children.Add(slider);
            AxisPanel.Children.Add(line);
        }
    }
}
