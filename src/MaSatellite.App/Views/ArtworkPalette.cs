using System.Runtime.InteropServices.WindowsRuntime;
using MaSatellite.Host.Presentation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MaSatellite.App.Views;

/// <summary>Reads a representative colour from encoded artwork by decoding it to a tiny bitmap.</summary>
internal static class ArtworkPalette
{
    public static async Task<Rgb> PickAsync(byte[] encoded)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(encoded.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = 24,
            ScaledHeight = 24,
            InterpolationMode = BitmapInterpolationMode.Linear,
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return ArtworkColor.Pick(pixels.DetachPixelData());
    }
}
