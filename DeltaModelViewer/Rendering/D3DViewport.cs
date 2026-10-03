using System.Numerics;
using System.Runtime.InteropServices;
using DeltaCore;
using DeltaModelViewer.Core;
using Microsoft.UI.Xaml.Controls;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WinUI;
using WinRT;

namespace DeltaModelViewer.Rendering;

/// <summary>
/// 基于 SwapChainPanel + Direct3D 11 的模型渲染视口。
/// 全部资源在 UI 线程创建/更新，渲染由 CompositionTarget.Rendering 驱动。
/// </summary>
public sealed class D3DViewport : IDisposable
{
    private const int VertexStride = 48;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private ID3D11RenderTargetView _rtv = null!;
    private ID3D11DepthStencilView _dsv = null!;
    private ID3D11RasterizerState _rsCullBack = null!;
    private ID3D11RasterizerState _rsCullNone = null!;
    private ID3D11DepthStencilState _dsState = null!;
    private ID3D11BlendState _blendState = null!;
    private ID3D11SamplerState _sampler = null!;
    private ID3D11VertexShader _bgVs = null!;
    private ID3D11PixelShader _bgPs = null!;
    private ID3D11Buffer _bgParams = null!;
    private ID3D11ShaderResourceView _bgSrv = null!;
    private ID3D11DepthStencilState _dsStateNoWrite = null!;
    private int _bgWidth, _bgHeight;
    private Vector2 _bgScale = Vector2.One, _bgOffset = Vector2.Zero;
    private Vector3 _bgAverage = new(0.42f, 0.42f, 0.42f);
    private ID3D11InputLayout _layout = null!;
    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _ps = null!;
    private ID3D11Buffer _perFrame = null!;
    private ID3D11Buffer _vb = null!;
    private ID3D11Buffer _ib = null!;
    private ID3D11ShaderResourceView _baseSrv = null!;
    private ID3D11ShaderResourceView _normalSrv = null!;
    private ID3D11Texture2D _offscreenTex = null!;
    private bool _isOffscreen;

    private int _panelWidth, _panelHeight;
    private int _pendingWidth = 1, _pendingHeight = 1;

    public OrbitCamera Camera { get; } = new();
    public bool NormalMappingEnabled { get; set; } = true;
    public bool DoubleSided { get; set; }
    /// <summary>金属度 0-1（0 = 非金属）。</summary>
    public float Metallic { get; set; }
    /// <summary>粗糙度 0-1（默认 0.6，值越小高光越锐）。</summary>
    public float Roughness { get; set; } = 0.6f;
    /// <summary>曝光（ACES 色调映射前乘），默认 1.15。</summary>
    public float Exposure { get; set; } = 1.15f;
    /// <summary>环境反射增益（背景图较暗，金属需要提亮），默认 14。</summary>
    public float EnvSpecBoost { get; set; } = 14f;
    /// <summary>主光方位角（度，0=+X，90=+Y），默认 66。</summary>
    public float LightAzimuth { get; set; } = 66f;
    /// <summary>主光高度角（度，0=水平，90=正上方），默认 30。</summary>
    public float LightElevation { get; set; } = 30f;
    /// <summary>主光强度（ACES 前线性缩放），默认 3.6。</summary>
    public float LightIntensity { get; set; } = 3.6f;
    public bool IsInitialized { get; private set; }

    public void Initialize(SwapChainPanel panel)
    {
        CreateDevice();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        var desc = new SwapChainDescription1(
            1, 1, Format.B8G8R8A8_UNorm,
            false, Usage.RenderTargetOutput, 2,
            Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapChainFlags.None);

        _swapChain = factory.CreateSwapChainForComposition(_device, desc);

        // 注意：Vortice.WinUI 的 ISwapChainPanelNative 是 SharpGen 包装类（非接口），
        // As<T>() 会走托管强转而失败；这里用自声明的 ComImport 接口做 QI。
        var native = panel.As<ISwapChainPanelNativeInterop>();
        native.SetSwapChain(_swapChain.NativePointer);

        CreatePipeline();
        _perFrame = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<PerFrameConstants>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write));

        panel.SizeChanged += (_, _) => RequestResize(panel);
        panel.CompositionScaleChanged += (_, _) => RequestResize(panel);
        RequestResize(panel);
        IsInitialized = true;
    }

    /// <summary>离屏模式（自检用）：渲染到纹理而不是交换链。</summary>
    public void InitializeOffscreen(int width, int height)
    {
        CreateDevice();
        CreatePipeline();
        _perFrame = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<PerFrameConstants>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _isOffscreen = true;
        _pendingWidth = Math.Max(8, width);
        _pendingHeight = Math.Max(8, height);
        IsInitialized = true;
    }

    private void CreateDevice()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[]
            {
                FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
            },
            out _device,
            out _,
            out _context);
    }

    private void RequestResize(SwapChainPanel panel)
    {
        _pendingWidth = Math.Max(8, (int)Math.Ceiling(panel.ActualWidth * panel.CompositionScaleX));
        _pendingHeight = Math.Max(8, (int)Math.Ceiling(panel.ActualHeight * panel.CompositionScaleY));
    }

    private void CreatePipeline()
    {
        // 注意：必须用字节重载编译，string 重载在含非 ASCII 字符时长度会错位
        var vsResult = Compiler.Compile(
            System.Text.Encoding.UTF8.GetBytes(Shaders.VsSource), "VSMain", "shaders.hlsl", "vs_4_0", out var vsBlob, out var vsError);
        if (vsResult.Failure)
            throw new InvalidOperationException("顶点着色器编译失败: " + System.Text.Encoding.UTF8.GetString(vsError.AsBytes()));
        var psResult = Compiler.Compile(
            System.Text.Encoding.UTF8.GetBytes(Shaders.PsSource), "PSMain", "shaders.hlsl", "ps_4_0", out var psBlob, out var psError);
        if (psResult.Failure)
            throw new InvalidOperationException("像素着色器编译失败: " + System.Text.Encoding.UTF8.GetString(psError.AsBytes()));
        _vs = _device.CreateVertexShader(vsBlob);
        _ps = _device.CreatePixelShader(psBlob);

        var elements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 24, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
        };
        _layout = _device.CreateInputLayout(elements, vsBlob);

        var rsBack = new RasterizerDescription(CullMode.Back, FillMode.Solid)
        {
            DepthClipEnable = true,
            FrontCounterClockwise = false,
        };
        _rsCullBack = _device.CreateRasterizerState(rsBack);
        var rsNone = new RasterizerDescription(CullMode.None, FillMode.Solid)
        {
            DepthClipEnable = true,
            FrontCounterClockwise = false,
        };
        _rsCullNone = _device.CreateRasterizerState(rsNone);

        _dsState = _device.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Less));

        // 注意：BlendDescription 零值默认的 RenderTargetWriteMask 是 None（不写颜色），必须显式指定
        var blendDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = false,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = _device.CreateBlendState(blendDesc);

        _sampler = _device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap,
            0f, 4, ComparisonFunction.Never, new Color4(0, 0, 0, 0), 0f, 0f));

        // 背景图管线：全屏三角形 + 背景图采样
        var bgVsResult = Compiler.Compile(
            System.Text.Encoding.UTF8.GetBytes(Shaders.BgVsSource), "VSMain", "bg.hlsl", "vs_4_0", out var bgVsBlob, out var bgVsError);
        if (bgVsResult.Failure)
            throw new InvalidOperationException("背景顶点着色器编译失败: " + System.Text.Encoding.UTF8.GetString(bgVsError.AsBytes()));
        var bgPsResult = Compiler.Compile(
            System.Text.Encoding.UTF8.GetBytes(Shaders.BgPsSource), "PSMain", "bg.hlsl", "ps_4_0", out var bgPsBlob, out var bgPsError);
        if (bgPsResult.Failure)
            throw new InvalidOperationException("背景像素着色器编译失败: " + System.Text.Encoding.UTF8.GetString(bgPsError.AsBytes()));
        _bgVs = _device.CreateVertexShader(bgVsBlob);
        _bgPs = _device.CreatePixelShader(bgPsBlob);
        _bgParams = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _dsStateNoWrite = _device.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.Always));
    }

    private void ResizeSwapChain()
    {
        if (_pendingWidth == _panelWidth && _pendingHeight == _panelHeight)
            return;
        _panelWidth = _pendingWidth;
        _panelHeight = _pendingHeight;

        _rtv?.Dispose();
        _dsv?.Dispose();
        _offscreenTex?.Dispose();

        if (_isOffscreen)
        {
            _offscreenTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_panelWidth,
                Height = (uint)_panelHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
            _rtv = _device.CreateRenderTargetView(_offscreenTex);
        }
        else
        {
            _swapChain.ResizeBuffers(2u, (uint)_panelWidth, (uint)_panelHeight, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device.CreateRenderTargetView(backBuffer);
        }

        using (var depth = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_panelWidth,
            Height = (uint)_panelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        }))
            _dsv = _device.CreateDepthStencilView(depth);
    }

    public void SetMesh(RenderMesh mesh)
    {
        _vb?.Dispose();
        _ib?.Dispose();
        _vb = null!;
        _ib = null!;
        _ranges = mesh.Ranges;
        _indexCount = mesh.Indices.Length;
        _vertexCount = mesh.Vertices.Length;

        unsafe
        {
            fixed (Vertex* p = mesh.Vertices)
            {
                _vb = _device.CreateBuffer(
                    new BufferDescription((uint)(mesh.Vertices.Length * VertexStride), BindFlags.VertexBuffer),
                    new SubresourceData((IntPtr)p));
            }
            fixed (uint* p = mesh.Indices)
            {
                _ib = _device.CreateBuffer(
                    new BufferDescription((uint)(mesh.Indices.Length * sizeof(uint)), BindFlags.IndexBuffer),
                    new SubresourceData((IntPtr)p));
            }
        }
    }

    private List<MeshRange> _ranges = new();
    private int _indexCount;
    private int _vertexCount;

    public void SetTextures(TextureData? baseColor, TextureData? normal)
    {
        _baseSrv?.Dispose();
        _normalSrv?.Dispose();
        _baseSrv = CreateTexture(baseColor, srgb: true);
        _normalSrv = CreateTexture(normal, srgb: false);
        _hasBaseColor = baseColor != null;
        _hasNormal = normal != null;
    }

    private bool _hasBaseColor;
    private bool _hasNormal;

    private ID3D11ShaderResourceView CreateTexture(TextureData? data, bool srgb)
    {
        if (data == null || data.Pixels.Length == 0)
            return null!;
        var desc = new Texture2DDescription
        {
            Width = data.Width,
            Height = data.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = srgb ? Format.B8G8R8A8_UNorm_SRgb : Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        unsafe
        {
            fixed (byte* p = data.Pixels)
            {
                using var tex = _device.CreateTexture2D(desc, new SubresourceData(
                    (IntPtr)p, (uint)(data.Width * 4)));
                return _device.CreateShaderResourceView(tex);
            }
        }
    }

    public void Render()
    {
        if (!IsInitialized || _device == null)
            return;
        ResizeSwapChain();
        if (_panelWidth == 0 || _panelHeight == 0)
            return;

        _context.OMSetRenderTargets(_rtv, _dsv);
        _context.RSSetViewport(0, 0, _panelWidth, _panelHeight);
        _context.ClearRenderTargetView(_rtv, new Color4(0.052f, 0.056f, 0.068f, 1f));
        _context.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1f, 0);
        _context.OMSetDepthStencilState(_dsState);
        _context.OMSetBlendState(_blendState);
        _context.RSSetState(DoubleSided ? _rsCullNone : _rsCullBack);

        ComputeCoverUv();
        DrawBackground();

        if (_vb != null)
        {
            float aspect = _panelWidth / (float)_panelHeight;
            float near = MathF.Max(0.001f, Camera.Distance - Camera.SceneRadius * 2.2f);
            float far = Camera.Distance + Camera.SceneRadius * 4f + 1f;

            var view = BuildLookAtLH(Camera.Eye, Camera.Target, Camera.Up);
            var proj = BuildPerspectiveFovLH(MathF.PI / 4f, aspect, near, far);

            var cb = new PerFrameConstants
            {
                World = Matrix4x4.Identity,
                ViewProj = view * proj,
                CameraPos = Camera.Eye,
                LightDir = GetLightDirection(),
                Params = new Vector4(_hasBaseColor ? 1f : 0f,
                                     _hasNormal && NormalMappingEnabled ? 1f : 0f,
                                     Metallic,
                                     Roughness),
                EnvAverage = _bgAverage,
                HasEnv = _bgSrv != null ? 1f : 0f,
                EnvUvTransform = new Vector4(_bgScale.X, _bgScale.Y, _bgOffset.X, _bgOffset.Y),
                FillDir = Vector3.Normalize(new Vector3(-0.55f, -0.35f, 0.45f)),
                Exposure = Exposure,
                EnvBoost = EnvSpecBoost,
                LightIntensity = LightIntensity,
            };
            UpdateConstantBuffer(cb);

            _context.IASetInputLayout(_layout);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.IASetVertexBuffer(0, _vb, VertexStride);
            _context.IASetIndexBuffer(_ib, Format.R32_UInt, 0);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.VSSetConstantBuffer(0, _perFrame);
            _context.PSSetConstantBuffer(0, _perFrame);
            _context.PSSetSampler(0, _sampler);
            _context.PSSetShaderResource(0, _hasBaseColor ? _baseSrv : null!);
            _context.PSSetShaderResource(1, _hasNormal && NormalMappingEnabled ? _normalSrv : null!);
            _context.PSSetShaderResource(2, _bgSrv); // 背景图同时作为环境反射贴图（IBL）

            foreach (var range in _ranges)
                _context.DrawIndexed((uint)range.IndexCount, (uint)range.IndexOffset, 0);
        }

        if (!_isOffscreen)
            _swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>离屏模式下把渲染结果读回 CPU（BGRA8）。</summary>
    public byte[] Capture()
    {
        if (!_isOffscreen || _offscreenTex == null || _panelWidth <= 0 || _panelHeight <= 0)
            throw new InvalidOperationException("Capture 只能在离屏模式下调用");

        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_panelWidth,
            Height = (uint)_panelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _context.CopyResource(staging, _offscreenTex);

        var mapped = _context.Map(staging, 0, MapMode.Read);
        int rowBytes = _panelWidth * 4;
        var result = new byte[rowBytes * _panelHeight];
        unsafe
        {
            byte* src = (byte*)mapped.DataPointer;
            fixed (byte* dst = result)
            {
                for (int y = 0; y < _panelHeight; y++)
                    Buffer.MemoryCopy(src + y * mapped.RowPitch, dst + y * rowBytes, rowBytes, rowBytes);
            }
        }
        _context.Unmap(staging, 0);
        return result;
    }

    /// <summary>设置视口背景图（sRGB），铺满视口并居中裁剪（cover）；同时作为环境贴图。</summary>
    public void SetBackground(TextureData? bg)
    {
        _bgSrv?.Dispose();
        _bgSrv = null!;
        _bgAverage = new Vector3(0.42f, 0.42f, 0.42f);
        if (bg == null)
            return;
        _bgSrv = CreateTexture(bg, srgb: true);
        _bgWidth = (int)bg.Width;
        _bgHeight = (int)bg.Height;
        _bgAverage = ComputeAverageColor(bg);
    }

    /// <summary>背景图平均色：步长采样求平均，sRGB→线性，亮度归一化到 0.42 作环境光。</summary>
    private static Vector3 ComputeAverageColor(TextureData bg)
    {
        int stride = Math.Max(1, (int)((long)bg.Width * bg.Height / 262144));
        double r = 0, g = 0, b = 0;
        long n = 0;
        var px = bg.Pixels;
        for (int i = 0; i + 2 < px.Length; i += 4 * stride)
        {
            b += px[i];
            g += px[i + 1];
            r += px[i + 2];
            n++;
        }
        if (n == 0)
            return new Vector3(0.42f, 0.42f, 0.42f);

        float sr = MathF.Pow((float)(r / n / 255.0), 2.2f);
        float sg = MathF.Pow((float)(g / n / 255.0), 2.2f);
        float sb = MathF.Pow((float)(b / n / 255.0), 2.2f);
        float lum = 0.2126f * sr + 0.7152f * sg + 0.0722f * sb;
        if (lum < 0.001f)
            return new Vector3(0.42f, 0.42f, 0.42f);

        float k = MathF.Min(0.42f / lum, 3.0f);
        return new Vector3(
            MathF.Min(sr * k, 1.2f),
            MathF.Min(sg * k, 1.2f),
            MathF.Min(sb * k, 1.2f));
    }

    /// <summary>计算背景图 cover 裁剪的 UV 变换，供背景与 IBL 共用。</summary>
    private void ComputeCoverUv()
    {
        if (_bgSrv == null)
        {
            _bgScale = Vector2.One;
            _bgOffset = Vector2.Zero;
            return;
        }
        float viewAspect = _panelWidth / (float)_panelHeight;
        float texAspect = _bgWidth / (float)_bgHeight;
        float uWin, vWin;
        if (texAspect > viewAspect) { uWin = viewAspect / texAspect; vWin = 1f; }
        else { uWin = 1f; vWin = texAspect / viewAspect; }
        _bgScale = new Vector2(1f / uWin, 1f / vWin);
        _bgOffset = new Vector2((1f - uWin) / 2f, (1f - vWin) / 2f);
    }

    private void DrawBackground()
    {
        if (_bgSrv == null)
            return;

        var bg = new BgParams
        {
            UvScale = _bgScale,
            UvOffset = _bgOffset,
        };
        var mapped = _context.Map(_bgParams, 0, MapMode.WriteDiscard);
        unsafe
        {
            BgParams* p = &bg;
            Buffer.MemoryCopy(p, (void*)mapped.DataPointer, 16, 16);
        }
        _context.Unmap(_bgParams, 0);

        // 全屏三角形（SV_VertexID 生成），背向剔除对闭合窗口全屏三角形不适用，用 CullNone
        _context.RSSetState(_rsCullNone);
        _context.OMSetDepthStencilState(_dsStateNoWrite);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffer(0, null!, 0, 0);
        _context.IASetIndexBuffer(null, Format.Unknown, 0);
        _context.VSSetShader(_bgVs);
        _context.PSSetShader(_bgPs);
        _context.VSSetConstantBuffer(0, _bgParams);
        _context.PSSetConstantBuffer(0, _bgParams);
        _context.PSSetSampler(0, _sampler);
        _context.PSSetShaderResource(0, _bgSrv);
        _context.Draw(3, 0);

        _context.OMSetDepthStencilState(_dsState);
        _context.RSSetState(DoubleSided ? _rsCullNone : _rsCullBack);
    }

    private void UpdateConstantBuffer(in PerFrameConstants cb)
    {
        var mapped = _context.Map(_perFrame, 0, MapMode.WriteDiscard);
        unsafe
        {
            fixed (PerFrameConstants* p = &cb)
            {
                Buffer.MemoryCopy(p, (void*)mapped.DataPointer, Marshal.SizeOf<PerFrameConstants>(),
                    Marshal.SizeOf<PerFrameConstants>());
            }
        }
        _context.Unmap(_perFrame, 0);
    }

    /// <summary>由方位角/高度角计算主光方向（Z 轴向上）。</summary>
    private Vector3 GetLightDirection()
    {
        float az = LightAzimuth * MathF.PI / 180f;
        float el = LightElevation * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(
            MathF.Cos(el) * MathF.Cos(az),
            MathF.Cos(el) * MathF.Sin(az),
            MathF.Sin(el)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrameConstants
    {
        public Matrix4x4 World;       // 64
        public Matrix4x4 ViewProj;    // 64
        public Vector3 CameraPos;     // 12
        public float Pad0;            // 4
        public Vector3 LightDir;      // 12
        public float Pad1;            // 4
        public Vector4 Params;        // 16
        public Vector3 EnvAverage;    // 12
        public float HasEnv;          // 4
        public Vector4 EnvUvTransform;// 16
        public Vector3 FillDir;       // 12
        public float Exposure;        // 4
        public float EnvBoost;        // 4
        public float LightIntensity;  // 4
        public Vector2 Pad3;          // 8
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BgParams
    {
        public Vector2 UvScale;   // 8
        public Vector2 UvOffset;  // 8
    }

    /// <summary>左手系（Z 轴向上）视图矩阵，行向量约定（row_major cbuffer）。</summary>
    private static Matrix4x4 BuildLookAtLH(Vector3 eye, Vector3 target, Vector3 up)
    {
        var zAxis = Vector3.Normalize(target - eye); // 前向
        var xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
        var yAxis = Vector3.Cross(zAxis, xAxis);
        return new Matrix4x4(
            xAxis.X, yAxis.X, zAxis.X, 0f,
            xAxis.Y, yAxis.Y, zAxis.Y, 0f,
            xAxis.Z, yAxis.Z, zAxis.Z, 0f,
            -Vector3.Dot(xAxis, eye), -Vector3.Dot(yAxis, eye), -Vector3.Dot(zAxis, eye), 1f);
    }

    /// <summary>左手系透视投影矩阵（D3D 深度约定 [0,1]），行向量约定。</summary>
    private static Matrix4x4 BuildPerspectiveFovLH(float fovY, float aspect, float zNear, float zFar)
    {
        float yScale = 1f / MathF.Tan(fovY * 0.5f);
        float xScale = yScale / aspect;
        return new Matrix4x4(
            xScale, 0f, 0f, 0f,
            0f, yScale, 0f, 0f,
            0f, 0f, zFar / (zFar - zNear), 1f,
            0f, 0f, -zNear * zFar / (zFar - zNear), 0f);
    }

    public void Dispose()
    {
        _vb?.Dispose();
        _ib?.Dispose();
        _baseSrv?.Dispose();
        _normalSrv?.Dispose();
        _bgSrv?.Dispose();
        _perFrame?.Dispose();
        _bgParams?.Dispose();
        _layout?.Dispose();
        _vs?.Dispose();
        _ps?.Dispose();
        _bgVs?.Dispose();
        _bgPs?.Dispose();
        _dsStateNoWrite?.Dispose();
        _sampler?.Dispose();
        _rsCullBack?.Dispose();
        _rsCullNone?.Dispose();
        _dsState?.Dispose();
        _blendState?.Dispose();
        _rtv?.Dispose();
        _dsv?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}

/// <summary>ISwapChainPanelNative 的 ComImport 声明，用于 WinUI 3 交换链互操作（QI）。</summary>
[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNativeInterop
{
    [PreserveSig]
    int SetSwapChain(IntPtr swapChain);
}
