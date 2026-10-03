using System.Runtime.InteropServices.WindowsRuntime;
using DeltaCore;
using DeltaModelViewer.Core;
using DeltaModelViewer.Rendering;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeltaModelViewer;

/// <summary>
/// 命令行自检（DeltaModelViewer.exe --selftest <模型名> [输出.png]）：
/// 解析模型 → 加载贴图 → D3D11 离屏渲染一帧 → 保存 PNG。用于无头验证渲染管线。
/// </summary>
public static class SelfTest
{
    public const int ImageSize = 1024;

    /// <summary>
    /// 背景图自检（DeltaModelViewer.exe --bgtest [输出.png]）：
    /// 解码嵌入背景图 → 离屏渲染一帧（只画背景）→ 保存 PNG，验证背景管线。
    /// </summary>
    public static async Task RunBgTestAsync(string outPath)
    {
        var asm = typeof(SelfTest).Assembly;
        using var resource = asm.GetManifestResourceStream("DeltaModelViewer.Assets.Warhouse_Bg_01.png")
            ?? throw new Exception("未找到嵌入背景图资源");
        using var ms = new MemoryStream();
        await resource.CopyToAsync(ms);
        var bg = await TextureLoader.LoadFromBytesAsync(ms.ToArray())
            ?? throw new Exception("背景图解码失败");

        const int w = 640, h = 360;
        using var viewport = new D3DViewport();
        viewport.InitializeOffscreen(w, h);
        viewport.SetBackground(bg);
        viewport.Render();
        var pixels = viewport.Capture();
        await PngWriter.SaveAsync(outPath, pixels, w, h);
        Console.WriteLine($"BGTEST OK | {bg.Width}x{bg.Height} -> {w}x{h} | out={outPath}");
    }

    public static async Task RunAsync(string modelName, string outPath)
    {
        AppConfig.Load();

        string? folder = PathResolver.FindModelFolder(AppConfig.ModelsRoot, modelName)
            ?? throw new Exception($"找不到模型文件夹: {modelName} (root={AppConfig.ModelsRoot}, rootExists={Directory.Exists(AppConfig.ModelsRoot)})");
        string? uemodel = Directory.GetFiles(folder, "*.uemodel")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new Exception("该文件夹中没有 .uemodel 文件");

        var (model, mesh) = await Task.Run(() =>
        {
            var m = UemodelReader.Read(File.ReadAllBytes(uemodel));
            return (m, MeshBuilder.Build(m, 0));
        });

        var (basePath, normalPath) = PathResolver.FindTextures(AppConfig.TexturesRoot, Path.GetFileName(folder));
        var baseTex = basePath is not null ? await TextureLoader.LoadAsync(basePath) : null;
        var normalTex = normalPath is not null ? await TextureLoader.LoadAsync(normalPath) : null;

        // 设 DMV_EXPORT_OBJ=路径 / DMV_EXPORT_FBX=路径 可导出当前网格（验证导出器）
        var exportObj = Environment.GetEnvironmentVariable("DMV_EXPORT_OBJ");
        if (!string.IsNullOrEmpty(exportObj))
            ModelExporter.ExportObj(mesh, exportObj, basePath);
        var exportFbx = Environment.GetEnvironmentVariable("DMV_EXPORT_FBX");
        if (!string.IsNullOrEmpty(exportFbx))
            ModelExporter.ExportFbx(mesh, exportFbx, basePath);

        using var viewport = new D3DViewport();
        viewport.InitializeOffscreen(ImageSize, ImageSize);
        viewport.SetMesh(mesh);
        viewport.SetTextures(baseTex, normalTex);
        viewport.NormalMappingEnabled = Environment.GetEnvironmentVariable("DMV_NONORMAL") is null;
        viewport.Camera.Fit(mesh.Center, mesh.Radius);
        // 默认双面（排除绕序问题）；设 DMV_CULL=back 可测背面剔除下的绕序
        viewport.DoubleSided = Environment.GetEnvironmentVariable("DMV_CULL") != "back";
        // 设 DMV_ROTATE="dx,dy"（像素）可在渲染前旋转任意角度，验证轨迹球相机
        var rotateArg = Environment.GetEnvironmentVariable("DMV_ROTATE");
        if (rotateArg is not null)
        {
            var parts = rotateArg.Split(',');
            if (parts.Length == 2 && float.TryParse(parts[0], out var rx) && float.TryParse(parts[1], out var ry))
                viewport.Camera.Rotate(rx, ry);
        }
        // 设 DMV_METAL=0-1、DMV_ROUGH=0-1 可指定金属度/粗糙度
        if (float.TryParse(Environment.GetEnvironmentVariable("DMV_METAL"), out var metal))
            viewport.Metallic = metal;
        if (float.TryParse(Environment.GetEnvironmentVariable("DMV_ROUGH"), out var rough))
            viewport.Roughness = rough;
        if (float.TryParse(Environment.GetEnvironmentVariable("DMV_ENVBOOST"), out var envBoost))
            viewport.EnvSpecBoost = envBoost;
        // 设 DMV_AZIMUTH/DMV_ELEVATION/DMV_INTENSITY 可指定灯光
        if (float.TryParse(Environment.GetEnvironmentVariable("DMV_AZIMUTH"), out var az))
            viewport.LightAzimuth = az;
        if (float.TryParse(Environment.GetEnvironmentVariable("DMV_ELEVATION"), out var el))
            viewport.LightElevation = el;
        if (float.TryParse(Environment.GetEnvironmentVariable("DMV_INTENSITY"), out var li))
            viewport.LightIntensity = li;
        // 设 DMV_BGFILE=路径 可指定自定义背景图（验证背景+环境光联动）
        var bgFile = Environment.GetEnvironmentVariable("DMV_BGFILE");
        if (!string.IsNullOrEmpty(bgFile) && File.Exists(bgFile))
        {
            var custom = await TextureLoader.LoadAsync(bgFile);
            if (custom is not null)
                viewport.SetBackground(custom);
        }
        // 设 DMV_BG=1 时同时渲染背景图（验证 IBL 环境反射）
        if (Environment.GetEnvironmentVariable("DMV_BG") is not null)
        {
            using var res = typeof(SelfTest).Assembly
                .GetManifestResourceStream("DeltaModelViewer.Assets.Warhouse_Bg_01.png");
            if (res is not null)
            {
                using var ms = new MemoryStream();
                res.CopyTo(ms);
                var bgTex = await TextureLoader.LoadFromBytesAsync(ms.ToArray());
                if (bgTex is not null)
                    viewport.SetBackground(bgTex);
            }
        }
        viewport.Render();

        var pixels = viewport.Capture();
        await PngWriter.SaveAsync(outPath, pixels, ImageSize, ImageSize);

        Console.WriteLine(
            $"SELFTEST OK | {model.Name} | verts={mesh.Vertices.Length} tris={mesh.Indices.Length / 3} | " +
            $"bbox=({mesh.Min.X:F1},{mesh.Min.Y:F1},{mesh.Min.Z:F1})-({mesh.Max.X:F1},{mesh.Max.Y:F1},{mesh.Max.Z:F1}) | " +
            $"base={(basePath is null ? "-" : Path.GetFileName(basePath))} " +
            $"normal={(normalPath is null ? "-" : Path.GetFileName(normalPath))} | out={outPath}");
    }
}
