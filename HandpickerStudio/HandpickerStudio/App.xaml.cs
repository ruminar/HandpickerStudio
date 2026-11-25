using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;

namespace HandpickerStudio;

public partial class App : Application
{
    private AppearanceTheme appliedAppearance;
    private AppearanceTheme paletteAppearance;

    internal void ApplyAppearance(AppearanceTheme appearance)
    {
        if (appliedAppearance == appearance)
            return;

        if (appliedAppearance == AppearanceTheme.System)
            SystemEvents.UserPreferenceChanged -= SystemAppearanceChanged;
        appliedAppearance = appearance;

#pragma warning disable WPF0001 // Fluent controls and window chrome follow the selected appearance.
        ThemeMode = appearance switch
        {
            AppearanceTheme.System => System.Windows.ThemeMode.System,
            AppearanceTheme.Dark => System.Windows.ThemeMode.Dark,
            _ => System.Windows.ThemeMode.Light
        };
#pragma warning restore WPF0001

        if (appearance == AppearanceTheme.System)
            SystemEvents.UserPreferenceChanged += SystemAppearanceChanged;
        UpdatePalette();
    }

    private void SystemAppearanceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.InvokeAsync(UpdatePalette, DispatcherPriority.Background);

    private void UpdatePalette()
    {
        var appearance = appliedAppearance == AppearanceTheme.System
            ? (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 0 ? AppearanceTheme.Dark : AppearanceTheme.Light)
            : appliedAppearance;
        if (paletteAppearance == appearance)
            return;

        var dictionaries = Resources.MergedDictionaries;
        var palette = dictionaries.First(dictionary =>
            dictionary.Source?.OriginalString == $"/HandpickerStudio;component/Themes/{paletteAppearance}.xaml");
        dictionaries[dictionaries.IndexOf(palette)] = new ResourceDictionary
        {
            Source = new Uri($"/HandpickerStudio;component/Themes/{appearance}.xaml", UriKind.Relative)
        };
        paletteAppearance = appearance;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (appliedAppearance == AppearanceTheme.System)
            SystemEvents.UserPreferenceChanged -= SystemAppearanceChanged;
        base.OnExit(e);
    }
}
