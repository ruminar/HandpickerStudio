using System.Windows;

namespace HandpickerStudio;

public partial class AppearanceSettingsWindow : Window
{
    internal AppearanceSettingsWindow(AppearanceTheme appearance)
    {
        InitializeComponent();
        AppearancePicker.SelectedIndex = (int)appearance;
    }

    internal AppearanceTheme Appearance => (AppearanceTheme)AppearancePicker.SelectedIndex;

    private void Apply(object sender, RoutedEventArgs e) => DialogResult = true;
}
