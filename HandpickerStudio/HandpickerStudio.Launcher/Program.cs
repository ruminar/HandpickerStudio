using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HandpickerStudio.Update;

namespace HandpickerStudio.Launcher;

internal static partial class Program
{
    private static readonly string Binary = Environment.ProcessPath!;
    private static readonly Assembly Assembly = typeof(Program).Assembly;
    private static readonly UpdateProfile? Profile = UpdateProfile.Load(Assembly);

    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--supervise", var root, var parent] && int.TryParse(parent, out int pid))
                return Supervise(new UpdateLayout(root), pid);
            if (args is ["--verify-entry", var location, var current, var candidate, var repository, var profileKind])
            {
                var layout = new UpdateLayout(location);
                if (!UpdateLayout.PathEquals(Binary, layout.Launcher) || Profile is null ||
                    !Profile.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) || Profile.Test != (profileKind == "test"))
                    return 1;
                CheckApplication(layout, current);
                CheckApplication(layout, candidate);
                return 0;
            }
            if (args.Length != 0)
                return 1;

            var installation = new UpdateLayout(Path.GetDirectoryName(Binary)!);
            installation.EnsureUpdateDirectories();
            string worker = Path.Combine(installation.UpdateDirectory, $"worker-{Guid.NewGuid():N}.exe");
            File.Copy(Binary, worker, overwrite: false);
            using var process = Start(worker, installation.Root, "--supervise", installation.Root, Environment.ProcessId.ToString());
            return 0;
        }
        catch (Exception error) when (IsUpdateError(error))
        {
            Notify("HandpickerStudioを起動できません", error.Message);
            return 1;
        }
    }

    private static int Supervise(UpdateLayout layout, int parent)
    {
        string name = Path.GetFileNameWithoutExtension(Binary);
        if (!name.StartsWith("worker-", StringComparison.Ordinal) || !Guid.TryParseExact(name[7..], "N", out _) ||
            !UpdateLayout.PathEquals(Path.GetDirectoryName(Binary)!, layout.UpdateDirectory))
            return 1;
        try
        {
            using var source = Process.GetProcessById(parent);
            if (!source.WaitForExit(10000))
                return 1;
        }
        catch (ArgumentException) { }

        layout.EnsureUpdateDirectories();
        string lockPath = Path.Combine(layout.UpdateDirectory, "run.lock");
        if (UpdateLayout.Exists(lockPath))
            UpdateLayout.RequireFile(lockPath);
        using var installationLock = OpenInstallationLock(lockPath);
        var updater = new InstallationUpdater(layout, Profile);
        var state = UpdateLayout.Read<InstallationState>(layout.StatePath);
        if (state is null)
        {
            string initial = UpdatePackage.CurrentVersion(Assembly);
            CheckApplication(layout, initial);
            if (LegacyDataNeedsMigration(layout))
            {
                Probe(layout, initial);
                MigrateLegacyData(layout);
            }
            state = new InstallationState(initial);
            layout.WriteState(state);
        }
        CheckApplication(layout, state.Current);
        bool recoveryFinished = updater.Recover(state);
        updater.RemoveObsolete(state);
        CleanOldWorkers(layout);

        // A pending download alone never authorizes updating after a crash or an ordinary restart.
        if (recoveryFinished)
            updater.DiscardPending();
        updater.CleanStaleWork();
        while (true)
        {
            using var app = Start(layout.ApplicationPath(state.Current), layout.Root, "--portable", layout.Root);
            app.WaitForExit();
            if (app.ExitCode != UpdateLayout.ApplyExitCode)
                return app.ExitCode;

            try { state = updater.Apply(state); }
            catch (Exception error) when (IsUpdateError(error))
            {
                state = UpdateLayout.Read<InstallationState>(layout.StatePath) ?? state;
                try { updater.Recover(state); }
                catch (Exception recovery) when (IsUpdateError(recovery))
                {
                    Notify("更新の復旧に確認が必要です", $"{error.Message}\n{recovery.Message}");
                    return 1;
                }
                Notify("HandpickerStudioの更新結果", $"現在の版 v{state.Current} を起動します。\n{error.Message}");
            }
            updater.RemoveObsolete(state);
        }
    }

    private static FileStream OpenInstallationLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            throw new IOException("この展開先のHandpickerStudioは起動中か、更新中です。", error);
        }
    }

    private static void CleanOldWorkers(UpdateLayout layout)
    {
        foreach (string path in Directory.EnumerateFiles(layout.UpdateDirectory, "worker-*.exe"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (Guid.TryParseExact(name[7..], "N", out _) && !UpdateLayout.PathEquals(path, Binary))
            {
                try { UpdateLayout.RequireFile(path); File.Delete(path); }
                catch (Exception error) when (IsUpdateError(error)) { }
            }
        }
    }

    internal static void CheckApplication(UpdateLayout layout, string version)
    {
        UpdateLayout.RequireDirectory(layout.VersionDirectory(version));
        UpdateLayout.RequireFile(layout.ApplicationPath(version));
    }

    internal static void Probe(UpdateLayout layout, string version)
    {
        CheckApplication(layout, version);
        using var probe = Start(layout.ApplicationPath(version), layout.Root, "--probe", layout.Root);
        if (!probe.WaitForExit(30000) || probe.ExitCode != 0)
            throw new IOException($"v{version} の起動を確認できませんでした。");
    }

    internal static Process Start(string exe, string workingDirectory, params string[] arguments)
    {
        UpdateLayout.RequireFile(exe);
        var start = new ProcessStartInfo(exe) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException($"起動できません。{exe}");
    }

    private static bool LegacyDataNeedsMigration(UpdateLayout layout)
    {
        bool legacy = UpdateLayout.Exists(Path.Combine(layout.Root, "settings.json")) ||
            UpdateLayout.Exists(Path.Combine(layout.Root, "archive-history"));
        if (UpdateLayout.Exists(layout.Data))
        {
            UpdateLayout.RequireDirectory(layout.Data);
            if (legacy && !File.Exists(Path.Combine(layout.Data, ".legacy-imported")))
                throw new IOException("旧配置とdataフォルダーの両方に保存情報があります。内容を確認してから起動してください。");
            return false;
        }
        return legacy;
    }

    private static void MigrateLegacyData(UpdateLayout layout)
    {
        string staging = layout.NewWorkDirectory();
        try
        {
            string copy = Directory.CreateDirectory(Path.Combine(staging, "data")).FullName;
            string oldSettings = Path.Combine(layout.Root, "settings.json");
            string history = Path.Combine(layout.Root, "archive-history");
            string settingsLock = oldSettings + ".lock";
            if (UpdateLayout.Exists(settingsLock))
                UpdateLayout.RequireFile(settingsLock);
            using var settingsWriteLock = new FileStream(settingsLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (UpdateLayout.Exists(oldSettings))
                CopyVerified(oldSettings, Path.Combine(copy, "settings.json"));
            if (UpdateLayout.Exists(history))
            {
                UpdateLayout.RequireDirectory(history);
                string historyLock = Path.Combine(history, ".lock");
                if (UpdateLayout.Exists(historyLock))
                    UpdateLayout.RequireFile(historyLock);
                using var historyWriteLock = new FileStream(historyLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                string historyCopy = Directory.CreateDirectory(Path.Combine(copy, "archive-history")).FullName;
                foreach (string projectHistory in Directory.EnumerateDirectories(history))
                {
                    UpdateLayout.RequireDirectory(projectHistory);
                    if (!Guid.TryParseExact(Path.GetFileName(projectHistory), "N", out _))
                        continue;
                    string destination = Directory.CreateDirectory(Path.Combine(historyCopy, Path.GetFileName(projectHistory))).FullName;
                    foreach (string receipt in Directory.EnumerateFiles(projectHistory, "*.json"))
                        CopyVerified(receipt, Path.Combine(destination, Path.GetFileName(receipt)));
                }
                File.WriteAllText(Path.Combine(copy, ".legacy-imported"), "");
                Directory.Move(copy, layout.Data);
            }
            else
            {
                File.WriteAllText(Path.Combine(copy, ".legacy-imported"), "");
                Directory.Move(copy, layout.Data);
            }
        }
        finally { layout.DeleteWorkDirectory(staging); }
    }

    private static void CopyVerified(string source, string destination)
    {
        UpdateLayout.RequireFile(source);
        using var input = File.OpenRead(source);
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        input.Position = 0;
        if (UpdatePackage.Hash(input) != UpdatePackage.HashFile(destination))
            throw new IOException($"設定の引き継ぎを検証できません。{source}");
    }

    internal static bool IsUpdateError(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException or
        ArgumentException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException;

    internal static void Notify(string title, string text) => MessageBox(IntPtr.Zero, text, title, 0x00000030);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint options);
}
