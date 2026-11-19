using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace FontBook.Workbench;

public partial class MainWindow
{
    private SpecimenLayout? _layout;
    private string? _layoutText;
    private readonly LatestWork _scroller = new();
    private float _scrollY;
    private int _anchor;
    private (Point Position, bool Extend, int Clicks, string Text)? _pendingClick;
    private Rectangle? _visibleCaret;
    private readonly DispatcherTimer _blink = new(DispatcherPriority.Background);
    [DllImport("user32.dll")] private static extern uint GetCaretBlinkTime();
    private bool _selecting, _applyingFrame;
    // Headless input tests cannot move the system pointer; supply viewport coordinates only.
    internal Func<MouseEventArgs, Point>? EditorPointerForChecks
    {
        get; set;
    }
    private Point EditorPoint(MouseEventArgs e) => EditorPointerForChecks?.Invoke(e) ?? e.GetPosition(EditOverlay);
    private readonly Dictionary<string, bool> _featureOverrides = new(StringComparer.Ordinal);
    private Native.Feature[] Features => _featureOverrides.OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => new Native.Feature(EncodeTag(p.Key), p.Value ? 1u : 0u)).ToArray();
    private static uint EncodeTag(string tag) => (uint)tag[0] | (uint)tag[1] << 8 | (uint)tag[2] << 16 | (uint)tag[3] << 24;

    private void InitializeEditor()
    {
        uint blink = GetCaretBlinkTime();
        if (blink is > 0 and < 0xffffffff)
        {
            _blink.Interval = TimeSpan.FromMilliseconds(Math.Max(100u, blink));
            _blink.Tick += (_, _) => { if (_visibleCaret != null) _visibleCaret.Opacity = _visibleCaret.Opacity > 0 ? 0 : 1; };
        }
        Closed += (_, _) => _blink.Stop();
        SampleBox.PreviewMouseLeftButtonDown += EditorMouseDown;
        SampleBox.PreviewMouseMove += EditorMouseMove;
        SampleBox.PreviewMouseLeftButtonUp += (_, e) => { if (_selecting) { _selecting = false; SampleBox.ReleaseMouseCapture(); e.Handled = true; } };
        SampleBox.LostMouseCapture += (_, _) => _selecting = false;
        SampleBox.GotKeyboardFocus += (_, _) => UpdateEditorOverlay();
        SampleBox.LostKeyboardFocus += (_, _) => UpdateEditorOverlay();
        SampleBox.SelectionChanged += (_, _) => UpdateEditorOverlay();
        SampleBox.PreviewKeyDown += EditorKeyDown;
        SampleBox.PreviewMouseWheel += (_, e) =>
        {
            if (_layout == null || _layout.MaxScroll <= 0)
                return;
            ScrollEditor(_scrollY - Math.Sign(e.Delta) * Math.Max(16, SizeSlider.Value * 2));
            e.Handled = true;
        };
        SampleBox.TextChanged += (_, _) =>
        {
            ClearCoverage();
            _layoutText = null;
            EditOverlay.Children.Clear();
            Draw(0);
        };
    }
    private void ClearEditor()
    {
        _pendingClick = null;
        _blink.Stop();
        _visibleCaret = null;
        _scroller.Cancel();
        _layout?.Dispose();
        _layout = null;
        _layoutText = null;
        _scrollY = 0;
        EditOverlay.Children.Clear();
    }
    private void Draw(int delay = 25)
    {
        if (_closed || _face is not { } face)
            return;
        string text = SampleBox.Text;
        int caret = Math.Clamp(SampleBox.CaretIndex, 0, text.Length);
        float scale = (float)Math.Clamp(VisualTreeHelper.GetDpi(this).DpiScaleX, .5, 8);
        int width = Math.Clamp((int)Math.Floor(Math.Max(1, SampleBox.ActualWidth) * scale), 1, 4096);
        int height = Math.Clamp((int)Math.Floor(Math.Max(1, SampleBox.ActualHeight) * scale), 1, Math.Min(2048, 4194304 / width));
        var pt = (float)SizeSlider.Value;
        var axes = (float[])_axisValues.Clone();
        var features = Features;
        Color ink = WindowsAppearance.Ink(this);
        _scroller.Cancel();
        var prior = _scrollY;
        var focus = SampleBox.IsKeyboardFocusWithin;
        _ = _paint.Run(ct => face.BuildEditorFrame(text, pt, scale, width, height, axes, features, caret, prior, focus, ink, ct), frame =>
        {
            _applyingFrame = true;
            try
            {
                _layout?.Dispose();
                _layout = frame.Layout;
                _layoutText = text;
                _scrollY = frame.Scroll;
                SpecimenImage.Source = frame.Image;
                FaceDetail.Text = FaceSummary(face);
                _coverage = frame.Coverage;
                CoverageText.Text = frame.Coverage.Summary;
                CoverageText.ToolTip = frame.Coverage.Explain;
                CopyMissingButton.IsEnabled = frame.Coverage.Missing.Length != 0;
                PreviewNote.Text = frame.Error ?? (text.Length == 0 && !SampleBox.IsKeyboardFocusWithin ? "直接点击此处输入标本文字" : "");
            }
            finally { _applyingFrame = false; }
            if (_pendingClick is { } click && Editable && click.Text == SampleBox.Text)
            {
                _pendingClick = null;
                PlacePointer(HitAt(click.Position), click.Extend, click.Clicks, false);
            }
            UpdateEditorOverlay();
        }, ex => { ClearEditor(); ClearCoverage(); SpecimenImage.Source = null; PreviewNote.Text = ex.Message; }, delay, frame => frame.Dispose());
    }
    private bool Editable => !_closed && _layout != null && _layoutText == SampleBox.Text;
    private int HitAt(Point p) => Math.Clamp(_layout!.Hit(p, _scrollY), 0, SampleBox.Text.Length);
    private void EditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_face == null)
            return;
        Point at = EditorPoint(e);
        bool extend = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        SampleBox.Focus();
        e.Handled = true;
        if (!Editable)
        {
            _pendingClick = (at, extend, e.ClickCount, SampleBox.Text);
            return;
        }
        PlacePointer(HitAt(at), extend, e.ClickCount, true);
    }
    private void PlacePointer(int position, bool extend, int clicks, bool capture)
    {
        if (clicks >= 3)
        {
            SampleBox.SelectAll();
            _anchor = 0;
        }
        else if (clicks == 2)
            SelectWord(position);
        else
        {
            if (!extend)
                _anchor = position;
            else if (SampleBox.SelectionLength == 0)
                _anchor = SampleBox.CaretIndex;
            SelectTo(position);
            if (capture)
            {
                _selecting = true;
                SampleBox.CaptureMouse();
            }
        }
    }
    private void EditorMouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting || e.LeftButton != MouseButtonState.Pressed || !Editable)
            return;
        var at = EditorPoint(e);
        if (at.Y < 0 || at.Y > EditOverlay.ActualHeight)
            ScrollEditor(_scrollY + (at.Y < 0 ? -1 : 1) * SizeSlider.Value);
        SelectTo(HitAt(at));
        e.Handled = true;
    }
    private void SelectWord(int position)
    {
        string t = SampleBox.Text;
        if (t.Length == 0)
            return;
        int[] breaks = StringInfo.ParseCombiningCharacters(t);
        int k = Array.FindLastIndex(breaks, n => n <= Math.Min(t.Length - 1, position));
        int first = k, last = k;
        bool letter = char.IsLetterOrDigit(t, breaks[k]);
        if (letter)
        {
            while (first > 0 && char.IsLetterOrDigit(t, breaks[first - 1]))
                first--;
            while (last + 1 < breaks.Length && char.IsLetterOrDigit(t, breaks[last + 1]))
                last++;
        }
        _anchor = breaks[first];
        SelectTo(last + 1 < breaks.Length ? breaks[last + 1] : t.Length);
    }
    private void SelectTo(int end)
    {
        _anchor = Math.Clamp(_anchor, 0, SampleBox.Text.Length);
        int start = Math.Min(_anchor, end);
        SampleBox.Select(start, Math.Abs(end - _anchor));
    }
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (!Editable || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
            || e.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.Left or Key.Right))
            return;
        bool extend = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var len = SampleBox.SelectionLength;
        int start = SampleBox.SelectionStart;
        int cur = len == 0 ? SampleBox.CaretIndex : (_anchor == start ? start + len : start);
        var r = _layout!.Caret(cur);
        var point = new Point(r.X, r.Y + r.Height * .5 - _scrollY);
        int next;
        if (e.Key is Key.Left or Key.Right)
        {
            int direction = e.Key == Key.Left ? -1 : 1;
            if (!extend && len > 0)
                next = direction < 0 ? start : start + len;
            else
                next = AdjacentCaret(cur, point, direction);
        }
        else
        {
            if (e.Key == Key.Up)
                point.Y -= Math.Max(r.Height, 1);
            else if (e.Key == Key.Down)
                point.Y += Math.Max(r.Height, 1);
            else
                point.X = e.Key == Key.Home ? 0 : EditOverlay.ActualWidth;
            next = HitAt(point);
        }
        if (!extend)
            _anchor = next;
        else if (len == 0)
            _anchor = cur;
        SelectTo(next);
        e.Handled = true;
    }
    private int AdjacentCaret(int current, Point point, int direction)
    {
        // Navigate visual order within the DirectWrite line (bidi and ligatures included),
        // rather than the layout of the transparent WPF input host.
        var width = Math.Max(EditOverlay.ActualWidth, 1);
        var native = _layout!;
        double advance = 0.5;
        while (advance < width + 2)
        {
            double x = Math.Clamp(point.X + direction * advance, 0, width);
            int candidate = HitAt(new(x, point.Y));
            if (candidate != current)
                return candidate;
            if (x <= 0 || x >= width)
                break;
            advance += advance < 4 ? .5 : 1.5;
        }
        // At visual boundaries step to the adjacent line, or stay at the document edge.
        var bounds = native.Caret(current);
        double adjacent = point.Y + direction * Math.Max(1, bounds.Height);
        if (adjacent + _scrollY >= 0 && adjacent + _scrollY < native.Extent)
            return HitAt(new(direction < 0 ? width : 0, adjacent));
        return current;
    }
    private void ScrollEditor(double y)
    {
        if (!Editable || _layout is not { } layout)
            return;
        float offset = (float)Math.Clamp(y, 0, layout.MaxScroll);
        if (Math.Abs(offset - _scrollY) < .3)
            return;
        _scrollY = offset;
        var ink = WindowsAppearance.Ink(this);
        _ = _scroller.Run(ct => { ct.ThrowIfCancellationRequested(); return layout.Draw(offset, ink); }, bmp =>
        {
            if (ReferenceEquals(_layout, layout) && _layoutText == SampleBox.Text)
            {
                SpecimenImage.Source = bmp;
                UpdateEditorOverlay(false);
            }
        }, ex => { if (ReferenceEquals(_layout, layout)) PreviewNote.Text = ex.Message; });
    }
    private void UpdateEditorOverlay(bool followCaret = true)
    {
        if (_applyingFrame)
            return;
        _blink.Stop();
        _visibleCaret = null;
        EditOverlay.Children.Clear();
        if (!Editable || !SampleBox.IsKeyboardFocusWithin)
            return;
        PreviewNote.Text = "";
        int begin = SampleBox.SelectionStart, length = SampleBox.SelectionLength;
        int end = length == 0 ? SampleBox.CaretIndex : (_anchor == begin ? begin + length : begin);
        try
        {
            if (followCaret && !_selecting)
            {
                var desired = _layout!.ScrollFor(end, _scrollY, true);
                if (Math.Abs(desired - _scrollY) > .5)
                {
                    ScrollEditor(desired);
                    return;
                }
            }
            Brush accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : SystemColors.AccentColorBrush;
            foreach (Rect r in _layout!.Selection(begin, length))
                AddEditorRect(r, accent, .30, false);
            AddEditorRect(_layout.Caret(end), new SolidColorBrush(WindowsAppearance.Ink(this)), 1, true);
            ImeAnchor.Follow(SampleBox, _layout.Caret(end).BottomLeft - new Vector(0, _scrollY));
        }
        catch (InvalidOperationException ex) { PreviewNote.Text = ex.Message; }
    }
    private void AddEditorRect(Rect r, Brush brush, double opacity, bool caret)
    {
        var area = new Rect(0, _scrollY, Math.Max(0, EditOverlay.ActualWidth), Math.Max(0, EditOverlay.ActualHeight));
        if (caret)
            r.Width = Math.Max(1.25, SystemParameters.CaretWidth);
        r.Intersect(area);
        if (r.IsEmpty || r.Width == 0 || r.Height == 0)
            return;
        var shape = new Rectangle { Width = r.Width, Height = r.Height, Fill = brush, Opacity = opacity, IsHitTestVisible = false, Tag = caret ? "Caret" : "Selection" };
        Canvas.SetLeft(shape, r.X);
        Canvas.SetTop(shape, r.Y - _scrollY);
        EditOverlay.Children.Add(shape);
        if (caret)
        {
            _visibleCaret = shape;
            if (_blink.Interval.TotalMilliseconds > 0 && SampleBox.SelectionLength == 0)
                _blink.Start();
        }
    }
}
