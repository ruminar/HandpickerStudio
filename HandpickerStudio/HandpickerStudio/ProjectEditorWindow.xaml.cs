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

    internal ProjectEditorWindow(Project? project, bool showRoots)
    {
        InitializeComponent();
        projectId = project?.Id ?? Guid.NewGuid();
        Title = project is null ? "プロジェクトを登録" : "プロジェクト設定";
        NameInput.Text = project?.Name ?? "";
        CheckpointInput.Text = project?.CheckpointPath ?? "";
        EvaluationInput.Text = project?.EvaluationPath ?? "";
        CacheInput.Text = project?.CachePath ?? "";
        roots = new((project?.Roots ?? []).Select(root => new RootDraft(root)));
        RootsGrid.ItemsSource = roots;
        if (showRoots)
            EditorTabs.SelectedItem = RootsTab;
        Loaded += (_, _) => { if (!showRoots) NameInput.Focus(); };
    }

    internal Project? Result { get; private set; }

    private void Save(object sender, RoutedEventArgs e)
    {
        CommitRootEdits();
        try
        {
            Result = WorkspacePaths.Normalize(new Project(projectId, NameInput.Text, CheckpointInput.Text,
                EvaluationInput.Text, CacheInput.Text, roots.Select(root => root.ToSettings()).ToArray()));
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            ErrorText.Text = exception.Message;
        }
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
