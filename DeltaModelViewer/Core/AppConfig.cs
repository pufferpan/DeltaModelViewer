using System.Text.Json;

namespace DeltaModelViewer.Core;

/// <summary>应用配置：模型与贴图根目录，保存在 %LOCALAPPDATA%\DeltaModelViewer\config.json。</summary>
public static class AppConfig
{
    // 开发机回退默认（无安装版 Data 时使用）
    private const string FallbackModelsRoot = @"E:\deltamodel\DeltaForce\Content\Models\Props\Collect";
    private const string FallbackTexturesRoot =
        @"F:\FModel-dec-2025\FModel\bin\Release\net8.0-windows\win-x64\Output\Exports\DeltaForce\Content\GenTexDirHD\Game\Models\Props\Collect";

    /// <summary>默认模型目录：优先探测安装目录 exe 旁的 Data\Models，其次开发机路径。</summary>
    public static string DefaultModelsRoot => ProbeDataDir("Models") ?? FallbackModelsRoot;

    /// <summary>默认贴图目录：优先探测安装目录 exe 旁的 Data\Textures，其次开发机路径。</summary>
    public static string DefaultTexturesRoot => ProbeDataDir("Textures") ?? FallbackTexturesRoot;

    /// <summary>安装版数据目录探测（exe 位于安装目录根，数据在安装目录\Data\<子目录>）。</summary>
    private static string? ProbeDataDir(string sub)
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, "Data", sub);
            if (Directory.Exists(probe)) return probe;
            // 开发布局兼容：bin\...\win-x64 上两级
            var probeUp = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Data", sub));
            if (Directory.Exists(probeUp)) return probeUp;
        }
        catch
        {
            // 探测失败回退默认值
        }
        return null;
    }

    public static string ModelsRoot { get; private set; } = DefaultModelsRoot;
    public static string TexturesRoot { get; private set; } = DefaultTexturesRoot;

    /// <summary>从未保存过配置（首次使用）。</summary>
    public static bool IsFirstRun => !File.Exists(ConfigPath);

    private static string ConfigPath => AppPaths.ConfigFile;

    private sealed class ConfigFile
    {
        public string? ModelsRoot { get; set; }
        public string? TexturesRoot { get; set; }
        public string? BackgroundPath { get; set; }
        public bool NoticeShown { get; set; }
        public string? Theme { get; set; }
    }

    /// <summary>自定义背景图路径（null = 使用内置默认背景）。</summary>
    public static string? BackgroundPath { get; private set; }

    /// <summary>主题模式字符串："System" / "Light" / "Dark"（解析见 <see cref="ThemeService"/>）。</summary>
    public static string Theme { get; private set; } = "System";

    /// <summary>开屏免费提示是否已显示过（只显示一次）。</summary>
    public static bool NoticeShown { get; private set; }

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var cfg = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(ConfigPath));
            if (cfg is null) return;
            if (!string.IsNullOrWhiteSpace(cfg.ModelsRoot)) ModelsRoot = Normalize(cfg.ModelsRoot);
            if (!string.IsNullOrWhiteSpace(cfg.TexturesRoot)) TexturesRoot = Normalize(cfg.TexturesRoot);
            if (!string.IsNullOrWhiteSpace(cfg.BackgroundPath)) BackgroundPath = Normalize(cfg.BackgroundPath);
            if (!string.IsNullOrWhiteSpace(cfg.Theme)) Theme = cfg.Theme.Trim();
            NoticeShown = cfg.NoticeShown;
        }
        catch
        {
            // 配置损坏时回退默认值
        }
    }

    public static void Save(string modelsRoot, string texturesRoot)
    {
        ModelsRoot = modelsRoot;
        TexturesRoot = texturesRoot;
        WriteConfig();
    }

    /// <summary>保存自定义背景图路径（不覆盖目录配置）。</summary>
    public static void SaveBackground(string? path)
    {
        BackgroundPath = path;
        WriteConfig();
    }

    /// <summary>保存主题模式（不覆盖目录 / 背景配置）。</summary>
    public static void SaveTheme(string theme)
    {
        Theme = string.IsNullOrWhiteSpace(theme) ? "System" : theme.Trim();
        WriteConfig();
    }

    private static void WriteConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(
                new ConfigFile
                {
                    ModelsRoot = ModelsRoot,
                    TexturesRoot = TexturesRoot,
                    BackgroundPath = BackgroundPath,
                    NoticeShown = NoticeShown,
                    Theme = Theme,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败不影响本次会话
        }
    }

    /// <summary>统一路径分隔符为正斜杠兼容（Windows 反斜杠，消除安装器/手写配置的斜杠差异）。</summary>
    private static string Normalize(string path) => path.Replace('/', '\\');

    /// <summary>标记开屏免费提示已显示（立即写入配置）。</summary>
    public static void MarkNoticeShown()
    {
        NoticeShown = true;
        WriteConfig();
    }

    /// <summary>删除配置文件并恢复默认值（下次启动重新进入首次使用引导）。</summary>
    public static void Reset()
    {
        ModelsRoot = DefaultModelsRoot;
        TexturesRoot = DefaultTexturesRoot;
        BackgroundPath = null;
        NoticeShown = false;
        Theme = "System";
        try
        {
            if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);
        }
        catch
        {
            // 删除失败仍回退默认值
        }
    }
}
