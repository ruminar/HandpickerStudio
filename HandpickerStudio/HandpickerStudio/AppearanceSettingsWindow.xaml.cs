using System.Windows;

namespace HandpickerStudio;

public partial class AppearanceSettingsWindow : Window
{
    internal AppearanceSettingsWindow(AppearanceTheme appearance, bool createImageCache)
    {
        InitializeComponent();
        AppearancePicker.SelectedIndex = (int)appearance;
        CacheOption.IsChecked = createImageCache;
    }

    internal AppearanceTheme Appearance => (AppearanceTheme)AppearancePicker.SelectedIndex;
    internal bool CreateImageCache => CacheOption.IsChecked == true;

    private void Apply(object sender, RoutedEventArgs e) => DialogResult = true;
}
