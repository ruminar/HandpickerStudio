using HandpickerStudio.Update;

namespace HandpickerStudio.Launcher;

internal sealed class InstallationUpdater(UpdateLayout layout, UpdateProfile? profile)
{
    private sealed record Attempt(string Version, string WorkDirectory);
    internal InstallationState Apply(InstallationState current)
    {
        if (UpdateLayout.Exists(layout.AttemptPath))
            throw new IOException("前回の更新の後処理が完了していません。");
        if (profile is null)
            throw new InvalidDataException("このビルドには更新用の公開鍵がありません。");
        var pending = UpdateLayout.Read<PendingUpdate>(layout.PendingPath) ?? throw new IOException("更新の準備が見つかりません。");
        if (UpdatePackage.ParseVersion(pending.Version) is not { } next ||
            UpdatePackage.ParseVersion(current.Current) is not { } version || next <= version)
            throw new InvalidDataException("更新先が現在の版より新しくありません。");

        string work = layout.WorkDirectory(pending.WorkDirectory);
        UpdateLayout.RequireDirectory(work);
        var manifest = UpdatePackage.ReadManifest(
            UpdateLayout.ReadSmallFile(Path.Combine(work, UpdatePackage.ManifestName)),
            UpdateLayout.ReadSmallFile(Path.Combine(work, UpdatePackage.SignatureName)), profile, pending.Version);
        string zipPath = Path.Combine(work, "package.zip");
        UpdateLayout.RequireFile(zipPath);
        string destination = layout.VersionDirectory(pending.Version);
        if (UpdateLayout.Exists(destination))
            throw new IOException($"更新先の版が既にあります。{destination}");
        UpdateLayout.WriteAtomic(layout.AttemptPath, new Attempt(pending.Version, pending.WorkDirectory));
        string extracted = Path.Combine(work, "extracted");
        UpdateLayout.EnsureDirectory(extracted);
        using (var package = File.OpenRead(zipPath))
        {
            UpdatePackage.VerifyZip(package, manifest);
            UpdatePackage.Extract(package, extracted, pending.Version);
        }

        string stagedVersion = Path.Combine(extracted, "app", "versions", pending.Version);
        File.WriteAllText(Path.Combine(stagedVersion, ".update-owner"), pending.WorkDirectory);
        Directory.Move(stagedVersion, destination);
        Program.Probe(layout, pending.Version);
        ReplaceLauncherIfChanged(Path.Combine(extracted, "HandpickerStudio.exe"), work, current.Current, pending.Version, profile);

        var obsolete = (current.Obsolete ?? []).Concat(current.Previous is { } old ? [old] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
        var nextState = new InstallationState(pending.Version, current.Current, obsolete);
        layout.WriteState(nextState);
        Recover(nextState);
        return nextState;
    }

    private void ReplaceLauncherIfChanged(string candidate, string work, string current, string next, UpdateProfile release)
    {
        UpdateLayout.RequireFile(layout.Launcher);
        if (UpdatePackage.HashFile(candidate) == UpdatePackage.HashFile(layout.Launcher))
            return;

        string backup = Path.Combine(work, "launcher-backup.exe");
        File.Replace(candidate, layout.Launcher, backup);
        using var check = Program.Start(layout.Launcher, layout.Root, "--verify-entry", layout.Root, current, next,
            release.Repository, release.Test ? "test" : "release");
        if (!check.WaitForExit(15000) || check.ExitCode != 0)
            throw new IOException("新しい起動窓口を検証できませんでした。");
    }

    internal bool Recover(InstallationState state)
    {
        var attempt = UpdateLayout.Read<Attempt>(layout.AttemptPath);
        if (attempt is null)
            return true;
        string work = layout.WorkDirectory(attempt.WorkDirectory);
        string attemptedVersion = layout.VersionDirectory(attempt.Version);
        UpdateLayout.RequireDirectory(work);
        string backup = Path.Combine(work, "launcher-backup.exe");
        bool committed = state.Current == attempt.Version;
        if (UpdateLayout.Exists(backup))
        {
            UpdateLayout.RequireFile(backup);
            if (committed)
            {
                // Keep the transaction backup until the recovery marker has been cleared.
                CopyReplacing(backup, layout.LauncherBackup);
            }
            else if (!UpdateLayout.Exists(layout.Launcher) || UpdatePackage.HashFile(backup) != UpdatePackage.HashFile(layout.Launcher))
                CopyReplacing(backup, layout.Launcher);
        }

        if (!committed && state.Previous != attempt.Version && IsManagedVersion(attemptedVersion, attempt.WorkDirectory))
        {
            // Move the failed version out of the way as a unit. If a trial process is still
            // running, leave its files and the recovery record in place while the old app starts.
            try { Directory.Move(attemptedVersion, Path.Combine(work, "failed-version")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        DeleteRegularFile(layout.AttemptPath);
        DeleteRegularFile(layout.PendingPath);
        TryClean(() => layout.DeleteWorkDirectory(work));
        return true;
    }

    internal void DiscardPending()
    {
        var pending = UpdateLayout.Read<PendingUpdate>(layout.PendingPath);
        if (pending is not null)
        {
            DeleteRegularFile(layout.PendingPath);
            TryClean(() => layout.DeleteWorkDirectory(layout.WorkDirectory(pending.WorkDirectory)));
        }
    }

    internal void CleanStaleWork()
    {
        if (UpdateLayout.Exists(layout.AttemptPath) || UpdateLayout.Exists(layout.PendingPath))
            return;
        foreach (string folder in Directory.EnumerateDirectories(layout.UpdateDirectory, "stage-*"))
            TryClean(() => layout.DeleteWorkDirectory(folder));
    }

    internal void RemoveObsolete(InstallationState state)
    {
        var remaining = new List<string>();
        foreach (string old in state.Obsolete ?? [])
        {
            if (old == state.Current || old == state.Previous)
                continue;
            try
            {
                string folder = layout.VersionDirectory(old);
                if (!UpdateLayout.Exists(folder))
                    continue;
                if (!IsManagedVersion(folder))
                {
                    // An original ZIP version has no transaction token. Its recorded version must match its own executable.
                    Program.CheckApplication(layout, old);
                    string? product = System.Diagnostics.FileVersionInfo.GetVersionInfo(layout.ApplicationPath(old)).ProductVersion;
                    if (product?.Split('+')[0] != old)
                        continue;
                }
                if (!ContainsOnlyPackageFiles(folder))
                    continue;
                UpdateLayout.DeleteOwnedTree(folder);
            }
            catch (Exception error) when (Program.IsUpdateError(error))
            {
                remaining.Add(old);
            }
        }
        if (state.Obsolete is { } oldVersions && !oldVersions.SequenceEqual(remaining))
            TryClean(() => layout.WriteState(state with { Obsolete = remaining.ToArray() }));
    }

    private static bool ContainsOnlyPackageFiles(string folder)
    {
        string[]? inventory = UpdateLayout.Read<string[]>(Path.Combine(folder, ".package-files.json"));
        if (inventory is null)
            return false;
        var files = inventory.ToHashSet(StringComparer.OrdinalIgnoreCase);
        files.Add(".package-files.json");
        files.Add(".update-owner");
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
                else if (!files.Contains(Path.GetRelativePath(folder, path).Replace('\\', '/')))
                    return false;
            }
        }
        return true;
    }

    private static bool IsManagedVersion(string directory, string? token = null)
    {
        if (!UpdateLayout.Exists(directory))
            return false;
        UpdateLayout.RequireDirectory(directory);
        string path = Path.Combine(directory, ".update-owner");
        if (!UpdateLayout.Exists(path))
            return false;
        string value = System.Text.Encoding.UTF8.GetString(UpdateLayout.ReadSmallFile(path));
        return value.StartsWith("stage-", StringComparison.Ordinal) &&
            Guid.TryParseExact(value[6..], "N", out _) && (token is null || token == value);
    }

    private static void CopyReplacing(string source, string destination)
    {
        string temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary);
            if (UpdateLayout.Exists(destination))
            {
                UpdateLayout.RequireFile(destination);
                File.Replace(temporary, destination, null);
            }
            else
                File.Move(temporary, destination);
        }
        finally { File.Delete(temporary); }
    }

    private static void DeleteRegularFile(string path)
    {
        if (UpdateLayout.Exists(path))
        {
            UpdateLayout.RequireFile(path);
            File.Delete(path);
        }
    }

    private static void TryClean(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) when (Program.IsUpdateError(error)) { }
    }
}
