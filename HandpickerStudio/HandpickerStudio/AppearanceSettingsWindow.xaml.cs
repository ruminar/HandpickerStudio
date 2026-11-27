using System.Windows;

namespace HandpickerStudio;

public partial class AppearanceSettingsWindow : Window
{
    internal AppearanceSettingsWindow(AppearanceTheme appearance, bool createImageCache, bool checkUpdatesOnStartup)
    {
        InitializeComponent();
        AppearancePicker.SelectedIndex = (int)appearance;
        CacheOption.IsChecked = createImageCache;
        UpdateOption.IsChecked = checkUpdatesOnStartup;
    }

    internal AppearanceTheme Appearance => (AppearanceTheme)AppearancePicker.SelectedIndex;
    internal bool CreateImageCache => CacheOption.IsChecked == true;
    internal bool CheckUpdatesOnStartup => UpdateOption.IsChecked == true;

    private void Apply(object sender, RoutedEventArgs e) => DialogResult = true;
}
