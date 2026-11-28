using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace HandpickerStudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new(new SettingsStore(WorkspacePaths.SettingsPath, readOnly: App.IsProbe));
    private bool wasMaximized;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelChanged;
    }

    private void RestorePlacement(object? sender, EventArgs e)
    {
        if (viewModel.LoadSettings() is { } saved)
            NativeWindowPlacement.Restore(new WindowInteropHelper(this).Handle, saved);
    }

    private void RememberWindowState(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            wasMaximized = WindowState == WindowState.Maximized;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (App.IsProbe)
            return;
        startupRunning = true;
        try
        {
            var initialize = viewModel.InitializeAsync();
            Guid? startupProject = viewModel.SelectedProject?.Id;
            await initialize;
            if (IsLoaded && viewModel.SelectedProject is { } project && project.Id == startupProject &&
                viewModel.GetArchivePreference(project.Id).Automatic)
                await RunArchiveAsync(project, selection: null, restore: false, automatic: true);
        }
        finally { startupRunning = false; }
        if (IsLoaded && viewModel.CheckUpdatesOnStartup && App.ReleaseProfile is not null)
            await CheckForAppUpdateAsync(automatic: true);
    }

    internal bool ProbeSucceeded => viewModel.SettingsLoaded;

    private void OnClosed(object? sender, EventArgs e)
    {
        windowLifetime.Cancel();
        viewModel.Cancel();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = operationRunning;
        if (App.IsProbe || e.Cancel)
            return;
        if (App.ExitForUpdate && !CanExitForUpdate())
        {
            e.Cancel = true;
            return;
        }
        if (NativeWindowPlacement.TryRead(new WindowInteropHelper(this).Handle, out var placement) &&
            !viewModel.SavePlacement(placement.Capture(wasMaximized)) && App.ExitForUpdate)
            e.Cancel = true;
    }

    private async void AddProject(object sender, RoutedEventArgs e) => await EditProjectAsync(null);

    private async void EditProject(object sender, RoutedEventArgs e) => await EditProjectAsync(viewModel.SelectedProject);

    private async void EditAppearance(object sender, RoutedEventArgs e)
    {
        var editor = new AppearanceSettingsWindow(viewModel.Appearance, viewModel.CreateImageCache,
            viewModel.CheckUpdatesOnStartup) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            displaySettingsRunning = true;
            try { await viewModel.ChangeDisplaySettingsAsync(editor.Appearance, editor.CreateImageCache, editor.CheckUpdatesOnStartup); }
            finally { displaySettingsRunning = false; }
        }
    }

    private async void EditRoots(object sender, RoutedEventArgs e) => await EditProjectAsync(viewModel.SelectedProject, showRoots: true);

    private async Task EditProjectAsync(Project? project, bool showRoots = false)
    {
        var editor = new ProjectEditorWindow(project, showRoots, viewModel) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } edited)
            await viewModel.SetProjectAsync(edited, editor.AutoArchive);
    }

    private async void RemoveProject(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is { } project && MessageBox.Show(this,
            $"「{project.Name}」の登録を解除しますか？画像とCheckpointは元の場所に残ります。", "登録解除",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await viewModel.RemoveProjectAsync(project);
    }

    private async void ToggleRoot(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ImageRoot root })
            await viewModel.ToggleRootAsync(root);
    }

    private async void Reload(object sender, RoutedEventArgs e) => await viewModel.ReloadAsync();

    private void ChangeGalleryView(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } picker)
            return;

        var layout = (GalleryLayout)picker.SelectedIndex;
        if (layout == viewModel.GalleryView)
            return;

        galleryWheelRemainder = 0;
        var selection = Gallery.SelectedItems.Cast<ImageEntry>().ToArray();
        viewModel.ChangeGalleryView(layout);
        if (selection.Length == 0)
            return;

        // Apply the new panel before restoring any selection lost when its containers were replaced.
        Gallery.UpdateLayout();
        if (!selection.SequenceEqual(Gallery.SelectedItems.Cast<ImageEntry>()))
        {
            Gallery.SelectedItems.Clear();
            foreach (var image in selection)
                Gallery.SelectedItems.Add(image);
        }
        Gallery.ScrollIntoView(selection[0]);
    }

    private void ShowUnresolved(object sender, RoutedEventArgs e) => viewModel.ShowUnresolved();

    private void OpenImage(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None ||
            e.OriginalSource is not DependencyObject source)
            return;
        if (ItemsControl.ContainerFromElement(Gallery, source) is ListBoxItem { DataContext: ImageEntry image })
        {
            e.Handled = true;
            ShowImage(image);
        }
    }

    private void OpenImageWithKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            RecycleSelectedImages(sender, e);
            return;
        }
        if (e.Key == Key.Enter && Gallery.SelectedItem is ImageEntry image)
        {
            e.Handled = true;
            ShowImage(image);
        }
    }

    private void OpenSelectedImage(object sender, RoutedEventArgs e)
    {
        if (Gallery.SelectedItem is ImageEntry image)
            ShowImage(image);
    }

    private async void ShowImage(ImageEntry image)
    {
        ImageEntry[] images = viewModel.Images;
        int index = Array.IndexOf(images, image);
        if (index >= 0 && viewModel.SelectedProject is { } project)
        {
            var viewer = new ImageViewerWindow(images, index, project, viewModel.SelectedCheckpoint, viewModel.GalleryCache) { Owner = this };
            viewer.ShowDialog();
            if (viewer.ImagesChanged)
                await ReloadGalleryAfterRecycleAsync();
        }
    }

    private void ShowWarnings(object sender, RoutedEventArgs e) => ShowReport("確認事項", viewModel.Warnings);

    private void ShowCollisions(object sender, RoutedEventArgs e) => ShowReport("Checkpoint名の重複", viewModel.CollisionReport);

    private void ShowReport(string title, string text) => ShowReport(this, title, text);

    private static bool ShowReport(Window owner, string title, string text, string? action = null)
    {
        var report = new Window
        {
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = title,
            Width = 720, Height = 410, MinWidth = 380, MinHeight = 250
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0) };
        var copy = new Button { Content = "コピー", Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); }
            catch (ExternalException) { MessageBox.Show(report, "クリップボードを利用できません。", title); }
        };
        actions.Children.Add(copy);
        if (action is not null)
        {
            var confirm = new Button { Content = action, Margin = new Thickness(0, 0, 8, 0) };
            confirm.Click += (_, _) => report.DialogResult = true;
            actions.Children.Add(confirm);
        }
        var close = new Button { Content = action is null ? "閉じる" : "キャンセル", IsCancel = true, IsDefault = action is not null };
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        panel.Children.Add(new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top
        });
        report.Content = panel;
        return report.ShowDialog() == true;
    }
}
