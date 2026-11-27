using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HandpickerStudio.Update;

namespace HandpickerStudio;

public partial class App : Application
{
    internal static UpdateLayout? Installation { get; private set; }
    internal static UpdateProfile? ReleaseProfile { get; private set; }
    internal static bool IsProbe { get; private set; }
    internal static bool ExitForUpdate { get; set; }
    internal static string Version { get; } = UpdatePackage.CurrentVersion(typeof(App).Assembly);

    private AppearanceTheme appliedAppearance;
    private AppearanceTheme paletteAppearance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args is [var mode, var root] && mode is "--portable" or "--probe")
            {
                IsProbe = mode == "--probe";
                Installation = new UpdateLayout(root);
                if (!UpdateLayout.PathEquals(AppContext.BaseDirectory, Installation.VersionDirectory(Version)) ||
                    !UpdateLayout.PathEquals(Environment.ProcessPath!, Installation.ApplicationPath(Version)))
                    throw new InvalidDataException("ポータブル版の本体の配置を確認できません。");
                UpdateLayout.RequireDirectory(Installation.Root);
                if (UpdateLayout.Exists(Installation.Data))
                    UpdateLayout.RequireDirectory(Installation.Data);
                else if (!IsProbe)
                    UpdateLayout.EnsureDirectory(Installation.Data);
                if (UpdateLayout.Exists(WorkspacePaths.SettingsPath))
                    UpdateLayout.RequireFile(WorkspacePaths.SettingsPath);
                ReleaseProfile = UpdateProfile.Load(typeof(App).Assembly);
            }
            else if (e.Args.Length != 0)
                throw new InvalidDataException("起動引数が正しくありません。");
            else if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) is { } parent &&
                string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), "app", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("展開先直下の HandpickerStudio.exe から起動してください。");

            var window = new MainWindow();
            MainWindow = window;
            window.Title += ReleaseProfile?.Test == true ? $"（更新試験用 {Version}）" : $" {Version}";
            if (IsProbe)
            {
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.Opacity = 0;
            }
            window.Show();
            if (IsProbe)
                Dispatcher.BeginInvoke(() => Shutdown(window.ProbeSucceeded ? 0 : 1), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception error) when (HandpickerStudio.MainWindow.IsOperationError(error))
        {
            if (!IsProbe)
                MessageBox.Show(error.Message, "HandpickerStudioを起動できません", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

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
        if (ExitForUpdate)
            e.ApplicationExitCode = UpdateLayout.ApplyExitCode;
    }
}
