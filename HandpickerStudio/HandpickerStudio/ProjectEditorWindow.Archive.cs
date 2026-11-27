using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HandpickerStudio;

public partial class ProjectEditorWindow
{
    private async void ArchiveProject(object sender, RoutedEventArgs e) => await RunProjectArchiveAsync(restore: false);

    private async void RestoreProjectArchive(object sender, RoutedEventArgs e) => await RunProjectArchiveAsync(restore: true);

    private async Task RunProjectArchiveAsync(bool restore)
    {
        if (projectOperationRunning || !SaveButton.IsEnabled || viewModel.IsLoading || originalProject is not { } project ||
            viewModel.SelectedProject?.Id != project.Id)
            return;

        await RunProjectOperationAsync(ArchiveReport, restore ? "Outputに戻しています…" : "Archive化しています…", async () =>
        {
            bool confirmed;
            try { confirmed = MainWindow.ConfirmArchive(this, viewModel, project, restore); }
            finally
            {
                if (restore)
                    AutoArchiveCheckBox.IsChecked = viewModel.GetArchivePreference(project.Id).Automatic;
            }
            if (!confirmed)
                return "キャンセルしました。";

            var cleanup = viewModel.GalleryCache is { } cache ? new PreviewCache.RemovalBatch(cache) : null;
            var action = MainWindow.CreateArchiveOperation(this, viewModel, project, selection: null, restore, cleanup);
            string? report = null;
            try { report = await Task.Run(action); }
            finally
            {
                if (cleanup is not null && await Task.Run(cleanup.Complete) is { } warning)
                    report += Environment.NewLine + warning;
                await viewModel.ReloadAsync(keepGallery: true);
            }
            return report ?? "";
        });
    }

    private async void OpenProjectArchive(object sender, RoutedEventArgs e)
    {
        if (projectOperationRunning || originalProject is not { } project || ArchiveRootPicker.SelectedItem is not ImageRoot root)
            return;
        string archive = ArchiveDirectory.GetPath(project.Id, root);
        await RunProjectOperationAsync(ArchiveReport, "Archiveを確認しています…", async () =>
        {
            try
            {
                await Task.Run(() =>
                {
                    ArchiveDirectory.RequireManagedDirectory(Path.GetDirectoryName(archive)!);
                    ArchiveDirectory.RequireManagedDirectory(archive);
                    if (!Directory.Exists(archive))
                        throw new DirectoryNotFoundException($"Archiveに接続できません。{archive}");
                });
                using var explorer = Process.Start(new ProcessStartInfo(archive) { UseShellExecute = true });
            }
            catch (Win32Exception error) { throw new IOException($"Archiveを開けません。{error.Message}", error); }
            return archive;
        });
    }
}
