using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HandpickerStudio;

public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    private const double MinimumTileWidth = 174;
    private const double TileHeight = 154;
    private int columns = 1;
    private double tileWidth;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => ViewportWidth;
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }
    public ScrollViewer? ScrollOwner { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null)
            return default;

        int count = owner.Items.Count;
        double width = double.IsPositiveInfinity(availableSize.Width) ? MinimumTileWidth : availableSize.Width;
        columns = Math.Max(1, (int)(width / MinimumTileWidth));
        tileWidth = width / columns;
        double extent = Math.Ceiling((double)count / columns) * TileHeight;
        double height = double.IsPositiveInfinity(availableSize.Height) ? extent : availableSize.Height;
        double offset = Math.Clamp(VerticalOffset, 0, Math.Max(0, extent - height));
        if (ViewportWidth != width || ViewportHeight != height || ExtentHeight != extent || VerticalOffset != offset)
        {
            ViewportWidth = width;
            ViewportHeight = height;
            ExtentHeight = extent;
            VerticalOffset = offset;
            ScrollOwner?.InvalidateScrollInfo();
        }

        // Realize the viewport and one extra row on either side for smooth scrolling.
        int first = Math.Min(count, Math.Max(0, (int)(offset / TileHeight) - 1) * columns);
        int end = Math.Min(count, ((int)Math.Ceiling((offset + height) / TileHeight) + 1) * columns);
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (int child = children.Count - 1; child >= 0; child--)
        {
            var position = new GeneratorPosition(child, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if ((index >= first && index < end) || children[child].IsKeyboardFocusWithin)
                continue;

            if (generator is IRecyclingItemContainerGenerator recycling)
                recycling.Recycle(position, 1);
            else
                generator.Remove(position, 1);
            RemoveInternalChildRange(child, 1);
        }

        if (first < end)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int child = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (int index = first; index < end; index++, child++)
                {
                    var element = (UIElement)generator.GenerateNext(out _);
                    if (!children.Contains(element))
                    {
                        if (child == children.Count)
                            AddInternalChild(element);
                        else
                            InsertInternalChild(child, element);
                        generator.PrepareItemContainer(element);
                    }
                    element.Measure(new Size(tileWidth, TileHeight));
                }
            }
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int child = 0; child < InternalChildren.Count; child++)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(child, 0));
            if (index >= 0)
                InternalChildren[child].Arrange(GetTileRect(index));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
        {
            int index = args.Action == NotifyCollectionChangedAction.Move ? args.OldPosition.Index : args.Position.Index;
            if (args.ItemUICount > 0 && index >= 0)
                RemoveInternalChildRange(index, args.ItemUICount);
        }
    }

    protected override void BringIndexIntoView(int index)
    {
        if (ScrollToTile(index))
            UpdateLayout();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual;
        while (child is not null && child != this && VisualTreeHelper.GetParent(child) != this)
            child = VisualTreeHelper.GetParent(child);

        if (child is not UIElement element || child == this)
            return Rect.Empty;
        int position = InternalChildren.IndexOf(element);
        if (position < 0)
            return Rect.Empty;
        int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(position, 0));
        ScrollToTile(index);
        return Rect.Intersect(GetTileRect(index), new Rect(0, 0, ViewportWidth, ViewportHeight));
    }

    private Rect GetTileRect(int index) =>
        new(index % columns * tileWidth, index / columns * TileHeight - VerticalOffset, tileWidth, TileHeight);

    private bool ScrollToTile(int index)
    {
        if (index < 0)
            return false;
        double originalOffset = VerticalOffset;
        double top = index / columns * TileHeight;
        if (top < VerticalOffset)
            SetVerticalOffset(top);
        else if (top + TileHeight > VerticalOffset + ViewportHeight)
            SetVerticalOffset(Math.Min(top, top + TileHeight - ViewportHeight));
        return originalOffset != VerticalOffset;
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (double.IsNaN(offset) || offset == VerticalOffset)
            return;
        VerticalOffset = offset;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 24);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 24);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 72);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 72);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
