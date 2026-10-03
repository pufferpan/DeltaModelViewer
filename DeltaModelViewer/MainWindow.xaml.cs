using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeltaCore;
using DeltaModelViewer.Core;
using DeltaModelViewer.Rendering;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;

namespace DeltaModelViewer;

public sealed partial class MainWindow : Window
{
    private D3DViewport? _viewport;
    private List<GalleryItem> _galleryItems = new();
    private ThumbnailService? _thumbnails;
    private RenderMesh? _currentMesh;
    private TextureData? _currentBaseColor;
    private TextureData? _currentNormal;
    private string _currentFolder = "";
    private string? _autoLoadName;
    private string? _currentBaseColorPath;
    private string _currentModelName = "model";
    private bool _autoLoadDone;
    private bool _suppressFileBox;
    private bool _isLoading;
    private bool _firstFrameLogged;

    // 鼠标拖拽状态
    private bool _pointerCaptured;
    private Point _lastPointer;

    public MainWindow()
    {
        InitializeComponent();
        InitTheme();
        Title = "UEFormat格式查看工具";
        ApplyWindowIcon();

#if ALPHA_EXPIRY
        // Alpha 定时过期版：标题与状态栏角标标注版本与过期日（Release 正式版不含此段）
        Title = "UEFormat格式查看工具 · Alpha测试版（2026-09-24 过期）";
        UpdateAlphaBadge();
#endif

        // 窗口尺寸钳制到主屏工作区（避免底部状态栏被任务栏遮挡）
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(1280, Math.Max(600, area.Width - 60));
        int h = Math.Min(800, Math.Max(500, area.Height - 60));
        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(
            area.X + Math.Max(0, (area.Width - w) / 2),
            area.Y + Math.Max(0, (area.Height - h) / 2 - 20)));

        // 自定义标题栏：内容延伸到标题栏区域，玻璃胶囊下方的透明条作为拖拽区
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonHoverForegroundColor = Colors.White;

        var cmd = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(cmd, "--autoload");
        if (i >= 0 && i + 1 < cmd.Length) _autoLoadName = cmd[i + 1];

        if (Content is FrameworkElement root)
            root.Loaded += OnLoaded;
    }

    /// <summary>设置窗口 / 任务栏图标（exe 已内嵌图标，这里显式再设一次，保证 WinUI 窗口标题栏也用同一张图）。</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
                Log.Info("窗口图标已设置: " + icon);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"设置窗口图标失败: {ex.Message}");
        }
    }

    /// <summary>加载背景图（自定义优先，回退内置）；同时更新环境光。</summary>
    private async Task LoadBackgroundAsync()
    {
        // 优先使用用户自定义背景；不存在或解码失败则回退内置默认背景
        if (!string.IsNullOrEmpty(AppConfig.BackgroundPath) && File.Exists(AppConfig.BackgroundPath))
        {
            var custom = await TextureLoader.LoadAsync(AppConfig.BackgroundPath);
            if (custom is not null)
            {
                _viewport?.SetBackground(custom);
                Log.Info($"自定义背景已加载 ({custom.Width}×{custom.Height}): {AppConfig.BackgroundPath}");
                return;
            }
            Log.Error($"自定义背景解码失败，回退默认背景: {AppConfig.BackgroundPath}");
        }

        try
        {
            var asm = typeof(MainWindow).Assembly;
            using var resource = asm.GetManifestResourceStream("DeltaModelViewer.Assets.Warhouse_Bg_01.png");
            if (resource is null)
            {
                Log.Error("未找到背景图嵌入资源 Warhouse_Bg_01.png");
                return;
            }
            using var ms = new MemoryStream();
            await resource.CopyToAsync(ms);
            var bg = await TextureLoader.LoadFromBytesAsync(ms.ToArray());
            if (bg is null)
            {
                Log.Error("背景图解码失败");
                return;
            }
            _viewport?.SetBackground(bg);
            Log.Info($"背景图已加载 ({bg.Width}×{bg.Height})");
        }
        catch (Exception ex)
        {
            Log.Error($"背景图加载失败: {ex}");
        }
    }

    // ---------- 灯光与背景 ----------

    private void LightFlyout_Opening(object sender, object e)
    {
        if (_viewport is null)
            return;
        LightAzimuthSlider.Value = _viewport.LightAzimuth;
        LightElevationSlider.Value = _viewport.LightElevation;
        LightIntensitySlider.Value = _viewport.LightIntensity;
        LightAzimuthValue.Text = $"{_viewport.LightAzimuth:F0}°";
        LightElevationValue.Text = $"{_viewport.LightElevation:F0}°";
        LightIntensityValue.Text = $"{_viewport.LightIntensity:F1}";
    }

    private void LightSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider || _viewport is null)
            return;
        switch (slider.Tag as string)
        {
            case "azimuth":
                _viewport.LightAzimuth = (float)e.NewValue;
                LightAzimuthValue.Text = $"{e.NewValue:F0}°";
                break;
            case "elevation":
                _viewport.LightElevation = (float)e.NewValue;
                LightElevationValue.Text = $"{e.NewValue:F0}°";
                break;
            case "intensity":
                _viewport.LightIntensity = (float)e.NewValue;
                LightIntensityValue.Text = $"{e.NewValue:F1}";
                break;
        }
    }

    /// <summary>更换背景图：SetBackground 会同步更新背景显示、环境光平均色与环境反射。</summary>
    private async void BackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            var bg = await TextureLoader.LoadAsync(file.Path);
            if (bg is null)
            {
                ShowNotice(InfoBarSeverity.Error, "无法读取该图片，请选择 PNG/JPG 格式");
                return;
            }

            _viewport?.SetBackground(bg);
            AppConfig.SaveBackground(file.Path);
            ShowNotice(InfoBarSeverity.Success, "背景已更换，环境光已同步更新");
            Log.Info($"背景已更换: {file.Path} ({bg.Width}×{bg.Height})");
        }
        catch (Exception ex)
        {
            Log.Error($"更换背景失败: {ex}");
            ShowNotice(InfoBarSeverity.Error, "更换背景失败");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
#if ALPHA_EXPIRY
        // Alpha 定时过期版：先在启动加载层里完成 NTP 网络时间校验，未通过就不进入主界面
        if (!await RunAlphaStartupCheckAsync())
            return;
#endif
        if (_viewport is null)
        {
            try
            {
                _viewport = new D3DViewport();
                _viewport.Initialize(ViewportPanel);
                _viewport.NormalMappingEnabled = NormalSwitch.IsOn;
                _viewport.DoubleSided = CullSwitch.IsOn;
                _viewport.Metallic = (float)MetallicSlider.Value;
                _viewport.Roughness = (float)RoughnessSlider.Value;
                CompositionTarget.Rendering += OnRendering;
                Log.Info("D3D11 渲染器初始化完成（交换链已挂载到 SwapChainPanel）");
            }
            catch (Exception ex)
            {
                Log.Error($"渲染器初始化失败: {ex}");
                ShowNotice(InfoBarSeverity.Error, $"无法初始化 3D 渲染器: {ex.Message}");
            }
        }

        AppConfig.Load();
        _thumbnails = new ThumbnailService();
        await LoadBackgroundAsync();
        SplashStatus.Text = "正在准备模型库…";
        BuildGallery();

        bool needsSetup = AppConfig.IsFirstRun
            || !Directory.Exists(AppConfig.ModelsRoot)
            || !Directory.Exists(AppConfig.TexturesRoot);
        if (needsSetup)
        {
            if (!AppConfig.IsFirstRun)
                ShowNotice(InfoBarSeverity.Warning, "模型/贴图目录不存在，请重新配置路径");
            ShowFirstRun();
        }
        else
        {
            StatusText.Text = $"就绪 · 模型目录: {AppConfig.ModelsRoot}";
        }

        await ShowOnceNoticeAsync();

#if ALPHA_EXPIRY
        StartAlphaWatchdog();
#endif

        if (_autoLoadName is not null && !_autoLoadDone && _viewport is not null && !needsSetup)
        {
            _autoLoadDone = true;
            await LoadAsync(_autoLoadName);
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_firstFrameLogged && _viewport is { IsInitialized: true })
        {
            _firstFrameLogged = true;
            Log.Info("首帧已渲染");
            Log.Info($"主题：{ThemeService.ToDisplayName(ThemeService.Current)}（实际视觉 {(RootGrid.ActualTheme == ElementTheme.Dark ? "深色" : "浅色")}）");
        }
        _viewport?.Render();
    }

    // ---------- 材质滑条 ----------

    private void MetallicSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        MetallicValue.Text = e.NewValue.ToString("0.00");
        if (_viewport is not null)
            _viewport.Metallic = (float)e.NewValue;
    }

    private void RoughnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        RoughnessValue.Text = e.NewValue.ToString("0.00");
        if (_viewport is not null)
            _viewport.Roughness = (float)e.NewValue;
    }

    // ---------- 加载 ----------

    private async void FileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFileBox || _currentFolder.Length == 0) return;
        if (FileBox.SelectedItem is string file)
            await LoadFileAsync(Path.Combine(_currentFolder, file));
    }

    private async Task LoadAsync(string input)
    {
        if (_isLoading || string.IsNullOrWhiteSpace(input)) return;
        try
        {
            string? folder = PathResolver.FindModelFolder(AppConfig.ModelsRoot, input);
            if (folder is null)
            {
                ShowNotice(InfoBarSeverity.Error, $"找不到模型「{input.Trim()}」");
                return;
            }

            var files = Directory.GetFiles(folder, "*.uemodel")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                ShowNotice(InfoBarSeverity.Error, $"文件夹 {Path.GetFileName(folder)} 中没有 .uemodel 文件");
                return;
            }

            _currentFolder = folder;

            _suppressFileBox = true;
            FileBox.ItemsSource = files.Select(Path.GetFileName).ToList();
            FileBox.SelectedIndex = 0;
            _suppressFileBox = false;

            await LoadFileAsync(files[0]);
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, ex.Message);
        }
    }

    private async Task LoadFileAsync(string uemodelPath)
    {
        if (_isLoading) return;
        _isLoading = true;
        ShowLoading(true);
        try
        {
            var (model, mesh) = await Task.Run(() =>
            {
                var bytes = File.ReadAllBytes(uemodelPath);
                var m = UemodelReader.Read(bytes);
                var built = MeshBuilder.Build(m, 0);
                return (m, built);
            });

            // 贴图目录与模型目录结构同名：用相对路径定位（支持子文件夹）
            string relativePath = Path.GetRelativePath(
                AppConfig.ModelsRoot, Path.GetDirectoryName(uemodelPath) ?? "");
            var (basePath, normalPath) = PathResolver.FindTextures(AppConfig.TexturesRoot, relativePath);
            _currentBaseColorPath = basePath;

            var baseTex = basePath is not null ? await TextureLoader.LoadAsync(basePath) : null;
            var normalTex = normalPath is not null ? await TextureLoader.LoadAsync(normalPath) : null;

            _currentMesh = mesh;
            _currentBaseColor = baseTex;
            _currentNormal = normalTex;
            ReapplyTextures();

            if (_viewport is not null)
            {
                _viewport.SetMesh(mesh);
                _viewport.Camera.Fit(mesh.Center, mesh.Radius);
            }

            Log.Info($"已加载 {Path.GetFileName(uemodelPath)}: {mesh.Vertices.Length} 顶点 / {mesh.Indices.Length / 3} 三角形, " +
                     $"baseColor={(basePath is null ? "无" : Path.GetFileName(basePath))}, " +
                     $"normal={(normalPath is null ? "无" : Path.GetFileName(normalPath))}");

            StatusText.Text =
                $"{model.Name} · {mesh.Vertices.Length:N0} 顶点 · {mesh.Indices.Length / 3:N0} 三角形 · {mesh.Ranges.Count} 材质组";
            HintPill.Visibility = Visibility.Collapsed;
            GalleryPanel.Visibility = Visibility.Collapsed;

            // 玻璃信息卡片
            InfoName.Text = model.Name;
            _currentModelName = model.Name;
            InfoStats.Text = $"{mesh.Vertices.Length:N0} 顶点 · {mesh.Indices.Length / 3:N0} 三角形\n{mesh.Ranges.Count} 材质组 · {mesh.UvChannelCount} UV 通道";
            InfoBase.Text = basePath is null ? "（未找到）" : Path.GetFileName(basePath);
            InfoNormal.Text = normalPath is null ? "（未找到）" : Path.GetFileName(normalPath);
            var size = mesh.Max - mesh.Min;
            InfoSize.Text = $"尺寸 ≈ {size.X:F1} × {size.Y:F1} × {size.Z:F1}";
            InfoCard.Visibility = Visibility.Visible;

            var missing = new List<string>();
            if (baseTex is null) missing.Add("BaseColor 贴图");
            if (normalTex is null) missing.Add("法线贴图");
            if (missing.Count == 0)
            {
                ShowNotice(InfoBarSeverity.Success, $"已加载 {Path.GetFileName(uemodelPath)}（含 BaseColor 与法线贴图）");
            }
            else
            {
                ShowNotice(InfoBarSeverity.Warning,
                    $"已加载 {Path.GetFileName(uemodelPath)}（缺少: {string.Join("、", missing)}）");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"加载失败 {uemodelPath}: {ex}");
            ShowNotice(InfoBarSeverity.Error, $"加载失败: {ex.Message}");
        }
        finally
        {
            ShowLoading(false);
            _isLoading = false;
        }
    }

    private void ReapplyTextures()
    {
        if (_viewport is null) return;
        _viewport.NormalMappingEnabled = NormalSwitch.IsOn;
        _viewport.SetTextures(_currentBaseColor, NormalSwitch.IsOn ? _currentNormal : null);
    }

    private void ShowLoading(bool loading)
    {
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = loading ? "加载中…" : "从模型库选择模型开始预览";
        // 加载中或有待加载提示时显示胶囊；已加载模型后收起，避免遮挡画面中央
        HintPill.Visibility = loading || _currentMesh is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowNotice(InfoBarSeverity severity, string message)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    // ---------- 工具栏与模型库 ----------

    private void GalleryToggle_Click(object sender, RoutedEventArgs e)
    {
        GalleryPanel.Visibility = GalleryPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void NormalSwitch_Toggled(object sender, RoutedEventArgs e) => ReapplyTextures();

    private void CullSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_viewport is not null) _viewport.DoubleSided = CullSwitch.IsOn;
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMesh is not null && _viewport is not null)
            _viewport.Camera.Fit(_currentMesh.Center, _currentMesh.Radius);
    }

    /// <summary>递归扫描模型根目录下所有子文件夹，构建模型库并后台生成缩略图。</summary>
    private void BuildGallery()
    {
        try
        {
            _galleryItems = Directory.EnumerateDirectories(
                    AppConfig.ModelsRoot, "*", SearchOption.AllDirectories)
                .Where(HasUemodel)
                .Select(d => new GalleryItem
                {
                    RelativePath = Path.GetRelativePath(AppConfig.ModelsRoot, d),
                    FolderName = Path.GetFileName(d),
                })
                .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            _galleryItems = new List<GalleryItem>();
        }
        Gallery.ItemsSource = _galleryItems;
        GalleryCount.Text = $"{_galleryItems.Count} 个模型";
        Log.Info($"模型库扫描完成：{_galleryItems.Count} 个模型文件夹（递归）");
        SearchBox.Text = ""; // 重建后清空搜索过滤

        // 诊断：确认滚轮事件能到达画廊（只记录一次）
        // 此时窗口根已 Loaded，GridView 模板已应用，可直接找到内部 ScrollViewer
        _galleryScroll = FindDescendant<ScrollViewer>(Gallery);
        Log.Info(_galleryScroll is null
            ? "画廊 ScrollViewer 未找到"
            : $"画廊 ScrollViewer 就绪 extent={_galleryScroll.ExtentHeight:F0}");

        // 列表已就绪即可显示主界面；缩略图在后台继续生成（进度显示在画廊标题）
        _ = SweepThumbnailsAsync();
        CloseSplash();
    }

    private static bool HasUemodel(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.uemodel").Any();
        }
        catch
        {
            return false;
        }
    }

    private ScrollViewer? _galleryScroll;

    /// <summary>画廊滚轮：托管代码手动滚动，阻断 WinAppSDK 2.2 默认滚轮路径（原生崩溃源）。</summary>
    private void Gallery_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_galleryScroll is null)
        {
            Log.Info("画廊滚轮: ScrollViewer 未找到!");
            return;
        }
        int delta = e.GetCurrentPoint(Gallery).Properties.MouseWheelDelta;
        _galleryScroll.ChangeView(null, Math.Max(0, _galleryScroll.VerticalOffset - delta), null, true);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>按顺序后台生成全部缩略图（有缓存则直接读），生成期间 UI 不阻塞。</summary>
    private async Task SweepThumbnailsAsync()
    {
        if (_thumbnails is null) return;
        _thumbTotal = _galleryItems.Count;
        _thumbDone = 0;
        foreach (var item in _galleryItems.ToList())
        {
            if (item.Thumbnail is not null)
            {
                _thumbDone++;
                UpdateSplashProgress();
                continue;
            }
            item.Thumbnail = await _thumbnails.GetThumbnailAsync(item.RelativePath);
            _thumbDone++;
            UpdateSplashProgress();
        }
        GalleryCount.Text = $"{_galleryItems.Count} 个模型";
        CloseSplash();
    }

    private int _thumbTotal;
    private int _thumbDone;
    private bool _splashClosed;

    private void UpdateSplashProgress()
    {
        GalleryCount.Text = $"缩略图 {_thumbDone}/{_thumbTotal}";
        if (_thumbTotal <= 0) return;
        SplashProgress.IsIndeterminate = false;
        SplashProgress.Value = _thumbDone * 100.0 / _thumbTotal;
        SplashStatus.Text = $"正在生成模型缩略图 {_thumbDone}/{_thumbTotal}";
    }

    /// <summary>加载完成：450ms 淡出加载层，显示主界面（幂等）。</summary>
    private void CloseSplash()
    {
        if (_splashClosed) return;
        _splashClosed = true;
        Log.Info("加载完成，显示主界面");

        var animation = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(450)),
        };
        Storyboard.SetTarget(animation, LoadingLayer);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            LoadingLayer.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private async void Gallery_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GalleryItem item)
            await LoadAsync(item.RelativePath);
    }

    private void GalleryRefresh_Click(object sender, RoutedEventArgs e)
    {
        _thumbnails?.ClearCache();
        foreach (var item in _galleryItems)
            item.Thumbnail = null;
        _ = SweepThumbnailsAsync();
        ShowNotice(InfoBarSeverity.Informational, "正在重新生成缩略图…");
    }

    // ---------- 模型搜索 ----------

    /// <summary>实时过滤画廊；空关键字恢复完整列表。</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        if (q.Length == 0)
        {
            Gallery.ItemsSource = _galleryItems;
            GalleryCount.Text = $"{_galleryItems.Count} 个模型";
            return;
        }

        var filtered = FilterModels(q);
        Gallery.ItemsSource = filtered;
        GalleryCount.Text = $"{filtered.Count} / {_galleryItems.Count} 个模型";
    }

    /// <summary>回车加载最佳匹配模型。</summary>
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        var match = FilterModels(SearchBox.Text.Trim()).FirstOrDefault();
        if (match is not null)
            _ = LoadAsync(match.RelativePath);
    }

    private List<GalleryItem> FilterModels(string q) =>
        _galleryItems
            .Where(i => i.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || i.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.RelativePath.Length)
            .ToList();

    // ---------- 模型导出 ----------

    private async void ExportFbx_Click(object sender, RoutedEventArgs e) => await ExportAsync("fbx");

    private async void ExportObj_Click(object sender, RoutedEventArgs e) => await ExportAsync("obj");

    private async Task ExportAsync(string format)
    {
        if (_currentMesh is null)
        {
            ShowNotice(InfoBarSeverity.Warning, "请先加载一个模型再导出");
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.FileTypeChoices.Add(
                format == "fbx" ? "FBX 模型文件" : "OBJ 模型文件",
                new List<string> { "." + format });
            picker.SuggestedFileName = SanitizeFileName(_currentModelName) + "." + format;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            await Task.Run(() =>
            {
                if (format == "fbx")
                    ModelExporter.ExportFbx(_currentMesh, file.Path, _currentBaseColorPath);
                else
                    ModelExporter.ExportObj(_currentMesh, file.Path, _currentBaseColorPath);
            });

            ShowNotice(InfoBarSeverity.Success, $"已导出: {Path.GetFileName(file.Path)}");
            Log.Info($"模型已导出 ({format.ToUpperInvariant()}): {file.Path}");
        }
        catch (Exception ex)
        {
            Log.Error($"导出失败: {ex}");
            ShowNotice(InfoBarSeverity.Error, $"导出失败: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "model" : name;
    }

    // ---------- 全屏水印 ----------

    /// <summary>窗口尺寸变化时重建水印网格。</summary>
    private void WatermarkCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => BuildWatermark();

    /// <summary>铺满全屏的半透明水印（斜向重复排列，随窗口尺寸自适应）。</summary>
    private void BuildWatermark()
    {
        WatermarkCanvas.Children.Clear();
        double w = WatermarkCanvas.ActualWidth;
        double h = WatermarkCanvas.ActualHeight;
        if (w < 60 || h < 60)
            return;

        const string text = "软件制作：河豚潘PufferPan 禁止用于非法用途 AlPhaTest";
        const double cellW = 640, cellH = 260;
        var brush = new SolidColorBrush(Colors.White);

        for (double y = -cellH; y < h + cellH; y += cellH)
        {
            for (double x = -cellW; x < w + cellW; x += cellW)
            {
                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 16,
                    Foreground = brush,
                    RenderTransform = new RotateTransform { Angle = -24 },
                };
                WatermarkCanvas.Children.Add(tb);
                Canvas.SetLeft(tb, x);
                Canvas.SetTop(tb, y);
            }
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync()
    {
        var modelsBox = new TextBox
        {
            Text = AppConfig.ModelsRoot,
            PlaceholderText = "例如 E:\\deltamodel\\DeltaForce\\Content\\Models\\Props\\Collect",
        };
        var texturesBox = new TextBox
        {
            Text = AppConfig.TexturesRoot,
            PlaceholderText = "例如 F:\\FModel-dec-2025\\...\\GenTexDirHD\\Game\\Models\\Props\\Collect",
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "模型目录（含各模型子文件夹）", Foreground = SecondaryBrush() });
        panel.Children.Add(modelsBox);
        panel.Children.Add(new TextBlock { Text = "贴图目录（BaseColor / 法线贴图，结构与模型目录同名）", Foreground = SecondaryBrush() });
        panel.Children.Add(texturesBox);

        var dialog = new ContentDialog
        {
            Title = "路径设置",
            Content = panel,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "重置设置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            AppConfig.Save(modelsBox.Text.Trim(), texturesBox.Text.Trim());
            BuildGallery();
            ShowNotice(InfoBarSeverity.Success, "路径已保存");
        }
        else if (result == ContentDialogResult.Secondary)
        {
            // 重置：删除配置文件、恢复默认目录，并重新进入首次使用引导
            AppConfig.Reset();
            BuildGallery();
            ShowNotice(InfoBarSeverity.Informational, "设置已重置");
            ShowFirstRun();
        }
    }

    // ---------- 首次使用引导 ----------

    /// <summary>显示首次使用引导层：预填当前配置并淡入。</summary>
    private void ShowFirstRun()
    {
        FirstRunModelsBox.Text = AppConfig.ModelsRoot;
        FirstRunTexturesBox.Text = AppConfig.TexturesRoot;
        FirstRunModelsHint.Visibility = Visibility.Collapsed;
        FirstRunTexturesHint.Visibility = Visibility.Collapsed;
        FirstRunLayer.Opacity = 0;
        FirstRunLayer.Visibility = Visibility.Visible;
        Log.Info("显示首次使用引导");

        var animation = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
        };
        Storyboard.SetTarget(animation, FirstRunLayer);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void FirstRunStart_Click(object sender, RoutedEventArgs e)
    {
        string models = FirstRunModelsBox.Text.Trim();
        string textures = FirstRunTexturesBox.Text.Trim();
        bool modelsOk = Directory.Exists(models);
        bool texturesOk = Directory.Exists(textures);
        FirstRunModelsHint.Visibility = modelsOk ? Visibility.Collapsed : Visibility.Visible;
        FirstRunTexturesHint.Visibility = texturesOk ? Visibility.Collapsed : Visibility.Visible;
        if (!modelsOk || !texturesOk)
            return;

        AppConfig.Save(models, textures);
        BuildGallery();
        FirstRunLayer.Visibility = Visibility.Collapsed;
        StatusText.Text = $"就绪 · 模型目录: {AppConfig.ModelsRoot}";
        ShowNotice(InfoBarSeverity.Success, "配置已保存，开始使用");
        Log.Info($"首次使用引导完成 models={models} textures={textures}");
    }

    private void FirstRunSkip_Click(object sender, RoutedEventArgs e)
    {
        // 默认目录可用时静默保存；否则仅本次跳过，下次启动仍会引导
        if (Directory.Exists(AppConfig.DefaultModelsRoot) && Directory.Exists(AppConfig.DefaultTexturesRoot))
            AppConfig.Save(AppConfig.DefaultModelsRoot, AppConfig.DefaultTexturesRoot);
        FirstRunLayer.Visibility = Visibility.Collapsed;
        Log.Info("首次使用引导：跳过");
    }

    private async void FirstRunModelsBrowse_Click(object sender, RoutedEventArgs e) =>
        await PickFolderAsync(FirstRunModelsBox);

    private async void FirstRunTexturesBrowse_Click(object sender, RoutedEventArgs e) =>
        await PickFolderAsync(FirstRunTexturesBox);

    private async Task PickFolderAsync(TextBox target)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            target.Text = folder.Path;
    }

    private static SolidColorBrush SecondaryBrush() => new(Microsoft.UI.Colors.Gray);

    // ---------- 主题（跟随系统 / 浅色 / 深色） ----------

    /// <summary>
    /// 主题接入：读配置里的模式 → 落到 <c>RootGrid.RequestedTheme</c>；
    /// 之后再订阅「用户手动切换」与「系统换肤」两个事件。
    /// System 模式下 <c>ElementTheme.Default</c> 只在进程启动时取一次系统主题，
    /// 所以系统换肤时会用 <see cref="ThemeService.ResolveFor"/> 解析成具体主题再赋一次。
    /// </summary>
    private void InitTheme()
    {
        try
        {
            ThemeService.Initialize();
            ThemeService.ModeChanged += _ => ApplyTheme();
            ThemeService.SystemThemeChanged += () => DispatcherQueue.TryEnqueue(ApplyTheme);
            ApplyTheme();
        }
        catch (Exception ex)
        {
            Log.Error("初始化主题失败（回退系统默认）：" + ex.Message);
        }
    }

    /// <summary>把当前主题模式落到根元素上，并同步菜单里的选中项与图标。</summary>
    private void ApplyTheme()
    {
        try
        {
            RootGrid.RequestedTheme = ThemeService.ResolveFor(RootGrid);

            // 菜单选中态 / 图标（AppWindow 标题栏按钮颜色也跟着主题走）
            ThemeSystemItem.IsChecked = ThemeService.Current == ThemeMode.System;
            ThemeLightItem.IsChecked = ThemeService.Current == ThemeMode.Light;
            ThemeDarkItem.IsChecked = ThemeService.Current == ThemeMode.Dark;

            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            ThemeIcon.Glyph = dark ? "\uE708" : "\uE706";

            if (AppWindow.TitleBar.ExtendsContentIntoTitleBar)
            {
                AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
                AppWindow.TitleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            }

            // 记一行"真正拿到的颜色"（不是应该是什么），排「改了主题没生效」时一眼能看出来
            Log.Info($"主题：{ThemeService.ToDisplayName(ThemeService.Current)} → 实际 {(dark ? "深色" : "浅色")}；" +
                     $"面板底色 {Describe(TopBar.Background)}，状态栏文字 {Describe(StatusText.Foreground)}");
        }
        catch (Exception ex)
        {
            Log.Error("应用主题失败：" + ex.Message);
        }
    }

    /// <summary>日志用：把纯色画刷描述成 #AARRGGBB。</summary>
    private static string Describe(Brush? brush)
        => brush is SolidColorBrush s ? $"#{s.Color.A:X2}{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "(非纯色画刷)";

    /// <summary>外观菜单：跟随系统 / 浅色 / 深色。</summary>
    private void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var mode = ThemeService.Parse(tag);
        ThemeService.SetMode(mode);
        Log.Info($"主题切换为：{ThemeService.ToDisplayName(mode)}");
    }

    /// <summary>开屏提示：软件完全免费，仅显示一次（先落盘标记，防重复弹出）。</summary>
    private async Task ShowOnceNoticeAsync()
    {
        if (AppConfig.NoticeShown) return;
        AppConfig.MarkNoticeShown();
        try
        {
            var dlg = new ContentDialog
            {
                Title = "重要提示",
                Content = new TextBlock
                {
                    Text = "该软件完全免费，如果该软件你是花钱买来的，请劈头盖地骂死这个倒卖软件的狗",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15,
                    LineHeight = 26,
                },
                PrimaryButtonText = "知道了",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            await dlg.ShowAsync();
        }
        catch
        {
            // 对话框异常不阻塞启动
        }
    }


    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // 兜底：吃掉所有未被其他元素消费的滚轮事件。
        // WinAppSDK 2.2 的默认滚轮处理路径存在原生崩溃（退出码 127），必须绕开。
        e.Handled = true;
    }

    // ---------- 鼠标交互 ----------

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_viewport is null) return;
        var point = e.GetCurrentPoint(ViewportPanel);
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _pointerCaptured = ViewportPanel.CapturePointer(e.Pointer);
            _lastPointer = point.Position;
            e.Handled = true;
        }
    }

    private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerCaptured || _viewport is null) return;
        var point = e.GetCurrentPoint(ViewportPanel);
        float dx = (float)(point.Position.X - _lastPointer.X);
        float dy = (float)(point.Position.Y - _lastPointer.Y);
        _lastPointer = point.Position;

        if (point.Properties.IsLeftButtonPressed)
            _viewport.Camera.Rotate(dx, dy);
        else if (point.Properties.IsRightButtonPressed)
            _viewport.Camera.Pan(dx, dy, (float)ViewportPanel.ActualHeight);
        e.Handled = true;
    }

    private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerCaptured)
        {
            ViewportPanel.ReleasePointerCapture(e.Pointer);
            _pointerCaptured = false;
            e.Handled = true;
        }
    }

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_viewport is null) return;
        int delta = e.GetCurrentPoint(ViewportPanel).Properties.MouseWheelDelta;
        _viewport.Camera.Zoom(MathF.Pow(1.0015f, -delta));
        e.Handled = true;
    }

    private void Viewport_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 指针捕获期间会持续收到事件，无需处理
    }

    // ---------- Alpha 定时过期版 · 启动校验闸门 + 运行期时间看门狗 ----------

#if ALPHA_EXPIRY
    private StackPanel? _alphaBlockPanel;
    private TextBlock? _alphaBlockText;
    private TaskCompletionSource<bool>? _alphaDecision;
    private DispatcherQueueTimer? _alphaWatchdog;
    private int _alphaWatchdogFailures;

    /// <summary>
    /// 启动闸门（复用启动加载层，不额外创建窗口）：
    /// 校验通过返回 true 继续加载；未通过则停留在统一的过期提示上，只允许「重新校验 / 退出程序」。
    /// </summary>
    private async Task<bool> RunAlphaStartupCheckAsync()
    {
        ShowAlphaChecking();
        while (true)
        {
            TimeCheckResult result;
            try
            {
                result = await TimeGuard.CheckAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"ALPHA 启动校验异常: {ex}");
                result = new TimeCheckResult
                {
                    Status = TimeCheckStatus.Unreachable,
                    LocalUtc = DateTimeOffset.UtcNow,
                    Detail = "时间校验过程发生异常：" + ex.Message,
                };
            }

            if (result.IsOk)
            {
                Log.Info("ALPHA 启动校验通过，继续加载界面");
                if (_alphaBlockPanel is not null) _alphaBlockPanel.Visibility = Visibility.Collapsed;
                UpdateAlphaBadge();
                LoadRing.Visibility = Visibility.Visible;
                LoadRing.IsActive = true;
                SplashProgress.Visibility = Visibility.Visible;
                SplashStatus.Text = "正在准备模型库…";
                return true;
            }

            Log.Error($"ALPHA 启动拦截：status={result.Status} detail={result.Detail}");
            ShowAlphaBlocked(result);
            _alphaDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool retry = await _alphaDecision.Task;
            _alphaDecision = null;
            if (!retry)
            {
                Log.Error("ALPHA 用户选择退出程序");
                Environment.Exit(0);
                return false;
            }
            ShowAlphaChecking();
        }
    }

    /// <summary>更新状态栏右侧的 Alpha 角标（显示距过期剩余天数，校时通过后才会带上天数）。</summary>
    private void UpdateAlphaBadge()
    {
        var ntpNow = TimeGuard.LastResult?.NtpUtc;
        int daysLeft = ntpNow is null
            ? -1
            : (int)Math.Floor(TimeGuard.ExpiryUtc.Subtract(ntpNow.Value).TotalDays);
        AlphaBadge.Text = daysLeft >= 0
            ? $"Alpha测试版 · 2026-09-24 过期（剩余 {daysLeft} 天）"
            : "Alpha测试版 · 2026-09-24 过期";
        AlphaBadge.Visibility = Visibility.Visible;
        Log.Info($"ALPHA 界面标识: {Title} / {AlphaBadge.Text}");
    }

    /// <summary>校验中：沿用启动加载层的转圈 + 进度条，只改文案。</summary>
    private void ShowAlphaChecking()
    {
        LoadRing.Visibility = Visibility.Visible;
        LoadRing.IsActive = true;
        SplashProgress.Visibility = Visibility.Visible;
        SplashStatus.Text = "正在通过 NTP 服务器校验版本有效期…";
        if (_alphaBlockPanel is not null) _alphaBlockPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>拦截提示：展示统一文案 + 失败原因，提供「重新校验 / 退出程序」。</summary>
    private void ShowAlphaBlocked(TimeCheckResult result)
    {
        EnsureAlphaBlockPanel();
        LoadRing.IsActive = false;
        LoadRing.Visibility = Visibility.Collapsed;
        SplashProgress.Visibility = Visibility.Collapsed;
        SplashStatus.Text = "";
        _alphaBlockText!.Text = $"{TimeGuard.ExpiryMessage}\n\n{result.Detail}";
        _alphaBlockPanel!.Visibility = Visibility.Visible;
    }

    /// <summary>按需创建拦截提示面板（挂在启动加载层的纵向面板末尾）。</summary>
    private void EnsureAlphaBlockPanel()
    {
        if (_alphaBlockPanel is not null) return;

        _alphaBlockText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            LineHeight = 26,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0xD2, 0x8A)),
        };

        var retryButton = new Button { Content = "重新校验" };
        retryButton.Click += (_, _) => _alphaDecision?.TrySetResult(true);
        var exitButton = new Button { Content = "退出程序" };
        exitButton.Click += (_, _) => _alphaDecision?.TrySetResult(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        buttons.Children.Add(retryButton);
        buttons.Children.Add(exitButton);

        _alphaBlockPanel = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _alphaBlockPanel.Children.Add(_alphaBlockText);
        _alphaBlockPanel.Children.Add(buttons);

        if (SplashStatus.Parent is Panel splashPanel)
            splashPanel.Children.Add(_alphaBlockPanel);
    }

    /// <summary>
    /// 运行期复核：每 15 分钟用 NTP 重新取一次网络时间。
    /// 已过期 / 系统时间与 NTP 不一致 → 立即提示并退出；网络暂时不可达 → 连续两次失败才拦截（容忍单次抖动）。
    /// </summary>
    private void StartAlphaWatchdog()
    {
        _alphaWatchdog = DispatcherQueue.CreateTimer();
        _alphaWatchdog.Interval = TimeSpan.FromMinutes(15);
        _alphaWatchdog.IsRepeating = true;
        _alphaWatchdog.Tick += async (_, _) => await AlphaWatchdogTickAsync();
        _alphaWatchdog.Start();
        Log.Info("ALPHA 看门狗已启动（每 15 分钟复核一次网络时间）");
    }

    private async Task AlphaWatchdogTickAsync()
    {
        try
        {
            var result = await TimeGuard.CheckAsync();
            if (result.IsOk)
            {
                _alphaWatchdogFailures = 0;
                return;
            }

            // 已过期 / 时钟被改：立即拦截；NTP 不可达：给一次重试机会
            bool hardFail = result.Status is TimeCheckStatus.Expired or TimeCheckStatus.ClockMismatch;
            _alphaWatchdogFailures++;
            Log.Error($"ALPHA 看门狗校验失败（第 {_alphaWatchdogFailures} 次）: {result.Status} {result.Detail}");
            if (!hardFail && _alphaWatchdogFailures < 2)
                return;

            _alphaWatchdog?.Stop();
            if (await ShowAlphaRuntimeBlockedAsync(result))
                return;     // 重新校验后恢复，继续使用

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log.Error($"ALPHA 看门狗异常: {ex}");
        }
    }

    /// <summary>
    /// 运行期拦截弹窗：过期 / 系统时间不一致只提供「退出程序」；NTP 不可达允许「重新校验」。
    /// 返回 true 表示重试后校验恢复通过。
    /// </summary>
    private async Task<bool> ShowAlphaRuntimeBlockedAsync(TimeCheckResult result)
    {
        while (true)
        {
            bool canRetry = result.Status == TimeCheckStatus.Unreachable;
            var dialog = new ContentDialog
            {
                Title = "版本校验未通过",
                Content = new TextBlock
                {
                    Text = $"{TimeGuard.ExpiryMessage}\n\n{result.Detail}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15,
                    LineHeight = 26,
                },
                CloseButtonText = "退出程序",
                XamlRoot = Content.XamlRoot,
            };
            if (canRetry)
            {
                dialog.PrimaryButtonText = "重新校验";
                dialog.DefaultButton = ContentDialogButton.Primary;
            }
            else
            {
                dialog.DefaultButton = ContentDialogButton.Close;
            }

            Log.Error($"ALPHA 运行期拦截：status={result.Status} detail={result.Detail}");
            var choice = await dialog.ShowAsync();
            if (!canRetry || choice != ContentDialogResult.Primary)
                return false;

            result = await TimeGuard.CheckAsync();
            if (result.IsOk)
            {
                _alphaWatchdogFailures = 0;
                _alphaWatchdog?.Start();
                UpdateAlphaBadge();
                Log.Info("ALPHA 重新校验通过，继续运行");
                return true;
            }
        }
    }
#endif

    private void NoticeBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
    }
}
