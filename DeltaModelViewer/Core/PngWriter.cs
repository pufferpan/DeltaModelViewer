using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeltaModelViewer.Core;

/// <summary>PNG 编码写入（BGRA8 像素）。</summary>
public static class PngWriter
{
    public static async Task SaveAsync(string path, byte[] bgra, uint width, uint height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, bgra);
        await encoder.FlushAsync();

        await using var file = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(file);
    }
}
