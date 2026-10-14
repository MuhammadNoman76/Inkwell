using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Rendering;

namespace Inkwell.Tests;

/// <summary>Renders laid-out pages to PNG files (for looking at layouts while developing; INKWELL_RENDER_OUT=folder).</summary>
internal static class RenderHelper
{
    public static string? OutputFolder => Environment.GetEnvironmentVariable("INKWELL_RENDER_OUT");

    public static void SavePages(DocumentLayout layout, string name, int maxPages = 4, double scale = 1.0, bool marks = false)
    {
        var folder = OutputFolder;
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder!);
        var renderer = new PageRenderer(layout);
        for (var i = 0; i < Math.Min(maxPages, layout.Pages.Count); i++)
        {
            var page = layout.Pages[i];
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                renderer.DrawPage(dc, page, new RenderExtras { FormattingMarks = marks });
            }
            var w = (int)Math.Ceiling(page.Size.Width * scale);
            var h = (int)Math.Ceiling(Math.Min(page.Size.Height, 4000) * scale);
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(folder!, $"{name}-p{i + 1}.png"));
            encoder.Save(fs);
        }
    }
}
