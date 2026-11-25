using System.IO;
using System.Text.Json;

namespace HandpickerStudio;

internal sealed record ImageRoot(Guid Id, string Name, string OutputPath, string ArchivePath, bool Enabled);

internal sealed record Project(Guid Id, string Name, string CheckpointPath, string EvaluationPath, string CachePath, ImageRoot[] Roots);

internal sealed record HeldLabel(string OutputPath, string Label);

internal sealed record WorkspaceSettings(int Version, Project[] Projects, Guid? SelectedProjectId, HeldLabel[] HeldLabels,
    bool UseTileView = false)
{
    public static WorkspaceSettings Empty { get; } = new(1, [], null, []);
}

internal static class WorkspacePaths
{
    public static string Normalize(string path)
    {
        path = path.Trim();
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("ドライブ名を含む絶対パスか、UNCパスを指定してください。");

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Overlap(string first, string second) => Within(first, second) || Within(second, first);

    public static bool Within(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static string DefaultCachePath => Path.Combine(AppContext.BaseDirectory, "cache");

    public static Project Normalize(Project project)
    {
        if (project.Id == Guid.Empty || string.IsNullOrWhiteSpace(project.Name) || project.Roots is null)
            throw new ArgumentException("プロジェクト名とCheckpointフォルダーを指定してください。");

        var roots = project.Roots.Select(root =>
        {
            if (root is null || root.Id == Guid.Empty || string.IsNullOrWhiteSpace(root.Name))
                throw new ArgumentException("参照ルートの名前を指定してください。");

            return root with
            {
                Name = root.Name.Trim(),
                OutputPath = Normalize(root.OutputPath ?? ""),
                ArchivePath = Normalize(root.ArchivePath ?? "")
            };
        }).ToArray();

        string cachePath = string.IsNullOrWhiteSpace(project.CachePath) ? "" : Normalize(project.CachePath);
        string effectiveCachePath = cachePath.Length == 0 ? DefaultCachePath : cachePath;
        if (roots.Select(root => root.Id).Distinct().Count() != roots.Length)
            throw new ArgumentException("参照ルートの識別子が重複しています。");

        foreach (var root in roots)
        {
            if (roots.Any(other => other.Id != root.Id && Overlap(root.OutputPath, other.OutputPath)))
                throw new ArgumentException($"Outputの範囲が重複しています。{root.Name}");
            if (roots.Any(other => Overlap(root.OutputPath, other.ArchivePath)) || Overlap(root.OutputPath, effectiveCachePath))
                throw new ArgumentException($"Outputとarchiveまたはcacheの範囲が重複しています。{root.Name}");
        }

        return project with
        {
            Name = project.Name.Trim(),
            CheckpointPath = Normalize(project.CheckpointPath ?? ""),
            EvaluationPath = string.IsNullOrWhiteSpace(project.EvaluationPath) ? "" : Normalize(project.EvaluationPath),
            CachePath = cachePath,
            Roots = roots
        };
    }
}

internal sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private byte[]? savedBytes;

    public WorkspaceSettings Load()
    {
        byte[]? bytes = ReadBytes();
        if (bytes is null)
        {
            savedBytes = null;
            return WorkspaceSettings.Empty;
        }

        var settings = JsonSerializer.Deserialize<WorkspaceSettings>(bytes, JsonOptions);
        if (settings is not { Version: 1, Projects: not null, HeldLabels: not null } ||
            settings.Projects.Any(project => project is null) ||
            settings.Projects.Select(project => project.Id).Distinct().Count() != settings.Projects.Length ||
            settings.HeldLabels.Any(hold => hold is null || string.IsNullOrWhiteSpace(hold.Label) ||
                string.IsNullOrWhiteSpace(hold.OutputPath)))
            throw new InvalidDataException("設定ファイルの形式に対応していません。");

        var projects = settings.Projects.Select(WorkspacePaths.Normalize).ToArray();
        var holds = settings.HeldLabels.Select(hold => hold with { OutputPath = WorkspacePaths.Normalize(hold.OutputPath) }).ToArray();
        savedBytes = bytes;
        return settings with { Projects = projects, HeldLabels = holds };
    }

    public void Save(WorkspaceSettings settings)
    {
        using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[]? currentBytes = ReadBytes();
        if ((savedBytes is null) != (currentBytes is null) ||
            (savedBytes is not null && !savedBytes.AsSpan().SequenceEqual(currentBytes)))
            throw new IOException("別のウィンドウまたはアプリで設定が変わりました。再起動して読み直してください。");

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
                temporary.Write(bytes);
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            savedBytes = bytes;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private byte[]? ReadBytes()
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
    }
}
