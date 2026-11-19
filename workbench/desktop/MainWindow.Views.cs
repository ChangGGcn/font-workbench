using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FontBook.Workbench;

public partial class MainWindow
{
    private enum LibraryView
    {
        Compact, Grid, Samples
    }
    private sealed record LibraryLayout(DataTemplate? Template, Style? ContainerStyle, ItemsPanelTemplate Panel);

    private LibraryView _libraryView;
    private LibraryLayout[]? _libraryLayouts;
    private LibraryPreviewQueue? _libraryPreviews;
    private DispatcherOperation? _restoreLibraryViewport;
    private bool IsLibraryGridView => _libraryView == LibraryView.Grid;

    private void InitializeLibraryViews()
    {
        var picker = ViewPicker;

        var views = new ResourceDictionary { Source = new Uri("/FontWorkbench;component/LibraryViews.xaml", UriKind.Relative) };
        var listPanel = FamilyList.ItemsPanel;
        if (FamilyList.ItemContainerStyle is null)
        {
            // The stock alignment bindings look for an ancestor even while the theme replaces detached recycled items.
            var compactStyle = new Style(typeof(ListBoxItem), FamilyList.TryFindResource(typeof(ListBoxItem)) as Style);
            compactStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, FamilyList.HorizontalContentAlignment));
            compactStyle.Setters.Add(new Setter(VerticalContentAlignmentProperty, FamilyList.VerticalContentAlignment));
            FamilyList.ItemContainerStyle = compactStyle;
        }
        _libraryLayouts =
        [
            new(FamilyList.ItemTemplate, FamilyList.ItemContainerStyle, listPanel),
            new((DataTemplate)views["LibraryCardTemplate"], (Style)views["LibraryCardItem"], (ItemsPanelTemplate)views["LibraryTilePanel"]),
            new((DataTemplate)views["LibrarySampleTemplate"], (Style)views["LibrarySampleItem"], listPanel),
        ];
        FamilyList.SelectionMode = SelectionMode.Extended;
        // Take grid arrows before the later window shortcut from the library to the style picker.
        PreviewKeyDown += NavigateLibraryGrid;
        _libraryPreviews = new LibraryPreviewQueue(this, FamilyList, () => _closed ? null : _catalog);
        Closed += (_, _) =>
        {
            _restoreLibraryViewport?.Abort();
            _libraryPreviews.Dispose();
        };

        picker.MinWidth = Math.Max(picker.MinWidth, 104);
        picker.ItemsSource = new[] { "紧凑列表", "网格卡片", "样本列表" };
        picker.SelectedIndex = (int)LibraryView.Compact;
        picker.SelectionChanged += (_, _) => SetLibraryView(picker.SelectedIndex);
    }

    private void InvalidateLibraryPreviews() => _libraryPreviews?.Invalidate();

    private void SetLibraryView(int index)
    {
        if (_closed || _libraryLayouts is not { } layouts || (uint)index >= layouts.Length || index == (int)_libraryView)
            return;

        InvalidateLibraryPreviews();
        _libraryView = (LibraryView)index;
        var layout = layouts[index];
        FamilyList.ItemsPanel = layout.Panel;
        FamilyList.ItemContainerStyle = layout.ContainerStyle;
        FamilyList.ItemTemplate = layout.Template;

        // Only the presentation changes. The ListBox still owns SelectedItems, SelectedItem and the extended-selection anchor.
        _restoreLibraryViewport?.Abort();
        _restoreLibraryViewport = Dispatcher.InvokeAsync(() =>
        {
            _restoreLibraryViewport = null;
            if (!_closed && FamilyList.SelectedItem is FamilyRow primary)
                FamilyList.ScrollIntoView(primary);
        }, DispatcherPriority.Loaded);
    }

    private void NavigateLibraryGrid(object sender, KeyEventArgs e)
    {
        if (!IsLibraryGridView || !FamilyList.IsKeyboardFocusWithin ||
            (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0 ||
            e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) ||
            FamilyList.Items.Count == 0)
            return;

        FamilyList.UpdateLayout();
        if (FindLibraryTilePanel(FamilyList) is not { } panel)
            return;

        var container = e.OriginalSource is DependencyObject source ? ItemsControl.ContainerFromElement(FamilyList, source) : null;
        int focused = container is null ? -1 : FamilyList.ItemContainerGenerator.IndexFromContainer(container);
        int current = focused >= 0 ? focused : FamilyList.SelectedIndex;
        int next = panel.NavigationIndex(current, FamilyList.Items.Count, e.Key);
        e.Handled = true;
        if (next == focused)
            return;

        panel.BringIndexIntoViewPublic(next);
        FamilyList.UpdateLayout();
        // Keep focus transitions inside the ListBox: its native Extended selection handles Shift/Ctrl and ranges.
        if (FamilyList.ItemContainerGenerator.ContainerFromIndex(next) is ListBoxItem item)
            item.Focus();
    }

    private static VirtualizingTilePanel? FindLibraryTilePanel(Visual parent)
    {
        if (parent is VirtualizingTilePanel panel)
            return panel;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is Visual child && FindLibraryTilePanel(child) is { } match)
                return match;
        }
        return null;
    }
}
