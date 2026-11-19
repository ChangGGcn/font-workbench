using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FontBook.Workbench;

/// <summary>Modeless, follows the main window's selected face. All long work is cancelable;
/// never hold borrowed native string pointers across a continuation or a face change.</summary>
internal partial class GlyphWindow : Window
{
    private readonly LatestWork _opening = new(), _paging = new(), _details = new();
    private FaceSelection? _face;
    private GlyphBook? _book;
    private float[] _axes = [];
    private uint _first, _aliasStart, _activeGid;
    private float _lastScale;
    private uint? _requestedScalar;
    private bool _closed;

    internal GlyphWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        if (!owner.ShowActivated)
        {
            // Non-activating hosts also own non-activating utility windows (e.g. offscreen rendering).
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left;
            Top = owner.Top;
        }
        GlyphPrevButton.Click += (_, _) => { if (_first >= GlyphBook.PageSize) ShowGid(_first - GlyphBook.PageSize); };
        GlyphNextButton.Click += (_, _) => { if (_face is { } f && _first + GlyphBook.PageSize < f.Glyphs) ShowGid(_first + GlyphBook.PageSize); };
        GlyphJumpButton.Click += (_, _) => Jump();
        GlyphJumpBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Jump(); } };
        GlyphList.SelectionChanged += (_, _) => ShowDetails();
        GlyphScalarBox.SelectionChanged += (_, _) => GlyphCopyButton.IsEnabled = GlyphList.SelectedItem is GlyphEntry { Gid: > 0 } && GlyphScalarBox.SelectedItem is MappedScalar { CanCopy: true };
        GlyphCopyButton.Click += (_, _) =>
        {
            if (GlyphList.SelectedItem is GlyphEntry { Gid: > 0 } && GlyphScalarBox.SelectedItem is MappedScalar { CanCopy: true } cp)
                GlyphStatus.Text = ClipboardText.TryWrite(cp.Character, $"已复制 {cp.Label}。");
        };
        AliasPrevious.Click += (_, _) => { if (_aliasStart >= GlyphBook.ScalarPageSize) ShowDetails(_aliasStart - GlyphBook.ScalarPageSize); };
        AliasNext.Click += (_, _) => { if (GlyphList.SelectedItem is GlyphEntry row && _aliasStart + GlyphBook.ScalarPageSize < row.MappingCount) ShowDetails(_aliasStart + GlyphBook.ScalarPageSize); };
        DpiChanged += (_, _) =>
        {
            // Descendant visuals also raise this routed event when realized. Repaint only for
            // an actual window-scale transition, after layout has finished with the current grid.
            float scale = Scale();
            if (scale == _lastScale)
                return;
            _lastScale = scale;
            Dispatcher.InvokeAsync(Redraw, System.Windows.Threading.DispatcherPriority.Background);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                GlyphJumpBox.Focus();
                GlyphJumpBox.SelectAll();
            }
            else if (!GlyphJumpBox.IsKeyboardFocusWithin && e.Key is Key.PageDown or Key.PageUp && _face is { } f && _book != null)
            {
                e.Handled = true;
                if (e.Key == Key.PageDown && _first + GlyphBook.PageSize < f.Glyphs)
                    ShowGid(_first + GlyphBook.PageSize);
                if (e.Key == Key.PageUp && _first >= GlyphBook.PageSize)
                    ShowGid(_first - GlyphBook.PageSize);
            }
        };
        Closed += (_, _) => { _closed = true; _opening.Cancel(); _paging.Cancel(); _details.Cancel(); _book?.Dispose(); _book = null; _face = null; };
        ResetGrid();
    }
    internal void Follow(FaceSelection? face, string title, float[] axes)
    {
        _opening.Cancel();
        _paging.Cancel();
        _details.Cancel();
        _book?.Dispose();
        _book = null;
        _face = face;
        _axes = (float[])axes.Clone();
        _first = 0;
        _requestedScalar = null;
        GlyphTitle.Text = title;
        GlyphJumpBox.Clear();
        ResetGrid();
        GlyphStatus.Text = face == null ? "请选择一个字体样式。" : "正在按需读取所选字面的映射与名称…";
        if (face == null || _closed)
            return;
        _ = _opening.Run(face.OpenBook, book => { _book = book; ShowGid(0); }, ex => GlyphStatus.Text = ex.Message, discard: b => b.Dispose());
    }
    internal void SetAxes(float[] axes)
    {
        _axes = (float[])axes.Clone();
        Redraw();
    }
    internal void Redraw()
    {
        if (_face != null && _book != null && !_closed)
            ShowGid((GlyphList.SelectedItem as GlyphEntry)?.Gid ?? _activeGid);
    }
    private void ResetGrid()
    {
        GlyphList.ItemsSource = null;
        ClearDetail();
        GlyphPageText.Text = "—";
        GlyphPrevButton.IsEnabled = GlyphNextButton.IsEnabled = GlyphJumpButton.IsEnabled = false;
    }
    private void ClearDetail()
    {
        _details.Cancel();
        GlyphDetails.Text = "—";
        AliasPager.Visibility = Visibility.Collapsed;
        GlyphSelectedImage.Source = null;
        GlyphScalarBox.ItemsSource = null;
        GlyphCopyButton.IsEnabled = GlyphScalarBox.IsEnabled = false;
    }
    private void ShowGid(uint gid, uint? requestedScalar = null)
    {
        if (_face is not { } f || _book is not { } b || gid >= f.Glyphs || _closed)
            return;
        _activeGid = gid;
        _requestedScalar = requestedScalar;
        _first = gid / GlyphBook.PageSize * GlyphBook.PageSize;
        uint first = _first;
        ResetGrid();
        GlyphJumpButton.IsEnabled = true;
        GlyphStatus.Text = "正在绘制当前页…";
        var scale = Scale();
        _lastScale = scale;
        var axes = _axes;
        var ink = ForegroundInk();
        _ = _paging.Run(ct => b.Page(f, first, scale, axes, ink, ct), page =>
        {
            GlyphList.ItemsSource = page.Items;
            GlyphPageText.Text = $"{first / GlyphBook.PageSize + 1:N0} / {(f.Glyphs + GlyphBook.PageSize - 1) / GlyphBook.PageSize:N0}";
            GlyphPrevButton.IsEnabled = first > 0;
            GlyphNextButton.IsEnabled = first + GlyphBook.PageSize < f.Glyphs;
            GlyphStatus.Text = $"{f.Glyphs:N0} 个字形 · 本页 GID {first}–{first + (uint)page.Items.Length - 1} · " +
                (b.NameStatus switch
                {
                    1 => "已读取 post 名称",
                    2 => "post 名称表不受支持或无效",
                    _ => "此字体未提供 post 名称"
                }) +
                (page.Unavailable > 0 ? $" · {page.Unavailable} 项不能绘制" : "");
            GlyphList.SelectedIndex = (int)(gid - first);
            var selected = GlyphList.SelectedItem;
            Dispatcher.InvokeAsync(() =>
            {
                if (!_closed && selected != null && ReferenceEquals(GlyphList.SelectedItem, selected))
                    GlyphList.ScrollIntoView(selected);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, ex => GlyphStatus.Text = ex.Message, 15);
    }
    private void ShowDetails(uint? aliasStart = null)
    {
        ClearDetail();
        if (_book is not { } b || _face is not { } f || GlyphList.SelectedItem is not GlyphEntry row)
            return;
        var axes = _axes;
        var scale = Scale();
        _lastScale = scale;
        var ink = ForegroundInk();
        var target = _requestedScalar;
        _requestedScalar = null;
        GlyphDetails.Text = $"GID {row.Gid} · {(row.Name.Length == 0 ? "无 post 名称" : row.Name)}\n{(row.MappingCount == 0 ? "无直接 Unicode 映射" : $"{row.MappingCount} 个直接映射")}";
        _ = _details.Run(ct =>
        {
            uint start = aliasStart ?? b.PageForScalar(row, target);
            var scalars = b.ReadScalars(row, start, ct);
            ct.ThrowIfCancellationRequested();
            System.Windows.Media.Imaging.BitmapSource? image = null;
            try
            {
                image = f.RenderGlyph(row.Gid, 120, scale, axes, ink);
            }
            catch (InvalidOperationException) { }
            return (scalars, image, start);
        }, result =>
        {
            GlyphSelectedImage.Source = result.image;
            if (result.image == null)
                GlyphDetails.Text += " · 放大图不可绘制";
            _aliasStart = result.start;
            AliasPager.Visibility = row.MappingCount > GlyphBook.ScalarPageSize ? Visibility.Visible : Visibility.Collapsed;
            AliasPrevious.IsEnabled = _aliasStart > 0;
            AliasNext.IsEnabled = _aliasStart + GlyphBook.ScalarPageSize < row.MappingCount;
            AliasRange.Text = $"{_aliasStart + 1}–{_aliasStart + result.scalars.Length}";
            GlyphScalarBox.ItemsSource = result.scalars;
            GlyphScalarBox.SelectedItem = result.scalars.FirstOrDefault(s => s.Value == target) ?? result.scalars.FirstOrDefault();
            GlyphScalarBox.IsEnabled = result.scalars.Length > 0 && row.Gid != 0;
            GlyphCopyButton.IsEnabled = GlyphScalarBox.IsEnabled && GlyphScalarBox.SelectedItem is MappedScalar { CanCopy: true };
        }, ex => GlyphDetails.Text = ex.Message);
    }
    private void Jump()
    {
        if (_book == null || _face is not { } f)
            return;
        var value = GlyphJumpBox.Text.Trim();
        uint gid;
        uint? cp = null;
        if (value.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint scalar) || scalar > 0x10ffff || !Rune.IsValid((int)scalar))
            {
                GlyphStatus.Text = "请输入有效的 Unicode 标量，如 U+0041。";
                return;
            }
            try
            {
                gid = f.MapOne(scalar);
            }
            catch (InvalidOperationException ex) { GlyphStatus.Text = ex.Message; return; }
            if (gid == 0)
            {
                _paging.Cancel();
                GlyphList.SelectedIndex = -1;
                ClearDetail();
                GlyphStatus.Text = $"U+{scalar:X4} 在此字面中没有直接映射。";
                return;
            }
            cp = scalar;
        }
        else if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out gid) || gid >= f.Glyphs)
        {
            GlyphStatus.Text = $"请输入 0–{f.Glyphs - 1} 的十进制 GID，或 U+ 码点。";
            return;
        }
        ShowGid(gid, cp);
    }
    private float Scale() => (float)Math.Clamp(VisualTreeHelper.GetDpi(this).DpiScaleX, 0.5, 4);
    private Color? ForegroundInk() => WindowsAppearance.Ink(this);
}
internal static class ClipboardText
{
    internal static string TryWrite(string text, string success)
    {
        try
        {
            Clipboard.SetText(text);
            return success;
        }
        catch (System.Runtime.InteropServices.ExternalException) { return "剪贴板正被占用，请重试。"; }
    }
}
