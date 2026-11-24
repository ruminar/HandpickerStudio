using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandpickerStudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new(new SettingsStore(Path.Combine(AppContext.BaseDirectory, "settings.json")));

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e) => viewModel.Cancel();

    private async void AddProject(object sender, RoutedEventArgs e) => await EditProjectAsync(null);

    private async void EditProject(object sender, RoutedEventArgs e) => await EditProjectAsync(viewModel.SelectedProject);

    private async void EditRoots(object sender, RoutedEventArgs e) => await EditProjectAsync(viewModel.SelectedProject, showRoots: true);

    private async Task EditProjectAsync(Project? project, bool showRoots = false)
    {
        var editor = new ProjectEditorWindow(project, showRoots) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } edited)
            await viewModel.SetProjectAsync(edited);
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

    private void SelectCheckpoint(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is Checkpoint checkpoint)
            viewModel.SelectCheckpoint(checkpoint);
    }

    private void ShowUnresolved(object sender, RoutedEventArgs e)
    {
        if (CheckpointTree.SelectedItem is { } selection && FindTreeItem(CheckpointTree, selection) is { } container)
            container.IsSelected = false;
        viewModel.ShowUnresolved();
    }

    private static TreeViewItem? FindTreeItem(ItemsControl control, object selection)
    {
        if (control.ItemContainerGenerator.ContainerFromItem(selection) is TreeViewItem selected)
            return selected;

        foreach (object item in control.Items)
        {
            if (control.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem child &&
                FindTreeItem(child, selection) is { } nested)
                return nested;
        }

        return null;
    }

    private void OpenImage(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject source)
            return;
        if (ItemsControl.ContainerFromElement(Gallery, source) is ListBoxItem { DataContext: ImageEntry image })
            ShowImage(image);
    }

    private void OpenImageWithKey(object sender, KeyEventArgs e)
    {
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

    private void ShowImage(ImageEntry image)
    {
        ImageEntry[] images = viewModel.Images;
        int index = Array.IndexOf(images, image);
        if (index >= 0)
            new ImageViewerWindow(images, index) { Owner = this }.ShowDialog();
    }

    private void ShowWarnings(object sender, RoutedEventArgs e) => ShowReport("確認事項", viewModel.Warnings);

    private void ShowCollisions(object sender, RoutedEventArgs e) => ShowReport("Checkpoint名の重複", viewModel.CollisionReport);

    private void ShowReport(string title, string text)
    {
        var report = new Window
        {
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = title,
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
        var close = new Button { Content = "閉じる", IsCancel = true };
        actions.Children.Add(copy);
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        panel.Children.Add(new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top
        });
        report.Content = panel;
        report.ShowDialog();
    }
}
