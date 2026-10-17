// SPDX-License-Identifier: MPL-2.0
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CliWatch.App;

internal static class WindowCapture
{
    public static void SavePng(Window window, string path)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Screenshot path is empty.", nameof(path));

        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        if (width < 8 || height < 8)
        {
            width = Math.Max(width, (int)Math.Ceiling(window.Width));
            height = Math.Max(height, (int)Math.Ceiling(window.Height));
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        rtb.Render(window);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        encoder.Save(fs);
    }
}
