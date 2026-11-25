using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace HandpickerStudio;

internal sealed record ImageTarget(ImageEntry Image, string RootPath, FileStamp Stamp);

internal static class ImageOperations
{
    private static readonly string[] ThumbnailExtensions = [".jpeg", ".jpg", ".png", ".webp"];
    private static readonly string[] ThumbnailSuffixes = [".thumbnail", ".thumb", ".preview"];

    public static ImageTarget Capture(Project project, ImageEntry image)
    {
        var root = project.Roots.SingleOrDefault(item => item.Id == image.Root.Id && item.Enabled) ??
            throw new IOException("画像の参照ルートが無効です。");
        string rootPath = image.FromArchive ? ArchiveDirectory.GetPath(project.Id, root) : root.OutputPath;
        if (!Path.GetFullPath(Path.Combine(rootPath, image.RelativePath)).Equals(image.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("画像の参照先が変わりました。");
        using var file = VerifiedFile.Open(image.FullPath, rootPath);
        return new(image, rootPath, file.Stamp);
    }

    public static void Recycle(ImageTarget target)
    {
        if (target.Image.CheckpointPath is null)
            throw new IOException("所属未確定の画像は振り分けを先に行ってください。");
        RecycleBin.Send(target.Image.FullPath, () =>
        {
            using var file = VerifiedFile.Open(target.Image.FullPath, target.RootPath, target.Stamp);
        });
    }

    public static void Move(ImageTarget source, Checkpoint checkpoint, ImageRoot destinationRoot)
    {
        string checkpointFolder = PrepareArchive(checkpoint, destinationRoot);
        string relativePath = source.Image.RelativePath;
        int slash = relativePath.IndexOf('/');
        if (source.Image.FromArchive && slash >= 0)
            relativePath = relativePath[(slash + 1)..];

        string destination = Path.GetFullPath(Path.Combine(checkpointFolder, relativePath));
        if (!WorkspacePaths.Within(destination, checkpointFolder) || destination.Equals(checkpointFolder, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Archiveの保存先が正しくありません。");
        CreateBelow(checkpointFolder, Path.GetDirectoryName(destination)!);

        using var file = VerifiedFile.Open(source.Image.FullPath, source.RootPath, source.Stamp, forMove: true);
        if (file.TryRename(destination))
            return;

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var input = file.OpenStream();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
                if (output.Length != file.Stamp.Length)
                    throw new IOException("コピーした画像のサイズが元画像と一致しません。");
            }
            File.Move(temporary, destination);
            file.RemoveAfterCopy();
        }
        finally { RemoveTemporary(temporary); }
    }

    public static string SetThumbnail(ImageTarget source, Checkpoint checkpoint)
    {
        checkpoint.RequireUniquePath();
        if (source.Image.CheckpointPath != checkpoint.RelativePath)
            throw new IOException("選んだ画像の所属を確認してください。");
        using var model = VerifiedFile.Open(checkpoint.FullPath, checkpoint.RootPath);
        SaveThumbnail(checkpoint, model.Stamp, EncodeThumbnail(source), overwrite: true);
        return Path.ChangeExtension(checkpoint.FullPath, ".jpg");
    }

    public static string RegisterMissingThumbnails(Project project, IEnumerable<Checkpoint> checkpoints, IEnumerable<ImageEntry> images)
    {
        var byCheckpoint = images.Where(image => image.CheckpointPath is not null && image.Problem is null)
            .ToLookup(image => image.CheckpointPath, StringComparer.Ordinal);
        int registered = 0, existing = 0, noImages = 0, ambiguous = 0;
        List<string> errors = [];
        foreach (var checkpoint in checkpoints)
        {
            if (checkpoint.SharedRelativePath)
            {
                ambiguous++;
                continue;
            }

            try
            {
                if (HasThumbnail(checkpoint))
                {
                    existing++;
                    continue;
                }

                var candidates = byCheckpoint[checkpoint.RelativePath];
                if (!candidates.Any())
                {
                    noImages++;
                    continue;
                }

                using var model = VerifiedFile.Open(checkpoint.FullPath, checkpoint.RootPath);
                var encoder = EncodeFirstThumbnail(project, candidates);
                if (SaveThumbnail(checkpoint, model.Stamp, encoder, overwrite: false))
                    registered++;
                else
                    existing++;
            }
            catch (Exception error) when (MainWindow.IsOperationError(error))
            {
                errors.Add($"{checkpoint.RelativePath}{Environment.NewLine}{error.Message}");
            }
        }

        string counts = $"登録 {registered} 件 / 設定済み {existing} 件 / 候補画像なし {noImages} 件 / 参照先の重複 {ambiguous} 件 / 失敗 {errors.Count} 件";
        return errors.Count == 0 ? counts : counts + Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine + Environment.NewLine, errors);
    }

    private static JpegBitmapEncoder EncodeFirstThumbnail(Project project, IEnumerable<ImageEntry> candidates)
    {
        Exception? firstError = null;
        foreach (var image in candidates.OrderByDescending(image => image.FromArchive)
            .ThenBy(image => image.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(image => image.FullPath, StringComparer.Ordinal))
        {
            try { return EncodeThumbnail(Capture(project, image)); }
            catch (Exception error) when (MainWindow.IsOperationError(error)) { firstError ??= error; }
        }
        throw new IOException($"候補画像を読み込めません。{Environment.NewLine}{firstError?.Message}", firstError);
    }

    private static JpegBitmapEncoder EncodeThumbnail(ImageTarget source)
    {
        using var file = VerifiedFile.Open(source.Image.FullPath, source.RootPath, source.Stamp);
        using var input = file.OpenStream();
        var bitmap = ImagePreview.Decode(input, 512);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return encoder;
    }

    private static bool SaveThumbnail(Checkpoint checkpoint, FileStamp modelStamp, JpegBitmapEncoder encoder, bool overwrite)
    {
        string destination = Path.ChangeExtension(checkpoint.FullPath, ".jpg");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
                output.Flush(flushToDisk: true);
            }
            using (VerifiedFile.Open(checkpoint.FullPath, checkpoint.RootPath, modelStamp)) { }
            if (!overwrite && HasThumbnail(checkpoint))
                return false;
            if (overwrite && File.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("リンクになっている代表サムネイルは置き換えられません。");
            try { File.Move(temporary, destination, overwrite); }
            catch (IOException) when (!overwrite && Path.Exists(destination)) { return false; }
            return true;
        }
        finally { RemoveTemporary(temporary); }
    }

    private static bool HasThumbnail(Checkpoint checkpoint)
    {
        foreach (string path in ThumbnailCandidates(checkpoint.FullPath))
        {
            try
            {
                _ = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { }
        }
        return false;
    }

    public static BitmapSource? ReadThumbnail(Checkpoint checkpoint, CancellationToken token)
    {
        if (checkpoint.SharedRelativePath)
            return null;

        foreach (string candidate in ThumbnailCandidates(checkpoint.FullPath))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(candidate))
                continue;

            try
            {
                using var file = VerifiedFile.Open(candidate, checkpoint.RootPath);
                using var input = file.OpenStream();
                return ImagePreview.Decode(input, 320);
            }
            catch (Exception error) when (MainWindow.IsOperationError(error)) { }
        }
        return null;
    }

    private static IEnumerable<string> ThumbnailCandidates(string modelPath)
    {
        string stem = Path.ChangeExtension(modelPath, null);
        yield return stem + ".jpg";
        foreach (string extension in ThumbnailExtensions)
        {
            if (extension != ".jpg")
                yield return stem + extension;
            yield return modelPath + extension;
            foreach (string prefix in new[] { stem, modelPath })
            {
                foreach (string suffix in ThumbnailSuffixes)
                    yield return prefix + suffix + extension;
            }
        }
    }

    public static IEnumerable<string> FindArchiveFolders(Checkpoint checkpoint, ImageRoot root)
    {
        string archive = ArchiveDirectory.GetPath(checkpoint.Project.Id, root);
        ArchiveDirectory.RequireManagedDirectory(Path.GetDirectoryName(archive)!);
        ArchiveDirectory.RequireManagedDirectory(archive);
        if (!Directory.Exists(archive))
            yield break;

        foreach (var folder in new DirectoryInfo(archive).EnumerateDirectories())
        {
            if (folder.Name.StartsWith('★') || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            string ownerPath = Path.Combine(folder.FullName, "_checkpoint.json");
            if (!File.Exists(ownerPath) || File.GetAttributes(ownerPath).HasFlag(FileAttributes.ReparsePoint))
                continue;
            using var stream = CatalogService.OpenForReading(ownerPath);
            using var owner = JsonDocument.Parse(stream);
            if (owner.RootElement.ValueKind == JsonValueKind.Object &&
                owner.RootElement.TryGetProperty("checkpointRelativePath", out var relativePath) &&
                relativePath.ValueKind == JsonValueKind.String && relativePath.GetString() == checkpoint.RelativePath)
                yield return folder.FullName;
        }
    }

    public static void MarkArchive(Checkpoint checkpoint)
    {
        foreach (var root in checkpoint.Project.Roots.Where(root => root.Enabled))
        {
            foreach (string folder in FindArchiveFolders(checkpoint, root).ToArray())
            {
                string destination = Path.Combine(Path.GetDirectoryName(folder)!, "★" + Path.GetFileName(folder));
                if (Path.Exists(destination))
                    destination += "-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(folder, destination);
            }
        }
    }

    private static string PrepareArchive(Checkpoint checkpoint, ImageRoot root)
    {
        checkpoint.RequireUniquePath();
        if (!root.Enabled || !checkpoint.Project.Roots.Contains(root) || !Directory.Exists(root.OutputPath))
            throw new IOException("接続中で有効な参照ルートを選んでください。");
        using (VerifiedFile.Open(checkpoint.FullPath, checkpoint.RootPath)) { }
        string archive = ArchiveDirectory.GetPath(checkpoint.Project.Id, root);
        ArchiveDirectory.EnsureExists(root, archive, CancellationToken.None);
        string? existing = FindArchiveFolders(checkpoint, root).FirstOrDefault();
        if (existing is not null)
            return existing;

        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkpoint.RelativePath)))[..16];
        string safeName = checkpoint.SafeName[..Math.Min(checkpoint.SafeName.Length, 100)];
        string folder = Path.Combine(archive, $"{safeName}-{suffix}");
        ArchiveDirectory.RequireManagedDirectory(folder);
        Directory.CreateDirectory(folder);
        string ownerPath = Path.Combine(folder, "_checkpoint.json");
        string temporary = ownerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, new { checkpointRelativePath = checkpoint.RelativePath });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, ownerPath);
        }
        catch (IOException) when (FindArchiveFolders(checkpoint, root).Contains(folder, StringComparer.OrdinalIgnoreCase)) { }
        finally { RemoveTemporary(temporary); }

        return folder;
    }

    private static void CreateBelow(string root, string directory)
    {
        string current = root;
        foreach (string segment in Path.GetRelativePath(root, directory).Split(Path.DirectorySeparatorChar))
        {
            if (segment == ".")
                continue;
            current = Path.Combine(current, segment);
            ArchiveDirectory.RequireManagedDirectory(current);
            Directory.CreateDirectory(current);
        }
    }

    internal static void RemoveTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
