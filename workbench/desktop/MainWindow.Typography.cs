namespace FontBook.Workbench;

public partial class MainWindow
{
    private OpenTypeWindow? _typeWindow;
    private void ShowOpenType()
    {
        if (_face is null)
            return;
        if (_typeWindow is { } current)
        {
            current.Activate();
            return;
        }
        var w = new OpenTypeWindow(this, (tag, mode) =>
        {
            if (tag.Length == 0)
                _featureOverrides.Clear();
            else if (mode == 0)
                _featureOverrides.Remove(tag);
            else if (_featureOverrides.Count < 64 || _featureOverrides.ContainsKey(tag))
                _featureOverrides[tag] = mode == 1;
            else
            {
                StatusText.Text = "每次标本最多明确指定 64 项特性。";
                _typeWindow?.Follow(_face, GlyphCaption(), _featureOverrides);
                return;
            }
            Draw(0);
        });
        _typeWindow = w;
        w.Closed += (_, _) => _typeWindow = null;
        w.Follow(_face, GlyphCaption(), _featureOverrides);
        w.Show();
    }
    private void InspectFont()
    {
        if (_face != null && FamilyList.SelectedItem is FamilyRow family && FacePicker.SelectedItem is StyleRow style)
            FontDetailsWindow.ShowFor(this, _face, family, style.Name);
    }
}
