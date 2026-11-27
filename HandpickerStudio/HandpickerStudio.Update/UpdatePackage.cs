using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HandpickerStudio.Update;

public sealed record UpdateProfile(string Repository, string AssetPrefix, bool Test, Dictionary<string, string> PublicKeys)
{
    public static UpdateProfile? Load(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream("HandpickerStudio.UpdateProfile.json");
        return stream is null ? null : JsonSerializer.Deserialize<UpdateProfile>(stream, UpdatePackage.Json);
    }

    public string AssetName(string version) => $"{AssetPrefix}-{version}-win-x64.zip";
}

public sealed record UpdateManifest(string Product, string Repository, string Version, string Runtime, string Asset,
    long Size, string Sha256);

public sealed record UpdateSignature(string Key, string Signature);

public static partial class UpdatePackage
{
    public const string ManifestName = "update-manifest.json";
    public const string SignatureName = "update-signatures.json";
    public const long MaximumZipBytes = 256L * 1024 * 1024;
    private const long MaximumExtractedBytes = 1024L * 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"\A(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\z")]
    private static partial Regex StableVersion();

    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();

    public static Version? ParseVersion(string? value) =>
        value is not null && StableVersion().IsMatch(value) && Version.TryParse(value, out var version) ? version : null;

    public static string CurrentVersion(Assembly assembly) => assembly.GetName().Version!.ToString(3);

    public static string Hash(Stream file) => Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();

    public static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Hash(file);
    }

    public static UpdateManifest ReadManifest(byte[] bytes, byte[] signatures, UpdateProfile profile, string expectedVersion)
    {
        var proofs = JsonSerializer.Deserialize<UpdateSignature[]>(signatures, Json) ?? [];
        if (proofs.Length > 8 || !proofs.Any(proof => IsTrustedSignature(proof, bytes, profile)))
            throw new InvalidDataException("更新マニフェストの署名を検証できません。");

        var value = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json);
        if (value is not { Product: "HandpickerStudio", Runtime: "win-x64", Size: > 0 and <= MaximumZipBytes } ||
            ParseVersion(value.Version) is null || value.Version != expectedVersion ||
            !string.Equals(value.Repository, profile.Repository, StringComparison.OrdinalIgnoreCase) ||
            value.Asset != profile.AssetName(expectedVersion) ||
            value.Sha256 is not { Length: 64 } || !value.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新マニフェストと配布対象が一致しません。");
        return value;
    }

    private static bool IsTrustedSignature(UpdateSignature? proof, byte[] bytes, UpdateProfile profile)
    {
        if (proof?.Key is null || proof.Signature is null || !profile.PublicKeys.TryGetValue(proof.Key, out string? publicKey))
            return false;
        try
        {
            using var rsa = RSA.Create();
            byte[] key = Convert.FromBase64String(publicKey);
            rsa.ImportSubjectPublicKeyInfo(key, out int consumed);
            return consumed == key.Length && rsa.KeySize >= 2048 &&
                rsa.VerifyData(bytes, Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception error) when (error is FormatException or CryptographicException) { return false; }
    }

    public static void VerifyZip(Stream stream, UpdateManifest manifest)
    {
        if (stream.Length != manifest.Size || !Hash(stream).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新ZIPのサイズまたはSHA-256が一致しません。");
        stream.Position = 0;
    }

    public static void Extract(Stream stream, string destination, string version)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is < 1 or > 4096)
            throw new InvalidDataException("更新ZIPの項目数が上限を超えています。");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefix = $"app/versions/{version}/";
        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "HandpickerStudio.exe", prefix + "HandpickerStudio.exe", prefix + "HandpickerStudio.dll",
            prefix + "HandpickerStudio.runtimeconfig.json", prefix + "HandpickerStudio.deps.json", prefix + ".package-files.json",
            "README.md", "LICENSE", "ThirdPartyNotices.txt"
        };
        long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            bool directory = name.EndsWith('/');
            string relative = directory ? name[..^1] : name;
            ValidateEntry(entry, relative);
            bool code = name.StartsWith(prefix, StringComparison.Ordinal);
            bool allowedDirectory = directory && (code || prefix.StartsWith(name, StringComparison.Ordinal));
            if (!allowedDirectory && (directory || (!code && !required.Contains(name))))
                throw new InvalidDataException($"更新ZIPに想定外のファイルがあります。{name}");
            if (code && !directory && IsUserData(relative[prefix.Length..]))
                throw new InvalidDataException($"更新ZIPにユーザーデータが含まれています。{name}");

            string target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            string? ancestor = directory ? relative : ParentEntry(relative);
            while (ancestor is not null)
            {
                if (files.Contains(ancestor))
                    throw new InvalidDataException($"更新ZIP内でパスが衝突しています。{name}");
                directories.Add(ancestor);
                ancestor = ParentEntry(ancestor);
            }
            if (directory)
                continue;
            if (directories.Contains(relative) || !files.Add(relative))
                throw new InvalidDataException($"更新ZIP内でパスが衝突しています。{name}");
            if (entry.Length < 0 || entry.Length > MaximumExtractedBytes - bytes)
                throw new InvalidDataException("更新ZIPの展開サイズが上限を超えています。");
            bytes += entry.Length;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyLimited(input, output, entry.Length);
            required.Remove(name);
        }
        if (required.Count > 0)
            throw new InvalidDataException("更新ZIPに必要なファイルがありません。");
    }

    private static string? ParentEntry(string path)
    {
        int end = path.LastIndexOf('/');
        return end >= 0 ? path[..end] : null;
    }

    private static bool IsUserData(string path) =>
        path.Split('/').Any(part => part.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("archive-history", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".update-owner", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("data", StringComparison.OrdinalIgnoreCase));

    private static void ValidateEntry(ZipArchiveEntry entry, string relative)
    {
        int attrs = entry.ExternalAttributes;
        int type = (attrs >> 16) & 0xf000;
        if (relative.Length > 200 || (attrs & (int)FileAttributes.ReparsePoint) != 0 ||
            type is not (0 or 0x4000 or 0x8000) ||
            relative.Split('/').Any(part => part.Length == 0 || part.Length > 120 || part.EndsWith('.') ||
                part.EndsWith(' ') || ReservedName().IsMatch(part) ||
                part.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c))))
            throw new InvalidDataException($"更新ZIPに使用できないパスがあります。{entry.FullName}");
    }

    private static void CopyLimited(Stream source, Stream destination, long length)
    {
        byte[] buffer = new byte[64 * 1024];
        long written = 0;
        int count;
        while ((count = source.Read(buffer)) != 0)
        {
            written += count;
            if (written > length)
                throw new InvalidDataException("更新ZIPの展開結果が宣言されたサイズを超えています。");
            destination.Write(buffer, 0, count);
        }
        if (written != length)
            throw new InvalidDataException("更新ZIPの展開結果が宣言されたサイズと一致しません。");
        destination.Flush();
    }
}
