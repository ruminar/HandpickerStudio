using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace HandpickerStudio;

public partial class CheckpointPane : UserControl
{
    private Checkpoint[] items = [];
    private CancellationTokenSource? previewRequest;
    private ToolTip? previewToolTip;
    private FrameworkElement? previewTarget;
    private Image? previewImage;

    internal CheckpointPane(CheckpointGroup group)
    {
        InitializeComponent();
        Rating = group.Rating;
        HeaderButton.IsChecked = group.Items.Length > 0;
        UpdateGroup(group);
    }

    internal Rating Rating { get; }
    internal bool IsExpanded => HeaderButton.IsChecked == true;
    internal double? PreferredHeight { get; set; }

    internal void UpdateGroup(CheckpointGroup group)
    {
        RatingText.Text = group.Name;
        CountText.Text = group.Items.Length.ToString();
        AutomationProperties.SetName(HeaderButton, $"{group.Name} ({group.Items.Length})");
        AutomationProperties.SetName(CheckpointList, group.Name);
        if (!items.SequenceEqual(group.Items))
        {
            items = group.Items;
            CheckpointList.ItemsSource = items;
        }
    }

    internal void ShowDropTarget(bool target) => DropFrame.Visibility = target ? Visibility.Visible : Visibility.Collapsed;

    private async void OpenCheckpointPreview(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip
            {
                PlacementTarget: FrameworkElement { DataContext: Checkpoint checkpoint } target,
                Content: StackPanel content
            } tooltip || content.Children[0] is not Image image)
            return;

        StopCheckpointPreview();
        using var request = new CancellationTokenSource();
        previewRequest = request;
        previewToolTip = tooltip;
        previewTarget = target;
        previewImage = image;
        target.DataContextChanged += CloseRecycledPreview;
        try
        {
            var bitmap = await Task.Run(() => ImageOperations.ReadThumbnail(checkpoint, request.Token), request.Token);
            if (!request.IsCancellationRequested && tooltip.IsOpen)
                image.Source = bitmap;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(previewRequest, request))
                previewRequest = null;
        }
    }

    private void CloseRecycledPreview(object sender, DependencyPropertyChangedEventArgs e)
    {
        var tooltip = previewToolTip;
        StopCheckpointPreview();
        if (tooltip is not null)
            tooltip.IsOpen = false;
    }

    private void CloseCheckpointPreview(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, previewToolTip))
            StopCheckpointPreview();
    }

    private void StopCheckpointPreview()
    {
        previewRequest?.Cancel();
        previewRequest = null;
        if (previewTarget is { } target)
            target.DataContextChanged -= CloseRecycledPreview;
        if (previewImage is { } image)
            image.Source = null;
        previewTarget = null;
        previewToolTip = null;
        previewImage = null;
    }
}
