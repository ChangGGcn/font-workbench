using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FontBook.Workbench;

public partial class App
{
    // Set before App.xaml loads Fluent resources or creates a window. The switch is process-local.
    static App() => WindowsAppearance.UseSolidSurfaces();
}

internal static class WindowsAppearance
{
    internal static void UseSolidSurfaces() =>
        AppContext.SetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", true);

    internal static Color Ink(Window owner)
    {
        if (SystemParameters.HighContrast || owner.TryFindResource("TextFillColorPrimaryBrush") is not SolidColorBrush ink)
            return SystemColors.WindowTextColor;

        // Fluent text can be translucent; native previews use the opaque ink and their own coverage alpha.
        return Color.FromRgb(ink.Color.R, ink.Color.G, ink.Color.B);
    }

    internal static void Apply(Window owner, int selection, bool refreshTheme)
    {
        UseSolidSurfaces();
        bool highContrast = SystemParameters.HighContrast;
        // .NET 10's runtime ThemeMode APIs are still flagged experimental.
        // WPF sets the DWM dark frame where supported and otherwise leaves the native frame alone.
#pragma warning disable WPF0001
        // WPF loads its high-contrast dictionary for any non-None ThemeMode. Keep the normal
        // caption in that case so Windows can draw it with its high-contrast system colors.
        ThemeMode mode = highContrast ? ThemeMode.Light : selection switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System
        };

        var app = Application.Current;
        if (app is not null)
        {
            if (refreshTheme || app.ThemeMode != mode)
                app.ThemeMode = mode;
        }
        else if (refreshTheme || owner.ThemeMode != mode)
        {
            owner.ThemeMode = mode;
        }
#pragma warning restore WPF0001

        var resources = app?.Resources ?? owner.Resources;
        EnsureContainerStyle(owner, resources, typeof(ListBoxItem), "WorkbenchListBoxItemStyle");
        EnsureContainerStyle(owner, resources, typeof(ComboBoxItem), "WorkbenchComboBoxItemStyle");
        Brush surface = highContrast ? SystemColors.WindowBrush : Ink(owner).R >= 128 ? Brushes.Black : Brushes.White;
        UpdateResource(resources, "WorkbenchSurfaceBrush", surface);
        var rendering = SystemTextRenderingMode();
        UpdateResource(resources, "WorkbenchTextRenderingMode", rendering);
        UpdateResource(resources, "WorkbenchClearTypeHint",
            rendering == TextRenderingMode.ClearType ? ClearTypeHint.Enabled : ClearTypeHint.Auto);

        Color selectionColor = highContrast ? SystemColors.ControlColor : SystemColors.AccentColor;
        double opacity = highContrast ? 1 : 0.18;
        if (owner.Resources["NavSelectionBrush"] is not SolidColorBrush existing ||
            existing.Color != selectionColor || existing.Opacity != opacity)
        {
            var brush = new SolidColorBrush(selectionColor) { Opacity = opacity };
            brush.Freeze();
            owner.Resources["NavSelectionBrush"] = brush;

        }
    }

    private static void EnsureContainerStyle(Window owner, ResourceDictionary resources, Type type, string key)
    {
        if (resources.Contains(key))
            return;

        // These app resources outlive Fluent color-dictionary swaps. Keep the stock template, but
        // don't bind alignment to an ItemsControl that a recycled item may no longer be under.
        var style = new Style(type, owner.TryFindResource(type) as Style);
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        resources[key] = style;
        resources[type] = style;
    }

    private static void UpdateResource(ResourceDictionary resources, string key, object value)
    {
        if (!Equals(resources[key], value))
            resources[key] = value;
    }

    private const uint SpiGetFontSmoothing = 0x004A;
    private const uint SpiGetFontSmoothingType = 0x200A;
    private const uint ClearTypeSmoothing = 2;

    private static TextRenderingMode SystemTextRenderingMode()
    {
        if (!SystemParametersInfo(SpiGetFontSmoothing, 0, out uint enabled, 0))
            return TextRenderingMode.Auto;
        if (enabled == 0)
            return SystemParameters.HighContrast ? TextRenderingMode.Aliased : TextRenderingMode.Grayscale;
        if (!SystemParametersInfo(SpiGetFontSmoothingType, 0, out uint type, 0))
            return TextRenderingMode.Auto;
        return type == ClearTypeSmoothing ? TextRenderingMode.ClearType : TextRenderingMode.Grayscale;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out uint value, uint flags);
}
