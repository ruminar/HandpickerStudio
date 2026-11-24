using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HandpickerStudio;

internal static class ArchiveDirectory
{
    public static string GetPath(Guid projectId, ImageRoot root) =>
        Path.Combine(root.ArchivePath, projectId.ToString("N"), root.Id.ToString("N"));

    public static void EnsureExists(ImageRoot root, string archivePath, CancellationToken token)
    {
        // An archive base can be a mapped drive or a link. Compare the resolved locations
        // before creating anything, and keep links out of Studio's own namespace.
        if (WorkspacePaths.Overlap(ResolvePath(root.OutputPath, token), ResolvePath(root.ArchivePath, token)))
            throw new IOException("OutputとArchiveが同じ領域を参照しています。");

        RequireManagedDirectory(Path.GetDirectoryName(archivePath)!);
        RequireManagedDirectory(archivePath);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(archivePath);
    }

    public static void RequireManagedDirectory(string path)
    {
        if (GetAttributes(path) is { } attributes &&
            (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new IOException($"Archiveの管理フォルダーを利用できません。{path}");
    }

    private static string ResolvePath(string path, CancellationToken token)
    {
        var missing = new Stack<string>();
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            token.ThrowIfCancellationRequested();
            if (GetAttributes(directory.FullName) is { } attributes)
            {
                if (!attributes.HasFlag(FileAttributes.Directory))
                    throw new IOException($"フォルダーではありません。{directory.FullName}");

                return Path.TrimEndingDirectorySeparator(Path.Combine([ResolveExistingPath(directory.FullName), .. missing]));
            }
            missing.Push(directory.Name);
        }

        throw new DirectoryNotFoundException($"フォルダーの保存先を確認できません。{path}");
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException) { return null; }
    }

    private static string ResolveExistingPath(string path)
    {
        const uint OpenDirectory = 0x02000000;
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, OpenDirectory, IntPtr.Zero);
        if (handle.IsInvalid)
            throw ResolutionError(path);

        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw ResolutionError(path);
        return buffer.ToString();
    }

    private static IOException ResolutionError(string path) =>
        new($"フォルダーの実体を確認できません。{path}", new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint attributes, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
