using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandpickerStudio;

public partial class MainWindow
{
    private int galleryWheelRemainder;
    private bool wheelResizesTiles;

    private sealed record GalleryPosition(GalleryLayout View, ImageEntry[] Images, double Offset, int Anchor, double Inset);

    private void ScrollGalleryWithWheel(object sender, MouseWheelEventArgs e)
    {
        bool tiled = viewModel.GalleryView != GalleryLayout.List;
        bool resize = tiled && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (wheelResizesTiles != resize)
        {
            galleryWheelRemainder = 0;
            wheelResizesTiles = resize;
        }

        const int WheelDelta = 120;
        galleryWheelRemainder += e.Delta;
        int steps = galleryWheelRemainder / WheelDelta;
        galleryWheelRemainder %= WheelDelta;
        e.Handled = true;
        if (steps == 0)
            return;

        if (resize)
            viewModel.ChangeTileSize(viewModel.TileSize + steps * 24);
        else if (tiled && FindGalleryVisual<VirtualizingTilePanel>(Gallery) is { } tiles)
            tiles.ScrollRows(-steps);
        else if (FindGalleryVisual<ScrollViewer>(Gallery) is { } scroll)
        {
            for (int index = 0; index < Math.Abs(steps); index++)
            {
                if (steps > 0)
                    scroll.LineUp();
                else
                    scroll.LineDown();
            }
        }
    }

    private async Task ReloadGalleryAfterRecycleAsync()
    {
        var context = GalleryContext();
        GalleryPosition? position = null;
        await viewModel.ReloadAsync(keepGallery: true, beforeGalleryUpdate: () =>
        {
            if (context == GalleryContext())
                position = CaptureGalleryPosition();
        });

        if (position is null || !IsLoaded || context != GalleryContext() || position.View != viewModel.GalleryView ||
            viewModel.Images.Length == 0)
            return;

        Gallery.UpdateLayout();
        if (FindGalleryVisual<ScrollViewer>(Gallery) is not { } scroll)
            return;
        double offset = position.Offset;
        if (position.Anchor >= 0 && FindSurvivingImage(position, viewModel.Images) is { } anchor)
        {
            offset = FindGalleryVisual<VirtualizingTilePanel>(scroll) is { } tiles
                ? tiles.RowOffset(anchor) + position.Inset
                : anchor + position.Inset;
        }
        scroll.ScrollToVerticalOffset(Math.Min(offset, scroll.ScrollableHeight));
    }

    private (Guid? Project, string Title, string Checkpoint) GalleryContext() =>
        (viewModel.SelectedProject?.Id, viewModel.GalleryTitle, viewModel.GalleryContext);

    private GalleryPosition? CaptureGalleryPosition()
    {
        if (viewModel.Images.Length == 0 || FindGalleryVisual<ScrollViewer>(Gallery) is not { VerticalOffset: > 0 } scroll)
            return null;

        double offset = scroll.VerticalOffset;
        var tiles = FindGalleryVisual<VirtualizingTilePanel>(scroll);
        int anchor = tiles?.FirstVisibleIndex ??
            (VirtualizingPanel.GetScrollUnit(Gallery) == ScrollUnit.Item ? (int)offset : -1);
        double inset = anchor < 0 ? 0 : offset - (tiles?.RowOffset(anchor) ?? anchor);
        return new(viewModel.GalleryView, viewModel.Images, offset, anchor, inset);
    }

    private static int? FindSurvivingImage(GalleryPosition position, ImageEntry[] remaining)
    {
        var indices = new Dictionary<string, int>(remaining.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < remaining.Length; index++)
            indices.TryAdd(remaining[index].FullPath, index);

        int first = Math.Min(position.Anchor, position.Images.Length - 1);
        for (int index = first; index < position.Images.Length; index++)
            if (indices.TryGetValue(position.Images[index].FullPath, out int match))
                return match;
        for (int index = first - 1; index >= 0; index--)
            if (indices.TryGetValue(position.Images[index].FullPath, out int match))
                return match;
        return null;
    }

    private static T? FindGalleryVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T target)
                return target;
            if (FindGalleryVisual<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }
}
