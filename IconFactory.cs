using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SteadyDim;

internal static class IconFactory
{
    public static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 24, 90, 170));
            graphics.FillEllipse(background, 2, 2, 28, 28);

            using var moon = new SolidBrush(Color.FromArgb(255, 245, 248, 255));
            graphics.FillEllipse(moon, 9, 6, 16, 16);

            using var cutout = new SolidBrush(Color.FromArgb(255, 24, 90, 170));
            graphics.FillEllipse(cutout, 13, 4, 16, 16);

            using var dot = new SolidBrush(Color.FromArgb(255, 245, 248, 255));
            graphics.FillEllipse(dot, 22, 8, 3, 3);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }
}
