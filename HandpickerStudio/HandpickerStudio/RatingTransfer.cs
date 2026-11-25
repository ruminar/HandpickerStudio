using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandpickerStudio;

internal sealed record RatingTransferEntry(string FileName, long FileSize, string Tag);
internal sealed record RatingTransferDocument(int FormatVersion, DateTimeOffset ExportedAt, RatingTransferEntry[] Evaluations,
    [property: JsonPropertyName(DeletedCheckpointHistory.Field)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeletedCheckpoint[]? Deleted);
internal sealed record RatingImportChange(Checkpoint Checkpoint, FileStamp Stamp, Rating Before, Rating After);
internal sealed record RatingImportPlan(RatingImportChange[] Changes, int Unchanged, int Missing, int Ambiguous, int Skipped,
    DeletedCheckpoint[] Deleted)
{
    public int Overwrites => Changes.Count(change => change.Before != Rating.None);
}

internal static class RatingTransfer
{
    private static readonly JsonSerializerOptions FileOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static string SuggestedName(Project project, DateTime date)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new(project.Name.Select(value => invalid.Contains(value) ? '_' : value).Take(100).ToArray());
        name = name.Trim().TrimEnd('.');
        return $"{(name.Length == 0 ? "checkpoint_tags" : name)}_{date:yyyyMMdd_HHmmss}.json";
    }

    public static string Export(Project project, string path)
    {
        if (project.EvaluationPath.Length == 0)
            throw new IOException("評価JSONが未指定です。プロジェクト設定から指定できます。");

        var (ratings, deleted) = ReadExportSnapshot(project);
        var models = ratings.Length == 0 ? [] : ReadModels(project);
        var byPath = models.ToLookup(item => item.Model.RelativePath, StringComparer.OrdinalIgnoreCase);
        var exported = new List<RatingTransferEntry>();
        int skipped = 0;
        foreach (var (key, rating) in ratings)
        {
            var candidates = byPath[key].ToArray();
            if (candidates.Length != 1 || candidates[0].Model.RelativePath != key)
            {
                skipped++;
                continue;
            }
            var identity = candidates[0].Identity;
            exported.Add(new(identity.Name, identity.Size, SuiteEvaluation.RatingValue(rating)));
        }

        List<RatingTransferEntry> entries = [];
        foreach (var group in exported.GroupBy(item => (item.FileName, item.FileSize)))
        {
            if (group.Select(item => item.Tag).Distinct().Skip(1).Any())
                skipped += group.Count();
            else
                entries.Add(group.First());
        }
        var liveIdentities = exported.Select(item => (item.FileName, item.FileSize)).ToHashSet();
        entries.AddRange(deleted.Where(item => !liveIdentities.Contains((item.FileName, item.FileSize)))
            .Select(item => new RatingTransferEntry(item.FileName, item.FileSize, "delete")));

        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var document = new RatingTransferDocument(1, DateTimeOffset.Now, entries.ToArray(), deleted.Length > 0 ? deleted : null);
                JsonSerializer.Serialize(stream, document, FileOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
        return $"書き出し {entries.Count} 件 / 廃棄履歴 {deleted.Length} 件 / スキップ {skipped} 件{Environment.NewLine}{path}";
    }

    private static (KeyValuePair<string, Rating>[] Ratings, DeletedCheckpoint[] Deleted) ReadExportSnapshot(Project project)
    {
        using var stream = CatalogService.OpenForReading(project.EvaluationPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var ratings = CatalogService.ParseRatings(root).Where(item => item.Value != Rating.None).ToArray();
        return (ratings, DeletedCheckpointHistory.ReadProject(root, project.Id));
    }

    public static RatingImportPlan Preview(Project project, string path)
    {
        using var input = CatalogService.OpenForReading(path);
        using var json = JsonDocument.Parse(input);
        var document = json.RootElement;
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("format_version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format) || format != 1 ||
            !document.TryGetProperty("evaluations", out var evaluations) || evaluations.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("HandpickerSuite互換の評価JSON（format_version 1）を指定してください。");

        var deleted = DeletedCheckpointHistory.ReadTransfer(document);
        int skipped = 0, missing = 0, ambiguous = 0, unchanged = 0;
        var entries = new List<((string Name, long Size) Identity, Rating Rating)>();
        foreach (var evaluation in evaluations.EnumerateArray())
        {
            if (TryReadEntry(evaluation, out var identity, out Rating rating))
                entries.Add(((identity.FileName, identity.FileSize), rating));
            else
                skipped++;
        }

        var models = ReadModels(project);
        var byIdentity = models.ToLookup(item => item.Identity);
        var byPath = models.ToLookup(item => item.Model.RelativePath, StringComparer.OrdinalIgnoreCase);
        using var suite = new SuiteEvaluation(project);
        var changes = new List<RatingImportChange>();
        foreach (var entry in entries.GroupBy(item => item.Identity))
        {
            var tags = entry.Select(item => item.Rating).Distinct().ToArray();
            var candidates = byIdentity[entry.Key].ToArray();
            if (tags.Length != 1 || candidates.Length > 1)
            {
                ambiguous++;
                continue;
            }
            if (candidates.Length == 0)
            {
                missing++;
                continue;
            }
            var checkpoint = candidates[0].Model;
            if (byPath[checkpoint.RelativePath].Skip(1).Any())
            {
                ambiguous++;
                continue;
            }

            try
            {
                suite.RequireIdle(checkpoint.RelativePath);
                var stamp = suite.VerifyCheckpoint(checkpoint);
                if (stamp.Length != entry.Key.Size)
                {
                    skipped++;
                    continue;
                }
                Rating current = suite.GetRating(checkpoint.RelativePath);
                if (current == tags[0])
                    unchanged++;
                else
                    changes.Add(new(checkpoint, stamp, current, tags[0]));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(changes.ToArray(), unchanged, missing, ambiguous, skipped, deleted);
    }

    public static string Import(Project project, RatingImportPlan plan)
    {
        var models = plan.Changes.Length == 0 ? [] : ReadModels(project);
        var byIdentity = models.ToLookup(item => item.Identity);
        var byPath = models.ToLookup(item => item.Model.RelativePath, StringComparer.OrdinalIgnoreCase);
        using var suite = new SuiteEvaluation(project);
        var accepted = new List<RatingImportChange>();
        foreach (var change in plan.Changes)
        {
            var checkpoint = change.Checkpoint;
            var key = (Path.GetFileName(checkpoint.RelativePath), change.Stamp.Length);
            var candidates = byIdentity[key].ToArray();
            if (candidates.Length != 1 || candidates[0].Model.FullPath != checkpoint.FullPath ||
                byPath[checkpoint.RelativePath].Skip(1).Any())
                continue;
            if (suite.GetRating(checkpoint.RelativePath) != change.Before)
                continue;

            try
            {
                suite.RequireIdle(checkpoint.RelativePath);
                if (suite.VerifyCheckpoint(checkpoint) == change.Stamp)
                    accepted.Add(change);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        string? notice = suite.SaveImportedRatings(accepted, plan.Deleted, out int historyAdded);
        string result = $"適用 {accepted.Count} 件 / 変更なし {plan.Unchanged} 件 / 一致なし {plan.Missing} 件 / " +
            $"複数一致・競合 {plan.Ambiguous} 件 / スキップ {plan.Skipped + plan.Changes.Length - accepted.Count} 件" +
            (plan.Deleted.Length == 0 ? "" : $"{Environment.NewLine}廃棄履歴に追加 {historyAdded} 件");
        return notice is null ? result : $"{result}{Environment.NewLine}{notice}";
    }

    private static ((string Name, long Size) Identity, Checkpoint Model)[] ReadModels(Project project)
    {
        var checkpoints = CatalogService.ReadCheckpoints(project, error => throw new IOException($"Checkpointをすべて確認できません。{error}"),
            CancellationToken.None);
        return checkpoints.Select(checkpoint =>
            ((Path.GetFileName(checkpoint.RelativePath), new FileInfo(checkpoint.FullPath).Length), checkpoint)).ToArray();
    }

    private static bool TryReadEntry(JsonElement entry, out DeletedCheckpoint identity, out Rating rating)
    {
        rating = Rating.None;
        return DeletedCheckpointHistory.TryReadIdentity(entry, out identity) && entry.TryGetProperty("tag", out var tag) &&
            tag.ValueKind == JsonValueKind.String && SuiteEvaluation.ParseRating(tag.GetString(), out rating) && rating != Rating.None;
    }
}
