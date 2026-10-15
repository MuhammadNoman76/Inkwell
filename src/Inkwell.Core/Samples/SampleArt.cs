using System.IO.Compression;

namespace Inkwell.Core.Samples;

/// <summary>
/// Pictures for the demo documents, drawn in code (no image files to license): a bar chart, a landscape, a
/// portrait silhouette. Encoded as PNG without WPF, so any thread can make them.
/// </summary>
public static class SampleArt
{
    private sealed class Canvas
    {
        public Canvas(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new uint[width * height];
        }

        public int Width { get; }
        public int Height { get; }
        public uint[] Pixels { get; }

        public void Fill(uint color)
        {
            for (var i = 0; i < Pixels.Length; i++) Pixels[i] = color;
        }

        public void Rect(double x, double y, double w, double h, uint color)
        {
            for (var py = Math.Max(0, (int)y); py < Math.Min(Height, (int)(y + h)); py++)
                for (var px = Math.Max(0, (int)x); px < Math.Min(Width, (int)(x + w)); px++)
                    Blend(px, py, color, 1);
        }

        public void RoundRect(double x, double y, double w, double h, double r, uint color)
        {
            for (var py = Math.Max(0, (int)y); py < Math.Min(Height, (int)Math.Ceiling(y + h)); py++)
                for (var px = Math.Max(0, (int)x); px < Math.Min(Width, (int)Math.Ceiling(x + w)); px++)
                {
                    var cx = MathEx.Clamp(px + 0.5, x + r, x + w - r);
                    var cy = MathEx.Clamp(py + 0.5, y + r, y + h - r);
                    var d = Math.Sqrt((px + 0.5 - cx) * (px + 0.5 - cx) + (py + 0.5 - cy) * (py + 0.5 - cy));
                    var cover = MathEx.Clamp(r - d + 0.5, 0, 1);
                    if (cover > 0) Blend(px, py, color, cover);
                }
        }

        public void Circle(double cx, double cy, double r, uint color)
        {
            for (var py = Math.Max(0, (int)(cy - r - 1)); py < Math.Min(Height, (int)(cy + r + 2)); py++)
                for (var px = Math.Max(0, (int)(cx - r - 1)); px < Math.Min(Width, (int)(cx + r + 2)); px++)
                {
                    var d = Math.Sqrt((px + 0.5 - cx) * (px + 0.5 - cx) + (py + 0.5 - cy) * (py + 0.5 - cy));
                    var cover = MathEx.Clamp(r - d + 0.5, 0, 1);
                    if (cover > 0) Blend(px, py, color, cover);
                }
        }

        /// <summary>Fills below a curve y = f(x) down to the bottom.</summary>
        public void Hill(Func<double, double> top, uint color)
        {
            for (var px = 0; px < Width; px++)
            {
                var y = top(px);
                for (var py = Math.Max(0, (int)y); py < Height; py++) Blend(px, py, color, py == (int)y ? 1 - (y - Math.Floor(y)) : 1);
            }
        }

        public void Line(double x1, double y1, double x2, double y2, double width, uint color)
        {
            var minX = (int)Math.Max(0, Math.Min(x1, x2) - width);
            var maxX = (int)Math.Min(Width - 1, Math.Max(x1, x2) + width);
            var minY = (int)Math.Max(0, Math.Min(y1, y2) - width);
            var maxY = (int)Math.Min(Height - 1, Math.Max(y1, y2) + width);
            var dx = x2 - x1;
            var dy = y2 - y1;
            var len2 = dx * dx + dy * dy;
            for (var py = minY; py <= maxY; py++)
                for (var px = minX; px <= maxX; px++)
                {
                    var t = len2 == 0 ? 0 : MathEx.Clamp(((px + 0.5 - x1) * dx + (py + 0.5 - y1) * dy) / len2, 0, 1);
                    var ex = x1 + t * dx - (px + 0.5);
                    var ey = y1 + t * dy - (py + 0.5);
                    var d = Math.Sqrt(ex * ex + ey * ey);
                    var cover = MathEx.Clamp(width / 2 - d + 0.5, 0, 1);
                    if (cover > 0) Blend(px, py, color, cover);
                }
        }

        public void VerticalGradient(uint top, uint bottom)
        {
            for (var py = 0; py < Height; py++)
            {
                var c = Mix(top, bottom, py / (double)Math.Max(1, Height - 1));
                for (var px = 0; px < Width; px++) Pixels[py * Width + px] = c;
            }
        }

        private void Blend(int x, int y, uint color, double cover)
        {
            var a = ((color >> 24) & 0xFF) / 255.0 * cover;
            if (a <= 0) return;
            var i = y * Width + x;
            Pixels[i] = Mix(Pixels[i] | 0xFF000000, color | 0xFF000000, a);
        }
    }

    private static uint Mix(uint a, uint b, double t)
    {
        uint C(int shift) => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t) & 0xFF;
        return 0xFF000000 | (C(16) << 16) | (C(8) << 8) | C(0);
    }

    /// <summary>A bar chart with a trend line (quarterly figures).</summary>
    public static byte[] BarChart(int width = 1200, int height = 640)
    {
        var c = new Canvas(width, height);
        c.Fill(0xFFFFFFFF);
        double[] values = { 38, 46, 44, 57, 63, 61, 72, 81 };
        var left = width * 0.08;
        var bottom = height * 0.88;
        var top = height * 0.1;
        var plotW = width * 0.86;
        for (var g = 0; g <= 4; g++)
        {
            var y = bottom - (bottom - top) * g / 4.0;
            c.Rect(left, y, plotW, 2, 0xFFE4E7EC);
        }
        var slot = plotW / values.Length;
        var points = new List<(double X, double Y)>();
        for (var i = 0; i < values.Length; i++)
        {
            var h = (bottom - top) * values[i] / 90.0;
            var x = left + slot * i + slot * 0.18;
            var color = i < 4 ? 0xFF9DB7E0u : 0xFF2F5DA8u;
            c.RoundRect(x, bottom - h, slot * 0.64, h, 8, color);
            points.Add((x + slot * 0.32, bottom - h - 26));
        }
        for (var i = 1; i < points.Count; i++) c.Line(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y, 5, 0xFFE08A2Eu);
        foreach (var (x, y) in points) c.Circle(x, y, 9, 0xFFE08A2Eu);
        c.Rect(left, bottom, plotW, 3, 0xFF8A93A3);
        return Png(c);
    }

    /// <summary>Hills, a lake and the sun: a calm landscape for a floating picture.</summary>
    public static byte[] Landscape(int width = 960, int height = 640)
    {
        var c = new Canvas(width, height);
        c.VerticalGradient(0xFF9CC8EE, 0xFFF6E7D2);
        c.Circle(width * 0.72, height * 0.32, height * 0.11, 0xFFFFD27A);
        c.Hill(x => height * 0.52 + Math.Sin(x / width * 5.1) * height * 0.06 - x / width * height * 0.08, 0xFF7FA6A3);
        c.Hill(x => height * 0.6 + Math.Sin(x / width * 7.3 + 1.2) * height * 0.05, 0xFF4E7F6E);
        c.Hill(x => height * 0.74 + Math.Sin(x / width * 3.0 + 0.4) * height * 0.025, 0xFF6FA3C7);
        for (var i = 0; i < 9; i++) c.Rect(width * (0.1 + i * 0.09), height * (0.78 + (i % 3) * 0.04), width * 0.05, 3, 0xCCFFFFFF);
        c.Hill(x => height * 0.9 + Math.Sin(x / width * 9 + 2) * height * 0.015, 0xFF3D5F4E);
        return Png(c);
    }

    /// <summary>A neutral portrait placeholder (head and shoulders) for the résumé.</summary>
    public static byte[] Portrait(int size = 480)
    {
        var c = new Canvas(size, size);
        c.VerticalGradient(0xFFDDE6F0, 0xFFB9C7D8);
        c.Circle(size * 0.5, size * 0.4, size * 0.17, 0xFF6C7A8E);
        c.Circle(size * 0.5, size * 1.02, size * 0.38, 0xFF6C7A8E);
        return Png(c);
    }

    // ------------------------------------------------------------------ PNG

    private static byte[] Png(Canvas c)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)c.Width);
        WriteBE(ihdr, 4, (uint)c.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        Chunk(ms, "IHDR", ihdr);
        var raw = new byte[(c.Width * 3 + 1) * c.Height];
        var k = 0;
        for (var y = 0; y < c.Height; y++)
        {
            raw[k++] = 0;
            for (var x = 0; x < c.Width; x++)
            {
                var p = c.Pixels[y * c.Width + x];
                raw[k++] = (byte)(p >> 16);
                raw[k++] = (byte)(p >> 8);
                raw[k++] = (byte)p;
            }
        }
        using var z = new MemoryStream();
        z.WriteByte(0x78);
        z.WriteByte(0x9C);
        using (var deflate = new DeflateStream(z, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(raw, 0, raw.Length);
        var adler = Adler32(raw);
        var tail = new byte[4];
        WriteBE(tail, 0, adler);
        z.Write(tail, 0, 4);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        var crc = Crc32(typeBytes, data);
        var c = new byte[4];
        WriteBE(c, 0, crc);
        s.Write(c, 0, 4);
    }

    private static void WriteBE(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
