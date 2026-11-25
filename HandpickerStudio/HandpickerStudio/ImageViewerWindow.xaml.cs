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
    private int currentIndex;
    private int wheelRemainder;
    private bool operationRunning;

    internal ImageViewerWindow(ImageEntry[] images, int startIndex, Project project, Checkpoint? checkpoint)
    {
        InitializeComponent();
        this.images = [.. images];
        this.project = project;
        this.checkpoint = checkpoint;
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
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < images.Count - 1;
    }

    private void Previous(object sender, RoutedEventArgs e) => ShowAt(currentIndex - 1);

    private void Next(object sender, RoutedEventArgs e) => ShowAt(currentIndex + 1);

    private void NavigateWithKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            RecycleCurrent(sender, e);
            return;
        }
        if (e.Key is not (Key.Left or Key.Right))
            return;
        e.Handled = true;
        ShowAt(currentIndex + (e.Key == Key.Left ? -1 : 1));
    }

    private void NavigateWithWheel(object sender, MouseWheelEventArgs e)
    {
        const int WheelDelta = 120;
        wheelRemainder += e.Delta;
        int steps = wheelRemainder / WheelDelta;
        wheelRemainder %= WheelDelta;
        int index = Math.Clamp(currentIndex - steps, 0, images.Count - 1);
        if (index != currentIndex)
            ShowAt(index);
        e.Handled = true;
    }

    private void PrepareMenu(object sender, ContextMenuEventArgs e)
    {
        ViewerRecycle.IsEnabled = !operationRunning && images[currentIndex].CheckpointPath is not null;
        ViewerThumbnail.IsEnabled = ViewerRecycle.IsEnabled && checkpoint is not null;
    }

    private void ConfirmClose(object? sender, CancelEventArgs e) => e.Cancel = operationRunning;

    private async void RecycleCurrent(object sender, RoutedEventArgs e)
    {
        if (operationRunning || images.Count == 0 || images[currentIndex].CheckpointPath is null)
            return;

        ImageEntry image = images[currentIndex];
        bool recycled = await RunAsync(async () => await RecycleBin.RunOnSta(() =>
        {
            ImageOperations.Recycle(ImageOperations.Capture(project, image));
            return true;
        }));
        if (!recycled)
            return;

        ImagesChanged = true;
        images.RemoveAt(currentIndex);
        if (images.Count == 0)
            Close();
        else
            ShowAt(Math.Min(currentIndex, images.Count - 1));
    }

    private async void SetCurrentThumbnail(object sender, RoutedEventArgs e)
    {
        if (operationRunning || checkpoint is null)
            return;
        ImageEntry image = images[currentIndex];
        await RunAsync(async () =>
        {
            string path = await Task.Run(() => ImageOperations.SetThumbnail(ImageOperations.Capture(project, image), checkpoint));
            MessageBox.Show(this, $"保存しました。{Environment.NewLine}{path}", "代表サムネイル");
            return true;
        });
    }

    private async Task<bool> RunAsync(Func<Task<bool>> operation)
    {
        operationRunning = true;
        IsEnabled = false;
        try { return await operation(); }
        catch (Exception error) when (MainWindow.IsOperationError(error))
        {
            MessageBox.Show(this, error.Message, "画像の操作");
            return false;
        }
        finally
        {
            IsEnabled = true;
            operationRunning = false;
        }
    }
}
