using System.Windows;
using System.Windows.Controls;

namespace HandpickerStudio;

public partial class MoveDestinationWindow : Window
{
    private readonly ImageEntry[] images;
    private CancellationTokenSource? pending;
    private ImageMoveMatch[] matches = [];

    internal MoveDestinationWindow(Project[] projects, Guid currentProject, ImageEntry[] images)
    {
        this.images = images;
        InitializeComponent();
        Project[] eligible = projects.Where(project => project.Roots.Any(root => root.Enabled)).ToArray();
        ProjectsBox.ItemsSource = eligible;
        ProjectsBox.SelectedItem = eligible.FirstOrDefault(project => project.Id != currentProject) ?? eligible.FirstOrDefault();
    }

    internal Project? Destination { get; private set; }
    internal ImageRoot? DestinationRoot { get; private set; }
    internal ImageMoveMatch[] Confirmed { get; private set; } = [];

    private async void ProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        pending?.Cancel();
        matches = [];
        MoveButton.IsEnabled = false;
        DestinationPath.Text = "";
        RootsBox.ItemsSource = null;
        if (ProjectsBox.SelectedItem is not Project project)
            return;

        var roots = project.Roots.Where(root => root.Enabled).ToArray();
        RootsBox.ItemsSource = roots;
        if (roots.Length == 1)
            RootsBox.SelectedIndex = 0;
        DestinationPath.Text = "Checkpointを確認中…";
        using var load = new CancellationTokenSource();
        pending = load;
        try
        {
            var plan = await Task.Run(() => ImageOperations.PlanProjectMove(project, images, load.Token), load.Token);
            if (load.IsCancellationRequested)
                return;

            matches = plan;
            DestinationChanged(sender, e);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error) when (MainWindow.IsOperationError(error))
        {
            if (!load.IsCancellationRequested)
                DestinationPath.Text = error.Message;
        }
        finally
        {
            if (ReferenceEquals(pending, load))
                pending = null;
        }
    }

    private void DestinationChanged(object sender, SelectionChangedEventArgs e)
    {
        int eligible = matches.Count(match => match.Checkpoint is not null);
        MoveButton.IsEnabled = eligible > 0 && RootsBox.SelectedItem is ImageRoot;
        if (matches.Length == 0)
            return;

        DestinationPath.Text = $"移動対象 {eligible} 枚 / 対象外 {matches.Length - eligible} 枚";
        if (eligible == 0)
            DestinationPath.Text += $"{Environment.NewLine}ファイル名から移動先を特定できる画像がありません。";
        else if (ProjectsBox.SelectedItem is Project project && RootsBox.SelectedItem is ImageRoot root)
            DestinationPath.Text += $"{Environment.NewLine}{project.Name} / {root.Name}{Environment.NewLine}{root.ArchivePath}";
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (!MoveButton.IsEnabled || ProjectsBox.SelectedItem is not Project project || RootsBox.SelectedItem is not ImageRoot root)
            return;
        Destination = project;
        DestinationRoot = root;
        Confirmed = matches;
        DialogResult = true;
    }

    private void CancelLoad(object? sender, EventArgs e) => pending?.Cancel();
}
