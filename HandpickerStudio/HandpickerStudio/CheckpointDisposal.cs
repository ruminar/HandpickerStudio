using System.Diagnostics;
using System.IO;

namespace HandpickerStudio;

internal static class CheckpointDisposal
{
    public static FileStamp Capture(Checkpoint checkpoint)
    {
        using var suite = new SuiteEvaluation(checkpoint.Project);
        suite.RequireIdle(checkpoint.RelativePath);
        suite.ValidateDisposalHistory();
        if (suite.GetRating(checkpoint.RelativePath) != Rating.Delete)
            throw new IOException("delete評価のCheckpointだけを廃棄できます。");
        return suite.VerifyCheckpoint(checkpoint);
    }

    public static string? Execute(Checkpoint checkpoint, FileStamp expected, Project[] projects, HeldLabel[] holds,
        PreviewCache.RemovalBatch? cleanup, Func<string, string, bool> confirmCopy)
    {
        var project = checkpoint.Project;
        var catalog = CatalogService.ReadCatalog(projects, project, CancellationToken.None);
        if (!catalog.IsComplete || catalog.Labels[checkpoint.SafeName].Any(item => item.Project.Id != project.Id &&
            ArchiveDirectory.ResolveExistingPath(item.FullPath).Equals(ArchiveDirectory.ResolveExistingPath(checkpoint.FullPath),
                StringComparison.OrdinalIgnoreCase)))
            throw new IOException("このCheckpointの参照範囲を一意に確認できません。");

        var held = catalog.ObservedHolds.Concat(holds).DistinctBy(CatalogService.HoldKey).ToArray();
        ImageScan scan = ScanForDisposal();
        foreach (var image in scan.Images.Where(IsOutputImage))
        {
            var root = project.Roots.Single(item => item.Id == image.Root.Id);
            var source = ImageOperations.Capture(project, image);
            if (cleanup is null)
                ImageOperations.Move(source, checkpoint, root, confirmCopy);
            else
                cleanup.AfterRemoving(image.FullPath, () => ImageOperations.Move(source, checkpoint, root, confirmCopy));
        }

        // A fresh scan covers new outputs from other processes and directories that changed during the moves.
        if (ScanForDisposal().Images.Any(IsOutputImage))
            throw new IOException("退避中に対象の画像が追加されました。生成を停止したうえで、もう一度確認してください。");

        var operation = Guid.NewGuid();
        string directory = StageDirectory(checkpoint.RootPath, operation);
        using var process = Process.GetCurrentProcess();
        var record = new DisposalRecord(operation, project.Id, checkpoint.RelativePath, checkpoint.FullPath,
            Path.Combine(directory, Path.GetFileName(checkpoint.FullPath)), "prepared", expected, Environment.MachineName,
            process.Id, process.StartTime.ToUniversalTime().Ticks, RememberForTransfer: true);

        if (project.Roots.Any(root => WorkspacePaths.Overlap(root.OutputPath, directory) || WorkspacePaths.Overlap(root.ArchivePath, directory)))
            throw new IOException("画像の参照範囲外に廃棄用の作業場所を確保してください。");

        using (var suite = new SuiteEvaluation(project))
        {
            suite.RequireIdle(checkpoint.RelativePath);
            suite.ValidateDisposalHistory();
            if (suite.GetRating(checkpoint.RelativePath) != Rating.Delete || suite.VerifyCheckpoint(checkpoint) != expected)
                throw new IOException("画像の退避中に評価またはCheckpointが変わりました。");
            if (!suite.IsOutsideCheckpointRoots(directory))
                throw new IOException("Checkpointの参照範囲外に廃棄用の作業場所を確保してください。");

            ArchiveDirectory.RequireManagedDirectory(Path.GetDirectoryName(directory)!);
            Directory.CreateDirectory(directory);
            using var file = VerifiedFile.Open(checkpoint.FullPath, checkpoint.RootPath, expected, forMove: true);
            suite.WriteRecord(record);
            try
            {
                if (!file.TryRename(record.StagedPath))
                    throw new IOException("同じドライブにCheckpointを一時移動できません。");
            }
            catch
            {
                // A positively identified original with no staged file is sufficient to clear the reservation.
                if (File.Exists(record.OriginalPath) && !File.Exists(record.StagedPath))
                    suite.RemoveRecord(record);
                throw;
            }
        }

        try
        {
            RecycleBin.Send(record.StagedPath, () =>
            {
                using var file = VerifiedFile.Open(record.StagedPath, directory, expected);
            });
        }
        catch (Exception error)
        {
            try
            {
                using var file = VerifiedFile.Open(record.StagedPath, directory, expected, forMove: true);
                if (!file.TryRename(record.OriginalPath))
                    throw new IOException("Checkpointを元の場所に戻せません。");
                using var suite = new SuiteEvaluation(project);
                suite.RemoveRecord(record);
                RemoveEmptyStage(directory);
            }
            catch (Exception recovery)
            {
                throw new IOException($"ごみ箱への移動結果を確定できません。作業場所: {record.StagedPath}{Environment.NewLine}{recovery.Message}", error);
            }
            throw;
        }

        record = record with { Phase = "recycled" };
        using (var suite = new SuiteEvaluation(project))
            suite.WriteRecord(record);
        RemoveEmptyStage(directory);
        return Finish(checkpoint, record, cleanup);

        bool IsOutputImage(ImageEntry image) => !image.FromArchive && image.CheckpointPath == checkpoint.RelativePath;

        ImageScan ScanForDisposal()
        {
            var current = CatalogService.ReadImages(project, catalog, held, CancellationToken.None);
            if (current.Warnings.Length != 0 || current.Images.Any(image => !image.FromArchive && image.Problem is not null &&
                image.PossibleOwners.Contains(checkpoint.RelativePath)))
                throw new IOException("有効な参照ルートまたは対象画像の退避を確認できません。確認事項と所属未確定の画像を確認してください。");
            return current;
        }
    }

    public static string? Resume(Project project, PreviewCache.RemovalBatch? cleanup)
    {
        DisposalRecord[] pending;
        using (var suite = new SuiteEvaluation(project))
            pending = suite.Pending().Where(record => record.ProjectId == project.Id).ToArray();

        List<string> notices = [];
        foreach (var record in pending)
        {
            if (record.Phase == "prepared")
            {
                try
                {
                    RestoreInterrupted(project, record);
                    notices.Add($"途中の廃棄を取り消しました。{record.OriginalPath}");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    notices.Add($"廃棄結果を確認してください。{record.StagedPath}{Environment.NewLine}{error.Message}");
                }
                continue;
            }
            var checkpoint = new Checkpoint(project, record.Key, record.OriginalPath, CatalogService.SafeName(record.Key));
            if (Finish(checkpoint, record, cleanup) is { } warning)
                notices.Add(warning);
        }
        return notices.Count == 0 ? null : string.Join(Environment.NewLine, notices);
    }

    private static void RestoreInterrupted(Project project, DisposalRecord record)
    {
        if (!record.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("廃棄を実行したPCで復旧してください。");
        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks == record.ProcessStarted)
                throw new IOException("廃棄を実行したプロセスが稼働中です。");
        }
        catch (ArgumentException) { }

        string original = record.OriginalPath;
        var folder = project.ModelFolders.FirstOrDefault(folder => WorkspacePaths.Within(original, folder.Path) &&
            Path.GetFullPath(Path.Combine(folder.Path, record.Key)).Equals(original, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            throw new IOException("廃棄時のCheckpoint参照先と現在の設定が異なります。");
        string stageRoot = StageDirectory(folder.Path, record.Id);
        if (!Path.Combine(stageRoot, Path.GetFileName(original)).Equals(record.StagedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("廃棄時のCheckpoint参照先と現在の設定が異なります。");

        using var suite = new SuiteEvaluation(project);
        suite.VerifyRecord(record);
        if (File.Exists(record.StagedPath))
        {
            using var file = VerifiedFile.Open(record.StagedPath, stageRoot, record.Stamp, forMove: true);
            if (!file.TryRename(original))
                throw new IOException("Checkpointを元の場所に戻せません。");
        }
        else
        {
            using var originalFile = VerifiedFile.Open(original, folder.Path, record.Stamp);
        }
        suite.RemoveRecord(record);
        RemoveEmptyStage(stageRoot);
    }

    private static string StageDirectory(string root, Guid operation)
    {
        string parent = Path.GetDirectoryName(root) ?? throw new IOException("同じドライブに作業場所を確保できません。");
        return Path.Combine(parent, ".HandpickerStudio-Recycle", operation.ToString("N"));
    }

    private static string? Finish(Checkpoint checkpoint, DisposalRecord record, PreviewCache.RemovalBatch? cleanup)
    {
        string? notice;
        bool reservationCleared;
        using (var suite = new SuiteEvaluation(checkpoint.Project))
            notice = suite.FinishDisposal(record, out reservationCleared);
        ImageOperations.MarkArchive(checkpoint, cleanup);
        if (reservationCleared)
        {
            using var suite = new SuiteEvaluation(checkpoint.Project);
            suite.RemoveRecord(record);
        }
        return notice;
    }

    private static void RemoveEmptyStage(string directory)
    {
        try { Directory.Delete(directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
