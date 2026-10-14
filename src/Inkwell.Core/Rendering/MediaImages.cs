using System.Windows.Media;
using System.Windows.Media.Imaging;
using Inkwell.Core.Model;

namespace Inkwell.Core.Rendering;

/// <summary>Decoded pictures for drawing, cached by media id and size (decoded no larger than needed).</summary>
public sealed class MediaImages
{
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private const int MaxEntries = 160;

    public MediaImages(MediaStore store)
    {
        Store = store;
    }

    public MediaStore Store { get; set; }

    /// <summary>The picture for drawing at about <paramref name="width"/> × <paramref name="height"/> DIPs, or null if it can't be decoded.</summary>
    public ImageSource? Get(string mediaId, double width, double height)
    {
        // Decode at up to 2× the display size (sharp at 200 % zoom), in steps so small size changes reuse the bitmap.
        var target = (int)Math.Min(4096, Math.Max(32, Math.Ceiling(width * 2 / 64) * 64));
        var key = mediaId + "@" + target;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var item = Store.Get(mediaId);
        BitmapSource? bitmap = null;
        if (item != null) bitmap = Decode(item.Data, target);
        _cache[key] = bitmap;
        _order.AddLast(key);
        while (_order.Count > MaxEntries)
        {
            _cache.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }
        return bitmap;
    }

    /// <summary>Decodes picture bytes (PNG, JPEG, GIF, BMP, TIFF, ICO, WMP; EMF / WMF are not supported by WPF and return null).</summary>
    public static BitmapSource? Decode(byte[] data, int decodeWidth = 0)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();
            // Don't upscale small pictures.
            if (decodeWidth > 0 && image.PixelWidth >= decodeWidth)
            {
                var natural = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (natural.PixelWidth < decodeWidth) return Decode(data, 0);
            }
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Pixel size of a picture (0, 0 when unknown).</summary>
    public static (int Width, int Height, double DpiX, double DpiY) Measure(byte[] data)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight, frame.DpiX <= 1 ? 96 : frame.DpiX, frame.DpiY <= 1 ? 96 : frame.DpiY);
        }
        catch (Exception)
        {
            return (0, 0, 96, 96);
        }
    }

    public void Clear()
    {
        _cache.Clear();
        _order.Clear();
    }
}
