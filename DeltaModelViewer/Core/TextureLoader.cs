using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeltaModelViewer.Core;

public sealed record TextureData(byte[] Pixels, uint Width, uint Height);

/// <summary>PNG 贴图解码（WIC → BGRA8），超大贴图缩到 4096 以内。</summary>
public static class TextureLoader
{
    private const uint MaxDimension = 4096;

    public static async Task<TextureData?> LoadAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            return await DecodeAsync(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从内存中的 PNG 字节解码（用于嵌入资源）。</summary>
    public static async Task<TextureData?> LoadFromBytesAsync(byte[] pngBytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(pngBytes.AsBuffer());
            stream.Seek(0);
            return await DecodeAsync(stream);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<TextureData?> DecodeAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);

        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        var transform = new BitmapTransform();
        if (width > MaxDimension || height > MaxDimension)
        {
            float scale = MaxDimension / (float)Math.Max(width, height);
            width = (uint)Math.Max(1, (int)(width * scale));
            height = (uint)Math.Max(1, (int)(height * scale));
            transform.ScaledWidth = width;
            transform.ScaledHeight = height;
        }

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return new TextureData(pixelData.DetachPixelData(), width, height);
    }
}
