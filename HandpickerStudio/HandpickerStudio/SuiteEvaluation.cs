using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HandpickerStudio;

internal sealed record DisposalRecord(Guid Id, Guid ProjectId, string Key, string OriginalPath, string StagedPath, string Phase,
    FileStamp Stamp, string Machine, int ProcessId, long ProcessStarted);

internal sealed class SuiteEvaluation : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly FileStream writerLock;
    private readonly string databasePath;
    private readonly string queuePath;
    private readonly string recordDirectory;
    private readonly string[] roots;
    private readonly JsonObject database;
    private readonly JsonObject statuses;

    public static string ManifestPath(Project project) => Path.Combine(Path.GetDirectoryName(project.EvaluationPath)!, "checkpoint_studio.json");

    public static bool IsConfigured(Project project) => project.EvaluationPath.Length > 0 && File.Exists(ManifestPath(project));

    public SuiteEvaluation(Project project)
    {
        if (!IsConfigured(project))
            throw new IOException("HandpickerSuiteの連携対応版を起動してから評価を変更してください。");
        databasePath = project.EvaluationPath;
        string dataPath = Path.GetDirectoryName(databasePath)!;
        recordDirectory = Path.Combine(dataPath, "checkpoint_studio_disposals");
        writerLock = Lock(Path.Combine(dataPath, "checkpoint_statuses.lock"));
        try
        {
            var manifest = ReadObject(ManifestPath(project));
            if (manifest["protocolVersion"]?.GetValue<int>() != 1 ||
                manifest["checkpointRoots"] is not JsonArray checkpointRoots || checkpointRoots.Count == 0 ||
                manifest["deleteQueuePath"]?.GetValue<string>() is not { } queue || !Path.IsPathFullyQualified(queue))
                throw new InvalidDataException("HandpickerSuiteの連携情報を確認できません。");
            roots = checkpointRoots.Select(item => WorkspacePaths.Normalize(item!.GetValue<string>())).ToArray();
            queuePath = queue;
            database = ReadObject(databasePath);
            if (database["version"]?.GetValue<int>() != 1 || database["statuses"] is not JsonObject entries)
                throw new InvalidDataException("評価JSONの形式に対応していません。");
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, entry) in entries)
            {
                if (!keys.Add(key) || entry is not JsonObject || !ParseRating(entry["status"]?.GetValue<string>(), out _))
                    throw new InvalidDataException("評価JSONに重複または無効な評価があります。");
            }
            statuses = entries;
        }
        catch
        {
            writerLock.Dispose();
            throw;
        }
    }

    public FileStamp VerifyCheckpoint(Checkpoint checkpoint)
    {
        if (statuses.Any(item => item.Key.Equals(checkpoint.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            item.Key != checkpoint.RelativePath))
            throw new IOException("評価キーの大文字小文字が実ファイルと異なります。");

        using var file = VerifiedFile.Open(checkpoint.FullPath, checkpoint.Project.CheckpointPath);
        int matches = 0;
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                throw new IOException($"SuiteのCheckpoint参照先を確認できません。{root}");
            string candidate = Path.Combine(root, checkpoint.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            FileAttributes attributes;
            try { attributes = File.GetAttributes(candidate); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if (attributes.HasFlag(FileAttributes.Directory))
                continue;
            using var verified = VerifiedFile.Open(candidate, root);
            if (verified.Stamp != file.Stamp || ++matches > 1)
                throw new IOException("Suiteの評価キーが複数または別のCheckpointを指しています。");
        }

        if (matches != 1)
            throw new IOException("SuiteのCheckpoint参照先と一致しません。");
        return file.Stamp;
    }

    public Rating GetRating(string relativePath) =>
        ParseRating(statuses[relativePath]?["status"]?.GetValue<string>(), out Rating current) ? current : Rating.None;

    public string? ChangeRating(Checkpoint checkpoint, Rating rating)
    {
        if (rating == Rating.Unavailable)
            throw new ArgumentException("指定できない評価です。", nameof(rating));
        VerifyCheckpoint(checkpoint);
        RequireIdle(checkpoint.RelativePath);
        if (GetRating(checkpoint.RelativePath) == rating)
            return null;

        CancelReservation(checkpoint.RelativePath, $"studio_status_changed_to_{RatingValue(rating)}");
        UpdateRating(checkpoint, rating);
        return Save();
    }

    private void UpdateRating(Checkpoint checkpoint, Rating rating)
    {
        if (rating == Rating.None)
        {
            statuses.Remove(checkpoint.RelativePath);
            return;
        }

        var entry = statuses[checkpoint.RelativePath] as JsonObject ?? new JsonObject();
        entry["status"] = RatingValue(rating);
        entry["updated_at"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        entry["ckpt_name_safe"] = checkpoint.SafeName;
        if (entry.Parent is null)
            statuses[checkpoint.RelativePath] = entry;
    }

    public void RequireIdle(string relativePath)
    {
        if (Pending().Any(record => record.Key.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("このCheckpointの廃棄結果が確定していません。廃棄記録を確認してください。");
    }

    public bool IsOutsideCheckpointRoots(string path) => roots.All(root => !WorkspacePaths.Overlap(
        ArchiveDirectory.ResolveExistingPath(root), ArchiveDirectory.ResolvePath(path)));

    public DisposalRecord[] Pending()
    {
        ArchiveDirectory.RequireManagedDirectory(recordDirectory);
        if (!Directory.Exists(recordDirectory))
            return [];
        return Directory.EnumerateFiles(recordDirectory, "*.json").Select(path =>
        {
            var record = ReadObject(path).Deserialize<DisposalRecord>(JsonOptions);
            if (record is null || record.Id == Guid.Empty || record.Key is null || record.OriginalPath is null ||
                record.StagedPath is null || string.IsNullOrWhiteSpace(record.Machine) || record.Phase is not ("prepared" or "recycled") ||
                !Path.GetFileName(path).Equals(record.Id.ToString("N") + ".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checkpointの廃棄記録を確認できません。");
            return record;
        }).ToArray();
    }

    public void WriteRecord(DisposalRecord record)
    {
        ArchiveDirectory.RequireManagedDirectory(recordDirectory);
        Directory.CreateDirectory(recordDirectory);
        WriteJson(RecordPath(record), JsonSerializer.SerializeToNode(record, JsonOptions)!);
    }

    public void RemoveRecord(DisposalRecord record) => File.Delete(RecordPath(record));

    public void VerifyRecord(DisposalRecord record)
    {
        if (!Pending().Contains(record))
            throw new IOException("Checkpointの廃棄記録が変更されました。");
    }

    private string RecordPath(DisposalRecord record) => Path.Combine(recordDirectory, record.Id.ToString("N") + ".json");

    public string? FinishDisposal(DisposalRecord record)
    {
        if (record.Phase != "recycled")
            throw new IOException("ごみ箱への移動結果が確定していません。");
        VerifyRecord(record);
        CancelReservation(record.Key, "studio_checkpoint_recycled");
        if (GetRating(record.Key) == Rating.Delete)
            statuses.Remove(record.Key);
        return Save();
    }

    private void CancelReservation(string key, string reason)
    {
        string[] lines;
        try { lines = File.ReadAllLines(queuePath, new UTF8Encoding(false, true)); }
        catch (FileNotFoundException) { return; }
        string? reservationId = null;
        foreach (string line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var entry = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("削除予約情報が破損しています。");
            string? type = entry["type"]?.GetValue<string>();
            string? relpath = entry["ckpt_name_str"]?.GetValue<string>();
            string? id = entry["id"]?.GetValue<string>();
            if (type is not ("reserve" or "cancel") || string.IsNullOrWhiteSpace(id) ||
                (type == "reserve" && string.IsNullOrWhiteSpace(relpath)))
                throw new InvalidDataException("削除予約情報が破損しています。");
            if (type == "reserve" && string.Equals(relpath, key, StringComparison.OrdinalIgnoreCase))
                reservationId = id;
            if (type == "cancel" && id == reservationId &&
                (string.IsNullOrWhiteSpace(relpath) || string.Equals(relpath, key, StringComparison.OrdinalIgnoreCase)))
                reservationId = null;
        }
        if (reservationId is null)
            return;

        var cancel = new { version = 1, type = "cancel", id = reservationId, ckpt_name_str = key,
            cancelled_at = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"), reason };
        using var stream = new FileStream(queuePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(cancel) + "\n"));
        stream.Flush(flushToDisk: true);
    }

    private string? Save()
    {
        WriteJson(databasePath, database);
        try
        {
            var favorite = new JsonObject();
            foreach (var (key, entry) in statuses)
            {
                if (entry?["status"]?.GetValue<string>() != "favorite")
                    continue;
                string updated = entry["updated_at"]?.GetValue<string>() ?? DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");
                favorite[key] = new JsonObject { ["ckpt_name_str"] = key, ["ckpt_name_safe"] = CatalogService.SafeName(key),
                    ["favorited_at"] = updated, ["last_seen_at"] = updated };
            }
            WriteJson(Path.Combine(Path.GetDirectoryName(databasePath)!, "checkpoint_favorites.json"),
                new JsonObject { ["version"] = 1, ["favorites"] = favorite });
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"評価JSONは保存しました。favorite互換データの再生成が必要です。{exception.Message}";
        }
    }

    private static FileStream Lock(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                try { stream.Lock(0, 1); return stream; }
                catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(25); }
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static JsonObject ReadObject(string path)
    {
        using var stream = CatalogService.OpenForReading(path);
        using var document = JsonDocument.Parse(stream);
        ValidateUniqueKeys(document.RootElement);
        return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? throw new InvalidDataException($"JSONが正しくありません。{path}");
    }

    private static void ValidateUniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                    throw new InvalidDataException("JSONに重複キーがあります。");
                ValidateUniqueKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateUniqueKeys(item);
        }
    }

    private static void WriteJson(string path, JsonNode value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { ImageOperations.RemoveTemporary(temporary); }
    }

    internal static bool ParseRating(string? value, out Rating rating)
    {
        rating = value switch
        {
            "god" => Rating.God, "favorite" => Rating.Favorite, "nice" => Rating.Nice, "keep" => Rating.Keep,
            "delete" => Rating.Delete, "none" => Rating.None, _ => Rating.Unavailable
        };
        return rating != Rating.Unavailable;
    }

    private static string RatingValue(Rating rating) => rating.ToString().ToLowerInvariant();

    public void Dispose() => writerLock.Dispose();
}
