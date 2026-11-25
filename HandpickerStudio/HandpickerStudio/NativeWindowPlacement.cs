using System.Runtime.InteropServices;

namespace HandpickerStudio;

internal sealed record MainWindowPlacement(int Left, int Top, int Right, int Bottom, bool Maximized);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWindowPlacement
{
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;

    private int length, flags, showCommand;
    private int minX, minY, maxX, maxY;
    private NativeRect restorePosition;

    internal static bool TryRead(nint window, out NativeWindowPlacement placement)
    {
        placement = new() { length = Marshal.SizeOf<NativeWindowPlacement>() };
        return GetWindowPlacement(window, ref placement);
    }

    internal readonly MainWindowPlacement Capture(bool maximized) =>
        new(restorePosition.Left, restorePosition.Top, restorePosition.Right, restorePosition.Bottom, maximized);

    internal static void Restore(nint window, MainWindowPlacement saved)
    {
        if ((long)saved.Right - saved.Left is <= 0 or > int.MaxValue ||
            (long)saved.Bottom - saved.Top is <= 0 or > int.MaxValue)
            return;

        // Use the workspace coordinates returned by GetWindowPlacement. Windows adjusts off-screen placements.
        new NativeWindowPlacement
        {
            length = Marshal.SizeOf<NativeWindowPlacement>(),
            showCommand = saved.Maximized ? ShowMaximized : ShowNormal,
            restorePosition = new() { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom }
        }.Apply(window);
    }

    internal readonly bool Apply(nint window) => SetWindowPlacement(window, in this);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref NativeWindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window, in NativeWindowPlacement placement);
}
