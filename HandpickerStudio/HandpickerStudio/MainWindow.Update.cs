using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HandpickerStudio.Update;

namespace HandpickerStudio;

public partial class MainWindow
{
    private readonly CancellationTokenSource windowLifetime = new();
    private bool startupRunning;
    private bool displaySettingsRunning;
    private bool checkingUpdate;

    private async void CheckForAppUpdate(object sender, RoutedEventArgs e) => await CheckForAppUpdateAsync(automatic: false);

    private async Task CheckForAppUpdateAsync(bool automatic)
    {
        if (checkingUpdate || App.IsProbe)
            return;
        if (App.Installation is not { } installation || App.ReleaseProfile is not { } profile)
        {
            if (!automatic)
                ShowReport("アプリの更新",
                    $"現在の版 {App.Version}{Environment.NewLine}署名付きのポータブル版で更新を利用できます。");
            return;
        }
        if (!viewModel.CanSave)
        {
            if (!automatic)
                ShowReport("アプリの更新", "設定を読み込める状態にしてから、更新を確認してください。");
            return;
        }

        checkingUpdate = true;
        AppUpdateButton.IsEnabled = false;
        bool approved = false;
        var token = windowLifetime.Token;
        try
        {
            using var updates = new GitHubUpdates(profile);
            var candidate = await updates.CheckAsync(Version.Parse(App.Version), token);
            if (candidate is null)
            {
                if (!automatic)
                {
                    await WaitForIdleAsync(token);
                    ShowReport("アプリの更新", $"現在の版 {App.Version} は最新版です。");
                }
                return;
            }

            await WaitForIdleAsync(token);
            approved = MessageBox.Show(this,
                $"HandpickerStudio {App.Version} を {candidate.Version} に更新しますか？\n\n{candidate.Notes}",
                "アプリの更新", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
            if (!approved)
                return;

            AppUpdateButton.Content = "更新を準備中…";
            await ValidateUpdateLocationAsync(installation, token);
            await Task.Run(() => updates.StageAsync(candidate, installation, token), token);
            AppUpdateButton.Content = "操作の完了を待機中…";
            await WaitForIdleAsync(token);
            IsEnabled = false;
            try { await ValidateUpdateLocationAsync(installation, token); }
            finally { IsEnabled = true; }
            token.ThrowIfCancellationRequested();
            App.ExitForUpdate = true;
            Close();
            if (!windowLifetime.IsCancellationRequested)
            {
                App.ExitForUpdate = false;
                ShowReport("アプリの更新", "設定を保存して終了できませんでした。設定とアクセス権を確認してください。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsOperationError(error) || error is HttpRequestException or OperationCanceledException or KeyNotFoundException)
        {
            if (IsLoaded && (!automatic || approved))
            {
                try
                {
                    await WaitForIdleAsync(token);
                    ShowReport("アプリの更新を完了できません", error is OperationCanceledException ? "通信がタイムアウトしました。" : error.Message);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
        finally
        {
            checkingUpdate = false;
            AppUpdateButton.Content = "アプリの更新を確認";
            AppUpdateButton.IsEnabled = true;
        }
    }

    private bool CanExitForUpdate() => IsLoaded && IsEnabled && !startupRunning && !displaySettingsRunning &&
        !operationRunning && !viewModel.IsLoading && viewModel.CanSave &&
        !OwnedWindows.OfType<Window>().Any(window => window.IsVisible) &&
        IsWindowEnabled(new WindowInteropHelper(this).Handle);

    private async Task WaitForIdleAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (!CanExitForUpdate())
            await Task.Delay(300, token);
    }

    private Task ValidateUpdateLocationAsync(UpdateLayout installation, CancellationToken token)
    {
        var projects = viewModel.Projects;
        var cacheRoots = viewModel.CacheLocations;
        return Task.Run(() =>
        {
            string application = ArchiveDirectory.ResolvePath(installation.AppDirectory, token);
            foreach (var project in projects)
            {
                var locations = project.Roots.SelectMany(root => new[] { root.OutputPath, root.ArchivePath })
                    .Concat(project.ModelFolders.Select(folder => folder.Path))
                    .Append(WorkspacePaths.CacheBase(project));
                if (project.EvaluationPath.Length > 0)
                    locations = locations.Append(Path.GetDirectoryName(project.EvaluationPath)!);
                VerifyLocations(locations);
            }
            VerifyLocations(cacheRoots);

            void VerifyLocations(IEnumerable<string> locations)
            {
                foreach (string path in locations)
                    if (WorkspacePaths.Overlap(ArchiveDirectory.ResolvePath(path, token), application))
                        throw new IOException($"更新対象のフォルダーとデータの参照先が重複しています。{path}");
            }
        }, token);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
}
