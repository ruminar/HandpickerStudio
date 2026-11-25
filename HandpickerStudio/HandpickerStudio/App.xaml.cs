using System.Windows;

namespace HandpickerStudio;

public partial class App : Application
{
    private AppearanceTheme appliedAppearance;

    internal void ApplyAppearance(AppearanceTheme appearance)
    {
        if (appliedAppearance == appearance)
            return;

#pragma warning disable WPF0001 // Fluent controls and window chrome follow the selected appearance.
        ThemeMode = appearance == AppearanceTheme.Dark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light;
#pragma warning restore WPF0001

        var dictionaries = Resources.MergedDictionaries;
        var palette = dictionaries.First(dictionary =>
            dictionary.Source?.OriginalString == $"/HandpickerStudio;component/Themes/{appliedAppearance}.xaml");
        dictionaries[dictionaries.IndexOf(palette)] = new ResourceDictionary
        {
            Source = new Uri($"/HandpickerStudio;component/Themes/{appearance}.xaml", UriKind.Relative)
        };
        appliedAppearance = appearance;
    }
}
