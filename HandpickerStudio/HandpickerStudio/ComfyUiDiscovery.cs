using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace HandpickerStudio;

internal sealed record DetectedCheckpointFolder(string Path, string Status, bool AllowMissing);

internal sealed record DetectedSuite(string DataPath, string EvaluationPath, bool HasEvaluation, string Details);

internal sealed record ComfyUiInstallation(string SelectedPath, string RootPath, string ConfigStatus,
    DetectedCheckpointFolder[] Checkpoints, DetectedSuite[] Suites);

internal static partial class ComfyUiDiscovery
{
    private const string ConfigName = "extra_model_paths.yaml";
    private static readonly string[] SuiteFolderNames = ["checkpoint-handpicker-suite", "ComfyUI-CheckpointHandpickerSuite"];

    public static ComfyUiInstallation Read(string selectedPath, CancellationToken token)
    {
        string selected = WorkspacePaths.Normalize(selectedPath);
        string inner = Path.Combine(selected, "ComfyUI");
        string root = IsComfyUi(selected) ? selected : IsComfyUi(inner) ? inner :
            throw new DirectoryNotFoundException("ComfyUI本体、またはComfyUI_windows_portableのフォルダーを指定してください。");

        string config = Path.Combine(root, ConfigName);
        var checkpoints = new List<CheckpointFolder> { new(Path.Combine(root, "models", "checkpoints")) };
        var customNodes = new List<CheckpointFolder> { new(Path.Combine(root, "custom_nodes")) };
        bool hasConfig = ReadExtraPaths(config, checkpoints, customNodes, token);
        AddFolder(checkpoints, new(Path.Combine(root, "output", "checkpoints"), AllowMissing: true), preferred: false);

        var models = checkpoints.Select(folder =>
        {
            token.ThrowIfCancellationRequested();
            return new DetectedCheckpointFolder(folder.Path, Status(folder.Path, directory: true), folder.AllowMissing);
        }).ToArray();

        var suites = new List<DetectedSuite>();
        foreach (var folder in customNodes)
        {
            foreach (string name in SuiteFolderNames)
            {
                token.ThrowIfCancellationRequested();
                string extension = Path.Combine(folder.Path, name);
                if (!Directory.Exists(extension))
                    continue;
                string data = Path.Combine(extension, "data");
                string evaluation = Path.Combine(data, "checkpoint_statuses.json");
                string integration = Path.Combine(data, "checkpoint_studio.json");
                suites.Add(new(data, evaluation, File.Exists(evaluation),
                    $"data フォルダー  {Status(data, directory: true)}{Environment.NewLine}" +
                    $"checkpoint_statuses.json  {Status(evaluation, directory: false)}{Environment.NewLine}" +
                    $"checkpoint_studio.json  {Status(integration, directory: false)}"));
            }
        }

        return new(selected, root, hasConfig ? "extra_model_paths.yaml を読み込みました。" : "extra_model_paths.yaml はありません。",
            models, suites.ToArray());
    }

    private static bool IsComfyUi(string directory) =>
        File.Exists(Path.Combine(directory, "main.py")) && File.Exists(Path.Combine(directory, "folder_paths.py"));

    private static bool ReadExtraPaths(string config, List<CheckpointFolder> checkpoints, List<CheckpointFolder> customNodes,
        CancellationToken token)
    {
        try
        {
            using var stream = CatalogService.OpenForReading(config);
            using var reader = new StreamReader(stream);
            var document = new YamlStream();
            document.Load(reader);
            if (document.Documents.Count == 0 || (document.Documents.Count == 1 && IsEmpty(document.Documents[0].RootNode)))
                return true;
            if (document.Documents.Count != 1 || document.Documents[0].RootNode is not YamlMappingNode sections)
                throw new InvalidDataException("設定はセクション名で始まるマッピングにしてください。");

            string directory = Path.GetDirectoryName(config)!;
            foreach (var (name, node) in sections.Children)
            {
                token.ThrowIfCancellationRequested();
                if (IsEmpty(node))
                    continue;
                if (node is not YamlMappingNode section)
                    throw new InvalidDataException($"「{name}」の設定を読み込めません。");
                string? basePath = Scalar(section, "base_path");
                string baseDirectory = string.IsNullOrEmpty(basePath) ? directory : Path.GetFullPath(ExpandBasePath(basePath), directory);
                bool preferred = Scalar(section, "is_default")?.ToLowerInvariant() is "true" or "yes" or "on";

                AddEntries("checkpoints", checkpoints);
                AddEntries("custom_nodes", customNodes);

                void AddEntries(string name, List<CheckpointFolder> folders)
                {
                    foreach (string value in (Scalar(section, name) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        AddFolder(folders, new(WorkspacePaths.Normalize(Path.GetFullPath(value, baseDirectory))), preferred);
                }
            }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (Exception error) when (error is YamlException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"{ConfigName} を読み込めません。{error.Message}", error);
        }
    }

    private static bool IsEmpty(YamlNode node) => node is YamlScalarNode scalar &&
        (scalar.Value is null || (scalar.Style is ScalarStyle.Plain or ScalarStyle.Any &&
            (scalar.Value.Length == 0 || scalar.Value == "~" || scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase))));

    private static string? Scalar(YamlMappingNode section, string name)
    {
        if (!section.Children.TryGetValue(new YamlScalarNode(name), out var value) || IsEmpty(value))
            return null;
        return value is YamlScalarNode scalar ? scalar.Value :
            throw new InvalidDataException($"「{name}」は文字列または複数行の文字列で指定してください。");
    }

    private static void AddFolder(List<CheckpointFolder> folders, CheckpointFolder folder, bool preferred)
    {
        int existing = folders.FindIndex(item => item.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            if (!preferred)
                return;
            folders.RemoveAt(existing);
        }
        if (preferred)
            folders.Insert(0, folder);
        else
            folders.Add(folder);
    }

    private static string ExpandBasePath(string value)
    {
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value[1..];
        return UnixVariable().Replace(Environment.ExpandEnvironmentVariables(value), match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) ?? match.Value);
    }

    private static string Status(string path, bool directory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) == directory ? "あり" : "種類が異なります";
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return "なし"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "確認できません"; }
    }

    [GeneratedRegex(@"\$\{([^}]+)\}|\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex UnixVariable();
}
