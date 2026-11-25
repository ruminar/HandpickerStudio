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
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{original.ToUpperInvariant()}\n{version.Length}\n{version.Created}\n{version.Modified}\n{limit}")));
        var cached = stopped ? null : FindPreview(key);
        if (cached is not null)
            return cached;

        var bitmap = ImagePreview.Decode(original, limit);
        if (!stopped && !token.IsCancellationRequested)
            TryStore(original, version, key, bitmap, token);
        return bitmap;
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
                        try
                        {
                            if (IsRegular(file, directory: false))
                            {
                                using var verified = VerifiedFile.Open(file, directory, forMove: true);
                                verified.DeleteCacheFile();
                            }
                        }
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

    [GeneratedRegex(@"\A[0-9A-F]{64}(?:\.png|-[0-9a-f]{32}\.tmp)\z", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedName();

    private readonly record struct ImageVersion(long Length, long Created, long Modified)
    {
        public static ImageVersion Read(string path)
        {
            var file = new FileInfo(path);
            return new(file.Length, file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks);
        }
    }
}
