using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HandpickerStudio.Launcher;
using HandpickerStudio.Update;

if (args is not [var packages, var scratch])
    throw new ArgumentException("Pass the test release directory and a writable fixture directory.");

const string oldest = "0.0.0";
const string newest = "0.1.0";
string initialZip = Path.GetFullPath(Path.Combine(packages, "v" + oldest, $"HandpickerStudio-UpdateTest-{oldest}-win-x64.zip"));
string newestRelease = Path.GetFullPath(Path.Combine(packages, "v" + newest));
string fixture = Path.GetFullPath(Path.Combine(scratch, "run-" + Guid.NewGuid().ToString("N")));
var main = Prepare("success");
var appAssembly = Assembly.LoadFile(Path.Combine(main.VersionDirectory(oldest), "HandpickerStudio.dll"));
var profile = UpdateProfile.Load(appAssembly) ?? throw new Exception("Test profile is missing.");
Check(profile.Test, "test profile compiled into the portable app");
var assets = Directory.EnumerateFiles(newestRelease).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);

string external = Directory.CreateDirectory(Path.Combine(fixture, "output")).FullName;
string archive = Path.Combine(fixture, "archive");
string checkpoint = Directory.CreateDirectory(Path.Combine(fixture, "models")).FullName;
string suite = Directory.CreateDirectory(Path.Combine(fixture, "suite")).FullName;
string evaluation = Path.Combine(suite, "checkpoint_statuses.json");
string localCache = FileAt(main, "cache", "payload.txt", "cache");
string history = FileAt(main, "data", "archive-history/receipt.txt", "archive history");
string image = Path.Combine(external, "image.png");
File.WriteAllText(image, "original image");
File.WriteAllText(evaluation, "{}");
Guid projectId = Guid.NewGuid();
var settings = new
{
    version = 1,
    projects = new[] { new {
        id = projectId, name = "Fixture", checkpointPath = checkpoint, evaluationPath = evaluation, cachePath = "",
        roots = new[] { new { id = Guid.NewGuid(), name = "Output", outputPath = external, archivePath = archive, enabled = true } }
    } },
    selectedProjectId = projectId,
    heldLabels = new[] { new { outputPath = external, label = "Fixture", scoped = true } },
    archivePreferences = new[] { new { projectId, automatic = true, confirmed = true } },
    createImageCache = true
};
var originals = new[] { FileAt(main, "data", "settings.json", JsonSerializer.Serialize(settings, UpdatePackage.Json)),
    localCache, history, image, evaluation };
var fingerprints = originals.Select(UpdatePackage.HashFile).ToArray();
using (var updates = Client(assets))
{
    Check(await updates.CheckAsync(Version.Parse(newest), default) is null, "same version is ignored");
    var candidate = await updates.CheckAsync(Version.Parse(oldest), default) ?? throw new Exception("No update candidate.");
    Check(candidate.Version == newest, "newer stable release selected through the Latest API");
    await updates.StageAsync(candidate, main, default);
}

string oldLauncherHash = UpdatePackage.HashFile(main.Launcher);
var updater = new InstallationUpdater(main, profile);
var state = updater.Apply(new InstallationState(oldest));
Check(state.Current == newest && state.Previous == oldest, "v0.0.0 is promoted to v0.1.0");
Check(Directory.Exists(main.VersionDirectory(oldest)) && !File.Exists(main.PendingPath), "previous version retained and pending update cleared");
Check(UpdatePackage.HashFile(main.Launcher) != oldLauncherHash && UpdatePackage.HashFile(main.LauncherBackup) == oldLauncherHash,
    "the entry executable is replaced with a recoverable backup");
Check(originals.Select(UpdatePackage.HashFile).SequenceEqual(fingerprints) && !Directory.Exists(archive),
    "WPF startup probe leaves settings, archive, cache, images and Suite untouched");

string retired = MakeRetiredVersion("0.0.1");
string unknown = MakeRetiredVersion("0.0.2");
File.WriteAllText(Path.Combine(unknown, "user-note.txt"), "keep");
updater.RemoveObsolete(state with { Obsolete = ["0.0.1", "0.0.2"] });
Check(!Directory.Exists(retired) && File.Exists(Path.Combine(unknown, "user-note.txt")) &&
    Directory.Exists(main.VersionDirectory(oldest)) && Directory.Exists(main.VersionDirectory(newest)),
    "cleanup removes only a retired package and keeps old/current versions and unknown files");

using (var updates = Client(assets, prerelease: true))
    Check(await updates.CheckAsync(Version.Parse(oldest), default) is null, "prerelease is ignored");

var corrupt = Prepare("corrupt");
using (var updates = Client(assets, corruptZip: true))
{
    var candidate = (await updates.CheckAsync(Version.Parse(oldest), default))!;
    await RejectAsync(() => updates.StageAsync(candidate, corrupt, default), "tampered download is rejected");
    Check(!File.Exists(corrupt.PendingPath), "a failed download cannot request an update");
}
using (var updates = Client(assets, foreignRedirect: true))
{
    var candidate = (await updates.CheckAsync(Version.Parse(oldest), default))!;
    await RejectAsync(() => updates.StageAsync(candidate, corrupt, default), "an untrusted redirect is rejected");
}

var locked = Prepare("interrupted");
string workForLock = locked.NewWorkDirectory();
string candidateDirectory = Directory.CreateDirectory(locked.VersionDirectory(newest)).FullName;
File.Copy(locked.ApplicationPath(oldest), locked.ApplicationPath(newest));
string workName = Path.GetFileName(workForLock);
File.WriteAllText(Path.Combine(candidateDirectory, ".update-owner"), workName);
locked.WritePending(new PendingUpdate(newest, workName));
UpdateLayout.WriteAtomic(locked.AttemptPath, new { version = newest, workDirectory = workName });
var lockRecovery = new InstallationUpdater(locked, profile);
using (var handle = new FileStream(locked.ApplicationPath(newest), FileMode.Open, FileAccess.Read, FileShare.Read))
{
    Check(!lockRecovery.Recover(new InstallationState(oldest)) && File.Exists(locked.AttemptPath) &&
        File.Exists(locked.ApplicationPath(oldest)) && File.Exists(locked.ApplicationPath(newest)),
        "a locked failed version can be left in place while the old version remains available");
}
Check(lockRecovery.Recover(new InstallationState(oldest)) && !Directory.Exists(candidateDirectory) && !File.Exists(locked.AttemptPath),
    "interrupted cleanup resumes after the lock is released");

var invalidStartup = Prepare("startup-failure");
string invalidSettings = FileAt(invalidStartup, "data", "settings.json", "invalid JSON");
using (var updates = Client(assets))
    await updates.StageAsync((await updates.CheckAsync(Version.Parse(oldest), default))!, invalidStartup, default);
var recovery = new InstallationUpdater(invalidStartup, profile);
var oldState = new InstallationState(oldest);
await RejectAsync(() => Task.FromResult(recovery.Apply(oldState)), "a failed WPF startup does not activate the new version");
recovery.Recover(oldState);
Check(UpdateLayout.Read<InstallationState>(invalidStartup.StatePath)?.Current == oldest &&
    !Directory.Exists(invalidStartup.VersionDirectory(newest)) && !File.Exists(invalidStartup.PendingPath) &&
    File.ReadAllText(invalidSettings) == "invalid JSON", "startup failure keeps the old application and data");

var invalidLauncher = Prepare("launcher-failure");
FileAt(invalidLauncher, "data", "settings.json", JsonSerializer.Serialize(settings, UpdatePackage.Json));
using (var rsa = RSA.Create(2048))
{
    const string key = "fixture";
    var fixtureProfile = profile with { PublicKeys = new() { [key] = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) } };
    string work = invalidLauncher.NewWorkDirectory();
    string zipPath = Path.Combine(work, "package.zip");
    File.Copy(Path.Combine(newestRelease, profile.AssetName(newest)), zipPath);
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
    {
        zip.GetEntry("HandpickerStudio.exe")!.Delete();
        using var binary = zip.CreateEntry("HandpickerStudio.exe").Open();
        binary.Write("invalid executable"u8);
    }
    var manifest = new UpdateManifest("HandpickerStudio", profile.Repository, newest, "win-x64",
        profile.AssetName(newest), new FileInfo(zipPath).Length, UpdatePackage.HashFile(zipPath));
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdatePackage.Json);
    File.WriteAllBytes(Path.Combine(work, UpdatePackage.ManifestName), bytes);
    var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    File.WriteAllBytes(Path.Combine(work, UpdatePackage.SignatureName),
        JsonSerializer.SerializeToUtf8Bytes(new[] { new UpdateSignature(key, Convert.ToBase64String(signature)) }, UpdatePackage.Json));
    invalidLauncher.WritePending(new PendingUpdate(newest, Path.GetFileName(work)));
    var launcherRecovery = new InstallationUpdater(invalidLauncher, fixtureProfile);
    await RejectAsync(() => Task.FromResult(launcherRecovery.Apply(oldState)), "a bad new entry executable is rejected");
    launcherRecovery.Recover(oldState);
    Check(UpdatePackage.HashFile(invalidLauncher.Launcher) == oldLauncherHash &&
        UpdateLayout.Read<InstallationState>(invalidLauncher.StatePath)?.Current == oldest,
        "launcher failure restores the old executable and current version");
}

TestUnsafeEntry("../escape.txt");
TestUnsafeEntry("app/versions/0.1.0/CON.txt");
TestUnsafeEntry("app/versions/0.1.0/file:stream");
TestUnsafeEntry("app/versions/0.1.0/link", 0xa000 << 16);

await TestLegacyLaunchAsync();

string MakeRetiredVersion(string version)
{
    string folder = Directory.CreateDirectory(main.VersionDirectory(version)).FullName;
    File.WriteAllText(Path.Combine(folder, ".update-owner"), $"stage-{Guid.NewGuid():N}");
    File.WriteAllText(Path.Combine(folder, ".package-files.json"), """["body.txt"]""");
    File.WriteAllText(Path.Combine(folder, "body.txt"), "old application artifact");
    return folder;
}

async Task TestLegacyLaunchAsync()
{
    var layout = new UpdateLayout(Path.Combine(fixture, "legacy-launch"));
    ZipFile.ExtractToDirectory(initialZip, layout.Root);
    string oldSettings = Path.Combine(layout.Root, "settings.json");
    File.WriteAllText(oldSettings, """{"version":1,"projects":[],"heldLabels":[],"checkUpdatesOnStartup":false,"tileSize":234}""");
    string historyProject = Guid.NewGuid().ToString("N");
    string oldReceipt = Path.Combine(layout.Root, "archive-history", historyProject, "original.json");
    Directory.CreateDirectory(Path.GetDirectoryName(oldReceipt)!);
    File.WriteAllText(oldReceipt, """{"fixture":true}""");
    string cache = FileAt(layout, "cache", "existing.txt", "cache");
    string originalHash = UpdatePackage.HashFile(oldSettings);

    using var entry = Process.Start(new ProcessStartInfo(layout.Launcher)
    {
        WorkingDirectory = layout.Root, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden
    }) ?? throw new Exception("Could not launch the package.");
    Process? window = null;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (window is null)
        {
            foreach (var process in Process.GetProcessesByName("HandpickerStudio"))
            {
                try
                {
                    if (process.MainModule?.FileName is { } exe && UpdateLayout.PathEquals(exe, layout.ApplicationPath(oldest)) &&
                        process.MainWindowHandle != IntPtr.Zero)
                    {
                        window = process;
                        break;
                    }
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                process.Dispose();
            }
            if (window is null)
                await Task.Delay(200, timeout.Token);
        }
        using var migrated = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(layout.Data, "settings.json")));
        Check(migrated.RootElement.GetProperty("tileSize").GetInt32() == 234 &&
            UpdatePackage.HashFile(oldSettings) == originalHash && File.Exists(cache) &&
            File.ReadAllText(Path.Combine(layout.Data, "archive-history", historyProject, "original.json")) == File.ReadAllText(oldReceipt),
            "launching the portable package imports legacy data and leaves the originals intact");
    }
    finally
    {
        if (window is not null)
        {
            using (window)
            {
                window.CloseMainWindow();
                window.WaitForExit(5000);
            }
        }
    }
}

void TestUnsafeEntry(string path, int attrs = 0)
{
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        zip.CreateEntry(path).ExternalAttributes = attrs;
    buffer.Position = 0;
    try
    {
        UpdatePackage.Extract(buffer, Path.Combine(fixture, "unsafe"), newest);
        throw new Exception($"Unsafe ZIP entry was accepted: {path}");
    }
    catch (InvalidDataException) { Console.WriteLine($"PASS unsafe ZIP entry rejected: {path}"); }
}

UpdateLayout Prepare(string name)
{
    var layout = new UpdateLayout(Path.Combine(fixture, name));
    ZipFile.ExtractToDirectory(initialZip, layout.Root);
    layout.EnsureUpdateDirectories();
    layout.WriteState(new InstallationState(oldest));
    return layout;
}

GitHubUpdates Client(Dictionary<string, byte[]> files, bool prerelease = false, bool corruptZip = false, bool foreignRedirect = false) =>
    new(profile, new FixtureHttpHandler(profile, files, prerelease, corruptZip, foreignRedirect));

static string FileAt(UpdateLayout layout, string area, string name, string contents)
{
    string path = Path.Combine(layout.Root, area, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, contents);
    return path;
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
    Console.WriteLine($"PASS {message}");
}

static async Task RejectAsync(Func<Task> operation, string message)
{
    try { await operation(); }
    catch (Exception error) when (error is InvalidDataException or IOException or System.ComponentModel.Win32Exception)
    {
        Console.WriteLine($"PASS {message}");
        return;
    }
    throw new Exception(message);
}

sealed class FixtureHttpHandler(UpdateProfile profile, Dictionary<string, byte[]> files, bool prerelease,
    bool corruptZip, bool foreignRedirect) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "api.github.com" && uri.AbsolutePath == $"/repos/{profile.Repository}/releases/latest")
        {
            string metadata = JsonSerializer.Serialize(new
            {
                draft = false, prerelease, tag_name = "v0.1.0", body = "Fixture",
                assets = files.Select(file => new { name = file.Key, size = file.Value.Length, state = "uploaded",
                    digest = "sha256:" + Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant() })
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json")
            });
        }
        string name = Path.GetFileName(uri.AbsolutePath);
        if (uri.Host == "github.com")
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri($"https://{(foreignRedirect ? "example.invalid" : "release-assets.githubusercontent.com")}/{name}");
            return Task.FromResult(redirect);
        }
        if (uri.Host != "release-assets.githubusercontent.com" || !files.TryGetValue(name, out byte[]? asset))
            throw new Exception($"Unexpected request: {uri}");
        if (corruptZip && name.EndsWith(".zip", StringComparison.Ordinal))
        {
            asset = asset.ToArray();
            asset[0] ^= 1;
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(asset) });
    }
}
