using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sentinel.Models;
using static Sentinel.Services.NativeMethods;

namespace Sentinel.Services;

public static class ScreenCapture
{
    public sealed record MonitorSlot(IntPtr Handle, string Name, RECT Rect, bool Primary);

    public static List<MonitorSlot> Monitors()
    {
        var list = new List<MonitorSlot>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var primary = (info.dwFlags & 1) != 0;
                var index = list.Count + 1;
                var name = $"{(primary ? "Primary" : "Display " + index)} Â· {info.rcMonitor.Width}Ã—{info.rcMonitor.Height}";
                list.Add(new MonitorSlot(hMonitor, name, info.rcMonitor, primary));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static BitmapSource? Capture(RECT rect, int maxWidth = 0)
    {
        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0) return null;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var old = SelectObject(memDc, bitmap);
        BitmapSource? result = null;
        try
        {
            var copied = BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SRCCOPY | CAPTUREBLT);
            if (!copied) return null;

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };
            var buffer = new byte[width * height * 4];
            var lines = GetDIBits(memDc, bitmap, 0, (uint)height, buffer, ref info, DIB_RGB_COLORS);
            if (lines == 0) return null;

            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, width * 4);
            source.Freeze();
            if (maxWidth > 0 && width > maxWidth)
            {
                var scale = (double)maxWidth / width;
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                result = scaled;
            }
            else
            {
                result = source;
            }
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static string SavePng(BitmapSource image, string directory, string prefix = "shot")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..4]}.png");
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return path;
    }

    public static List<CaptureResult> CaptureAll(string directory, string trigger, int maxWidth = 0)
    {
        var results = new List<CaptureResult>();
        var slots = Monitors();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var image = Capture(slot.Rect, maxWidth);
            if (image == null) continue;
            string path;
            try
            {
                path = SavePng(image, directory, trigger);
            }
            catch
            {
                continue;
            }
            results.Add(new CaptureResult
            {
                Path = path,
                Monitor = i + 1,
                MonitorName = slot.Name,
                Width = (int)image.PixelWidth,
                Height = (int)image.PixelHeight,
                Trigger = trigger
            });
        }
        return results;
    }

    public static (int Width, int Height) ImageSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static BitmapImage? LoadThumbnail(string path, int decodeWidth = 520)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
