using System.IO;
using System.Runtime.InteropServices;

namespace HandpickerStudio;

internal static class LocalSmbShare
{
    public static string? GetLocalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        if (!path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string[] parts = path[uncPrefix.Length..].Split('\\', 3);
        if (parts.Length < 2 || parts[1].Length == 0 ||
            !parts[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return null;

        const uint sharePathLevel = 2;
        if (NetShareGetInfo(null, parts[1], sharePathLevel, out var buffer) != 0)
            return null;

        try
        {
            const uint shareTypeMask = 0xff;
            const uint diskShare = 0;
            var share = Marshal.PtrToStructure<ShareInfo2>(buffer);
            if ((share.Type & shareTypeMask) != diskShare || Marshal.PtrToStringUni(share.Path) is not { } root ||
                Path.GetPathRoot(root) is not { Length: 3 } volume || volume[1] != ':')
                return null;

            return parts.Length == 3 ? Path.Join(root, parts[2]) : root;
        }
        finally { NetApiBufferFree(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShareInfo2
    {
        public IntPtr Name;
        public uint Type;
        public IntPtr Remark;
        public uint Permissions;
        public uint MaxUses;
        public uint CurrentUses;
        public IntPtr Path;
        public IntPtr Password;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint NetShareGetInfo(string? serverName, string shareName, uint level, out IntPtr buffer);

    [DllImport("Netapi32.dll", ExactSpelling = true)]
    private static extern uint NetApiBufferFree(IntPtr buffer);
}
