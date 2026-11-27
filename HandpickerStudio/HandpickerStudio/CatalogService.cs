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
    ImageRoot Root, bool FromArchive, string[] PossibleOwners, HeldLabel[]? LegacyHolds = null)
{
    public string Name => Path.GetFileName(FullPath);
}

internal sealed record Catalog(Checkpoint[] Checkpoints, CheckpointGroup[] Groups,
    ILookup<string, Checkpoint> Labels, HeldLabel[] ObservedHolds, string CollisionReport, string[] Warnings, bool IsComplete,
    IReadOnlyDictionary<string, string> OutputPaths, string[] UnconfirmedOutputs)
{
    public string OutputPath(string path) => OutputPaths.GetValueOrDefault(path, path);
}

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

    internal static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    [GeneratedRegex("[^0-9A-Za-z_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeLabelCharacters();

    [GeneratedRegex("_{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedUnderscores();

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWindowsName();

    public static Catalog ReadCatalog(Project[] projects, Project active, CancellationToken token, bool includeRatings = true)
    {
        List<Checkpoint> checkpoints = [];
        List<string> warnings = [];
        HashSet<Guid> incomplete = [];
        foreach (Project project in projects)
            checkpoints.AddRange(ReadCheckpoints(project, error =>
            {
                incomplete.Add(project.Id);
                warnings.Add($"{project.Name}のCheckpoint一覧: {error}");
            }, token));

        var outputPaths = projects.SelectMany(project => project.Roots).Select(root => root.OutputPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => ResolveOutputPath(path, token), StringComparer.OrdinalIgnoreCase);
        var unconfirmedOutputs = projects.Where(project => incomplete.Contains(project.Id)).SelectMany(project => project.Roots)
            .Select(root => outputPaths[root.OutputPath]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var labels = IndexLabels(checkpoints);
        var (heldLabels, report) = FindLabelCollisions(labels, outputPaths, token);

        var activeModels = checkpoints.Where(item => item.Project.Id == active.Id).ToArray();
        var duplicates = activeModels.GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeCheckpoints = activeModels.DistinctBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => duplicates.Contains(item.RelativePath) ? item with { SharedRelativePath = true } : item)
            .OrderBy(item => item.RelativePath, CheckpointOrder).ToArray();
        bool isComplete = warnings.Count == 0;
        var ratings = includeRatings ? ReadRatings(active.EvaluationPath, warnings) : null;
        var byRating = activeCheckpoints.ToLookup(item => ratings?.GetValueOrDefault(item.RelativePath, Rating.None) ?? Rating.Unavailable);
        var groups = RatingGroups.Where(group => group.Rating != Rating.Unavailable || byRating.Contains(Rating.Unavailable))
            .Select(group => new CheckpointGroup(group.Label, byRating[group.Rating].ToArray(), group.Rating)).ToArray();
        return new(activeCheckpoints, groups, labels, heldLabels, report, warnings.Distinct().ToArray(), isComplete,
            outputPaths, unconfirmedOutputs);
    }

    private static (HeldLabel[] Holds, string Report) FindLabelCollisions(ILookup<string, Checkpoint> labels,
        IReadOnlyDictionary<string, string> outputPaths, CancellationToken token)
    {
        List<HeldLabel> holds = [];
        List<string> reports = [];
        foreach (var group in labels.Where(group => group.Skip(1).Any()).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var candidates = group.Distinct().ToArray();
            var conflicting = new HashSet<Checkpoint>();
            for (int first = 0; first < candidates.Length; first++)
            {
                var left = candidates[first];
                for (int second = first + 1; second < candidates.Length; second++)
                {
                    var right = candidates[second];
                    var shared = SharedOutputs(left.Project, right.Project, outputPaths).ToArray();
                    if (shared.Length == 0 && left.Project.Id != right.Project.Id)
                        continue;
                    conflicting.Add(left);
                    conflicting.Add(right);
                    foreach (string path in shared)
                    {
                        holds.Add(new(path, group.Key, Scoped: true));
                        holds.Add(new(path, left.SafeName, Scoped: true));
                        holds.Add(new(path, right.SafeName, Scoped: true));
                    }
                }
            }
            if (conflicting.Count > 0)
                reports.Add($"保存ラベル: {group.Key}{Environment.NewLine}" + string.Join(Environment.NewLine,
                    conflicting.OrderBy(item => item.Project.Name).ThenBy(item => item.RelativePath)
                        .Select(item => $"{item.Project.Name} | {item.SafeName} | {item.FullPath}")));
        }
        return (holds.DistinctBy(HoldKey).ToArray(), string.Join(Environment.NewLine + Environment.NewLine, reports));
    }

    private static IEnumerable<string> SharedOutputs(Project left, Project right, IReadOnlyDictionary<string, string> outputPaths)
    {
        foreach (var first in left.Roots)
        {
            string firstPath = outputPaths[first.OutputPath];
            foreach (var second in right.Roots)
            {
                string secondPath = outputPaths[second.OutputPath];
                if (WorkspacePaths.Within(firstPath, secondPath))
                    yield return firstPath;
                else if (WorkspacePaths.Within(secondPath, firstPath))
                    yield return secondPath;
            }
        }
    }

    private static string ResolveOutputPath(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return ResolveExistingDirectory(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return path; }
    }

    internal static string ResolveExistingDirectory(string path)
    {
        string resolved = ArchiveDirectory.ResolveExistingPath(path);
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal) && resolved.Length > 5 && resolved[5] == ':')
            resolved = resolved[4..];
        return Path.TrimEndingDirectorySeparator(resolved);
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

    internal static IEnumerable<Checkpoint> ReadCheckpoints(Project project, Action<string> warn, CancellationToken token)
    {
        foreach (var folder in project.ModelFolders)
        {
            token.ThrowIfCancellationRequested();
            if (folder.AllowMissing && !DirectoryAvailable(folder.Path, warn,
                allowMissing: Directory.Exists(Directory.GetParent(folder.Path)?.Parent?.FullName)))
                continue;

            foreach (string path in EnumerateCheckpoints(folder.Path, warn, token))
            {
                string relative = RelativePath(folder.Path, path);
                yield return new(project, relative, path, SafeName(relative), folder.Path);
            }
        }
    }

    internal static Dictionary<string, Rating>? ReadRatings(string path, List<string> warnings)
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
            return ParseRatings(document.RootElement);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            warnings.Add($"評価JSONを読み込めません。{exception.Message}");
            return null;
        }
    }

    internal static Dictionary<string, Rating> ParseRatings(JsonElement root)
    {
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

    public static ImageScan ReadImages(Project active, Catalog catalog, HeldLabel[] holds, CancellationToken token,
        bool includeArchive = true)
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

            string outputPath = catalog.OutputPath(root.OutputPath);
            var held = holds.Where(item => WorkspacePaths.Overlap(catalog.OutputPath(item.OutputPath), outputPath))
                .ToLookup(item => item.Label, StringComparer.OrdinalIgnoreCase);
            var unconfirmed = catalog.UnconfirmedOutputs.Where(path => WorkspacePaths.Overlap(path, outputPath)).ToArray();
            int previousWarnings = warnings.Count;
            bool outputAvailable = DirectoryAvailable(root.OutputPath, error => warnings.Add($"{root.Name}のOutput: {error}"));
            if (includeArchive)
                ReadArchive(active.Id, root, catalog.Checkpoints, images, warnings, outputAvailable, token);
            if (outputAvailable)
            {
                foreach (string path in EnumerateFiles(root.OutputPath, error => warnings.Add($"{root.Name}のOutput: {error}"), token))
                {
                    if (!IsImage(path))
                        continue;

                    string relativePath = RelativePath(root.OutputPath, path);
                    string physicalPath = Path.Combine(outputPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var labels = MatchingLabels(relativePath, catalog.Labels, held);
                    var matches = labels.SelectMany(label => catalog.Labels[label]).Distinct().Where(checkpoint => checkpoint.Project.Roots
                        .Any(source => WorkspacePaths.Within(physicalPath, catalog.OutputPath(source.OutputPath)))).ToArray();
                    var imageHolds = labels.SelectMany(label => held[label])
                        .Where(item => WorkspacePaths.Within(physicalPath, catalog.OutputPath(item.OutputPath))).ToArray();
                    bool isHeld = imageHolds.Length != 0;
                    bool isComplete = !unconfirmed.Any(folder => WorkspacePaths.Within(physicalPath, folder));
                    bool hasUniqueOwner = !isHeld && isComplete && matches.Length == 1;
                    if (hasUniqueOwner && matches[0].Project.Id != active.Id)
                        continue;

                    string? owner = hasUniqueOwner ? matches[0].RelativePath : null;
                    bool onlyLegacy = isHeld && imageHolds.All(item => !item.Scoped);
                    string? reason = (owner, isHeld, onlyLegacy, matches.Length, isComplete) switch
                    {
                        (not null, _, _, _, _) => null,
                        (_, true, true, _, _) => "旧版のラベル重複記録により保留",
                        (_, true, _, _, _) => "ラベルの重複により保留",
                        (_, _, _, > 1, _) => "複数のCheckpointが一致",
                        (_, _, _, _, false) => "照合対象の一覧が未確認",
                        _ => "一致するCheckpointなし"
                    };
                    if (reason is not null && matches.Length > 0)
                        reason += " · " + string.Join("、", matches.Select(item => $"{item.Project.Name}/{item.RelativePath}"));
                    var legacyHolds = onlyLegacy && isComplete && matches is [var current] && current.Project.Id == active.Id ? imageHolds : null;
                    images.Add(new(path, relativePath, $"{root.Name} / Output", owner, reason, root, false,
                        matches.Where(item => item.Project.Id == active.Id).Select(item => item.RelativePath).ToArray(), legacyHolds));
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
            if (!IsImage(path))
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

    internal static ILookup<string, Checkpoint> IndexLabels(IEnumerable<Checkpoint> checkpoints) =>
        checkpoints.SelectMany(checkpoint => SaverLabels(checkpoint.SafeName).Select(label => (Label: label, Checkpoint: checkpoint)))
            .ToLookup(item => item.Label, item => item.Checkpoint, StringComparer.OrdinalIgnoreCase);

    internal static Checkpoint[] MatchImageFileName(string fileName, ILookup<string, Checkpoint> labels) =>
        MatchingLabels(fileName, labels).SelectMany(label => labels[label]).Distinct().ToArray();

    private static HashSet<string> MatchingLabels(string relativePath, ILookup<string, Checkpoint> labels, ILookup<string, HeldLabel>? held = null)
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
            if (labels.Contains(label) || held?.Contains(label) == true)
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

    internal static IEnumerable<string> EnumerateFiles(string path, Action<string> warn, CancellationToken token)
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
