using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace HandpickerStudio;

public partial class MainWindow
{
    private const string RatingFileFilter = "Checkpoint評価 (*.json)|*.json";

    private void OpenRatingTransferMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void ExportRatings(object sender, RoutedEventArgs e)
    {
        if (operationRunning || viewModel.IsLoading || viewModel.SelectedProject is not { } project)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "評価をエクスポート",
            Filter = RatingFileFilter,
            FileName = RatingTransfer.SuggestedName(project, DateTime.Now),
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) == true)
            await RunOperation(dialog.Title, () => RatingTransfer.Export(project, dialog.FileName), reload: false);
    }

    private async void ImportRatings(object sender, RoutedEventArgs e)
    {
        if (operationRunning || viewModel.IsLoading || viewModel.SelectedProject is not { } project)
            return;
        var dialog = new OpenFileDialog { Title = "評価をインポート", Filter = RatingFileFilter };
        if (dialog.ShowDialog(this) != true)
            return;

        RatingImportPlan plan;
        operationRunning = true;
        IsEnabled = false;
        try
        {
            plan = await Task.Run(() => RatingTransfer.Preview(project, dialog.FileName));
        }
        catch (Exception error) when (IsOperationError(error))
        {
            IsEnabled = true;
            ShowReport(dialog.Title, error.Message);
            return;
        }
        finally
        {
            operationRunning = false;
            IsEnabled = true;
        }

        if (plan.Overwrites > 0 && MessageBox.Show(this,
            $"「{project.Name}」の既存評価を {plan.Overwrites} 件上書きします。評価の追加は {plan.Changes.Length - plan.Overwrites} 件です。続けますか？",
            dialog.Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        await RunOperation(dialog.Title, () => RatingTransfer.Import(project, plan), keepGallery: true);
    }
}
