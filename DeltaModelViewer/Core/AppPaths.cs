namespace DeltaModelViewer.Core;

/// <summary>
/// 本地数据目录（配置 / 日志 / 缩略图缓存）的统一入口。
/// Alpha 定时过期版使用独立目录 <c>%LOCALAPPDATA%\DeltaModelViewer-Alpha</c>，
/// 与正式版互不干扰：两个版本可以同时安装，配置与缩略图缓存不会互相覆盖。
/// </summary>
public static class AppPaths
{
#if ALPHA_EXPIRY
    public const string FolderName = "DeltaModelViewer-Alpha";
#else
    public const string FolderName = "DeltaModelViewer";
#endif

    /// <summary><c>%LOCALAPPDATA%\{FolderName}</c></summary>
    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    public static string ConfigFile => Path.Combine(LocalRoot, "config.json");

    public static string LogFile => Path.Combine(LocalRoot, "app.log");

    public static string ThumbDir => Path.Combine(LocalRoot, "thumbs");
}
