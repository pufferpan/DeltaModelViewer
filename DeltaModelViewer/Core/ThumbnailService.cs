using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using DeltaCore;
using DeltaModelViewer.Rendering;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DeltaModelViewer.Core;

/// <summary>
/// 模型缩略图服务：后台离屏渲染 256×256 → PNG 缓存（%LOCALAPPDATA%\DeltaModelViewer\thumbs）→ BitmapImage。
/// 首次生成后永久缓存，后续启动直接读文件。
/// </summary>
public sealed class ThumbnailService
{
    public const int ThumbSize = 256;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();
    private D3DViewport? _viewport;

    public string ThumbDir { get; }

    public ThumbnailService()
    {
        ThumbDir = AppPaths.ThumbDir;
        Directory.CreateDirectory(ThumbDir);
    }

    /// <summary>在 UI 线程调用：返回可直接绑定的 BitmapImage；生成失败返回 null。</summary>
    public async Task<ImageSource?> GetThumbnailAsync(string relativePath)
    {
        var bytes = await GetOrCreatePngAsync(relativePath);
        if (bytes is null) return null;
        return await PngBytesToImageAsync(bytes);
    }

    private Task<byte[]?> GetOrCreatePngAsync(string relativePath)
    {
        var task = _inflight.GetOrAdd(relativePath, _ => Task.Run(() => CreatePngAsync(relativePath)));
        // 请求完成后从去重表移除；失败不缓存，下次可重试
        _ = task.ContinueWith(_ => _inflight.TryRemove(relativePath, out _), TaskScheduler.Default!);
        return task;
    }

    private async Task<byte[]?> CreatePngAsync(string relativePath)
    {
        var cachePath = Path.Combine(ThumbDir, SafeCacheName(relativePath) + ".png");
        if (File.Exists(cachePath))
            return await File.ReadAllBytesAsync(cachePath);

        await _gate.WaitAsync();
        try
        {
            if (File.Exists(cachePath))
                return await File.ReadAllBytesAsync(cachePath);

            var folder = Path.Combine(AppConfig.ModelsRoot, relativePath);
            if (!Directory.Exists(folder)) return null;
            var uemodel = Directory.GetFiles(folder, "*.uemodel")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (uemodel is null) return null;

            var model = UemodelReader.Read(await File.ReadAllBytesAsync(uemodel));
            var mesh = MeshBuilder.Build(model, 0);

            _viewport ??= CreateViewport();
            var (basePath, normalPath) = PathResolver.FindTextures(AppConfig.TexturesRoot, relativePath);
            var baseTex = basePath is null ? null : await TextureLoader.LoadAsync(basePath);
            var normalTex = normalPath is null ? null : await TextureLoader.LoadAsync(normalPath);

            _viewport.SetMesh(mesh);
            _viewport.SetTextures(baseTex, normalTex);
            _viewport.NormalMappingEnabled = true;
            _viewport.DoubleSided = true; // 缩略图避免单面薄片模型露洞
            _viewport.Camera.Fit(mesh.Center, mesh.Radius);
            _viewport.Render();

            var pixels = _viewport.Capture();
            await PngWriter.SaveAsync(cachePath, pixels, ThumbSize, ThumbSize);
            return await File.ReadAllBytesAsync(cachePath);
        }
        catch (Exception ex)
        {
            Log.Error($"缩略图生成失败 {relativePath}: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>相对路径转安全缓存文件名（目录分隔符替换为下划线）。</summary>
    private static string SafeCacheName(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');

    private static D3DViewport CreateViewport()
    {
        var viewport = new D3DViewport();
        viewport.InitializeOffscreen(ThumbSize, ThumbSize);
        return viewport;
    }

    private static async Task<ImageSource?> PngBytesToImageAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage { DecodePixelWidth = 160 }; // 显示尺寸即解码尺寸，节约内存
        await image.SetSourceAsync(stream);
        return image;
    }

    public void ClearCache()
    {
        try
        {
            foreach (var file in Directory.GetFiles(ThumbDir))
                File.Delete(file);
        }
        catch
        {
            // 清理失败不影响使用
        }
    }
}
