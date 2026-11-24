using System.Windows;
using System.Windows.Input;

namespace HandpickerStudio;

public partial class ImageViewerWindow : Window
{
    private readonly ImageEntry[] images;
    private int currentIndex;
    private int wheelRemainder;

    internal ImageViewerWindow(ImageEntry[] images, int startIndex)
    {
        InitializeComponent();
        this.images = images;
        ShowAt(startIndex);
    }

    private void ShowAt(int index)
    {
        if (index < 0 || index >= images.Length)
            return;

        currentIndex = index;
        var image = images[index];
        Title = $"{image.Name} · HandpickerStudio";
        ImageName.Text = $"{image.Name} ({index + 1}/{images.Length})";
        ImagePath.Text = image.FullPath;
        ImagePath.ToolTip = image.FullPath;
        Preview.FilePath = image.FullPath;
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < images.Length - 1;
    }

    private void Previous(object sender, RoutedEventArgs e) => ShowAt(currentIndex - 1);

    private void Next(object sender, RoutedEventArgs e) => ShowAt(currentIndex + 1);

    private void NavigateWithKey(object sender, KeyEventArgs e)
    {
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
        int index = Math.Clamp(currentIndex - steps, 0, images.Length - 1);
        if (index != currentIndex)
            ShowAt(index);
        e.Handled = true;
    }
}
