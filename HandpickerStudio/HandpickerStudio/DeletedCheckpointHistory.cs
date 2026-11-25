using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HandpickerStudio;

internal sealed record DeletedCheckpoint(string FileName, long FileSize);

internal static class DeletedCheckpointHistory
{
    public const string Field = "studio_deleted_checkpoints";
    private static readonly JsonSerializerOptions HistoryOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

    public static DeletedCheckpoint[] ReadProject(JsonElement database, Guid projectId) =>
        database.TryGetProperty(Field, out var history) ? ReadProjectEntries(history, projectId) : [];

    public static DeletedCheckpoint[] ReadTransfer(JsonElement document) =>
        document.TryGetProperty(Field, out var entries) ? ReadEntries(entries) : [];

    public static void ValidateProject(JsonObject database, Guid projectId) => _ = ProjectEntries(database, projectId);

    public static int Remember(JsonObject database, Guid projectId, IReadOnlyCollection<DeletedCheckpoint> deleted)
    {
        if (deleted.Count == 0)
            return 0;
        if (deleted.Any(item => !IsValidIdentity(item.FileName, item.FileSize)))
            throw new InvalidDataException("Checkpointの廃棄履歴に不正なファイル名またはバイト数があります。");

        var existing = ProjectEntries(database, projectId);
        var merged = existing.Concat(deleted).Distinct()
            .OrderBy(item => item.FileName, StringComparer.Ordinal).ThenBy(item => item.FileSize).ToArray();
        int added = merged.Length - existing.Length;
        if (added == 0)
            return 0;

        var history = database[Field] as JsonObject ?? new JsonObject();
        history[projectId.ToString("N")] = JsonSerializer.SerializeToNode(merged, HistoryOptions);
        if (history.Parent is null)
            database[Field] = history;
        return added;
    }

    public static bool TryReadIdentity(JsonElement entry, out DeletedCheckpoint identity)
    {
        identity = null!;
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("file_name", out var fileName) ||
            fileName.ValueKind != JsonValueKind.String || fileName.GetString() is not { } value ||
            !entry.TryGetProperty("file_size", out var fileSize) || fileSize.ValueKind != JsonValueKind.Number ||
            !fileSize.TryGetInt64(out long size) || !IsValidIdentity(value, size))
            return false;
        identity = new(value, size);
        return true;
    }

    private static bool IsValidIdentity(string fileName, long size) =>
        size >= 0 && fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) &&
        fileName.IndexOfAny(InvalidFileNameCharacters) < 0;

    private static DeletedCheckpoint[] ProjectEntries(JsonObject database, Guid projectId) =>
        database.TryGetPropertyValue(Field, out var history)
            ? ReadProjectEntries(JsonSerializer.SerializeToElement(history), projectId) : [];

    private static DeletedCheckpoint[] ReadProjectEntries(JsonElement history, Guid projectId)
    {
        if (history.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Checkpointの廃棄履歴を読み込めません。");
        return history.TryGetProperty(projectId.ToString("N"), out var entries) ? ReadEntries(entries) : [];
    }

    private static DeletedCheckpoint[] ReadEntries(JsonElement entries)
    {
        if (entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Checkpointの廃棄履歴を読み込めません。");

        var deleted = new HashSet<DeletedCheckpoint>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryReadIdentity(entry, out var identity))
                throw new InvalidDataException("Checkpointの廃棄履歴に不正なファイル名またはバイト数があります。");
            deleted.Add(identity);
        }
        return deleted.ToArray();
    }
}
