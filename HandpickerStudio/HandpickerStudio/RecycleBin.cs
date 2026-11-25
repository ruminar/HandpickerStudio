using System.IO;
using System.Runtime.InteropServices;

namespace HandpickerStudio;

internal static class RecycleBin
{
    public static void Send(string path, Action validate)
    {
        string resolved = ArchiveDirectory.ResolveExistingPath(path);
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal) || resolved.Length < 7 || resolved[5] != ':' ||
            new DriveInfo(resolved[4..7]).DriveType != DriveType.Fixed)
            throw new IOException("ごみ箱を利用できるローカルの固定ドライブを指定してください。");

        const uint recycleOnDelete = 0x00080000;
        const uint noErrorUi = 0x0400;
        const uint earlyFailure = 0x00100000;
        const uint silent = 0x0004;
        var type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!;
        var operation = (IFileOperation)Activator.CreateInstance(type)!;
        IShellItem? item = null;
        try
        {
            Guid iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(resolved[4..], IntPtr.Zero, ref iid, out item));
            var sink = new RecycleSink(validate);
            operation.SetOperationFlags(recycleOnDelete | earlyFailure | noErrorUi | silent);
            operation.DeleteItem(item!, sink);
            try { operation.PerformOperations(); }
            catch (COMException) when (sink.Failure is not null) { throw sink.Failure; }
            operation.GetAnyOperationsAborted(out bool aborted);
            if (sink.Failure is not null)
                throw sink.Failure;
            if (aborted || !sink.Recycled)
                throw new IOException("Windowsでごみ箱への移動を確認できませんでした。");
        }
        finally
        {
            if (item is not null)
                Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(operation);
        }
    }

    public static Task<T> RunOnSta<T>(Func<T> operation)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(operation()); }
            catch (Exception exception) { result.SetException(exception); }
        }) { IsBackground = true, Name = "HandpickerStudio Shell" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private sealed class RecycleSink(Action validate) : IRecycleProgressSink
    {
        public bool Recycled { get; private set; }
        public Exception? Failure { get; private set; }
        public int PreDeleteItem(uint flags, IntPtr item)
        {
            if ((flags & 0x80) == 0)
                return unchecked((int)0x80004004);
            try
            {
                validate();
                return 0;
            }
            catch (Exception error)
            {
                Failure = error;
                return unchecked((int)0x80004004);
            }
        }
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr recycledItem)
        {
            Recycled = result >= 0 && recycledItem != IntPtr.Zero;
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, string name) => 0;
        public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr destination, string name) => 0;
        public int PostNewItem(uint flags, IntPtr destination, string name, string template, uint attributes, int result, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, out IShellItem? item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(IntPtr message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, IntPtr name, IntPtr sink);
        void RenameItems(IntPtr items, IntPtr name);
        void MoveItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IShellItem item, IRecycleProgressSink sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, IntPtr name, IntPtr template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}

[ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRecycleProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
    [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
    [PreserveSig] int PreNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IntPtr created);
    [PreserveSig] int UpdateProgress(uint total, uint completed);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}
