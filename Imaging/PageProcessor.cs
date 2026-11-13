using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace BetterScanner.Imaging;

/// <param name="PageSize">auto | a4 | letter | folio | legal | full</param>
public sealed record PageOptions(string PageSize, bool BlackWhite, bool SkipBlank, int Dpi);

/// <summary>
/// Software page sizing, ported from an internal document system. The printer
/// captures its whole scan area; here we trim the empty background, optionally
/// fit the page onto a named paper size, and drop blank pages.
/// </summary>
public static class PageProcessor
{
    private sealed record Analysis(Rectangle? Content, bool Blank);

    /// <summary>Processes one scanned JPEG. Returns null when the page is blank and blanks are skipped.</summary>
    public static PdfImage? Process(string jpegPath, PageOptions o)
    {
        byte[] original = File.ReadAllBytes(jpegPath);
        using var ms = new MemoryStream(original);
        using var src = new Bitmap(ms);

        var a = Analyze(src);
        if (o.SkipBlank && a.Blank) return null;

        var crop = new Rectangle(0, 0, src.Width, src.Height);
        bool changed = false;
        if (o.PageSize != "full" && a.Content is { } c)
        {
            crop = c;
            changed = true;
        }

        Size? canvas = PageCanvas(o.PageSize, o.Dpi);
        if (canvas is { } cs && (Math.Abs(cs.Width - crop.Width) > 1 || Math.Abs(cs.Height - crop.Height) > 1))
            changed = true;
        else
            canvas = null;

        // Untouched colour/grey page: embed the printer's JPEG as-is (no quality loss).
        if (!changed && !o.BlackWhite && PdfImage.FromJpeg(original, o.Dpi) is { } passthrough)
            return passthrough;

        using var page = Render(src, crop, canvas);
        return o.BlackWhite
            ? ToBitonal(page, o.Dpi)
            : PdfImage.FromJpeg(EncodeJpeg(page, 90), o.Dpi)!;
    }

    /// <summary>Paper size in pixels at the given dpi, or null for auto/full.</summary>
    private static Size? PageCanvas(string pageSize, int dpi)
    {
        (double w, double h)? inches = pageSize switch
        {
            "a4" => (8.27, 11.69),
            "letter" => (8.5, 11.0),   // PH "Short"
            "folio" => (8.5, 13.0),    // PH "Long"
            "legal" => (8.5, 14.0),
            _ => null,
        };
        return inches is { } i ? new Size((int)Math.Round(i.w * dpi), (int)Math.Round(i.h * dpi)) : null;
    }

    private static Bitmap Render(Bitmap src, Rectangle crop, Size? canvas)
    {
        int w = canvas?.Width ?? crop.Width, h = canvas?.Height ?? crop.Height;
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.White);

        Rectangle target;
        if (canvas is null)
        {
            target = new Rectangle(0, 0, w, h);
        }
        else
        {
            // Centre on the paper; shrink only when the document is bigger than it.
            double sc = Math.Min(1.0, Math.Min((double)w / crop.Width, (double)h / crop.Height));
            int nw = Math.Max(1, (int)Math.Round(crop.Width * sc));
            int nh = Math.Max(1, (int)Math.Round(crop.Height * sc));
            target = new Rectangle((w - nw) / 2, (h - nh) / 2, nw, nh);
        }

        bool scaled = target.Width != crop.Width || target.Height != crop.Height;
        g.InterpolationMode = scaled ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = scaled ? PixelOffsetMode.HighQuality : PixelOffsetMode.Half;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges when scaling
        g.DrawImage(src, target, crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    /// <summary>Luminance of a downscaled copy (max 1400 px), for cheap analysis.</summary>
    private static byte[] SmallLuma(Bitmap src, int dw, int dh)
    {
        var lum = new byte[dw * dh];
        using var small = new Bitmap(dw, dh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, new Rectangle(0, 0, dw, dh));
        }
        CopyLuma(small, lum);
        return lum;
    }

    private static void CopyLuma(Bitmap bmp24, byte[] lum)
    {
        int w = bmp24.Width, h = bmp24.Height;
        var data = bmp24.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
                int o = y * w;
                for (int x = 0, i = 0; x < w; x++, i += 3)
                    lum[o + x] = (byte)((row[i] * 114 + row[i + 1] * 587 + row[i + 2] * 299) / 1000); // BGR
            }
        }
        finally
        {
            bmp24.UnlockBits(data);
        }
    }

    /// <summary>
    /// Finds the inked area: adaptive threshold from the paper's own brightness,
    /// and a row/column only counts when it carries a real amount of ink, so
    /// specks and JPEG noise don't blow the box up to the full frame.
    /// </summary>
    private static Analysis Analyze(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        if (w < 8 || h < 8) return new Analysis(null, false);

        double scale = Math.Min(1.0, 1400.0 / Math.Max(w, h));
        int dw = Math.Max(1, (int)Math.Round(w * scale));
        int dh = Math.Max(1, (int)Math.Round(h * scale));
        var lum = SmallLuma(src, dw, dh);

        var hist = new int[256];
        foreach (var l in lum) hist[l]++;
        int total = dw * dh, bg = 255, acc = 0;
        for (int l = 255; l >= 0; l--)
        {
            acc += hist[l];
            if (acc >= total * 0.15) { bg = l; break; } // 85th percentile = paper
        }
        int threshold = Num.Clamp(bg - 45, 50, 220);

        var rowInk = new int[dh];
        var colInk = new int[dw];
        int mx = (int)(dw * 0.02), my = (int)(dh * 0.02);
        long innerInk = 0;
        for (int y = 0; y < dh; y++)
        {
            int o = y * dw;
            for (int x = 0; x < dw; x++)
            {
                if (lum[o + x] >= threshold) continue;
                rowInk[y]++;
                colInk[x]++;
                if (x >= mx && x < dw - mx && y >= my && y < dh - my) innerInk++;
            }
        }

        // Blank = practically no ink away from the edges (edge shadows ignored).
        bool blank = innerInk < total * 0.0005;

        int minRow = Math.Max(2, (int)Math.Round(dw * 0.004));
        int minCol = Math.Max(2, (int)Math.Round(dh * 0.004));
        int minx = -1, miny = -1, maxx = -1, maxy = -1;
        for (int y = 0; y < dh; y++)
        {
            if (rowInk[y] < minRow) continue;
            if (miny < 0) miny = y;
            maxy = y;
        }
        for (int x = 0; x < dw; x++)
        {
            if (colInk[x] < minCol) continue;
            if (minx < 0) minx = x;
            maxx = x;
        }
        if (minx < 0 || miny < 0) return new Analysis(null, true);

        int bw = maxx - minx + 1, bh = maxy - miny + 1;
        if (bw >= dw * 0.985 && bh >= dh * 0.985) return new Analysis(null, blank); // already fills the frame

        // Small breathing margin so text isn't flush against the edge.
        int padS = Math.Max(4, (int)Math.Round(0.02 * Math.Max(dw, dh)));
        int pad = (int)Math.Ceiling(padS / scale);
        int x0 = Math.Max(0, (int)Math.Floor(minx / scale) - pad);
        int y0 = Math.Max(0, (int)Math.Floor(miny / scale) - pad);
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(maxx / scale) + pad);
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(maxy / scale) + pad);
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;

        // Refuse a suspicious crop that would throw away almost the whole page.
        if (cw < 1 || ch < 1 || (long)cw * ch < (long)w * h * 0.04) return new Analysis(null, blank);

        return new Analysis(new Rectangle(x0, y0, cw, ch), blank);
    }

    private static byte[] EncodeJpeg(Bitmap bmp, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, p);
        return ms.ToArray();
    }

    /// <summary>Pure black &amp; white (1 bit per pixel, Otsu threshold) — tiny, crisp text PDFs.</summary>
    private static PdfImage ToBitonal(Bitmap bmp24, int dpi)
    {
        int w = bmp24.Width, h = bmp24.Height;
        var lum = new byte[w * h];
        CopyLuma(bmp24, lum);

        var hist = new long[256];
        foreach (var l in lum) hist[l]++;

        // Otsu splits ink from paper well on a normal page, but on a nearly blank
        // one it lands right at paper level and turns noise black. Cap it well
        // below the paper's brightness (85th percentile) so only real ink is black.
        long acc = 0;
        int paper = 255;
        for (int l = 255; l >= 0; l--)
        {
            acc += hist[l];
            if (acc >= lum.Length * 0.15) { paper = l; break; }
        }
        int t = Math.Min(Otsu(hist, lum.Length), paper - 50);

        int rowBytes = (w + 7) / 8;
        var packed = new byte[rowBytes * h];
        for (int y = 0; y < h; y++)
        {
            int o = y * w, r = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                if (lum[o + x] >= t) packed[r + (x >> 3)] |= (byte)(0x80 >> (x & 7)); // 1 = white
            }
        }

        return new PdfImage
        {
            Data = ZlibCompress(packed),
            Width = w,
            Height = h,
            ColorSpace = "DeviceGray",
            BitsPerComponent = 1,
            Filter = "FlateDecode",
            Dpi = dpi,
        };
    }

    /// <summary>zlib format (what PDF's FlateDecode expects): 2-byte header + deflate + Adler-32.</summary>
    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.WriteAll(data);

        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        uint adler = (b << 16) | a;
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static int Otsu(long[] hist, long total)
    {
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0, best = -1;
        long wB = 0;
        int threshold = 128;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += i * (double)hist[i];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = i + 1; }
        }
        return Num.Clamp(threshold, 60, 225);
    }
}
