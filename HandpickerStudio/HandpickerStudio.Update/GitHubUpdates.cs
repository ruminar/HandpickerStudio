using System.Net;
using System.Text.Json;

namespace HandpickerStudio.Update;

public sealed record UpdateAsset(string Name, long Size, string? Digest);
public sealed record UpdateCandidate(string Version, string Tag, string Notes, UpdateAsset Zip, UpdateAsset Manifest, UpdateAsset Signatures);

public sealed class GitHubUpdates : IDisposable
{
    private const int MetadataLimit = 1024 * 1024;
    private readonly HttpClient client;
    private readonly UpdateProfile profile;

    public GitHubUpdates(UpdateProfile profile)
        : this(profile, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    public GitHubUpdates(UpdateProfile profile, HttpMessageHandler handler)
    {
        this.profile = profile;
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HandpickerStudio-Updater/1.0");
    }

    public async Task<UpdateCandidate?> CheckAsync(Version current, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{profile.Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadSmallAsync(response, MetadataLimit, deadline.Token));
        var item = document.RootElement;
        if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean())
            return null;
        string tag = item.GetProperty("tag_name").GetString() ?? "";
        string version = tag.StartsWith('v') ? tag[1..] : tag;
        if (UpdatePackage.ParseVersion(version) is not { } next || next <= current)
            return null;
        var assets = item.GetProperty("assets").EnumerateArray().ToArray();
        string notes = item.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateCandidate(version, tag, notes.Length > 2500 ? notes[..2500] + "…" : notes,
            FindAsset(assets, profile.AssetName(version), UpdatePackage.MaximumZipBytes),
            FindAsset(assets, UpdatePackage.ManifestName, MetadataLimit),
            FindAsset(assets, UpdatePackage.SignatureName, MetadataLimit));
    }

    private static UpdateAsset FindAsset(JsonElement[] assets, string name, long maxSize)
    {
        var matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matches is not [var item] || item.GetProperty("state").GetString() != "uploaded" ||
            !item.GetProperty("size").TryGetInt64(out long size) || size <= 0 || size > maxSize)
            throw new InvalidDataException($"配布物を確認できません。{name}");
        return new UpdateAsset(name, size, item.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
    }

    public async Task StageAsync(UpdateCandidate candidate, UpdateLayout layout, CancellationToken token)
    {
        if (UpdateLayout.Exists(layout.AttemptPath))
            throw new IOException("前回の更新の後処理を待っています。HandpickerStudioを起動し直してください。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        string temporary = layout.NewWorkDirectory();
        try
        {
            byte[] manifestBytes = await DownloadSmallAsync(candidate.Tag, candidate.Manifest, deadline.Token);
            byte[] signatures = await DownloadSmallAsync(candidate.Tag, candidate.Signatures, deadline.Token);
            var manifest = UpdatePackage.ReadManifest(manifestBytes, signatures, profile, candidate.Version);
            if (candidate.Zip.Size != manifest.Size || !MatchesDigest(candidate.Zip.Digest, manifest.Sha256))
                throw new InvalidDataException("GitHub Releasesと署名済みマニフェストのZIP情報が一致しません。");

            using (var response = await GetAssetAsync(candidate.Tag, candidate.Zip.Name, deadline.Token))
            using (var file = new FileStream(Path.Combine(temporary, "package.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await CopyResponseAsync(response, file, manifest.Size, deadline.Token);
                file.Position = 0;
                UpdatePackage.VerifyZip(file, manifest);
                file.Flush(flushToDisk: true);
            }
            File.WriteAllBytes(Path.Combine(temporary, UpdatePackage.ManifestName), manifestBytes);
            File.WriteAllBytes(Path.Combine(temporary, UpdatePackage.SignatureName), signatures);
            layout.WritePending(new PendingUpdate(candidate.Version, Path.GetFileName(temporary)));
        }
        catch
        {
            try { layout.DeleteWorkDirectory(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<byte[]> DownloadSmallAsync(string tag, UpdateAsset asset, CancellationToken token)
    {
        using var response = await GetAssetAsync(tag, asset.Name, token);
        using var output = new MemoryStream();
        await CopyResponseAsync(response, output, asset.Size, token);
        output.Position = 0;
        if (!MatchesDigest(asset.Digest, UpdatePackage.Hash(output)))
            throw new InvalidDataException($"配布物のハッシュが一致しません。{asset.Name}");
        return output.ToArray();
    }

    private static bool MatchesDigest(string? digest, string sha256) =>
        digest is null || digest.Equals("sha256:" + sha256, StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> GetAssetAsync(string tag, string name, CancellationToken token)
    {
        var uri = new Uri($"https://github.com/{profile.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");
        for (int remaining = 5; remaining > 0; remaining--)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("配布ファイルのリダイレクト先が許可されていません。");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode)
                return response;
            using (response)
            {
                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location)
                    response.EnsureSuccessStatusCode();
                else
                {
                    uri = new Uri(uri, location);
                    if (uri.Host == "github.com")
                        throw new InvalidDataException("別のGitHubリリースへのリダイレクトを拒否しました。");
                }
            }
        }
        throw new InvalidDataException("配布ファイルのリダイレクト回数が上限を超えました。");
    }

    private static async Task<byte[]> ReadSmallAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
    {
        using var output = new MemoryStream();
        await CopyResponseAsync(response, output, response.Content.Headers.ContentLength ?? maxBytes, token,
            maxBytes, exact: response.Content.Headers.ContentLength is not null);
        return output.ToArray();
    }

    private static async Task CopyResponseAsync(HttpResponseMessage response, Stream output, long expected, CancellationToken token,
        long maximum = UpdatePackage.MaximumZipBytes, bool exact = true)
    {
        if (expected < 0 || expected > maximum ||
            (response.Content.Headers.ContentLength is { } length && length != expected))
            throw new InvalidDataException("配布ファイルのサイズが不正です。");
        using var input = await response.Content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[64 * 1024];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            copied += count;
            if (copied > expected)
                throw new InvalidDataException("ダウンロードサイズが上限を超えています。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (exact && copied != expected)
            throw new InvalidDataException("ダウンロードが途中で終了しました。");
    }

    public void Dispose() => client.Dispose();
}
