using System.IO;
using System.Text.Json;

namespace HandpickerStudio;

internal static partial class ArchiveHistory
{
    private const int DirectoryNotEmpty = unchecked((int)0x80070091);

    private sealed record CleanupSource(string Directory, string Root, string PhysicalRoot, string? CheckpointPath = null);

    private static void PruneEmptyDirectories(HashSet<CleanupSource> sources, IEnumerable<OutputFolder> outputRoots, Result result)
    {
        var protectedRoots = outputRoots.SelectMany(root => new[] { root.Location, root.PhysicalPath })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            string directory = source.Directory;
            try
            {
                while (!directory.Equals(source.Root, StringComparison.OrdinalIgnoreCase) && WorkspacePaths.Within(directory, source.Root))
                {
                    var attributes = File.GetAttributes(directory);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                        break;
                    string expected = Path.Combine(source.PhysicalRoot, Path.GetRelativePath(source.Root, directory));
                    if (protectedRoots.Contains(directory) || protectedRoots.Contains(expected) ||
                        !CatalogService.ResolveExistingDirectory(directory).Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                        protectedRoots.Contains(ArchiveDirectory.ResolvePhysicalPath(directory, CancellationToken.None)))
                        break;

                    string parent = Path.GetDirectoryName(directory)!;
                    string[] entries = Directory.EnumerateFileSystemEntries(directory).Take(2).ToArray();
                    bool removed = entries.Length == 0 ? DeleteEmptyDirectory(directory) :
                        entries.Length == 1 && source.CheckpointPath is { } checkpoint &&
                        parent.Equals(source.Root, StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(directory).StartsWith('★') &&
                        Path.GetFileName(entries[0]).Equals(ArchiveDirectory.CheckpointOwnerFileName, StringComparison.OrdinalIgnoreCase) &&
                        DeleteEmptyCheckpointDirectory(directory, source.Root, entries[0], checkpoint);
                    if (!removed)
                        break;
                    result.PrunedDirectories++;
                    directory = parent;
                }
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (IsFileError(error))
            {
                result.Report($"空フォルダーを削除できません。{directory}{Environment.NewLine}{error.Message}");
            }
        }
    }

    private static bool DeleteEmptyDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: false); return true; }
        catch (IOException error) when (error.HResult == DirectoryNotEmpty) { return false; }
    }

    private static bool DeleteEmptyCheckpointDirectory(string directory, string root, string ownerPath, string checkpoint)
    {
        if (File.GetAttributes(ownerPath).HasFlag(FileAttributes.ReparsePoint))
            return false;
        using var ownerFile = VerifiedFile.Open(ownerPath, directory, forMove: true);
        using var stream = ownerFile.OpenStream();
        using var owner = JsonDocument.Parse(stream);
        if (owner.RootElement.ValueKind != JsonValueKind.Object || owner.RootElement.EnumerateObject().Count() != 1 ||
            !owner.RootElement.TryGetProperty("checkpointRelativePath", out var key) ||
            key.ValueKind != JsonValueKind.String || key.GetString() != checkpoint)
            return false;

        // Keep the owner document available if another writer adds a file to the folder during cleanup.
        string backup = Path.Combine(root, $".checkpoint-{Guid.NewGuid():N}.tmp");
        if (!ownerFile.TryRename(backup))
            throw new IOException($"Archiveの管理ファイルを退避できません。{ownerPath}");
        bool removed = false;
        try
        {
            removed = DeleteEmptyDirectory(directory);
            if (removed)
                ownerFile.Delete($"Archiveの管理ファイルを削除できません。{backup}{Environment.NewLine}");
            return removed;
        }
        finally
        {
            if (!removed)
            {
                try
                {
                    if (!ownerFile.TryRename(ownerPath))
                        throw new IOException(ownerPath);
                }
                catch (IOException error)
                {
                    throw new IOException($"Archiveの管理ファイルを元に戻せません。退避先 {backup}", error);
                }
            }
        }
    }
}
