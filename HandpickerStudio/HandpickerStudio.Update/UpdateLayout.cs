using System.Text.Json;

namespace HandpickerStudio.Update;

public sealed record InstallationState(string Current, string? Previous = null, string[]? Obsolete = null);
public sealed record PendingUpdate(string Version, string WorkDirectory);

public sealed class UpdateLayout
{
    public const int ApplyExitCode = 73;
    public string Root { get; }
    public string AppDirectory => Path.Combine(Root, "app");
    public string Data => Path.Combine(Root, "data");
    public string Cache => Path.Combine(Root, "cache");
    public string Versions => Path.Combine(AppDirectory, "versions");
    public string UpdateDirectory => Path.Combine(AppDirectory, "update");
    public string Launcher => Path.Combine(Root, "HandpickerStudio.exe");
    public string LauncherBackup => Path.Combine(UpdateDirectory, "previous-launcher.exe");
    public string StatePath => Path.Combine(UpdateDirectory, "installation.json");
    public string PendingPath => Path.Combine(UpdateDirectory, "pending.json");
    public string AttemptPath => Path.Combine(UpdateDirectory, "attempt.json");

    public UpdateLayout(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public string VersionDirectory(string version)
    {
        if (UpdatePackage.ParseVersion(version) is null)
            throw new InvalidDataException("版のディレクトリ名が正しくありません。");
        return Path.Combine(Versions, version);
    }

    public string ApplicationPath(string version) => Path.Combine(VersionDirectory(version), "HandpickerStudio.exe");

    public void EnsureUpdateDirectories()
    {
        RequireDirectory(Root);
        RequireDirectory(AppDirectory);
        RequireDirectory(Versions);
        EnsureDirectory(UpdateDirectory);
    }

    public string NewWorkDirectory()
    {
        EnsureUpdateDirectories();
        string path = Path.Combine(UpdateDirectory, $"stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RequireDirectory(path);
        File.WriteAllText(Path.Combine(path, ".update-work"), Path.GetFileName(path));
        return path;
    }

    public string WorkDirectory(string name)
    {
        if (name.Length != 38 || !name.StartsWith("stage-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[6..], "N", out _))
            throw new InvalidDataException("更新作業ディレクトリの名前が正しくありません。");
        return Path.Combine(UpdateDirectory, name);
    }

    public void DeleteWorkDirectory(string path)
    {
        if (!PathEquals(path, WorkDirectory(Path.GetFileName(path))))
            throw new InvalidDataException("更新作業ディレクトリの範囲が正しくありません。");
        if (!Exists(path))
            return;
        RequireDirectory(path);
        if (!System.Text.Encoding.UTF8.GetString(ReadSmallFile(Path.Combine(path, ".update-work")))
            .Equals(Path.GetFileName(path), StringComparison.Ordinal))
            throw new IOException($"更新処理が作成したフォルダーではありません。{path}");
        DeleteOwnedTree(path);
    }

    public void WriteState(InstallationState state) => WriteAtomic(StatePath, state);

    public void WritePending(PendingUpdate pending)
    {
        if (Exists(AttemptPath))
            throw new IOException("前回の更新の後処理を待っています。HandpickerStudioを起動し直してください。");
        var old = Read<PendingUpdate>(PendingPath);
        WriteAtomic(PendingPath, pending);
        if (old is { WorkDirectory: not null } && old.WorkDirectory != pending.WorkDirectory)
        {
            try { DeleteWorkDirectory(WorkDirectory(old.WorkDirectory)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    public static bool PathEquals(string first, string second) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    public static void RequireDirectory(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException($"通常のフォルダーを指定してください。{path}");
    }

    public static void RequireFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException($"通常のファイルを指定してください。{path}");
    }

    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        RequireDirectory(path);
    }

    public static T? Read<T>(string path)
    {
        if (!Exists(path))
            return default;
        return JsonSerializer.Deserialize<T>(ReadSmallFile(path), UpdatePackage.Json) ??
            throw new InvalidDataException($"更新管理ファイルを読み込めません。{path}");
    }

    public static byte[] ReadSmallFile(string path)
    {
        RequireFile(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024)
            throw new InvalidDataException($"更新管理ファイルが大きすぎます。{path}");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static void WriteAtomic<T>(string path, T data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, UpdatePackage.Json);
                stream.Flush(flushToDisk: true);
            }
            if (Exists(path))
            {
                RequireFile(path);
                File.Replace(temporary, path, null);
            }
            else
                File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    public static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    public static void DeleteOwnedTree(string path)
    {
        if (!Exists(path))
            return;
        RequireDirectory(path);
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"リンクを含む更新フォルダーを削除できません。{child}");
            if ((attributes & FileAttributes.Directory) != 0)
                DeleteOwnedTree(child);
            else
                File.Delete(child);
        }
        Directory.Delete(path, recursive: false);
    }
}
