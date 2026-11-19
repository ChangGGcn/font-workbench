using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FontBook.Workbench;

public partial class MainWindow
{
    private bool? _appearanceHighContrast;
    private int _appearanceRefreshQueued;
    private GridLength _expandedNavigationWidth;

    // MainWindow.xaml.cs calls this after InitializeComponent, including under a generic Application.
    private void SyncContrast()
    {
        if (_appearanceHighContrast is null)
        {
            ThemePicker.SelectionChanged += OnThemeSelectionChanged;
            _expandedNavigationWidth = NavColumn.Width;
            NavToggleButton.Click += (_, _) => ToggleNavigationPane();
            SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;
            Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
        }

        bool highContrast = SystemParameters.HighContrast;
        bool contrastChanged = _appearanceHighContrast is bool previous && previous != highContrast;
        WindowsAppearance.Apply(this, ThemePicker.SelectedIndex, contrastChanged);
        _appearanceHighContrast = highContrast;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ThemePicker) && !_closed)
            RefreshAppearance();
    }

    private void OnSystemPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            QueueAppearanceRefresh();
    }

    private void OnWindowsPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or
            UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility or
            UserPreferenceCategory.Desktop or UserPreferenceCategory.Window)
            QueueAppearanceRefresh();
    }

    private void QueueAppearanceRefresh()
    {
        if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _appearanceRefreshQueued, 1) != 0)
            return;

        // Run after WPF processes its system resource notifications; this event can be off-thread.
        _ = Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _appearanceRefreshQueued, 0);
            if (!_closed)
                RefreshAppearance();
        }, DispatcherPriority.Background);
    }

    private void RefreshAppearance()
    {
        SyncContrast();
        Draw(0);
        _glyphWindow?.Redraw();
        InvalidateLibraryPreviews();
    }

    private void ToggleNavigationPane()
    {
        bool collapse = NavigationPane.Visibility == Visibility.Visible;
        NavColumn.Width = collapse ? new GridLength(0) : _expandedNavigationWidth;
        NavigationPane.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        string label = collapse ? "展开导航栏" : "折叠导航栏";
        NavToggleButton.ToolTip = label;
        AutomationProperties.SetName(NavToggleButton, label);
    }
}
