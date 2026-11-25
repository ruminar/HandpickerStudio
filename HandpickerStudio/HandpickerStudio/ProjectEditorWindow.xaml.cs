using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace HandpickerStudio;

public partial class ProjectEditorWindow : Window
{
    private readonly Guid projectId;
    private readonly ObservableCollection<RootDraft> roots;
    private readonly Project? originalProject;
    private CancellationTokenSource? pendingDetection;
    private ComfyUiInstallation? installation;
    private string preferredSuite = "";
    private bool initialized;

    internal ProjectEditorWindow(Project? project, bool showRoots)
    {
        InitializeComponent();
        originalProject = project;
        projectId = project?.Id ?? Guid.NewGuid();
        Title = project is null ? "プロジェクトを登録" : "プロジェクト設定";
        NameInput.Text = project?.Name ?? "";
        CheckpointInput.Text = project?.CheckpointPath ?? "";
        EvaluationInput.Text = project?.EvaluationPath ?? "";
        ComfyUiInput.Text = project?.ComfyUiPath ?? "";
        CacheInput.Text = project?.CachePath ?? "";
        roots = new((project?.Roots ?? []).Select(root => new RootDraft(root)));
        RootsGrid.ItemsSource = roots;
        if (showRoots)
            EditorTabs.SelectedItem = RootsTab;
        AdvancedSettings.IsExpanded = project is not null && ComfyUiInput.Text.Length == 0;
        initialized = true;
        Loaded += async (_, _) =>
        {
            if (!showRoots)
                NameInput.Focus();
            await DetectInstallationAsync();
        };
        Closed += (_, _) =>
        {
            initialized = false;
            pendingDetection?.Cancel();
        };
    }

    internal Project? Result { get; private set; }

    private async void Save(object sender, RoutedEventArgs e)
    {
        CommitRootEdits();
        SaveButton.IsEnabled = false;
        try
        {
            var imageRoots = roots.Select(root => root.ToSettings()).ToArray();
            if (string.IsNullOrWhiteSpace(ComfyUiInput.Text))
                Result = WorkspacePaths.Normalize(new Project(projectId, NameInput.Text, CheckpointInput.Text,
                    EvaluationInput.Text, CacheInput.Text, imageRoots));
            else
            {
                string input = WorkspacePaths.Normalize(ComfyUiInput.Text);
                if (installation?.SelectedPath != input && !await DetectInstallationAsync() &&
                    initialized && EditorTabs.SelectedItem == RootsTab &&
                    originalProject is { } existing && existing.ComfyUiPath.Equals(input, StringComparison.OrdinalIgnoreCase) &&
                    ComfyUiInput.Text.Trim().Equals(input, StringComparison.OrdinalIgnoreCase))
                {
                    Result = WorkspacePaths.Normalize(existing with
                    {
                        Name = NameInput.Text, CachePath = CacheInput.Text, Roots = imageRoots
                    });
                    DialogResult = true;
                    return;
                }
                if (!initialized || installation is not { } detected || detected.SelectedPath != WorkspacePaths.Normalize(ComfyUiInput.Text))
                    return;

                var suite = SuitePicker.SelectedItem as DetectedSuite;
                if (suite is null && detected.Suites.Count(candidate => candidate.HasEvaluation) > 1)
                    throw new ArgumentException("使用するHandpickerSuiteのデータフォルダーを選択してください。");
                var modelFolders = detected.Checkpoints.Select(folder => new CheckpointFolder(folder.Path, folder.AllowMissing)).ToArray();
                Result = WorkspacePaths.Normalize(new Project(projectId, NameInput.Text, modelFolders[0].Path,
                    suite?.EvaluationPath ?? "", CacheInput.Text, imageRoots,
                    detected.SelectedPath, modelFolders));
            }
            DialogResult = true;
        }
        catch (Exception exception) when (MainWindow.IsOperationError(exception))
        {
            ErrorText.Text = exception.Message;
        }
        finally { SaveButton.IsEnabled = true; }
    }

    private async void Detect(object sender, RoutedEventArgs e) => await DetectInstallationAsync();

    private async void ComfyUiPathChanged(object sender, TextChangedEventArgs e)
    {
        if (initialized)
            await DetectInstallationAsync(debounce: true);
    }

    private async Task<bool> DetectInstallationAsync(bool debounce = false)
    {
        pendingDetection?.Cancel();
        string input = ComfyUiInput.Text.Trim();
        installation = null;
        bool automatic = input.Length > 0;
        ManualPaths.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
        DetectButton.IsEnabled = automatic;
        DetectionResults.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";
        if (!automatic)
        {
            DetectionStatus.Text = "ComfyUI_windows_portable またはComfyUI本体のフォルダーを選択してください。";
            return false;
        }

        using var request = new CancellationTokenSource();
        pendingDetection = request;
        DetectionStatus.Text = "ComfyUIの参照先を確認中…";
        try
        {
            if (debounce)
                await Task.Delay(250, request.Token);
            var detected = await Task.Run(() => ComfyUiDiscovery.Read(input, request.Token), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!initialized)
                return false;

            installation = detected;
            DetectionStatus.Text = $"{detected.RootPath}{Environment.NewLine}{detected.ConfigStatus}";
            DetectionResults.Visibility = Visibility.Visible;
            DetectedFolders.ItemsSource = detected.Checkpoints;
            if (string.IsNullOrWhiteSpace(NameInput.Text))
                NameInput.Text = SuggestedName(detected.SelectedPath);
            string lastSuite = preferredSuite.Length == 0 ? originalProject?.EvaluationPath ?? "" : preferredSuite;
            var selected = detected.Suites.FirstOrDefault(suite => suite.EvaluationPath.Equals(lastSuite, StringComparison.OrdinalIgnoreCase));
            var available = detected.Suites.Where(suite => suite.HasEvaluation).ToArray();
            SuitePicker.ItemsSource = detected.Suites;
            SuitePicker.IsEnabled = detected.Suites.Length > 0;
            SuitePicker.SelectedItem = selected ?? (available.Length == 1 ? available[0] : detected.Suites.Length == 1 ? detected.Suites[0] : null);
            UpdateSuiteStatus();
            return true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        catch (Exception exception) when (MainWindow.IsOperationError(exception))
        {
            if (!request.IsCancellationRequested && initialized)
            {
                DetectionStatus.Text = "ComfyUIの参照先を検出できませんでした。";
                ErrorText.Text = exception.Message;
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(pendingDetection, request))
                pendingDetection = null;
        }
    }

    private void SuiteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuitePicker.SelectedItem is DetectedSuite suite)
            preferredSuite = suite.EvaluationPath;
        UpdateSuiteStatus();
    }

    private static string SuggestedName(string selectedPath)
    {
        var folder = new DirectoryInfo(selectedPath);
        if (folder.Name.Equals("ComfyUI", StringComparison.OrdinalIgnoreCase) && folder.Parent is { Parent: not null } parent)
            folder = parent;
        if (folder.Name.Equals("ComfyUI_windows_portable", StringComparison.OrdinalIgnoreCase) && folder.Parent is { Parent: not null } workspace)
            folder = workspace;
        return folder.Name;
    }

    private void UpdateSuiteStatus()
    {
        if (installation is not { } detected)
            return;

        SuiteStatus.Text = SuitePicker.SelectedItem is DetectedSuite suite ? suite.Details :
            detected.Suites.Length == 0 ? "HandpickerSuiteが見つかりませんでした。" : "データフォルダーを選択すると確認結果が表示されます。";
    }

    private void AddRoot(object sender, RoutedEventArgs e)
    {
        CommitRootEdits();
        var root = new RootDraft(new(Guid.NewGuid(), "参照ルート", "", "", true));
        roots.Add(root);
        RootsGrid.SelectedItem = root;
        RootsGrid.ScrollIntoView(root);
    }

    private void RemoveRoot(object sender, RoutedEventArgs e)
    {
        CommitRootEdits();
        if (RootsGrid.SelectedItem is RootDraft root)
            roots.Remove(root);
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox input } && PickFolder(input.Text) is { } selected)
            input.Text = selected;
    }

    private void BrowseEvaluation(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "HandpickerSuiteの評価JSONを選択", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true)
            EvaluationInput.Text = dialog.FileName;
    }

    private void BrowseOutput(object sender, RoutedEventArgs e) => BrowseRootPath(selectArchive: false);

    private void BrowseArchive(object sender, RoutedEventArgs e) => BrowseRootPath(selectArchive: true);

    private void BrowseRootPath(bool selectArchive)
    {
        CommitRootEdits();
        if (RootsGrid.SelectedItem is not RootDraft root)
        {
            ErrorText.Text = "編集する参照ルートを選択してください。";
            return;
        }

        string? selected = PickFolder(selectArchive ? root.ArchivePath : root.OutputPath);
        if (selected is null)
            return;

        if (selectArchive)
            root.ArchivePath = selected;
        else
            root.OutputPath = selected;
        RootsGrid.Items.Refresh();
        RootsGrid.SelectedItem = root;
        ErrorText.Text = "";
    }

    private string? PickFolder(string currentPath)
    {
        var dialog = new OpenFolderDialog { Title = "フォルダーを選択" };
        if (Path.IsPathFullyQualified(currentPath.Trim()))
            dialog.InitialDirectory = currentPath.Trim();
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void CommitRootEdits()
    {
        RootsGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        RootsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    public sealed class RootDraft
    {
        private readonly Guid id;
        internal RootDraft(ImageRoot root) => (id, Name, OutputPath, ArchivePath, Enabled) =
            (root.Id, root.Name, root.OutputPath, root.ArchivePath, root.Enabled);

        public string Name { get; set; }
        public string OutputPath { get; set; }
        public string ArchivePath { get; set; }
        public bool Enabled { get; set; }
        internal ImageRoot ToSettings() => new(id, Name, OutputPath, ArchivePath, Enabled);
    }
}
