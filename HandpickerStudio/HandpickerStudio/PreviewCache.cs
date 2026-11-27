using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace HandpickerStudio;

public sealed partial class PreviewCache
{
    private const string CacheNamespace = "HandpickerStudio-previews-v1";
    private const string OwnerFile = ".handpickerstudio-owner";
    public const int ListDecodeLimit = 280;
    public const int TileDecodeLimit = 420;
    private readonly string root;
    private readonly Guid project;
    private readonly Guid owner;
    private readonly SettingsStore settings;
    private volatile bool stopped;

    internal PreviewCache(Project project, Guid owner, SettingsStore settings) =>
        (root, this.project, this.owner, this.settings) = (WorkspacePaths.CacheBase(project), project.Id, owner, settings);

    internal void Stop() => stopped = true;

    internal BitmapSource Decode(string original, int limit, CancellationToken token)
    {
        var version = ImageVersion.Read(original);
        string versionKey = VersionKey(original, version, limit);
        // The path prefix lets cleanup find all versions and sizes of a removed image.
        string key = CacheKey(original, versionKey);
        var cached = stopped ? null : FindPreview(key) ?? FindPreview(versionKey);
        if (cached is not null)
            return cached;

        var bitmap = ImagePreview.Decode(original, limit);
        if (!stopped && !token.IsCancellationRequested)
            TryStore(original, version, key, bitmap, token);
        return bitmap;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string PathKey(string path) => Hash(path.ToUpperInvariant());

    private static string CacheKey(string path, string versionKey) => $"{PathKey(path)}-{versionKey}";

    private static string VersionKey(string path, ImageVersion version, int limit) =>
        Hash($"{path.ToUpperInvariant()}\n{version.Length}\n{version.Created}\n{version.Modified}\n{limit}");

    internal sealed class RemovalBatch(PreviewCache cache)
    {
        private readonly List<RemovedSource> removed = [];
        private readonly List<string> problems = [];

        internal void AfterRemoving(string path, Action remove)
        {
            var original = Capture(path);
            remove();
            removed.Add(original);
        }

        internal void AfterMoving(string path, string destination, string destinationRoot, Func<FileStamp> move)
        {
            var original = Capture(path);
            var stamp = move();
            removed.Add(original with { Destination = new(destination, destinationRoot, stamp) });
        }

        internal void MoveArchiveFolder(string source, string destination)
        {
            List<string> scanProblems = [];
            var originals = CatalogService.EnumerateFiles(source, scanProblems.Add, CancellationToken.None)
                .Where(CatalogService.IsImage).Select(Capture).ToArray();
            Directory.Move(source, destination);
            removed.AddRange(originals);
            problems.AddRange(scanProblems);
        }

        internal string? Complete()
        {
            try
            {
                if (removed.Count != 0)
                    cache.RemoveObsolete(removed, problems);
            }
            catch (Exception error) when (IsCacheError(error)) { problems.Add(error.Message); }

            return problems.Count == 0 ? null : "画像キャッシュの清掃を完了できませんでした。" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Distinct());
        }

        private static RemovedSource Capture(string path)
        {
            try { return new(path, ImageVersion.Read(path)); }
            catch (Exception error) when (IsCacheError(error)) { return new(path, null); }
        }
    }

    private void RemoveObsolete(IEnumerable<RemovedSource> removed, List<string> problems)
    {
        using var mutex = new Mutex(false, MutexName(owner));
        Enter(mutex);
        try
        {
            if (OwnerDirectory(root, owner, create: false) is not { } owned)
                return;
            string directory = Path.Combine(owned, project.ToString("N"));
            if (!IsRegular(directory, directory: true))
                return;

            var obsoleteKeys = new Dictionary<string, ObsoletePreview>(StringComparer.Ordinal);
            foreach (var source in removed)
            {
                try
                {
                    if (!IsMissing(source.Path))
                        continue;
                    string? listKey = null;
                    string? tileKey = null;
                    if (source.Version is { } version)
                    {
                        listKey = VersionKey(source.Path, version, ListDecodeLimit);
                        tileKey = VersionKey(source.Path, version, TileDecodeLimit);
                    }
                    var preview = new ObsoletePreview(source.Destination, listKey, tileKey);
                    obsoleteKeys[PathKey(source.Path)] = preview;
                    if (listKey is not null)
                        obsoleteKeys[listKey] = preview;
                    if (tileKey is not null)
                        obsoleteKeys[tileKey] = preview;
                }
                catch (Exception error) when (IsCacheError(error)) { problems.Add($"{source.Path} {error.Message}"); }
            }
            if (obsoleteKeys.Count == 0)
                return;

            bool? mayTransfer = null;
            foreach (string file in Directory.GetFiles(directory))
            {
                string name = Path.GetFileName(file);
                if (!GeneratedName().IsMatch(name) || !obsoleteKeys.TryGetValue(name[..64], out var preview))
                    continue;
                if (preview.Destination is { } destination && ReadDecodeLimit(name, preview) is { } limit)
                {
                    try
                    {
                        mayTransfer ??= !stopped && settings.CanWritePreviewCache(owner);
                        if (mayTransfer.Value && TransferPreview(file, directory, destination, limit))
                            continue;
                    }
                    catch (Exception error) when (IsCacheError(error)) { problems.Add($"{file} {error.Message}"); }
                }
                try { DeletePreview(file, directory); }
                catch (Exception error) when (IsCacheError(error)) { problems.Add($"{file} {error.Message}"); }
            }
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static int? ReadDecodeLimit(string name, ObsoletePreview preview)
    {
        if (!name.EndsWith(".png", StringComparison.Ordinal))
            return null;
        // Both current and legacy names end in the version hash before the extension.
        var key = name.AsSpan(name.Length - 68, 64);
        if (key.Equals(preview.ListKey, StringComparison.Ordinal))
            return ListDecodeLimit;
        if (key.Equals(preview.TileKey, StringComparison.Ordinal))
            return TileDecodeLimit;
        return null;
    }

    private static bool TransferPreview(string file, string directory, MovedImage destination, int limit)
    {
        using var image = VerifiedFile.Open(destination.Path, destination.Root, destination.Stamp, forMove: true);
        var version = ImageVersion.Read(destination.Path);
        string key = CacheKey(destination.Path, VersionKey(destination.Path, version, limit));
        string newFile = Path.Combine(directory, key + ".png");
        if (IsRegular(newFile, directory: false))
            return false;
        if (!IsRegular(file, directory: false))
            return true;

        using var preview = VerifiedFile.Open(file, directory, forMove: true);
        return preview.TryRename(newFile);
    }

    private static bool IsMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
    }

    private BitmapSource? FindPreview(string key)
    {
        try
        {
            if (OwnerDirectory(root, owner, create: false) is not { } directory)
                return null;
            string projectDirectory = Path.Combine(directory, project.ToString("N"));
            string file = Path.Combine(projectDirectory, key + ".png");
            return IsRegular(projectDirectory, directory: true) && IsRegular(file, directory: false)
                ? ImagePreview.Decode(file, 0) : null;
        }
        catch (Exception error) when (IsCacheError(error)) { return null; }
    }

    private void TryStore(string original, ImageVersion version, string key, BitmapSource bitmap, CancellationToken token)
    {
        try
        {
            using var mutex = new Mutex(false, MutexName(owner));
            Enter(mutex);
            try
            {
                if (stopped || token.IsCancellationRequested || !settings.CanWritePreviewCache(owner) ||
                    ImageVersion.Read(original) != version)
                    return;

                string owned = OwnerDirectory(root, owner, create: true)!;
                string directory = Path.Combine(owned, project.ToString("N"));
                if (!IsRegular(directory, directory: true))
                    Directory.CreateDirectory(directory);

                string file = Path.Combine(directory, key + ".png");
                _ = IsRegular(file, directory: false);
                string temporary = Path.Combine(directory, $"{key}-{Guid.NewGuid():N}.tmp");
                bool created = false;
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        encoder.Save(output);
                    }
                    if (!stopped && !token.IsCancellationRequested)
                        File.Move(temporary, file, overwrite: true);
                }
                finally
                {
                    if (created)
                        File.Delete(temporary);
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception error) when (IsCacheError(error)) { /* The original preview is still usable. */ }
    }

    internal static void Clear(string root, Guid owner, SettingsStore settings)
    {
        using var mutex = new Mutex(false, MutexName(owner));
        Enter(mutex);
        try
        {
            if (settings.CanWritePreviewCache(owner) || OwnerDirectory(root, owner, create: false) is not { } owned)
                return;

            var failures = new List<string>();
            foreach (string directory in Directory.EnumerateDirectories(owned))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                    continue;
                try
                {
                    if (!IsRegular(directory, directory: true))
                        continue;
                    foreach (string file in Directory.EnumerateFiles(directory).Where(file => GeneratedName().IsMatch(Path.GetFileName(file))))
                    {
                        try { DeletePreview(file, directory); }
                        catch (Exception error) when (IsCacheError(error)) { failures.Add(error.Message); }
                    }
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (Exception error) when (IsCacheError(error)) { failures.Add(error.Message); }
            }
            if (failures.Count > 0)
                throw new IOException(string.Join(Environment.NewLine, failures));

            string marker = Path.Combine(owned, OwnerFile);
            if (Directory.EnumerateFileSystemEntries(owned).All(path => path == marker))
            {
                File.Delete(marker);
                Directory.Delete(owned);
            }
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void DeletePreview(string file, string directory)
    {
        if (!IsRegular(file, directory: false))
            return;
        using var verified = VerifiedFile.Open(file, directory, forMove: true);
        verified.DeleteCacheFile();
    }

    private static string? OwnerDirectory(string root, Guid owner, bool create)
    {
        if (!create && !IsRegular(root, directory: true, allowLink: true))
            return null;

        string cache = Path.Combine(ArchiveDirectory.ResolvePath(root), CacheNamespace);
        string owned = Path.Combine(cache, owner.ToString("N"));
        if (!IsRegular(cache, directory: true) && !create)
            return null;
        if (!IsRegular(owned, directory: true) && !create)
            return null;
        if (create)
            Directory.CreateDirectory(owned);

        string marker = Path.Combine(owned, OwnerFile);
        string identity = owner.ToString("N");
        if (IsRegular(marker, directory: false))
        {
            if (File.ReadAllText(marker) == identity)
                return owned;
        }
        else if (create && !Directory.EnumerateFileSystemEntries(owned).Any())
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(identity);
            return owned;
        }
        else if (!create && !Directory.EnumerateFileSystemEntries(owned).Any())
        {
            return null;
        }

        throw new IOException($"画像キャッシュの管理フォルダーを確認できません。{owned}");
    }

    private static bool IsRegular(string path, bool directory, bool allowLink = false)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        if ((!allowLink && attributes.HasFlag(FileAttributes.ReparsePoint)) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new IOException($"画像キャッシュのパスを確認できません。{path}");
        return true;
    }

    private static string MutexName(Guid owner) => $@"Local\HandpickerStudio.PreviewCache.{owner:N}";

    private static void Enter(Mutex mutex)
    {
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
                throw new IOException("画像キャッシュは別の処理で使用中です。");
        }
        catch (AbandonedMutexException) { /* Ownership transfers to this process. */ }
    }

    internal static bool IsCacheError(Exception error) => error is IOException or UnauthorizedAccessException or
        JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException or COMException;

    [GeneratedRegex(@"\A(?:[0-9A-F]{64}-)?[0-9A-F]{64}(?:\.png|-[0-9a-f]{32}\.tmp)\z", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedName();

    private readonly record struct RemovedSource(string Path, ImageVersion? Version, MovedImage? Destination = null);
    private readonly record struct MovedImage(string Path, string Root, FileStamp Stamp);
    private readonly record struct ObsoletePreview(MovedImage? Destination, string? ListKey, string? TileKey);

    private readonly record struct ImageVersion(long Length, long Created, long Modified)
    {
        public static ImageVersion Read(string path)
        {
            var file = new FileInfo(path);
            return new(file.Length, file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks);
        }
    }
}
