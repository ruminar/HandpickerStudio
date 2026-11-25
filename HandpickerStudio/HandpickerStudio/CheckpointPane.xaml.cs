using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace HandpickerStudio;

public partial class CheckpointPane : UserControl
{
    private Checkpoint[] items = [];

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
}
