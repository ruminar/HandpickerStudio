using System.Windows;
using System.Windows.Controls;

namespace HandpickerStudio;

public partial class MoveDestinationWindow : Window
{
    private readonly int imageCount;
    private CancellationTokenSource? pending;

    internal MoveDestinationWindow(Project[] projects, Guid currentProject, int imageCount)
    {
        this.imageCount = imageCount;
        InitializeComponent();
        Project[] eligible = projects.Where(project => project.Roots.Any(root => root.Enabled)).ToArray();
        ProjectsBox.ItemsSource = eligible;
        ProjectsBox.SelectedItem = eligible.FirstOrDefault(project => project.Id != currentProject) ?? eligible.FirstOrDefault();
    }

    internal Checkpoint? Destination { get; private set; }
    internal ImageRoot? DestinationRoot { get; private set; }

    private async void ProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        pending?.Cancel();
        CheckpointsBox.ItemsSource = null;
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
            var catalog = await Task.Run(() => CatalogService.ReadCatalog([project], project, load.Token), load.Token);
            if (load.IsCancellationRequested)
                return;

            CheckpointsBox.ItemsSource = catalog.Checkpoints;
            if (catalog.Checkpoints.Length == 1)
                CheckpointsBox.SelectedIndex = 0;
            DestinationPath.Text = catalog.Checkpoints.Length == 0 ? "利用できるCheckpointがありません。" : "";
            DestinationChanged(sender, e);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error) when (MainWindow.IsOperationError(error)) { DestinationPath.Text = error.Message; }
        finally
        {
            if (ReferenceEquals(pending, load))
                pending = null;
        }
    }

    private void DestinationChanged(object sender, SelectionChangedEventArgs e)
    {
        MoveButton.IsEnabled = RootsBox.SelectedItem is ImageRoot && CheckpointsBox.SelectedItem is Checkpoint;
        if (MoveButton.IsEnabled && RootsBox.SelectedItem is ImageRoot root && CheckpointsBox.SelectedItem is Checkpoint target)
            DestinationPath.Text = $"{imageCount} 枚 → {target.Project.Name} / {root.Name} / {target.RelativePath}{Environment.NewLine}{root.ArchivePath}";
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (CheckpointsBox.SelectedItem is not Checkpoint checkpoint || RootsBox.SelectedItem is not ImageRoot root)
            return;
        Destination = checkpoint;
        DestinationRoot = root;
        DialogResult = true;
    }

    private void CancelLoad(object? sender, EventArgs e) => pending?.Cancel();
}
