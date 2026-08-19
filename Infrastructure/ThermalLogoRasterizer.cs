using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Turns the cafe's logo into ESC/POS raster print bytes — the command sequence a thermal
/// printer needs to put an actual image on the paper, as opposed to the text-only lines every
/// other line on the receipt is.
///
/// This lives on the server, not the client, for one reason: producing the bytes means
/// decoding a JPEG/PNG, resizing it, and dithering it to 1-bit — real image processing that
/// the browser can do (Canvas) but the React Native app cannot without a native image module
/// this project doesn't have. Doing it once here means every ESC/POS-emitting transport (WiFi,
/// Web Bluetooth) gets identical bytes from one small authenticated fetch, on any platform,
/// with no client-side image decoding at all.
/// </summary>
public static class ThermalLogoRasterizer
{
    /// <summary>Thermal heads only draw pure black or pure white — there is no grey ink.
    /// "Faint" logo prints on other POS software are exactly this: a dithering pattern
    /// (alternating black/white dots) that the eye blends into gauzy grey from normal reading
    /// distance, not an actual lighter shade of ink. Floyd–Steinberg is what produces that
    /// pattern instead of a blocky, aliased silhouette a hard threshold would give.</summary>
    public static byte[]? Rasterize(byte[]? logoBytes, int targetWidthDots)
    {
        if (logoBytes is null || logoBytes.Length == 0) return null;
        if (targetWidthDots <= 0) return null;

        try
        {
            using var image = Image.Load<L8>(logoBytes); // decode straight to 8-bit greyscale

            // Capped height keeps a very tall/narrow source image (a phone photo of a signboard,
            // not a cropped logo) from turning into a receipt-length wall of dots — the logo
            // is a header ornament, not the point of the printout.
            const int maxHeightDots = 160;
            var scale = (double)targetWidthDots / image.Width;
            var targetHeight = Math.Min(maxHeightDots, (int)Math.Round(image.Height * scale));
            if (targetHeight < 1) targetHeight = 1;

            image.Mutate(ctx => ctx
                .Resize(targetWidthDots, targetHeight)
                // A thermal head has no real greyscale, so pushing contrast up before dithering
                // is what keeps a logo that is mostly light grey (a thin outline mark, a pastel
                // background) from washing out to a nearly blank rectangle — dithering alone
                // reproduces the source's own contrast, it doesn't add any.
                .Contrast(1.3f));

            // Many uploaded logos are cut for a dark app icon or a social-media tile: a solid
            // black (or near-black) square behind a white/light mark. Thermal paper is white
            // and can only deposit BLACK ink — there's no "white ink" a printer can lay down —
            // so printing that kind of file as-is turns the BACKGROUND into a large solid ink
            // block with the actual logo showing through as bare paper: heavy, slow, and the
            // opposite of the light, legible mark every other thermal-printed logo on a bill
            // is going for. Auto-inverting whenever the source is majority-dark fixes exactly
            // that, and does nothing to a normal light-background logo (average stays > 128).
            if (AverageLuminance(image) < 128) image.Mutate(ctx => ctx.Invert());

            var bitmap = FloydSteinbergDither(image);
            return BuildRasterCommand(bitmap, targetWidthDots, targetHeight);
        }
        catch
        {
            // Same contract as CafeLogoLoader: a bill/receipt must never fail to print because
            // of a bad or unsupported logo file. No logo band beats no receipt.
            return null;
        }
    }

    private static double AverageLuminance(Image<L8> image)
    {
        long sum = 0;
        long count = (long)image.Width * image.Height;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                foreach (var px in row) sum += px.PackedValue;
            }
        });
        return count == 0 ? 255 : (double)sum / count;
    }

    /// <summary>Floyd–Steinberg error-diffusion dithering to 1-bit. Standard algorithm: each
    /// pixel is thresholded, and the resulting rounding error is pushed onto its unprocessed
    /// neighbours so the AVERAGE darkness across an area still matches the source image even
    /// though every individual dot is pure black or white.</summary>
    private static bool[,] FloydSteinbergDither(Image<L8> image)
    {
        var w = image.Width;
        var h = image.Height;
        var gray = new float[w, h];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < w; x++) gray[x, y] = row[x].PackedValue;
            }
        });

        // true = print a dot (dark). Threshold at mid-grey, same convention buildRasterCommand
        // below packs bits with.
        var dark = new bool[w, h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var old = gray[x, y];
                var isDark = old < 128f;
                dark[x, y] = isDark;
                var error = old - (isDark ? 0f : 255f);

                if (x + 1 < w) gray[x + 1, y] += error * 7 / 16f;
                if (y + 1 < h)
                {
                    if (x > 0) gray[x - 1, y + 1] += error * 3 / 16f;
                    gray[x, y + 1] += error * 5 / 16f;
                    if (x + 1 < w) gray[x + 1, y + 1] += error * 1 / 16f;
                }
            }
        }
        return dark;
    }

    /// <summary>
    /// Packs a 1-bit bitmap into the ESC/POS "GS v 0" raster bit-image command, the sequence
    /// every ESC/POS printer that can print images at all understands: header (mode, width in
    /// bytes, height in dots) followed by the bitmap itself, 8 dots per byte, MSB first, one
    /// row after another — the format is fixed by the command, not a choice made here.
    /// </summary>
    private static byte[] BuildRasterCommand(bool[,] dark, int width, int height)
    {
        var widthBytes = (width + 7) / 8;
        var bitmap = new byte[widthBytes * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!dark[x, y]) continue;
                var byteIndex = y * widthBytes + x / 8;
                bitmap[byteIndex] |= (byte)(0x80 >> (x % 8));
            }
        }

        var header = new byte[]
        {
            0x1D, 0x76, 0x30, 0x00, // GS v 0, m=0 (normal density)
            (byte)(widthBytes & 0xFF), (byte)((widthBytes >> 8) & 0xFF),
            (byte)(height & 0xFF), (byte)((height >> 8) & 0xFF),
        };

        var result = new byte[header.Length + bitmap.Length];
        header.CopyTo(result, 0);
        bitmap.CopyTo(result, header.Length);
        return result;
    }
}
