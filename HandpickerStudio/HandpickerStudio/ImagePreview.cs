using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace HandpickerStudio;

public sealed class ImagePreview : Image
{
    private static readonly SemaphoreSlim Decoders = new(4);
    private CancellationTokenSource? pending;

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(nameof(FilePath), typeof(string),
        typeof(ImagePreview), new PropertyMetadata(null, OnRequestChanged));

    public static readonly DependencyProperty DecodeLimitProperty = DependencyProperty.Register(nameof(DecodeLimit), typeof(int),
        typeof(ImagePreview), new PropertyMetadata(1600, OnRequestChanged));

    public static readonly DependencyProperty CacheProperty = DependencyProperty.Register(nameof(Cache), typeof(PreviewCache),
        typeof(ImagePreview), new PropertyMetadata(null, OnRequestChanged));

    public ImagePreview()
    {
        Loaded += (_, _) => LoadPreview();
        Unloaded += (_, _) =>
        {
            pending?.Cancel();
            Source = null;
        };
    }

    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public int DecodeLimit
    {
        get => (int)GetValue(DecodeLimitProperty);
        set => SetValue(DecodeLimitProperty, value);
    }

    public PreviewCache? Cache
    {
        get => (PreviewCache?)GetValue(CacheProperty);
        set => SetValue(CacheProperty, value);
    }

    internal event EventHandler? PreviewLoaded;

    private static void OnRequestChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((ImagePreview)sender).LoadPreview();

    private async void LoadPreview()
    {
        pending?.Cancel();
        Source = null;
        ToolTip = null;
        if (!IsLoaded || FilePath is not { Length: > 0 } path)
            return;

        using var request = new CancellationTokenSource();
        pending = request;
        var token = request.Token;
        int limit = DecodeLimit;
        var cache = Cache;
        try
        {
            await Decoders.WaitAsync(token);
            try
            {
                var bitmap = await Task.Run(() => cache is null ? Decode(path, limit) : cache.Decode(path, limit, token), token);
                if (!token.IsCancellationRequested)
                {
                    Source = bitmap;
                    PreviewLoaded?.Invoke(this, EventArgs.Empty);
                }
            }
            finally { Decoders.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or
            ArgumentException or InvalidOperationException or FileFormatException)
        {
            if (!token.IsCancellationRequested)
                ToolTip = $"画像を表示できません。{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(pending, request))
                pending = null;
        }
    }

    internal static BitmapSource Decode(string path, int limit)
    {
        using var stream = CatalogService.OpenForReading(path);
        return Decode(stream, limit);
    }

    internal static BitmapSource Decode(Stream stream, int limit)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (limit > 0 && Math.Max(width, height) > limit)
        {
            if (width >= height)
                bitmap.DecodePixelWidth = limit;
            else
                bitmap.DecodePixelHeight = limit;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
