using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;

namespace FontBook.Workbench;

internal static class WorkbenchDialog
{
    // All dialog values come from local UI; no shell command or path is assembled from input.
    internal static string? Prompt(Window owner, string title, string caption, string initial, IEnumerable<string>? choices = null, int maxLength = 128)
    {
        var panel = new StackPanel { Margin = new Thickness(22) };
        var description = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(description);
        var box = new TextBox { Text = initial, MaxLength = maxLength, Padding = new Thickness(7, 4, 7, 4) };
        var select = new ComboBox { ItemsSource = choices, SelectedItem = initial, Padding = new Thickness(6) };
        Control editor = choices is null ? box : select;
        AutomationProperties.SetName(editor, caption);
        panel.Children.Add(editor);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var no = new Button { Content = "取消", IsCancel = true, MinWidth = 68, Padding = new Thickness(8, 4, 8, 4) };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 68, Margin = new Thickness(8, 0, 0, 0), Padding = no.Padding };
        actions.Children.Add(no);
        actions.Children.Add(ok);
        panel.Children.Add(actions);
        var dlg = new Window { Title = title, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height, Width = 380, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Content = panel, FontFamily = owner.FontFamily, FontSize = 14 };
        dlg.SetResourceReference(Window.BackgroundProperty, "WorkbenchSurfaceBrush");
        dlg.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
        if (!owner.ShowActivated)
        {
            dlg.ShowActivated = false;
            dlg.WindowStartupLocation = WindowStartupLocation.Manual;
            dlg.Left = owner.Left;
            dlg.Top = owner.Top;
        }
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { editor.Focus(); if (choices is null) box.SelectAll(); };
        return dlg.ShowDialog() == true ? (choices is null ? box.Text : select.SelectedItem as string) : null;
    }
}
