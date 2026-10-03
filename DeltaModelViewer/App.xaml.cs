using DeltaModelViewer.Core;
using Microsoft.UI.Xaml;

namespace DeltaModelViewer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // 全局异常兜底日志（配合进程退出码定位崩溃）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { Log.Error($"未处理异常(域): {e.ExceptionObject}"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Log.Error($"未观察任务异常: {e.Exception}"); e.SetObserved(); } catch { }
        };
        UnhandledException += (_, e) =>
        {
            try { Log.Error($"未处理异常(XAML): {e.Exception}"); } catch { }
        };
        NativeCrashLogger.Install();
        Log.Info("应用启动");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs();

#if ALPHA_EXPIRY
        // Alpha 定时过期版 · 命令行诊断：DeltaModelViewer.exe --timecheck [报告文件]
        // 输出 NTP 校时结果（状态 / 网络时间 / 本机偏差 / 服务器）后退出，退出码 0=通过 3=未通过。
        int timecheck = Array.IndexOf(cmdArgs, "--timecheck");
        if (timecheck >= 0)
        {
            string? reportPath = timecheck + 1 < cmdArgs.Length && !cmdArgs[timecheck + 1].StartsWith("--")
                ? cmdArgs[timecheck + 1]
                : null;

            // --at / --drift 仅用于边界自测：按指定时间点复算判定结论，不改变实际校验行为
            DateTimeOffset? simulateAt = null;
            int atIndex = Array.IndexOf(cmdArgs, "--at");
            if (atIndex >= 0 && atIndex + 1 < cmdArgs.Length &&
                DateTimeOffset.TryParse(cmdArgs[atIndex + 1], out var parsedAt))
                simulateAt = parsedAt;

            TimeSpan? simulateDrift = null;
            int driftIndex = Array.IndexOf(cmdArgs, "--drift");
            if (driftIndex >= 0 && driftIndex + 1 < cmdArgs.Length &&
                double.TryParse(cmdArgs[driftIndex + 1], out var driftSeconds))
                simulateDrift = TimeSpan.FromSeconds(driftSeconds);

            Environment.Exit(await TimeGuard.RunDiagnosticAsync(reportPath, simulateAt, simulateDrift));
            return;
        }

        // Alpha 定时过期版 · 无头模式（--bgtest / --selftest）同样先做一次 NTP 网络时间校验，
        // 未通过直接退出（退出码 3），避免绕过时效限制。
        if (Array.IndexOf(cmdArgs, "--bgtest") >= 0 || Array.IndexOf(cmdArgs, "--selftest") >= 0)
        {
            var gate = await TimeGuard.CheckAsync();
            if (!gate.IsOk)
            {
                Log.Error($"ALPHA 无头模式校验未通过：{gate.Status} {gate.Detail}");
                Environment.Exit(3);
                return;
            }
        }
        // 图形界面的校验闸门在 MainWindow 的加载层里执行（不额外创建窗口）
#endif

        // 无头自检模式：DeltaModelViewer.exe --selftest <模型名> [输出.png]
        int bgtest = Array.IndexOf(cmdArgs, "--bgtest");
        if (bgtest >= 0)
        {
            string outPath = bgtest + 1 < cmdArgs.Length
                ? cmdArgs[bgtest + 1]
                : Path.Combine(Path.GetTempPath(), "dmv_bgtest.png");
            try
            {
                await SelfTest.RunBgTestAsync(outPath);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(outPath + ".err", ex.ToString()); } catch { }
                Log.Error($"bgtest 失败: {ex}");
                Environment.Exit(2);
            }
            return;
        }

        int selftest = Array.IndexOf(cmdArgs, "--selftest");
        if (selftest >= 0 && selftest + 1 < cmdArgs.Length)
        {
            string modelName = cmdArgs[selftest + 1];
            string outPath = selftest + 2 < cmdArgs.Length
                ? cmdArgs[selftest + 2]
                : Path.Combine(Path.GetTempPath(), "dmv_selftest.png");
            try
            {
                await SelfTest.RunAsync(modelName, outPath);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(outPath + ".err", ex.ToString()); } catch { }
                Log.Error($"selftest 失败: {ex}");
                Environment.Exit(2);
            }
            return;
        }

        // 配置要在主窗口构造之前读进来：主题模式（config.json 的 Theme）由 MainWindow 构造函数里的
        // InitTheme() 读取，晚于窗口构造再 Load 的话，启动时会先按默认「跟随系统」起一次、
        // 用户选的浅色/深色要等下一次才生效。
        AppConfig.Load();

        _window = new MainWindow();
        _window.Activate();
    }
}
