using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Rendering;
using Inkwell.Services;

namespace Inkwell.Views;

/// <summary>Pictures of first pages for the start screen (templates and recent documents), cached on disk.</summary>
internal static class Thumbnails
{
    private static readonly Dictionary<string, BitmapSource> Memory = new(StringComparer.OrdinalIgnoreCase);

    public static string Folder => Path.Combine(AppSettings.LocalDirectory, "Thumbnails");

    /// <summary>Renders the first page of a document (UI thread).</summary>
    public static BitmapSource RenderFirstPage(Document doc, int pixelWidth, bool dark = false)
    {
        using var layout = new DocumentLayout(doc, new ViewOptions { DarkPaper = dark });
        layout.Paginator.MaxPages = 1;
        layout.LayoutAll();
        var page = layout.Pages[0];
        var scale = pixelWidth / page.Size.Width;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            new PageRenderer(layout).DrawPage(dc, page);
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, (int)Math.Ceiling(page.Size.Height * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string KeyFor(string path)
    {
        var info = new FileInfo(path);
        var text = path.ToLowerInvariant() + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Take(12).Select(b => b.ToString("x2")));
    }

    /// <summary>A cached thumbnail of a file's first page, or null.</summary>
    public static BitmapSource? Cached(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var key = KeyFor(path);
            if (Memory.TryGetValue(key, out var hit)) return hit;
            var file = Path.Combine(Folder, key + ".png");
            if (!File.Exists(file)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(File.ReadAllBytes(file));
            image.EndInit();
            image.Freeze();
            Memory[key] = image;
            return image;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Read thumbnail");
            return null;
        }
    }

    /// <summary>Stores a thumbnail for a file (after saving or opening it).</summary>
    public static void Store(string path, BitmapSource bitmap)
    {
        if (AppSettings.InMemoryOnly) return;
        try
        {
            var key = KeyFor(path);
            Memory[key] = bitmap;
            Directory.CreateDirectory(Folder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            File.WriteAllBytes(Path.Combine(Folder, key + ".png"), ms.ToArray());
            // Keep the cache small: the 200 newest.
            var files = new DirectoryInfo(Folder).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(200);
            foreach (var old in files) old.Delete();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Store thumbnail");
        }
    }
}
