using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandpickerStudio;

public partial class MainWindow
{
    private const double CheckpointRowHeight = 24;
    private CheckpointGroup[] displayedGroups = [];
    private Guid? displayedProject;
    private bool synchronizingCheckpointSelection;
    private string? checkpointRangeAnchor;
    private CheckpointPane? highlightedDropPane;

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Appearance):
                if (Application.Current is App app)
                    app.ApplyAppearance(viewModel.Appearance);
                break;
            case nameof(MainViewModel.Groups):
            case nameof(MainViewModel.IsLoading):
                RefreshCheckpointPanes();
                break;
            case nameof(MainViewModel.SelectedCheckpoint):
                SynchronizeCheckpointSelection();
                break;
        }
    }

    private void RefreshCheckpointPanes()
    {
        Guid? project = viewModel.SelectedProject?.Id;
        if (displayedProject != project)
        {
            CheckpointPanes.Children.Clear();
            displayedGroups = [];
            displayedProject = project;
            checkpointRangeAnchor = null;
        }

        var groups = viewModel.Groups;
        // Keep the existing controls and their scroll positions while the same project is rescanned.
        if (ReferenceEquals(groups, displayedGroups) || (viewModel.IsLoading && groups.Length == 0))
            return;

        var selectedPaths = SelectedCheckpoints().Select(item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
        if (viewModel.SelectedCheckpoint is { } current)
            selectedPaths.Add(current.RelativePath);
        displayedGroups = groups;
        synchronizingCheckpointSelection = true;
        try
        {
            var panes = CheckpointPanes.Children.OfType<CheckpointPane>().ToDictionary(pane => pane.Rating);
            foreach (var pane in panes.Values.Where(pane => !groups.Any(group => group.Rating == pane.Rating)))
                CheckpointPanes.Children.Remove(pane);

            for (int index = 0; index < groups.Length; index++)
            {
                var group = groups[index];
                if (panes.TryGetValue(group.Rating, out var pane))
                {
                    pane.UpdateGroup(group);
                    continue;
                }

                pane = new CheckpointPane(group);
                pane.HeaderButton.Checked += (_, _) => ArrangeCheckpointPanes();
                pane.HeaderButton.Unchecked += (_, _) => ArrangeCheckpointPanes();
                pane.CheckpointList.SelectionChanged += SelectCheckpoint;
                pane.CheckpointList.PreviewKeyDown += NavigateCheckpointPanes;
                pane.ResizeGrip.DragDelta += (_, args) => ResizeCheckpointPanes(pane, args.VerticalChange);
                CheckpointPanes.Children.Insert(index, pane);
            }

            foreach (var list in CheckpointLists())
            {
                list.SelectedItems.Clear();
                foreach (var checkpoint in list.Items.OfType<Checkpoint>().Where(item => selectedPaths.Contains(item.RelativePath)))
                    list.SelectedItems.Add(checkpoint);
            }
        }
        finally { synchronizingCheckpointSelection = false; }
        UpdateActiveCheckpoint(viewModel.SelectedCheckpoint);
        ArrangeCheckpointPanes();
    }

    private void SelectCheckpoint(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizingCheckpointSelection || viewModel.Groups.Length == 0 || sender is not ListBox list)
            return;

        var recent = e.AddedItems.OfType<Checkpoint>().LastOrDefault();
        if (recent is not null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            synchronizingCheckpointSelection = true;
            try
            {
                foreach (var other in CheckpointLists().Where(other => other != list))
                    other.SelectedItems.Clear();
            }
            finally { synchronizingCheckpointSelection = false; }
        }

        if (recent is not null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            checkpointRangeAnchor = recent.RelativePath;
        UpdateActiveCheckpoint(recent);
    }

    private IEnumerable<ListBox> CheckpointLists() => CheckpointPanes.Children.OfType<CheckpointPane>().Select(pane => pane.CheckpointList);

    private Checkpoint[] SelectedCheckpoints() => CheckpointLists().SelectMany(list => list.SelectedItems.OfType<Checkpoint>()).ToArray();

    private void SelectCheckpointItems(ListBox list, Checkpoint checkpoint, ModifierKeys modifiers)
    {
        int checkpointIndex = list.Items.IndexOf(checkpoint);
        if (checkpointIndex < 0)
            return;

        bool extend = modifiers.HasFlag(ModifierKeys.Control);
        int anchor = -1;
        if (modifiers.HasFlag(ModifierKeys.Shift) && checkpointRangeAnchor is not null)
        {
            for (int index = 0; index < list.Items.Count; index++)
            {
                if (list.Items[index] is Checkpoint item && item.RelativePath == checkpointRangeAnchor)
                {
                    anchor = index;
                    break;
                }
            }
        }

        synchronizingCheckpointSelection = true;
        try
        {
            if (!extend)
            {
                foreach (var other in CheckpointLists())
                    other.SelectedItems.Clear();
            }

            if (anchor >= 0)
            {
                for (int itemIndex = Math.Min(checkpointIndex, anchor); itemIndex <= Math.Max(checkpointIndex, anchor); itemIndex++)
                {
                    var item = list.Items[itemIndex];
                    if (!list.SelectedItems.Contains(item))
                        list.SelectedItems.Add(item);
                }
            }
            else
            {
                checkpointRangeAnchor = checkpoint.RelativePath;
                if (extend && list.SelectedItems.Contains(checkpoint))
                    list.SelectedItems.Remove(checkpoint);
                else
                    list.SelectedItems.Add(checkpoint);
            }
        }
        finally { synchronizingCheckpointSelection = false; }
        UpdateActiveCheckpoint(checkpoint);
    }

    private void UpdateActiveCheckpoint(Checkpoint? preferred)
    {
        var selection = SelectedCheckpoints();
        var active = selection.FirstOrDefault(item => item.Equals(preferred)) ??
            selection.FirstOrDefault(item => item.Equals(viewModel.SelectedCheckpoint)) ?? selection.LastOrDefault();
        if (Equals(active, viewModel.SelectedCheckpoint))
            return;
        if (active is null)
            viewModel.ClearCheckpointSelection();
        else
            viewModel.SelectCheckpoint(active);
    }

    private void NavigateCheckpointPanes(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || Keyboard.Modifiers != ModifierKeys.None || sender is not ListBox list ||
            viewModel.Groups.Length == 0)
            return;

        int currentIndex = e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(list, source) is ListBoxItem focused
            ? list.ItemContainerGenerator.IndexFromContainer(focused) : list.SelectedIndex;
        bool down = e.Key == Key.Down;
        if (currentIndex < 0 || currentIndex != (down ? list.Items.Count - 1 : 0))
            return;

        var panes = CheckpointPanes.Children.OfType<CheckpointPane>().ToArray();
        int direction = down ? 1 : -1;
        int currentPane = Array.FindIndex(panes, pane => pane.CheckpointList == list);
        for (int index = currentPane + direction; index >= 0 && index < panes.Length; index += direction)
        {
            var pane = panes[index];
            var target = pane.CheckpointList;
            if (!pane.IsExpanded || !target.HasItems || target.Height == 0)
                continue;

            int targetIndex = down ? 0 : target.Items.Count - 1;
            var checkpoint = (Checkpoint)target.Items[targetIndex];
            SelectCheckpointItems(target, checkpoint, ModifierKeys.None);
            target.ScrollIntoView(checkpoint);
            target.UpdateLayout();
            if (target.ItemContainerGenerator.ContainerFromIndex(targetIndex) is ListBoxItem container)
                container.Focus();
            else
                target.Focus();
            e.Handled = true;
            return;
        }
    }

    private void SynchronizeCheckpointSelection()
    {
        synchronizingCheckpointSelection = true;
        try
        {
            var selected = viewModel.SelectedCheckpoint;
            foreach (var list in CheckpointLists())
            {
                if (selected is null)
                    list.SelectedItems.Clear();
                else if (list.Items.Contains(selected) && !list.SelectedItems.Contains(selected))
                    list.SelectedItems.Add(selected);
            }
        }
        finally { synchronizingCheckpointSelection = false; }
    }

    private void CheckpointPanesSizeChanged(object sender, SizeChangedEventArgs e) => ArrangeCheckpointPanes();

    private void ArrangeCheckpointPanes()
    {
        var panes = CheckpointPanes.Children.OfType<CheckpointPane>().ToArray();
        var expanded = panes.Where(pane => pane.IsExpanded).ToArray();
        var last = expanded.LastOrDefault();
        foreach (var pane in panes)
        {
            pane.CheckpointList.Visibility = pane.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            pane.ResizeGrip.Visibility = pane.IsExpanded && pane != last ? Visibility.Visible : Visibility.Collapsed;
        }
        if (last is null)
            return;

        double available = Math.Max(0, CheckpointPanes.ActualHeight - panes.Sum(pane => pane.HeaderButton.Height) -
            (expanded.Length - 1) * last.ResizeGrip.Height);
        double minimum = Math.Min(CheckpointRowHeight, available / expanded.Length);
        var flexible = expanded.FirstOrDefault(pane => pane.Rating == Rating.None && pane.CheckpointList.HasItems) ??
            expanded.LastOrDefault(pane => pane.CheckpointList.HasItems) ?? last;
        var heights = expanded.Select(pane => Math.Max(minimum, pane.PreferredHeight ??
            (pane == flexible ? minimum : Math.Clamp(pane.CheckpointList.Items.Count, 1, 3) * CheckpointRowHeight))).ToArray();
        double total = heights.Sum();
        double shrink = total > available ? (available - minimum * expanded.Length) / (total - minimum * expanded.Length) : 1;
        for (int i = 0; i < expanded.Length; i++)
        {
            double extra = expanded[i] == flexible ? Math.Max(0, available - total) : 0;
            expanded[i].CheckpointList.Height = Math.Max(0, minimum + (heights[i] - minimum) * shrink + extra);
        }
    }

    private void ResizeCheckpointPanes(CheckpointPane pane, double change)
    {
        var expanded = CheckpointPanes.Children.OfType<CheckpointPane>().Where(item => item.IsExpanded).ToArray();
        int index = Array.IndexOf(expanded, pane);
        if (index < 0 || index == expanded.Length - 1)
            return;

        var next = expanded[index + 1];
        double height = pane.CheckpointList.Height;
        double delta = change < 0
            ? Math.Max(change, -Math.Max(0, height - CheckpointRowHeight))
            : Math.Min(change, expanded.Skip(index + 1).Sum(item => Math.Max(0, item.CheckpointList.Height - CheckpointRowHeight)));
        if (delta == 0)
            return;

        foreach (var item in expanded)
            item.PreferredHeight = item.CheckpointList.Height;
        pane.PreferredHeight = height + delta;
        if (delta < 0)
            next.PreferredHeight = next.CheckpointList.Height - delta;
        else
        {
            // Compact panes at their minimum keep their row when the separator is dragged down.
            for (int i = index + 1; i < expanded.Length && delta > 0; i++)
            {
                var donor = expanded[i];
                double taken = Math.Min(delta, Math.Max(0, donor.CheckpointList.Height - CheckpointRowHeight));
                donor.PreferredHeight = donor.CheckpointList.Height - taken;
                delta -= taken;
            }
        }
        ArrangeCheckpointPanes();
    }

    private static CheckpointPane? FindCheckpointPane(object source)
    {
        for (var element = source as DependencyObject; element is not null;)
        {
            if (element is CheckpointPane pane)
                return pane;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private static (ListBox List, ListBoxItem Row, Checkpoint Checkpoint)? FindCheckpointRow(object source)
    {
        if (source is DependencyObject element && FindCheckpointPane(source) is { CheckpointList: var list } &&
            ItemsControl.ContainerFromElement(list, element) is ListBoxItem { DataContext: Checkpoint checkpoint } row)
            return (list, row, checkpoint);
        return null;
    }

    private void HighlightDropPane(CheckpointPane? pane)
    {
        if (highlightedDropPane == pane)
            return;
        highlightedDropPane?.ShowDropTarget(false);
        highlightedDropPane = pane;
        highlightedDropPane?.ShowDropTarget(true);
    }
}
