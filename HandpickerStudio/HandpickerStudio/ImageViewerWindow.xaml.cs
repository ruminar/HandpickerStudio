using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandpickerStudio;

public partial class ImageViewerWindow : Window
{
    private readonly List<ImageEntry> images;
    private readonly Project project;
    private readonly Checkpoint? checkpoint;
    private readonly PreviewCache? galleryCache;
    private int currentIndex;
    private int wheelRemainder;
    private bool operationRunning;

    internal ImageViewerWindow(ImageEntry[] images, int startIndex, Project project, Checkpoint? checkpoint, PreviewCache? galleryCache)
    {
        InitializeComponent();
        this.images = [.. images];
        this.project = project;
        this.checkpoint = checkpoint;
        this.galleryCache = galleryCache;
        Preview.PreviewLoaded += (_, _) =>
        {
            if (previousPlacement.HasValue && WindowState == WindowState.Normal)
                FitExpandedView();
        };
        ShowAt(startIndex);
    }

    internal bool ImagesChanged { get; private set; }

    private void ShowAt(int index)
    {
        if (index < 0 || index >= images.Count)
            return;

        currentIndex = index;
        var image = images[index];
        Title = $"{image.Name} · HandpickerStudio";
        ImageName.Text = $"{image.Name} ({index + 1}/{images.Count})";
        ImagePath.Text = image.FullPath;
        ImagePath.ToolTip = image.FullPath;
        Preview.FilePath = image.FullPath;
        UpdateNavigationButtons();
    }

    private void UpdateNavigationButtons()
    {
        PreviousButton.IsEnabled = !operationRunning && currentIndex > 0;
        NextButton.IsEnabled = !operationRunning && currentIndex < images.Count - 1;
    }

    private void MoveTo(int index)
    {
        if (!operationRunning)
            ShowAt(index);
    }

    private void Previous(object sender, RoutedEventArgs e) => MoveTo(currentIndex - 1);

    private void Next(object sender, RoutedEventArgs e) => MoveTo(currentIndex + 1);

    private void NavigateWithKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (!e.IsRepeat)
                RecycleCurrent(sender, e);
            return;
        }
        if (e.Key is not (Key.Left or Key.Right))
            return;
        e.Handled = true;
        MoveTo(currentIndex + (e.Key == Key.Left ? -1 : 1));
    }

    private void NavigateWithWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (operationRunning || images.Count == 0)
            return;

        const int WheelDelta = 120;
        wheelRemainder += e.Delta;
        int steps = wheelRemainder / WheelDelta;
        wheelRemainder %= WheelDelta;
        int index = Math.Clamp(currentIndex - steps, 0, images.Count - 1);
        if (index != currentIndex)
            ShowAt(index);
    }

    private void PrepareMenu(object sender, ContextMenuEventArgs e)
    {
        ViewerRecycle.IsEnabled = !operationRunning && images.Count > 0;
        ViewerThumbnail.IsEnabled = ViewerRecycle.IsEnabled && checkpoint is not null && images[currentIndex].CheckpointPath is not null;
    }

    private void ConfirmClose(object? sender, CancelEventArgs e) => e.Cancel = operationRunning;

    private async void RecycleCurrent(object sender, RoutedEventArgs e)
    {
        if (operationRunning || images.Count == 0)
            return;

        int removing = currentIndex;
        ImageEntry image = images[removing];
        if (!MainWindow.ConfirmImageRecycle(this, [image]))
            return;
        await RunAsync(async () =>
        {
            string? warning = await RecycleBin.RunOnSta(() =>
            {
                var target = ImageOperations.Capture(project, image);
                if (galleryCache is null)
                {
                    ImageOperations.Recycle(target);
                    return null;
                }
                var cleanup = new PreviewCache.RemovalBatch(galleryCache);
                cleanup.AfterRemoving(image.FullPath, () => ImageOperations.Recycle(target));
                return cleanup.Complete();
            });

            ImagesChanged = true;
            images.RemoveAt(removing);
            if (images.Count > 0)
                ShowAt(Math.Min(removing, images.Count - 1));
            if (warning is not null)
                MessageBox.Show(this, warning, "画像キャッシュ");
        });
        if (images.Count == 0)
            Close();
    }

    private async void SetCurrentThumbnail(object sender, RoutedEventArgs e)
    {
        if (operationRunning || checkpoint is null || images.Count == 0 || images[currentIndex].CheckpointPath is null)
            return;
        ImageEntry image = images[currentIndex];
        await RunAsync(async () =>
        {
            string path = await Task.Run(() => ImageOperations.SetThumbnail(ImageOperations.Capture(project, image), checkpoint));
            MessageBox.Show(this, $"保存しました。{Environment.NewLine}{path}", "代表サムネイル");
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        operationRunning = true;
        Preview.Focus();
        UpdateNavigationButtons();
        try { await operation(); }
        catch (Exception error) when (MainWindow.IsOperationError(error))
        {
            MessageBox.Show(this, error.Message, "画像の操作");
        }
        finally
        {
            operationRunning = false;
            UpdateNavigationButtons();
            if (IsActive)
                Preview.Focus();
        }
    }
}
