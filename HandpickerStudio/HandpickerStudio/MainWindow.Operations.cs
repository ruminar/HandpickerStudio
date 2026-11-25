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
    private bool deferCheckpointClick;

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
        ReleaseLegacyHoldsMenu.Visibility = viewModel.CanSave && images.Any(image => image.LegacyHolds is { Length: > 0 })
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ReleaseLegacyHolds(object sender, RoutedEventArgs e)
    {
        var holds = Gallery.SelectedItems.OfType<ImageEntry>().SelectMany(image => image.LegacyHolds ?? [])
            .DistinctBy(CatalogService.HoldKey).ToArray();
        if (holds.Length == 0 || viewModel.IsLoading || operationRunning || !viewModel.CanSave)
            return;

        string details = string.Join(Environment.NewLine, holds.Take(8).Select(hold => $"{hold.Label} | {hold.OutputPath}"));
        if (holds.Length > 8)
            details += $"{Environment.NewLine}ほか {holds.Length - 8} 件";
        if (MessageBox.Show(this, $"旧版の保留を {holds.Length} 件解除します。同じラベルとOutput範囲の画像をまとめて再照合します。" +
            $"{Environment.NewLine}当時の重複相手は記録されていません。所属を確認できる場合に続けてください。" +
            $"{Environment.NewLine}{Environment.NewLine}{details}", "旧版の保留を解除", MessageBoxButton.OKCancel,
            MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
            await viewModel.ReleaseLegacyHoldsAsync(holds);
    }

    private async void RecycleSelectedImages(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is not { } project)
            return;
        var images = Gallery.SelectedItems.OfType<ImageEntry>().ToArray();
        if (images.Length != 0)
        {
            var cleanup = BeginCacheCleanup();
            await RunOperation("ごみ箱へ移動", () => ProcessImages(project, images, ImageOperations.Recycle, cleanup),
                shell: true, cleanup: cleanup);
        }
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
        {
            var cleanup = BeginCacheCleanup();
            await RunOperation("画像を移動", () => ProcessImages(project, images, image => ImageOperations.Move(image, destination, root), cleanup),
                cleanup: cleanup);
        }
    }

    private async void SetSelectedThumbnail(object sender, RoutedEventArgs e)
    {
        if (Gallery.SelectedItems.Count != 1 || Gallery.SelectedItem is not ImageEntry image ||
            viewModel.SelectedProject is not { } project || viewModel.SelectedCheckpoint is not { } checkpoint)
            return;

        await RunOperation("代表サムネイルに設定", () =>
            $"保存しました。{Environment.NewLine}{ImageOperations.SetThumbnail(ImageOperations.Capture(project, image), checkpoint)}");
    }

    private static string? ProcessImages(Project project, ImageEntry[] images, Action<ImageTarget> operation, PreviewCache.RemovalBatch? cleanup)
    {
        int completed = 0;
        List<string> problems = [];
        foreach (ImageEntry image in images.DistinctBy(image => image.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var target = ImageOperations.Capture(project, image);
                if (cleanup is null)
                    operation(target);
                else
                    cleanup.AfterRemoving(image.FullPath, () => operation(target));
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
        if (FindCheckpointRow(e.OriginalSource) is not { } hit)
            return;
        if (hit.Row.IsSelected)
            UpdateActiveCheckpoint(hit.Checkpoint);
        else
            SelectCheckpointItems(hit.List, hit.Checkpoint, ModifierKeys.None);
    }

    private void PrepareCheckpointMenu(object sender, ContextMenuEventArgs e)
    {
        if (viewModel.SelectedProject is null || operationRunning || viewModel.IsLoading)
        {
            e.Handled = true;
            return;
        }

        var checkpoint = viewModel.SelectedCheckpoint;
        var selection = SelectedCheckpoints();
        RatingMenu.Items.Clear();
        RatingMenu.IsEnabled = selection.Length > 0;
        DisposeMenu.IsEnabled = selection.Length == 1 && checkpoint is not null && viewModel.Groups.Any(group =>
            group.Rating == Rating.Delete && group.Items.Contains(checkpoint));
        foreach (var group in viewModel.Groups.Where(group => group.Rating != Rating.Unavailable))
        {
            var item = new MenuItem { Header = group.Name };
            item.Click += async (_, _) => await ChangeCheckpointRatings(selection, group.Rating);
            RatingMenu.Items.Add(item);
        }
    }

    private void BeginCheckpointDrag(object sender, MouseButtonEventArgs e)
    {
        draggedCheckpoint = null;
        deferCheckpointClick = false;
        if (operationRunning || viewModel.IsLoading || FindCheckpointRow(e.OriginalSource) is not { } hit)
            return;

        checkpointDragOrigin = e.GetPosition(CheckpointPanes);
        draggedCheckpoint = hit.Checkpoint;
        var modifiers = Keyboard.Modifiers;
        // A plain press on a selected row keeps the group intact until it becomes a click or drag.
        deferCheckpointClick = modifiers == ModifierKeys.None && hit.Row.IsSelected && SelectedCheckpoints().Length > 1;
        if (!deferCheckpointClick)
        {
            SelectCheckpointItems(hit.List, hit.Checkpoint, modifiers);
            if (hit.Row.IsSelected)
                hit.Row.Focus();
        }
        e.Handled = true;
    }

    private void FinishCheckpointClick(object sender, MouseButtonEventArgs e)
    {
        if (deferCheckpointClick && !operationRunning && !viewModel.IsLoading &&
            FindCheckpointRow(e.OriginalSource) is { } hit && Equals(hit.Checkpoint, draggedCheckpoint))
        {
            SelectCheckpointItems(hit.List, hit.Checkpoint, ModifierKeys.None);
            hit.Row.Focus();
            e.Handled = true;
        }
        deferCheckpointClick = false;
        draggedCheckpoint = null;
    }

    private void CancelCheckpointPress(object sender, MouseEventArgs e)
    {
        draggedCheckpoint = null;
        deferCheckpointClick = false;
    }

    private void TrackCheckpointDrag(object sender, MouseEventArgs e)
    {
        if (draggedCheckpoint is not { } checkpoint || e.LeftButton != MouseButtonState.Pressed || operationRunning || viewModel.IsLoading)
            return;

        Point position = e.GetPosition(CheckpointPanes);
        if (Math.Abs(position.X - checkpointDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - checkpointDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var selection = SelectedCheckpoints();
        draggedCheckpoint = null;
        deferCheckpointClick = false;
        if (!selection.Contains(checkpoint))
            return;

        UpdateActiveCheckpoint(checkpoint);
        try { DragDrop.DoDragDrop(CheckpointPanes, new DataObject(CheckpointDragFormat, selection), DragDropEffects.Move); }
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
        if (target is not null && e.Data.GetData(CheckpointDragFormat) is Checkpoint[] selection)
        {
            var changes = selection.Where(checkpoint => !target.CheckpointList.Items.Contains(checkpoint)).ToArray();
            await ChangeCheckpointRatings(changes, target.Rating);
        }
    }

    private CheckpointPane? CheckpointDropTarget(DragEventArgs e) =>
        !operationRunning && !viewModel.IsLoading && e.Data.GetDataPresent(CheckpointDragFormat) &&
        FindCheckpointPane(e.OriginalSource) is { Rating: not Rating.Unavailable } pane ? pane : null;

    private Task ChangeCheckpointRatings(Checkpoint[] selection, Rating rating)
    {
        if (selection.Length == 0 || viewModel.SelectedProject is not { } project ||
            selection.Any(checkpoint => checkpoint.Project.Id != project.Id))
            return Task.CompletedTask;

        return RunOperation("評価を変更", () =>
        {
            using var suite = new SuiteEvaluation(project);
            return suite.ChangeRatings(selection, rating);
        }, keepGallery: true);
    }

    private async void DisposeCheckpoint(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedCheckpoint is not { } checkpoint || SelectedCheckpoints().Length != 1 ||
            operationRunning || viewModel.IsLoading)
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
        var cleanup = BeginCacheCleanup();
        await RunOperation("Checkpointを廃棄", () => CheckpointDisposal.Execute(checkpoint, stamp, projects, holds, cleanup),
            shell: true, cleanup: cleanup);
    }

    private async void ResumeDisposal(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is { } project)
        {
            var cleanup = BeginCacheCleanup();
            await RunOperation("廃棄後処理を再開", () => CheckpointDisposal.Resume(project, cleanup), cleanup: cleanup);
        }
    }

    private PreviewCache.RemovalBatch? BeginCacheCleanup() => viewModel.GalleryCache is { } cache ? new(cache) : null;

    private async Task RunOperation(string title, Func<string?> action, bool shell = false, bool keepGallery = false, bool reload = true,
        PreviewCache.RemovalBatch? cleanup = null)
    {
        if (operationRunning || viewModel.IsLoading)
            return;

        string? notice;
        operationRunning = true;
        IsEnabled = false;
        try
        {
            try { notice = shell ? await RecycleBin.RunOnSta(action) : await Task.Run(action); }
            catch (Exception error) when (IsOperationError(error)) { notice = error.Message; }

            if (cleanup is not null && await Task.Run(cleanup.Complete) is { } warning)
                notice = notice is null ? warning : notice + Environment.NewLine + Environment.NewLine + warning;
        }
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
