using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandpickerStudio;

public partial class MainWindow
{
    private const string CheckpointDragFormat = "HandpickerStudio.Checkpoint";
    private bool operationRunning;
    private Point checkpointDragOrigin;
    private Checkpoint? draggedCheckpoint;

    internal static bool IsOperationError(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or InvalidOperationException or JsonException or COMException or FileFormatException or
        ArgumentException or NotSupportedException;

    private void ImageMenuTarget(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(Gallery, source) is ListBoxItem { IsSelected: false, DataContext: ImageEntry image })
            Gallery.SelectedItem = image;
    }

    private void PrepareImageMenu(object sender, ContextMenuEventArgs e)
    {
        var images = Gallery.SelectedItems.OfType<ImageEntry>().ToArray();
        if (images.Length == 0 || viewModel.IsLoading || operationRunning)
        {
            e.Handled = true;
            return;
        }

        RecycleImagesMenu.IsEnabled = images.Any(image => image.CheckpointPath is not null);
        MoveImagesMenu.IsEnabled = viewModel.Projects.Any(project => project.Roots.Any(root => root.Enabled));
        ThumbnailMenu.IsEnabled = images is [{ CheckpointPath: not null }];
    }

    private async void RecycleSelectedImages(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is not { } project)
            return;
        var images = Gallery.SelectedItems.OfType<ImageEntry>().ToArray();
        if (images.Length != 0)
            await RunOperation("ごみ箱へ移動", () => ProcessImages(project, images, ImageOperations.Recycle), shell: true);
    }

    private async void MoveSelectedImages(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsLoading || operationRunning || viewModel.SelectedProject is not { } project)
            return;
        var images = Gallery.SelectedItems.OfType<ImageEntry>().ToArray();
        if (images.Length == 0)
            return;

        var dialog = new MoveDestinationWindow(viewModel.Projects, project.Id, images.Length) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Destination is { } destination && dialog.DestinationRoot is { } root)
            await RunOperation("画像を移動", () => ProcessImages(project, images, image => ImageOperations.Move(image, destination, root)));
    }

    private async void SetSelectedThumbnail(object sender, RoutedEventArgs e)
    {
        if (Gallery.SelectedItems.Count != 1 || Gallery.SelectedItem is not ImageEntry image ||
            viewModel.SelectedProject is not { } project || viewModel.SelectedCheckpoint is not { } checkpoint)
            return;

        await RunOperation("代表サムネイルに設定", () =>
            $"保存しました。{Environment.NewLine}{ImageOperations.SetThumbnail(ImageOperations.Capture(project, image), checkpoint)}");
    }

    private static string? ProcessImages(Project project, ImageEntry[] images, Action<ImageTarget> operation)
    {
        int completed = 0;
        List<string> problems = [];
        foreach (ImageEntry image in images.DistinctBy(image => image.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                operation(ImageOperations.Capture(project, image));
                completed++;
            }
            catch (Exception error) when (IsOperationError(error))
            {
                problems.Add($"{image.FullPath}{Environment.NewLine}{error.Message}");
            }
        }

        return problems.Count == 0 ? null : $"完了: {completed} 件 / 要確認: {problems.Count} 件{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine + Environment.NewLine, problems);
    }

    private void CheckpointMenuTarget(object sender, MouseButtonEventArgs e)
    {
        if (FindCheckpointPane(e.OriginalSource) is { } pane && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(pane.CheckpointList, source) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void PrepareCheckpointMenu(object sender, ContextMenuEventArgs e)
    {
        if (viewModel.SelectedProject is null || operationRunning || viewModel.IsLoading)
        {
            e.Handled = true;
            return;
        }

        var checkpoint = viewModel.SelectedCheckpoint;
        RatingMenu.Items.Clear();
        RatingMenu.IsEnabled = checkpoint is not null;
        DisposeMenu.IsEnabled = checkpoint is not null && viewModel.Groups.Any(group =>
            group.Rating == Rating.Delete && group.Items.Contains(checkpoint));
        foreach (var group in viewModel.Groups.Where(group => group.Rating != Rating.Unavailable))
        {
            var item = new MenuItem { Header = group.Name };
            item.Click += async (_, _) =>
            {
                if (checkpoint is not null)
                    await ChangeRating(checkpoint, group.Rating);
            };
            RatingMenu.Items.Add(item);
        }
    }

    private void BeginCheckpointDrag(object sender, MouseButtonEventArgs e)
    {
        checkpointDragOrigin = e.GetPosition(CheckpointPanes);
        draggedCheckpoint = FindCheckpointPane(e.OriginalSource) is { } pane && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(pane.CheckpointList, source) is ListBoxItem { DataContext: Checkpoint checkpoint }
            ? checkpoint : null;
    }

    private void TrackCheckpointDrag(object sender, MouseEventArgs e)
    {
        if (draggedCheckpoint is not { } checkpoint || e.LeftButton != MouseButtonState.Pressed || operationRunning || viewModel.IsLoading)
            return;

        Point position = e.GetPosition(CheckpointPanes);
        if (Math.Abs(position.X - checkpointDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - checkpointDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        draggedCheckpoint = null;
        try { DragDrop.DoDragDrop(CheckpointPanes, new DataObject(CheckpointDragFormat, checkpoint), DragDropEffects.Move); }
        finally { HighlightDropPane(null); }
    }

    private void CheckpointDragOver(object sender, DragEventArgs e)
    {
        var target = CheckpointDropTarget(e);
        HighlightDropPane(target);
        e.Effects = target is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void CheckpointDragLeave(object sender, DragEventArgs e) => HighlightDropPane(null);

    private async void DropCheckpoint(object sender, DragEventArgs e)
    {
        HighlightDropPane(null);
        e.Handled = true;
        var target = CheckpointDropTarget(e);
        e.Effects = target is not null ? DragDropEffects.Move : DragDropEffects.None;
        if (target is not null && e.Data.GetData(CheckpointDragFormat) is Checkpoint checkpoint &&
            !target.CheckpointList.Items.Contains(checkpoint))
            await ChangeRating(checkpoint, target.Rating);
    }

    private CheckpointPane? CheckpointDropTarget(DragEventArgs e) =>
        !operationRunning && !viewModel.IsLoading && e.Data.GetDataPresent(CheckpointDragFormat) &&
        FindCheckpointPane(e.OriginalSource) is { Rating: not Rating.Unavailable } pane ? pane : null;

    private Task ChangeRating(Checkpoint checkpoint, Rating rating) => RunOperation("評価を変更", () =>
    {
        using var suite = new SuiteEvaluation(checkpoint.Project);
        return suite.ChangeRating(checkpoint, rating);
    }, keepGallery: true);

    private async void DisposeCheckpoint(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedCheckpoint is not { } checkpoint || operationRunning || viewModel.IsLoading)
            return;

        FileStamp stamp;
        try { stamp = await Task.Run(() => CheckpointDisposal.Capture(checkpoint)); }
        catch (Exception error) when (IsOperationError(error)) { ShowReport("Checkpointを廃棄できません", error.Message); return; }
        if (viewModel.SelectedProject?.Id != checkpoint.Project.Id)
            return;

        string roots = string.Join(Environment.NewLine, checkpoint.Project.Roots.Where(root => root.Enabled)
            .Select(root => $"{root.Name}: {root.OutputPath}"));
        if (roots.Length == 0)
            roots = "有効な参照ルートなし";
        var answer = MessageBox.Show(this, $"Checkpoint本体をごみ箱へ移しますか？{Environment.NewLine}{checkpoint.FullPath}" +
            $"{Environment.NewLine}{Environment.NewLine}対象画像の生成を停止してから実行してください。退避対象のルート:{Environment.NewLine}{roots}",
            "Checkpointを廃棄", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
            return;

        Project[] projects = viewModel.Projects;
        HeldLabel[] holds = viewModel.HeldLabels;
        await RunOperation("Checkpointを廃棄", () => CheckpointDisposal.Execute(checkpoint, stamp, projects, holds), shell: true);
    }

    private async void ResumeDisposal(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is { } project)
            await RunOperation("廃棄後処理を再開", () => CheckpointDisposal.Resume(project));
    }

    private async Task RunOperation(string title, Func<string?> action, bool shell = false, bool keepGallery = false, bool reload = true)
    {
        if (operationRunning || viewModel.IsLoading)
            return;

        string? notice;
        operationRunning = true;
        IsEnabled = false;
        try
        {
            notice = shell ? await RecycleBin.RunOnSta(action) : await Task.Run(action);
        }
        catch (Exception error) when (IsOperationError(error)) { notice = error.Message; }
        finally
        {
            operationRunning = false;
            IsEnabled = true;
        }

        if (notice is not null)
            ShowReport(title, notice);
        if (reload)
            await viewModel.ReloadAsync(keepGallery);
    }
}
