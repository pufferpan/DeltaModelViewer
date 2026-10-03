namespace DeltaModelViewer.Core;

/// <summary>极简日志，写入 %LOCALAPPDATA%\DeltaModelViewer\app.log，用于排查启动/渲染问题。</summary>
public static class Log
{
    private static readonly object Gate = new();

    private static string LogPath => AppPaths.LogFile;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
