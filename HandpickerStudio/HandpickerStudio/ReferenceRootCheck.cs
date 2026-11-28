using System.IO;

namespace HandpickerStudio;

internal sealed record ReferenceRootConflict(Guid OutputId, Guid? OtherId, string Message)
{
    public bool Affects(Guid rootId) => rootId == OutputId || rootId == OtherId;
}

internal sealed class ReferenceRootCheck
{
    public List<ReferenceRootConflict> Conflicts { get; } = [];
    public Dictionary<Guid, string> OutputErrors { get; } = [];
    public Dictionary<Guid, string> ArchiveErrors { get; } = [];

    public static ReferenceRootCheck Inspect(Project project, CancellationToken token, bool includeDisabledOutputs = false)
    {
        var check = new ReferenceRootCheck();
        var outputs = new List<(ImageRoot Root, string Path)>();
        foreach (var root in project.Roots.Where(root => includeDisabledOutputs || root.Enabled))
        {
            try { outputs.Add((root, ArchiveDirectory.ResolvePhysicalPath(root.OutputPath, token))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                check.OutputErrors.Add(root.Id, $"「{root.Name}」のOutputを確認できません。{root.OutputPath}\n{error.Message}");
            }
        }

        var storage = new Dictionary<string, (string? PhysicalPath, string? Error)>(StringComparer.OrdinalIgnoreCase);
        var archives = new List<(ImageRoot Root, string Path)>();
        foreach (var root in project.Roots.Where(root => root.Enabled || outputs.Count > 0))
        {
            var (path, error) = ResolveStorage(root.ArchivePath);
            if (path is not null)
                archives.Add((root, path));
            else if (root.Enabled)
                check.ArchiveErrors.Add(root.Id, $"「{root.Name}」のArchiveを確認できません。{root.ArchivePath}\n{error}");
        }

        string cachePath = WorkspacePaths.CacheBase(project);
        string? physicalCache = outputs.Count == 0 ? null : ResolveStorage(cachePath).PhysicalPath;
        for (int index = 0; index < outputs.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var (output, outputPath) = outputs[index];
            string source = $"Output「{output.Name}」 {output.OutputPath}";
            for (int otherIndex = index + 1; otherIndex < outputs.Count; otherIndex++)
            {
                var (other, otherPath) = outputs[otherIndex];
                if (WorkspacePaths.Overlap(outputPath, otherPath))
                    AddConflict(other.Id, $"Output「{other.Name}」 {other.OutputPath}");
            }

            foreach (var (archive, archivePath) in archives)
            {
                if (WorkspacePaths.Overlap(outputPath, archivePath))
                    AddConflict(archive.Id, $"Archive「{archive.Name}」 {archive.ArchivePath}");
            }

            if (physicalCache is not null && WorkspacePaths.Overlap(outputPath, physicalCache))
                AddConflict(null, $"cache {cachePath}");

            void AddConflict(Guid? otherId, string destination) =>
                check.Conflicts.Add(new(output.Id, otherId, $"参照先の実体が重複しています。\n{source}\n{destination}"));
        }
        return check;

        (string? PhysicalPath, string? Error) ResolveStorage(string path)
        {
            if (!storage.TryGetValue(path, out var result))
            {
                try { result = (ArchiveDirectory.ResolvePhysicalPath(path, token, allowMissing: true), null); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    result = (null, error.Message);
                }
                storage.Add(path, result);
            }
            return result;
        }
    }
}
