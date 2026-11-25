using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HandpickerStudio;

internal readonly record struct FileStamp(ulong Volume, ulong IdLow, ulong IdHigh, long Length, long LastWrite);

internal sealed class VerifiedFile : IDisposable
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAccess = 0x80000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int NotSameDevice = 17;

    private readonly string path;
    private readonly SafeFileHandle handle;

    private VerifiedFile(string path, SafeFileHandle handle, FileStamp stamp) => (this.path, this.handle, Stamp) = (path, handle, stamp);

    public FileStamp Stamp { get; }

    public static VerifiedFile Open(string path, string root, FileStamp? expected = null, bool forMove = false)
    {
        var handle = CreateFile(path, forMove ? ReadAccess | DeleteAccess : ReadAccess,
            forMove ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, OpenReparsePoint, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
                throw Win32Error($"ファイルを開けません。{path}");
            if ((information.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !WorkspacePaths.Within(ArchiveDirectory.ResolveHandlePath(handle, path), ArchiveDirectory.ResolveExistingPath(root)))
                throw new IOException($"保存先の範囲内にある通常ファイルを指定してください。{path}");
            if (!GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileId>()))
                throw Win32Error($"ファイルを識別できません。{path}");
            var stamp = new FileStamp(id.Volume, id.Low, id.High,
                ((long)information.SizeHigh << 32) | information.SizeLow,
                ((long)information.WriteHigh << 32) | information.WriteLow);
            if (expected is { } value && stamp != value)
                throw new IOException($"確認後にファイルが変わりました。{path}");
            return new VerifiedFile(path, handle, stamp);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public FileStream OpenStream() => new(handle, FileAccess.Read);

    public bool TryRename(string destination)
    {
        byte[] name = Encoding.Unicode.GetBytes(destination);
        int nameOffset = (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FirstCharacter));
        int lengthOffset = (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.NameLength));
        byte[] buffer = new byte[Math.Max(Marshal.SizeOf<RenameInfo>(), nameOffset + name.Length + sizeof(char))];
        BitConverter.TryWriteBytes(buffer.AsSpan(lengthOffset), name.Length);
        name.CopyTo(buffer, nameOffset);

        // FILE_RENAME_INFO with ReplaceIfExists left false binds the rename to the opened file.
        if (SetFileInformationByHandle(handle, 3, buffer, (uint)buffer.Length))
            return true;
        if (Marshal.GetLastWin32Error() == NotSameDevice)
            return false;
        throw Win32Error($"移動先を利用できません。{destination}");
    }

    public void RemoveAfterCopy()
    {
        if (!SetFileInformationByHandle(handle, 4, [1], 1))
            throw Win32Error($"コピーは完了しました。移動元の整理に失敗しました。{path}");
    }

    public void Dispose() => handle.Dispose();

    private static IOException Win32Error(string message)
    {
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        return new IOException($"{message}{Environment.NewLine}{error.Message}", error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId { public ulong Volume, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        public FileAttributes Attributes;
        public uint CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RenameInfo
    {
        public byte ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint NameLength;
        public char FirstCharacter;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint attributes, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Information information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FileId information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte[] information, uint size);
}
