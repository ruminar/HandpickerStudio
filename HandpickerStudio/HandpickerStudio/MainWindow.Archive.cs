using System.IO;
using System.Windows;

namespace HandpickerStudio;

public partial class MainWindow
{
    private async void ArchiveSelectedCheckpoints(object sender, RoutedEventArgs e) => await RunSelectedArchiveAsync(restore: false);

    private async void RestoreSelectedCheckpoints(object sender, RoutedEventArgs e) => await RunSelectedArchiveAsync(restore: true);

    private Task RunSelectedArchiveAsync(bool restore)
    {
        if (viewModel.SelectedProject is not { } project)
            return Task.CompletedTask;
        var selection = SelectedCheckpoints().Select(item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
        return selection.Count == 0 ? Task.CompletedTask : RunArchiveAsync(project, selection, restore);
    }

    private async Task RunArchiveAsync(Project project, HashSet<string>? selection, bool restore, bool automatic = false)
    {
        if (operationRunning || viewModel.IsLoading || !viewModel.CanSave)
            return;
        try
        {
            if (!ConfirmArchive(this, viewModel, project, restore))
                return;
        }
        catch (IOException error)
        {
            ShowReport("Archive設定を保存できません", error.Message);
            return;
        }
        var cleanup = BeginCacheCleanup();
        await RunOperation(restore ? "アーカイブ化解除" : "アーカイブ化",
            CreateArchiveOperation(this, viewModel, project, selection, restore, cleanup, quiet: automatic),
            keepGallery: true, cleanup: cleanup);
    }

    internal static bool ConfirmArchive(Window owner, MainViewModel viewModel, Project project, bool restore)
    {
        var preference = viewModel.GetArchivePreference(project.Id);
        if (restore && preference.Automatic)
        {
            preference = preference with { Automatic = false };
            SaveArchivePreference(viewModel, preference);
        }
        if (preference.Confirmed)
            return true;

        string action = restore
            ? "Archive化した画像のうち、Archiveに残っている画像を元のOutputへ戻します。"
            : "Outputにある前日までの画像を、CheckpointごとのArchiveフォルダーへ移動します。";
        if (MessageBox.Show(owner, $"「{project.Name}」で{action}\nこのプロジェクトでは、次回からこの確認を省略します。",
            restore ? "Archiveを戻す" : "Archive実行", MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return false;

        SaveArchivePreference(viewModel, preference with { Confirmed = true });
        return true;
    }

    private static void SaveArchivePreference(MainViewModel viewModel, ArchivePreference preference)
    {
        if (!viewModel.SaveArchivePreference(preference))
            throw new IOException("Archive設定を保存できません。メイン画面の確認事項を確認してください。");
    }

    internal static Func<string?> CreateArchiveOperation(Window owner, MainViewModel viewModel, Project project,
        HashSet<string>? selection, bool restore, PreviewCache.RemovalBatch? cleanup, bool quiet = false)
    {
        var projects = viewModel.Projects;
        var holds = viewModel.HeldLabels;
        var confirmCopy = ConfirmCopyOnce(owner);
        string history = WorkspacePaths.ArchiveHistoryPath;
        return () => ArchiveHistory.Run(history, projects, project, holds, selection, restore, confirmCopy, cleanup, quiet);
    }
}
