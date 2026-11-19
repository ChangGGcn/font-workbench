using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FontBook.Workbench;

public partial class MainWindow
{
    private const string SelectionSuffix = "　·　已选 ";
    private void InitializeSelectionMenu()
    {
        var menu = new ContextMenu();
        MenuItem Add(string label, Action run, string? shortcut = null)
        {
            var item = new MenuItem { Header = label, InputGestureText = shortcut ?? "" };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
            return item;
        }
        var all = Add("全选当前结果", FamilyList.SelectAll, "Ctrl+A");
        var none = Add("清除选择", FamilyList.UnselectAll);
        menu.Items.Add(new Separator());
        var shelf = Add("加入或移出项目清单…", ToggleMembership);
        var details = Add("查看字面详情…", InspectFont);
        menu.Items.Add(new Separator());
        var disable = Add("停用所选家族…", () => BatchDisableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        var uninstall = Add("卸载所选家族…", () => BatchUninstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        menu.Opened += (_, _) =>
        {
            all.IsEnabled = FamilyList.Items.Count > 0;
            none.IsEnabled = FamilyList.SelectedItems.Count > 0;
            shelf.IsEnabled = ToggleMemberButton.IsEnabled;
            details.IsEnabled = FontInfoButton.IsEnabled;
            disable.IsEnabled = BatchDisableButton.IsEnabled;
            uninstall.IsEnabled = BatchUninstallButton.IsEnabled;
        };
        FamilyList.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject original && ItemsControl.ContainerFromElement(FamilyList, original) is ListBoxItem item && !item.IsSelected)
                FamilyList.SelectedItem = item.DataContext;
        };
        FamilyList.ContextMenu = menu;
        // Valid older journals retain the old recovery UI. New actions never depend on this optional component.
        var historyMenu = new ContextMenu();
        var legacy = new MenuItem { Header = "旧版日志与保险库…" };
        legacy.Click += (_, _) => OpenSafetyCenter();
        historyMenu.Items.Add(legacy);
        BatchEnableButton.ContextMenu = historyMenu;
    }
    private void UpdateSelectionSummary()
    {
        var label = ResultText.Text;
        int mark = label.IndexOf(SelectionSuffix, StringComparison.Ordinal);
        if (mark >= 0)
            label = label[..mark];
        if (FamilyList.SelectedItems.Count > 1)
            label += SelectionSuffix + FamilyList.SelectedItems.Count.ToString("N0");
        if (label != ResultText.Text)
            ResultText.Text = label;
    }
}
