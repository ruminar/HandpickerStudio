using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HandpickerStudio;

internal enum Rating { God, Favorite, Nice, Keep, Delete, None, Unavailable }

internal sealed record Checkpoint(Project Project, string RelativePath, string FullPath, string SafeName,
    string? SourceFolder = null, bool SharedRelativePath = false)
{
    public string Name => Path.GetFileNameWithoutExtension(RelativePath);
    public string RootPath => SourceFolder ?? Project.CheckpointPath;

    public void RequireUniquePath()
    {
        if (SharedRelativePath)
            throw new IOException($"同じ相対パスのCheckpointが複数の参照先にあります。{RelativePath}");
    }
}

internal sealed record CheckpointGroup(string Name, Checkpoint[] Items, Rating Rating);

internal sealed record RootStatus(ImageRoot Root, string Status)
{
    public string Action => Root.Enabled ? "無効にする" : "有効にする";
}

internal sealed record ImageEntry(string FullPath, string RelativePath, string Location, string? CheckpointPath, string? Problem,
    ImageRoot Root, bool FromArchive, string[] PossibleOwners)
{
    public string Name => Path.GetFileName(FullPath);
}

internal sealed record Catalog(Checkpoint[] Checkpoints, CheckpointGroup[] Groups,
    ILookup<string, Checkpoint> Labels, HeldLabel[] ObservedHolds, string CollisionReport, string[] Warnings, bool IsComplete);

internal sealed record ImageScan(ImageEntry[] Images, RootStatus[] Roots, string[] Warnings);

internal static partial class CatalogService
{
    private static readonly (Rating Rating, string Label)[] RatingGroups =
    [
        (Rating.God, "👑 god!"), (Rating.Favorite, "💛 favorite"), (Rating.Nice, "👍 nice"),
        (Rating.Keep, "✔ keep"), (Rating.Delete, "🗑 delete"), (Rating.None, "— none"),
        (Rating.Unavailable, "評価情報を取得できません")
    ];

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private static readonly Comparer<string> CheckpointOrder = Comparer<string>.Create(CompareCheckpointPaths);

    [GeneratedRegex("[^0-9A-Za-z_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeLabelCharacters();

    [GeneratedRegex("_{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedUnderscores();

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWindowsName();

    public static Catalog ReadCatalog(Project[] projects, Project active, CancellationToken token)
    {
        List<Checkpoint> checkpoints = [];
        List<string> warnings = [];
        foreach (Project project in projects)
        {
            void Warn(string error) => warnings.Add($"{project.Name}のCheckpoint一覧: {error}");
            foreach (var folder in project.ModelFolders)
            {
                token.ThrowIfCancellationRequested();
                if (folder.AllowMissing && !DirectoryAvailable(folder.Path, Warn,
                    allowMissing: Directory.Exists(Directory.GetParent(folder.Path)?.Parent?.FullName)))
                    continue;

                foreach (string path in EnumerateCheckpoints(folder.Path, Warn, token))
                {
                    string relativePath = RelativePath(folder.Path, path);
                    checkpoints.Add(new(project, relativePath, path, SafeName(relativePath), folder.Path));
                }
            }
        }

        var labels = checkpoints.SelectMany(checkpoint => SaverLabels(checkpoint.SafeName)
                .Select(label => (Label: label, Checkpoint: checkpoint)))
            .ToLookup(item => item.Label, item => item.Checkpoint, StringComparer.OrdinalIgnoreCase);
        var collisions = labels.Where(group => group.Skip(1).Any()).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var heldLabels = collisions.SelectMany(collision => collision.SelectMany(checkpoint => checkpoint.Project.Roots
                .SelectMany(root => new[] { new HeldLabel(root.OutputPath, collision.Key), new HeldLabel(root.OutputPath, checkpoint.SafeName) })))
            .DistinctBy(HoldKey).ToArray();
        string report = string.Join(Environment.NewLine + Environment.NewLine, collisions.Select(collision =>
            $"保存ラベル: {collision.Key}{Environment.NewLine}" + string.Join(Environment.NewLine,
                collision.OrderBy(item => item.Project.Name).ThenBy(item => item.RelativePath)
                    .Select(item => $"{item.Project.Name} | {item.SafeName} | {item.FullPath}"))));

        var activeModels = checkpoints.Where(item => item.Project.Id == active.Id).ToArray();
        var duplicates = activeModels.GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeCheckpoints = activeModels.DistinctBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => duplicates.Contains(item.RelativePath) ? item with { SharedRelativePath = true } : item)
            .OrderBy(item => item.RelativePath, CheckpointOrder).ToArray();
        bool isComplete = warnings.Count == 0;
        var ratings = ReadRatings(active.EvaluationPath, warnings);
        var byRating = activeCheckpoints.ToLookup(item => ratings?.GetValueOrDefault(item.RelativePath, Rating.None) ?? Rating.Unavailable);
        var groups = RatingGroups.Where(group => group.Rating != Rating.Unavailable || byRating.Contains(Rating.Unavailable))
            .Select(group => new CheckpointGroup(group.Label, byRating[group.Rating].ToArray(), group.Rating)).ToArray();
        return new(activeCheckpoints, groups, labels, heldLabels, report, warnings.Distinct().ToArray(), isComplete);
    }

    private static int CompareCheckpointPaths(string left, string right)
    {
        // ComfyUI sorts Windows relative paths in Python before Suite replaces path separators.
        var first = left.EnumerateRunes();
        var second = right.EnumerateRunes();
        while (first.MoveNext())
        {
            if (!second.MoveNext())
                return 1;
            int firstValue = first.Current.Value == '/' ? '\\' : first.Current.Value;
            int secondValue = second.Current.Value == '/' ? '\\' : second.Current.Value;
            if (firstValue != secondValue)
                return firstValue.CompareTo(secondValue);
        }
        return second.MoveNext() ? -1 : 0;
    }

    private static Dictionary<string, Rating>? ReadRatings(string path, List<string> warnings)
    {
        if (path.Length == 0)
        {
            warnings.Add("評価JSONが未指定です。プロジェクト設定から指定できます。");
            return null;
        }

        try
        {
            using var stream = OpenForReading(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1 ||
                !root.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("version 1の評価DBを指定してください。");

            var ratings = new Dictionary<string, Rating>(StringComparer.Ordinal);
            foreach (var property in statuses.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("status", out var status) ||
                    status.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("評価レコードの形式が正しくありません。");

                if (!SuiteEvaluation.ParseRating(status.GetString(), out Rating rating))
                    throw new InvalidDataException("未対応の評価値があります。");
                if (!ratings.TryAdd(property.Name, rating))
                    throw new InvalidDataException("評価JSONに重複したキーがあります。");
            }

            return ratings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            warnings.Add($"評価JSONを読み込めません。{exception.Message}");
            return null;
        }
    }

    public static ImageScan ReadImages(Project active, Catalog catalog, HeldLabel[] holds, CancellationToken token)
    {
        List<ImageEntry> images = [];
        List<RootStatus> roots = [];
        List<string> warnings = [];
        foreach (ImageRoot root in active.Roots)
        {
            token.ThrowIfCancellationRequested();
            if (!root.Enabled)
            {
                roots.Add(new(root, "無効・接続未確認"));
                continue;
            }

            var held = holds.Where(item => WorkspacePaths.Overlap(item.OutputPath, root.OutputPath))
                .Select(item => item.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int previousWarnings = warnings.Count;
            bool outputAvailable = DirectoryAvailable(root.OutputPath, error => warnings.Add($"{root.Name}のOutput: {error}"));
            ReadArchive(active.Id, root, catalog.Checkpoints, images, warnings, outputAvailable, token);
            if (outputAvailable)
            {
                foreach (string path in EnumerateFiles(root.OutputPath, error => warnings.Add($"{root.Name}のOutput: {error}"), token))
                {
                    if (!ImageExtensions.Contains(Path.GetExtension(path)))
                        continue;

                    string relativePath = RelativePath(root.OutputPath, path);
                    var labels = MatchingLabels(relativePath, catalog.Labels, held);
                    var matches = labels.SelectMany(label => catalog.Labels[label]).Distinct().ToArray();
                    bool isHeld = labels.Overlaps(held);
                    bool hasUniqueOwner = !isHeld && catalog.IsComplete && matches.Length == 1;
                    if (hasUniqueOwner && matches[0].Project.Id != active.Id)
                        continue;

                    string? owner = hasUniqueOwner ? matches[0].RelativePath : null;
                    string? reason = owner is not null ? null : isHeld ? "ラベルの重複により保留" :
                        matches.Length > 1 ? "複数のCheckpointが一致" : !catalog.IsComplete ? "照合対象の一覧が未確認" : "一致するCheckpointなし";
                    if (reason is not null && matches.Length > 0)
                        reason += " · " + string.Join("、", matches.Select(item => $"{item.Project.Name}/{item.RelativePath}"));
                    images.Add(new(path, relativePath, $"{root.Name} / Output", owner, reason, root, false,
                        matches.Where(item => item.Project.Id == active.Id).Select(item => item.RelativePath).ToArray()));
                }
            }
            roots.Add(new(root, !outputAvailable ? "Outputに接続できません" :
                previousWarnings == warnings.Count ? "接続中" : "一部取得できません"));
        }

        return new(images.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Location, StringComparer.OrdinalIgnoreCase).ToArray(), roots.ToArray(), warnings.Distinct().ToArray());
    }

    private static void ReadArchive(Guid projectId, ImageRoot root, Checkpoint[] checkpoints,
        List<ImageEntry> images, List<string> warnings, bool prepare, CancellationToken token)
    {
        string archive = ArchiveDirectory.GetPath(projectId, root);
        try
        {
            if (prepare && !Directory.Exists(archive))
                ArchiveDirectory.EnsureExists(root, archive, token);
            else
                ArchiveDirectory.RequireManagedDirectory(Path.GetDirectoryName(archive)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{root.Name}のarchive: {exception.Message}");
            return;
        }

        string? ReadOwner(string directory)
        {
            try
            {
                using var stream = OpenForReading(Path.Combine(directory, "_checkpoint.json"));
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("checkpointRelativePath", out var key) || key.ValueKind != JsonValueKind.String)
                    return null;
                return checkpoints.FirstOrDefault(checkpoint => !checkpoint.SharedRelativePath && checkpoint.RelativePath == key.GetString())?.RelativePath;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        if (!DirectoryAvailable(root.ArchivePath, error => warnings.Add($"{root.Name}のarchive: {error}"), allowMissing: !prepare) ||
            !DirectoryAvailable(archive, error => warnings.Add($"{root.Name}のarchive: {error}"), allowMissing: true, rejectLink: true))
            return;

        var owners = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EnumerateFiles(archive, error => warnings.Add($"{root.Name}のarchive: {error}"), token))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(path)))
                continue;

            string relativePath = RelativePath(archive, path);
            int separator = relativePath.IndexOf('/');
            string? owner = null;
            if (separator >= 0)
            {
                string directory = relativePath[..separator];
                if (directory.StartsWith('★'))
                    continue;
                if (!owners.TryGetValue(directory, out owner))
                {
                    owner = ReadOwner(Path.Combine(archive, directory));
                    owners.Add(directory, owner);
                }
            }

            images.Add(new(path, relativePath, $"{root.Name} / archive", owner,
                owner is null ? "archiveの所属を確認できません" : null, root, true, owner is null ? [] : [owner]));
        }
    }

    private static HashSet<string> MatchingLabels(string relativePath, ILookup<string, Checkpoint> labels, HashSet<string> held)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] parts = relativePath.Split('/');
        parts[^1] = Path.GetFileNameWithoutExtension(parts[^1]);
        foreach (string part in parts)
        {
            // Save nodes delimit the label with underscores. Inspect whole segments so a label such as
            // "cat" does not match "cats", and retain all claims when an underscore is ambiguous.
            for (int start = 0; start < part.Length;)
            {
                int end = part.IndexOf('_', start);
                while (end >= 0)
                {
                    if (end > start)
                        Add(part[start..end]);
                    end = part.IndexOf('_', end + 1);
                }
                Add(part[start..]);

                int next = part.IndexOf('_', start);
                if (next < 0)
                    break;
                start = next + 1;
            }
        }
        return matches;

        void Add(string label)
        {
            if (labels.Contains(label) || held.Contains(label))
                matches.Add(label);
        }
    }

    private static IEnumerable<string> SaverLabels(string safeName)
    {
        yield return safeName;
        string label = RepeatedUnderscores().Replace(safeName, "_");
        if (ReservedWindowsName().IsMatch(label))
            label = "_" + label;
        if (label.Length > 120)
            label = label[..120].TrimEnd('_');
        if (!label.Equals(safeName, StringComparison.OrdinalIgnoreCase))
            yield return label;
    }

    public static (string OutputPath, string Label) HoldKey(HeldLabel hold) =>
        (hold.OutputPath.ToUpperInvariant(), hold.Label.ToUpperInvariant());

    public static string SafeName(string relativePath)
    {
        var withoutSuffix = relativePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
            ? relativePath[..^".safetensors".Length] : relativePath;
        string safe = UnsafeLabelCharacters().Replace(withoutSuffix, "_").Trim('_');
        return safe.Length == 0 ? "checkpoint" : safe;
    }

    private static string RelativePath(string directory, string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');

    public static FileStream OpenForReading(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    private static bool DirectoryAvailable(string path, Action<string> warn, bool allowMissing = false, bool rejectLink = false)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory) || (rejectLink && attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException($"参照できるフォルダーではありません。{path}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!allowMissing || exception is not (DirectoryNotFoundException or FileNotFoundException))
                warn(exception.Message);
            return false;
        }
    }

    internal static IEnumerable<string> EnumerateCheckpoints(string path, Action<string> warn, CancellationToken token) =>
        EnumerateFiles(path, warn, token).Where(file => Path.GetExtension(file).Equals(".safetensors", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateFiles(string path, Action<string> warn, CancellationToken token)
    {
        var folders = new Stack<string>();
        folders.Push(path);
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        while (folders.TryPop(out string? directory))
        {
            token.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warn(exception.Message);
                continue;
            }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo)
                    folders.Push(entry.FullName);
                else
                    yield return entry.FullName;
            }
        }
    }
}
