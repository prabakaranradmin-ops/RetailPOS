using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Pos.Core.Hardware.Printing;

namespace Pos.Core.Hardware.Windows;

/// <summary>
/// Saves the dots a receipt would be printed as to an image file.
/// </summary>
/// <remarks>
/// This exists so a lane can be checked without a printer. Whether Tamil came out right is not a
/// question a byte count or a character preview can answer — somebody has to look at it — and
/// asking them to burn a roll of paper to find out is a poor way to run a rollout.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ReceiptImage
{
    public static void SavePng(MonochromeBitmap dots, string path)
    {
        ArgumentNullException.ThrowIfNull(dots);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // A 4-dot white margin, because a receipt image butted against the edge of the frame is
        // harder to judge than one with paper around it.
        const int margin = 4;

        using var image = new Bitmap(dots.Width + (2 * margin), dots.Height + (2 * margin), PixelFormat.Format32bppArgb);
        var area = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                var scan0 = (byte*)data.Scan0;

                for (var y = 0; y < image.Height; y++)
                {
                    var row = scan0 + ((long)y * data.Stride);

                    for (var x = 0; x < image.Width; x++)
                    {
                        var inked = dots[x - margin, y - margin];
                        var value = inked ? (byte)0 : (byte)255;
                        var pixel = row + (x * 4);

                        pixel[0] = value;
                        pixel[1] = value;
                        pixel[2] = value;
                        pixel[3] = 255;
                    }
                }
            }
        }
        finally
        {
            image.UnlockBits(data);
        }

        image.Save(path, ImageFormat.Png);
    }
}
