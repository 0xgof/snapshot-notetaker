using System.Windows;
using System.Windows.Media.Imaging;

namespace SnapshotNotetaker.Rendering;

/// <summary>Average luminance of an image region, used to pick readable label colors.</summary>
public sealed class ImageSampler
{
    private readonly BitmapSource _bitmap;

    public ImageSampler(BitmapSource bitmap) => _bitmap = bitmap;

    public double? Luminance(Rect area)
    {
        int x0 = (int)Math.Floor(Math.Max(0, area.Left));
        int y0 = (int)Math.Floor(Math.Max(0, area.Top));
        int x1 = (int)Math.Ceiling(Math.Min(_bitmap.PixelWidth, area.Right));
        int y1 = (int)Math.Ceiling(Math.Min(_bitmap.PixelHeight, area.Bottom));
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return null;

        int stride = w * 4;
        var buffer = new byte[stride * h];
        _bitmap.CopyPixels(new Int32Rect(x0, y0, w, h), buffer, stride, 0);

        int step = Math.Max(1, (int)Math.Sqrt(w * h / 1024.0));
        double sum = 0;
        int count = 0;
        for (int y = 0; y < h; y += step)
        {
            int row = y * stride;
            for (int x = 0; x < w; x += step)
            {
                int i = row + x * 4; // BGRA
                sum += 0.0722 * buffer[i] + 0.7152 * buffer[i + 1] + 0.2126 * buffer[i + 2];
                count++;
            }
        }
        return count == 0 ? null : sum / count / 255.0;
    }
}
