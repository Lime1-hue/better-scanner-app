using System.Globalization;
using System.Text;

namespace BetterScanner.Imaging;

/// <summary>One page image, already encoded the way it will sit inside the PDF.</summary>
public sealed class PdfImage
{
    public required byte[] Data { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string ColorSpace { get; init; }   // DeviceRGB, DeviceGray, DeviceCMYK
    public required int BitsPerComponent { get; init; }
    public required string Filter { get; init; }       // DCTDecode (JPEG) or FlateDecode
    public required int Dpi { get; init; }
    public string? Decode { get; init; }

    /// <summary>Wraps JPEG bytes as-is (no re-encoding). Null if the bytes aren't a readable JPEG.</summary>
    public static PdfImage? FromJpeg(byte[] jpeg, int dpi)
    {
        var info = ReadJpegInfo(jpeg);
        if (info is not { } i) return null;
        return new PdfImage
        {
            Data = jpeg,
            Width = i.Width,
            Height = i.Height,
            ColorSpace = i.Components switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" },
            BitsPerComponent = 8,
            Filter = "DCTDecode",
            Dpi = dpi,
            Decode = i.Components == 4 ? "[1 0 1 0 1 0 1 0]" : null,
        };
    }

    private static (int Width, int Height, int Components)? ReadJpegInfo(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
            int len = (d[i + 2] << 8) | d[i + 3];
            bool sof = m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC;
            if (sof)
            {
                int h = (d[i + 5] << 8) | d[i + 6];
                int w = (d[i + 7] << 8) | d[i + 8];
                int c = d[i + 9];
                return w > 0 && h > 0 ? (w, h, c) : null;
            }
            i += 2 + len;
        }
        return null;
    }
}

/// <summary>
/// Writes page images into a multi-page PDF. Each page is sized from its pixel
/// count and scan resolution, so a page scanned at 300 dpi prints at real size.
/// </summary>
public static class PdfWriter
{
    public static void Write(string path, IReadOnlyList<PdfImage> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("No pages to write.", nameof(pages));

        var tmp = path + ".part";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            int objCount = 2 + pages.Count * 3;
            var offsets = new long[objCount + 1];
            void A(string s) => fs.WriteAll(Encoding.ASCII.GetBytes(s));
            static string Pt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

            A("%PDF-1.4\n");
            fs.WriteAll(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

            offsets[1] = fs.Position;
            A("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{3 + i * 3} 0 R"));
            offsets[2] = fs.Position;
            A($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

            for (int i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                int pageNum = 3 + i * 3, contentNum = pageNum + 1, imgNum = pageNum + 2;
                int dpi = Math.Max(1, p.Dpi);
                var w = Pt(p.Width * 72.0 / dpi);
                var h = Pt(p.Height * 72.0 / dpi);

                offsets[pageNum] = fs.Position;
                A($"{pageNum} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
                  $"/Resources << /XObject << /Im0 {imgNum} 0 R >> >> /Contents {contentNum} 0 R >>\nendobj\n");

                var content = $"q\n{w} 0 0 {h} 0 0 cm\n/Im0 Do\nQ\n";
                offsets[contentNum] = fs.Position;
                A($"{contentNum} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");

                offsets[imgNum] = fs.Position;
                var decode = p.Decode is null ? "" : $" /Decode {p.Decode}";
                A($"{imgNum} 0 obj\n<< /Type /XObject /Subtype /Image /Width {p.Width} /Height {p.Height} " +
                  $"/ColorSpace /{p.ColorSpace} /BitsPerComponent {p.BitsPerComponent} /Filter /{p.Filter}{decode} " +
                  $"/Length {p.Data.Length} >>\nstream\n");
                fs.WriteAll(p.Data);
                A("\nendstream\nendobj\n");
            }

            long xref = fs.Position;
            A($"xref\n0 {objCount + 1}\n0000000000 65535 f \n");
            for (int n = 1; n <= objCount; n++) A($"{offsets[n]:D10} 00000 n \n");
            A($"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
        File.Move(tmp, path);
    }
}
