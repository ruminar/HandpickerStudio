using System.Windows;
using Microsoft.Win32;

namespace HandpickerStudio;

public partial class ProjectEditorWindow
{
    private const string RatingFileFilter = "Checkpoint評価 (*.json)|*.json";

    private async void ExportRatings(object sender, RoutedEventArgs e)
    {
        if (projectOperationRunning || !SaveButton.IsEnabled || originalProject is not { EvaluationPath.Length: > 0 } project)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "評価をエクスポート",
            Filter = RatingFileFilter,
            FileName = RatingTransfer.SuggestedName(project, DateTime.Now),
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string path = dialog.FileName;
        bool includeDeleted = IncludeDeletedHistory.IsChecked == true;
        await RunProjectOperationAsync(RatingTransferReport, "エクスポート中…",
            () => Task.Run(() => RatingTransfer.Export(project, path, includeDeleted)));
    }

    private async void ImportRatings(object sender, RoutedEventArgs e)
    {
        if (projectOperationRunning || !SaveButton.IsEnabled || originalProject is not { EvaluationPath.Length: > 0 } project)
            return;

        var dialog = new OpenFileDialog { Title = "評価をインポート", Filter = RatingFileFilter };
        if (dialog.ShowDialog(this) != true)
            return;

        string path = dialog.FileName;
        await RunProjectOperationAsync(RatingTransferReport, "インポート内容を確認中…", async () =>
        {
            var plan = await Task.Run(() => RatingTransfer.Preview(project, path));
            if (plan.Overwrites > 0 && MessageBox.Show(this,
                $"「{project.Name}」の共有評価DBで既存評価を {plan.Overwrites} 件上書きします。評価の追加は {plan.Changes.Length - plan.Overwrites} 件です。続けますか？",
                dialog.Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return "インポートをキャンセルしました。";

            RatingTransferReport.Text = "インポート中…";
            string result = await Task.Run(() => RatingTransfer.Import(project, plan));
            await viewModel.ReloadAsync(keepGallery: true);
            return result;
        });
    }
}
