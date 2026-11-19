using System.Windows;
namespace FontBook.Workbench;

public partial class MainWindow
{
    private GlyphWindow? _glyphWindow;
    private SampleCoverage? _coverage;

    private string GlyphCaption() => $"{FamilyTitle.Text} · {(FacePicker.SelectedItem as StyleRow)?.Name} · Face {_face?.FaceIndex}";

    private void ShowGlyphBrowser()
    {
        if (_face is not { } face || face.Glyphs == 0)
            return;
        if (_glyphWindow != null)
        {
            _glyphWindow.Activate();
            return;
        }
        var window = new GlyphWindow(this);
        _glyphWindow = window;
        window.Closed += (_, _) => _glyphWindow = null;
        window.Follow(face, GlyphCaption(), _axisValues);
        window.Show();
    }
    private void ClearCoverage()
    {
        _coverage = null;
        CoverageText.Text = "—";
        CoverageText.ToolTip = null;
        CopyMissingButton.IsEnabled = false;
    }
    private void CopyMissing()
    {
        if (_coverage is not { Missing.Length: > 0 } coverage)
            return;
        StatusText.Text = ClipboardText.TryWrite(coverage.Report, $"已复制 {coverage.Missing.Length} 种缺失字符的码点、字符和出现次数。");
    }
}
