using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandpickerStudio;

public partial class ImageViewerWindow
{
    private NativeWindowPlacement? previousPlacement;

    private void ToggleExpandedView(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || operationRunning)
            return;

        nint window = new WindowInteropHelper(this).Handle;
        if (previousPlacement is { } original)
        {
            if (original.Apply(window))
                previousPlacement = null;
            e.Handled = true;
            return;
        }

        if (Preview.Source is not BitmapSource)
            return;

        const uint MonitorDefaultToNearest = 2;
        nint monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == 0 || !NativeWindowPlacement.TryRead(window, out var placement))
            return;

        WindowState = WindowState.Normal;
        UpdateLayout();
        if (FitExpandedView(monitor))
            previousPlacement = placement;
        else
            placement.Apply(window);
        e.Handled = true;
    }

    private bool FitExpandedView(nint monitor = 0)
    {
        if (Preview.Source is not BitmapSource image)
            return false;

        nint window = new WindowInteropHelper(this).Handle;
        const uint MonitorDefaultToNearest = 2;
        if (monitor == 0)
            monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(window, out var current))
            return false;

        // Native window coordinates and the monitor work area are in the same coordinate space.
        var dpi = VisualTreeHelper.GetDpi(this);
        var padding = ImageViewport.Padding;
        double imageAreaWidth = (ImageViewport.ActualWidth - padding.Left - padding.Right) * dpi.DpiScaleX;
        double imageAreaHeight = (ImageViewport.ActualHeight - padding.Top - padding.Bottom) * dpi.DpiScaleY;
        double chromeWidth = Math.Max(0, current.Width - imageAreaWidth);
        double chromeHeight = Math.Max(0, current.Height - imageAreaHeight);
        var work = info.WorkArea;
        double imageHeight = Math.Max(1, work.Height - chromeHeight);
        double widthAtAspect = Math.Ceiling(chromeWidth + imageHeight * image.PixelWidth / image.PixelHeight);
        int minimumWidth = (int)Math.Min(work.Width, Math.Ceiling(MinWidth * dpi.DpiScaleX));
        int width = (int)Math.Clamp(widthAtAspect, minimumWidth, work.Width);
        int left = work.Left + (work.Width - width) / 2;

        const uint NoZOrder = 0x0004;
        const uint NoActivate = 0x0010;
        return SetWindowPos(window, 0, left, work.Top, width, work.Height, NoZOrder | NoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
