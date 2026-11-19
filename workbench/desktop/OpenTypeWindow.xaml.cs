using System.ComponentModel;
using System.Windows;

namespace FontBook.Workbench;

internal sealed class FeatureChoice(string tag, int initial, Action<string, int> changed) : INotifyPropertyChanged
{
    public string Tag { get; } = tag;
    public string Label => Tag switch
    {
        "kern" => "字偶距",
        "liga" => "标准连字",
        "dlig" => "自由连字",
        "calt" => "上下文替换",
        "smcp" => "小型大写字母",
        "c2sc" => "大写转小型大写",
        "onum" => "旧式数字",
        "lnum" => "等高数字",
        "tnum" => "等宽数字",
        "pnum" => "比例数字",
        "frac" => "分数",
        "zero" => "带斜线的零",
        "sups" => "上标",
        "subs" => "下标",
        "vert" => "竖排替代字形",
        "locl" => "本地化字形",
        "mark" => "标记定位",
        "mkmk" => "标记间定位",
        "rlig" => "必需连字",
        "ccmp" => "字形组合与分解",
        _ when Tag.StartsWith("ss", StringComparison.Ordinal) => "风格集 " + Tag[2..],
        _ when Tag.StartsWith("cv", StringComparison.Ordinal) => "字符变体 " + Tag[2..],
        _ => "排版特性"
    };
    public string[] Options { get; } = ["字体默认", "启用", "关闭"];
    private int _mode = initial;
    public int Mode
    {
        get => _mode; set
        {
            if (_mode == value || value is < 0 or > 2)
                return;
            _mode = value;
            PropertyChanged?.Invoke(this, new(nameof(Mode)));
            changed(Tag, value);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal partial class OpenTypeWindow : Window
{
    private readonly LatestWork _read = new();
    private readonly Action<string, int> _change;
    private bool _resetting;
    private FeatureChoice[] _items = [];
    internal OpenTypeWindow(Window owner, Action<string, int> change)
    {
        Owner = owner;
        _change = change;
        InitializeComponent();
        if (!owner.ShowActivated)
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left;
            Top = owner.Top;
        }
        CloseFeatures.Click += (_, _) => Close();
        ResetFeatures.Click += (_, _) =>
        {
            _resetting = true;
            try
            {
                foreach (var row in _items)
                    row.Mode = 0;
            }
            finally { _resetting = false; }
            _change("", 0);
        };
        Closed += (_, _) => _read.Cancel();
    }
    internal void Follow(FaceSelection? face, string label, IReadOnlyDictionary<string, bool> current)
    {
        _read.Cancel();
        FeatureFace.Text = label;
        FeatureList.ItemsSource = null;
        _items = [];
        ResetFeatures.IsEnabled = false;
        if (face is null)
        {
            FeatureStatus.Text = "请选择字体样式。";
            return;
        }
        FeatureStatus.Text = "正在读取 GSUB / GPOS…";
        _ = _read.Run(ct => { ct.ThrowIfCancellationRequested(); return face.ReadDetails().FeatureTags; }, tags =>
        {
            string[] first = ["kern", "liga", "calt", "dlig", "smcp", "onum", "tnum", "ss01"];
            _items = tags.Where(t => t.Length == 4).Distinct(StringComparer.Ordinal).OrderBy(t => Array.IndexOf(first, t) is int n && n >= 0 ? n : 100).ThenBy(t => t, StringComparer.Ordinal)
                .Select(t => new FeatureChoice(t, current.TryGetValue(t, out bool v) ? (v ? 1 : 2) : 0, (key, mode) => { if (!_resetting) _change(key, mode); })).ToArray();
            FeatureList.ItemsSource = _items;
            ResetFeatures.IsEnabled = _items.Length != 0;
            FeatureStatus.Text = _items.Length == 0 ? "此字面没有可用的特性。" : "实时应用于标本。特性是否改变字形取决于文字、语言和字体。";
        }, ex => FeatureStatus.Text = ex.Message);
    }
}
