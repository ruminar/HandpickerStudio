using System.Globalization;
using System.IO;
using System.Text.Json;

namespace HandpickerStudio;

internal static partial class ArchiveHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Receipt(Guid RootId, string CheckpointPath, string OutputPath, string ArchivePath,
        string OutputLocation, FileStamp ArchiveStamp);

    internal static string? Run(string historyBase, Project[] projects, Project project, HeldLabel[] holds,
        HashSet<string>? selection, bool restore, Func<string, string, bool> confirmCopy, PreviewCache.RemovalBatch? cleanup,
        bool quiet = false)
    {
        ArchiveDirectory.RequireManagedDirectory(historyBase);
        Directory.CreateDirectory(historyBase);
        using var writeLock = new FileStream(Path.Combine(historyBase, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string history = Path.Combine(historyBase, project.Id.ToString("N"));
        ArchiveDirectory.RequireManagedDirectory(history);
        Directory.CreateDirectory(history);

        var result = new Result();
        if (restore)
            Restore(history, projects, project, selection, confirmCopy, cleanup, result);
        else
            Archive(history, projects, project, holds, selection, confirmCopy, cleanup, result);
        return quiet && !result.HasWarnings ? null : result.Format(restore);
    }

    private static void Archive(string history, Project[] projects, Project project, HeldLabel[] holds,
        HashSet<string>? selection, Func<string, string, bool> confirmCopy, PreviewCache.RemovalBatch? cleanup, Result result)
    {
        var catalog = CatalogService.ReadCatalog(projects, project, CancellationToken.None, includeRatings: false);
        var scan = CatalogService.ReadImages(project, catalog, [.. holds, .. catalog.ObservedHolds], CancellationToken.None,
            includeArchive: false);
        foreach (string warning in catalog.Warnings.Concat(scan.Warnings).Distinct())
            result.Report(warning);

        var checkpoints = catalog.Checkpoints.Where(item => !item.SharedRelativePath && (selection?.Contains(item.RelativePath) ?? true))
            .ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var sources = new HashSet<CleanupSource>();
        foreach (var image in scan.Images)
        {
            if (image.CheckpointPath is not { } owner || !checkpoints.TryGetValue(owner, out var checkpoint) ||
                LastBucket(image.RelativePath) is not { } day || day >= DateOnly.FromDateTime(DateTime.Today))
                continue;

            try
            {
                string output = image.Root.OutputPath;
                string resolvedOutput = CatalogService.ResolveExistingDirectory(output);
                if (!resolvedOutput.Equals(catalog.OutputPath(output), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("走査後にOutputの参照先が変わりました。");
                using var file = VerifiedFile.Open(image.FullPath, output, forMove: true);
                var lastWrite = DateOnly.FromDateTime(DateTime.FromFileTime(file.Stamp.LastWrite));
                if (file.Stamp.LastWrite == 0 || lastWrite >= DateOnly.FromDateTime(DateTime.Today))
                    continue;

                string destination = ImageOperations.PrepareArchiveDestination(image, checkpoint, image.Root);
                if (Exists(destination))
                    throw new IOException($"Archiveに同名のファイルがあります。{destination}");

                string receiptPath = Path.Combine(history, $"{Guid.NewGuid():N}.json");
                var receipt = new Receipt(image.Root.Id, owner, image.FullPath, destination, resolvedOutput, file.Stamp);
                WriteReceipt(receiptPath, receipt);
                try
                {
                    MoveWithCache(cleanup, image.FullPath, () => ImageOperations.MoveFile(file, image.FullPath, destination, confirmCopy,
                        stamp => WriteReceipt(receiptPath, receipt with { ArchiveStamp = stamp }), preserveLastWrite: true));
                }
                catch (Exception error) when (IsFileError(error))
                {
                    DropUnusedReceipt(receiptPath, image.FullPath, destination);
                    throw;
                }
                result.Moved++;
                sources.Add(new(Path.GetDirectoryName(image.FullPath)!, output, resolvedOutput));
            }
            catch (Exception error) when (IsFileError(error))
            {
                result.Report($"{image.FullPath}{Environment.NewLine}{error.Message}");
            }
        }
        PruneEmptyDirectories(sources, catalog.OutputPaths.Values, result);
    }

    private static DateOnly? LastBucket(string relativePath)
    {
        DateOnly? latest = null;
        foreach (string part in Path.GetDirectoryName(relativePath)!.Split('/', '\\'))
        {
            if (part.Length != 8 || !part.All(char.IsAsciiDigit))
                continue;
            if (!DateOnly.TryParseExact(part, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return null;
            if (latest is null || latest < day)
                latest = day;
        }
        return latest;
    }

    private static void Restore(string history, Project[] projects, Project project, HashSet<string>? selection,
        Func<string, string, bool> confirmCopy, PreviewCache.RemovalBatch? cleanup, Result result)
    {
        var roots = project.Roots.Where(root => root.Enabled).ToDictionary(root => root.Id);
        var sources = new HashSet<CleanupSource>();
        foreach (string receiptPath in Directory.EnumerateFiles(history, "*.json"))
        {
            try
            {
                Receipt receipt;
                using (var input = VerifiedFile.Open(receiptPath, history))
                using (var stream = input.OpenStream())
                    receipt = JsonSerializer.Deserialize<Receipt>(stream, JsonOptions) ?? throw new IOException("移動記録を読み込めません。");
                if (selection is not null && !selection.Contains(receipt.CheckpointPath))
                    continue;
                if (!roots.TryGetValue(receipt.RootId, out var root))
                    throw new IOException($"参照ルートが無効か、登録されていません。{receipt.OutputPath}");

                string archive = ArchiveDirectory.GetPath(project.Id, root);
                string output = root.OutputPath;
                RequireFileBelow(receipt.OutputPath, output);
                RequireFileBelow(receipt.ArchivePath, archive);
                if (!Directory.Exists(output) || !Directory.Exists(archive))
                    throw new IOException($"OutputまたはArchiveに接続できません。{receipt.OutputPath}");
                ArchiveDirectory.RequireManagedDirectory(Path.GetDirectoryName(archive)!);
                ArchiveDirectory.RequireManagedDirectory(archive);
                string archiveLocation = CatalogService.ResolveExistingDirectory(archive);
                if (!CatalogService.ResolveExistingDirectory(output).Equals(receipt.OutputLocation, StringComparison.OrdinalIgnoreCase) ||
                    WorkspacePaths.Overlap(receipt.OutputLocation, archiveLocation))
                    throw new IOException($"OutputまたはArchiveの参照先が変わりました。{receipt.OutputPath}");

                if (!Exists(receipt.ArchivePath))
                {
                    File.Delete(receiptPath);
                    result.Missing++;
                    continue;
                }
                if (Exists(receipt.OutputPath))
                    throw new IOException($"元のOutputに同名のファイルがあります。{receipt.OutputPath}");

                using (var file = VerifiedFile.Open(receipt.ArchivePath, archive, receipt.ArchiveStamp, forMove: true))
                {
                    ImageOperations.CreateBelow(output, Path.GetDirectoryName(receipt.OutputPath)!);
                    MoveWithCache(cleanup, receipt.ArchivePath, () =>
                        ImageOperations.MoveFile(file, receipt.ArchivePath, receipt.OutputPath, confirmCopy, preserveLastWrite: true));
                }
                result.Moved++;
                sources.Add(new(Path.GetDirectoryName(receipt.ArchivePath)!, archive, archiveLocation, receipt.CheckpointPath));
                File.Delete(receiptPath);
            }
            catch (Exception error) when (IsFileError(error))
            {
                result.Report(error.Message);
            }
        }
        PruneEmptyDirectories(sources, projects.SelectMany(item => item.Roots).Select(root => root.OutputPath), result);
    }

    private static void RequireFileBelow(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path ||
            path.Equals(root, StringComparison.OrdinalIgnoreCase) || !WorkspacePaths.Within(path, root))
            throw new IOException($"移動記録のパスと現在の参照ルートが一致しません。{path}");
    }

    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static void MoveWithCache(PreviewCache.RemovalBatch? cleanup, string path, Action move)
    {
        if (cleanup is null)
            move();
        else
            cleanup.AfterRemoving(path, move);
    }

    private static void WriteReceipt(string path, Receipt receipt)
    {
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, receipt, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { ImageOperations.RemoveTemporary(temporary); }
    }

    private static void DropUnusedReceipt(string path, string output, string archive)
    {
        try
        {
            if (Exists(output) && !Exists(archive))
                File.Delete(path);
        }
        catch (Exception error) when (IsFileError(error)) { }
    }

    private static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or JsonException;

    private sealed class Result
    {
        private const int DetailLimit = 40;
        private readonly List<string> details = [];
        private int warnings;
        public int Moved { get; set; }
        public int Missing { get; set; }
        public int PrunedDirectories { get; set; }
        public bool HasWarnings => warnings != 0;

        public void Report(string detail)
        {
            warnings++;
            if (details.Count < DetailLimit)
                details.Add(detail);
        }

        public string Format(bool restore)
        {
            var lines = new List<string> { restore ? $"Outputに戻した画像 {Moved} 枚" : $"Archive化した画像 {Moved} 枚" };
            if (Missing > 0)
                lines.Add($"Archiveに見つからなかった画像 {Missing} 枚");
            if (PrunedDirectories > 0)
                lines.Add($"削除した空フォルダー {PrunedDirectories} 件");
            if (warnings > 0)
            {
                lines.Add($"確認事項 {warnings} 件");
                lines.AddRange(details);
                if (warnings > details.Count)
                    lines.Add($"ほか {warnings - details.Count} 件");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }
}
