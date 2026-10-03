using Microsoft.UI.Xaml;

namespace DeltaModelViewer.Core;

/// <summary>主题模式：跟随系统 / 浅色 / 深色。</summary>
public enum ThemeMode
{
    /// <summary>跟随系统（Windows 应用模式）。</summary>
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// 应用主题的唯一事实来源：读 / 存设置（<see cref="AppConfig.Theme"/>）+ 广播变化 + 监听系统主题变化。
///
/// 用法：
/// · 启动时 <see cref="Initialize()"/> 读一次配置；
/// · 窗口把 <c>RootGrid.RequestedTheme</c> 设成 <see cref="ToElementTheme(ThemeMode)"/>；
/// · 订阅 <see cref="ModeChanged"/>（用户手动切换：跟随系统 / 浅色 / 深色）与
///   <see cref="SystemThemeChanged"/>（System 模式下系统换主题 → 立刻跟上）。
///
/// 为什么要自己监听系统主题：WinUI 的 <c>ElementTheme.Default</c> 只在进程启动那一刻取一次
/// 系统主题，之后用户在「设置 → 个性化 → 颜色」里改浅色/深色，跑着的窗口不会自动变；
/// 所以 System 模式下要在系统颜色变化时重新解析一次并显式赋具体主题。
/// </summary>
public static class ThemeService
{
    private static bool _initialized;
    private static Windows.UI.ViewManagement.UISettings? _uiSettings;

    /// <summary>当前模式。</summary>
    public static ThemeMode Current { get; private set; } = ThemeMode.System;

    /// <summary>用户手动切换了模式（已经写进配置）。</summary>
    public static event Action<ThemeMode>? ModeChanged;

    /// <summary>系统浅色/深色变了（仅在 System 模式下有实际意义）。可能在后台线程触发。</summary>
    public static event Action? SystemThemeChanged;

    /// <summary>从配置读一次并挂上系统主题监听（幂等）。</summary>
    public static ThemeMode Initialize()
    {
        if (_initialized) return Current;
        _initialized = true;

        try { Current = Parse(AppConfig.Theme); }
        catch { Current = ThemeMode.System; }

        HookSystemWatcher();
        return Current;
    }

    /// <summary>配置字符串 → 模式（认不出来按「跟随系统」）。</summary>
    public static ThemeMode Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System
    };

    /// <summary>模式 → 配置字符串。</summary>
    public static string ToSettingValue(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "Light",
        ThemeMode.Dark => "Dark",
        _ => "System"
    };

    /// <summary>模式 → 界面上显示的中文名。</summary>
    public static string ToDisplayName(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "浅色",
        ThemeMode.Dark => "深色",
        _ => "跟随系统"
    };

    /// <summary>模式 → 根元素 RequestedTheme。</summary>
    public static ElementTheme ToElementTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>切换模式（可选写配置）并广播。</summary>
    public static void SetMode(ThemeMode mode, bool persist = true)
    {
        Initialize();
        Current = mode;

        if (persist)
        {
            try { AppConfig.SaveTheme(ToSettingValue(mode)); }
            catch (Exception ex) { Log.Error("保存主题设置失败：" + ex.Message); }
        }

        try { ModeChanged?.Invoke(mode); }
        catch (Exception ex) { Log.Error("应用主题失败：" + ex.Message); }
    }

    /// <summary>
    /// System 模式下「系统当前到底是深色还是浅色」。
    /// 取系统背景色算感知亮度（BT.601），取不到时回退应用级主题。
    /// </summary>
    public static bool IsSystemDark()
    {
        try
        {
            var ui = _uiSettings ??= new Windows.UI.ViewManagement.UISettings();
            var bg = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            int luma = (5 * bg.R + 9 * bg.G + 2 * bg.B) / 16;
            return luma < 128;
        }
        catch
        {
            // 取不到系统颜色就按应用级主题兜底
        }

        try { return Application.Current.RequestedTheme == ApplicationTheme.Dark; }
        catch { return true; }
    }

    /// <summary>窗口用：把当前模式落到某个根元素上（System 时解析成具体的浅/深，保证系统换肤能立刻跟上）。</summary>
    public static ElementTheme ResolveFor(FrameworkElement root)
    {
        var mode = ToElementTheme(Current);
        if (mode != ElementTheme.Default) return mode;
        return IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light;
    }

    private static void HookSystemWatcher()
    {
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            // 注意：这个事件在后台线程触发，订阅方自己负责切回 UI 线程
            _uiSettings.ColorValuesChanged += (_, _) =>
            {
                if (Current != ThemeMode.System) return;
                SystemThemeChanged?.Invoke();
            };
        }
        catch (Exception ex)
        {
            Log.Error("监听系统主题变化失败（不影响手动切换）：" + ex.Message);
        }
    }
}
