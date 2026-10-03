namespace DeltaModelViewer.Rendering;

/// <summary>运行时编译的 HLSL 着色器源码。</summary>
internal static class Shaders
{
    public const string VsSource = """
        struct VSIn
        {
            float3 pos : POSITION;
            float3 nrm : NORMAL;
            float4 tan : TANGENT;
            float2 uv  : TEXCOORD;
        };

        struct PSIn
        {
            float4 pos : SV_Position;
            float3 wpos : TEXCOORD1;
            float3 nrm  : TEXCOORD2;
            float4 tan  : TEXCOORD3;
            float2 uv   : TEXCOORD0;
        };

        cbuffer PerFrame : register(b0)
        {
            row_major float4x4 World;
            row_major float4x4 ViewProj;
            float3 CameraPos;
            float  _pad0;
            float3 LightDir;
            float  _pad1;
            float4 Params; // x: 有无 BaseColor 贴图, y: 有无法线贴图, z: 金属度, w: 粗糙度
            float3 EnvAverage;     // 背景图平均色（归一化环境光）
            float  HasEnv;         // 是否用背景图做环境反射
            float4 EnvUvTransform; // 环境图 cover 裁剪 UV 变换 (scale.xy, offset.xy)
            float3 FillDir;        // 冷色补光方向
            float  Exposure;       // 曝光（ACES 色调映射）
            float  EnvBoost;       // 环境反射增益
            float  LightIntensity; // 主光强度
            float2 _pad3;
        };

        PSIn VSMain(VSIn v)
        {
            PSIn o;
            float4 wp = mul(float4(v.pos, 1.0), World);
            o.wpos = wp.xyz;
            o.pos = mul(wp, ViewProj);
            o.nrm = v.nrm;
            o.tan = v.tan;
            o.uv = v.uv;
            return o;
        }
        """;

    public const string PsSource = """
        struct PSIn
        {
            float4 pos : SV_Position;
            float3 wpos : TEXCOORD1;
            float3 nrm  : TEXCOORD2;
            float4 tan  : TEXCOORD3;
            float2 uv   : TEXCOORD0;
        };

        cbuffer PerFrame : register(b0)
        {
            row_major float4x4 World;
            row_major float4x4 ViewProj;
            float3 CameraPos;
            float  _pad0;
            float3 LightDir;
            float  _pad1;
            float4 Params;
            float3 EnvAverage;
            float  HasEnv;
            float4 EnvUvTransform;
            float3 FillDir;
            float  Exposure;
            float  EnvBoost;
            float  LightIntensity;
            float2 _pad3;
        };

        Texture2D BaseColorTex : register(t0);
        Texture2D NormalTex     : register(t1);
        Texture2D BgTex         : register(t2);
        SamplerState Samp       : register(s0);

        // 方向 → 等距柱状投影 UV（Z 轴向上）
        float2 DirToEquirect(float3 d)
        {
            float3 n = normalize(d);
            float u = 0.5 + atan2(n.y, n.x) / 6.2831853;
            float v = 0.5 - asin(clamp(n.z, -1.0, 1.0)) / 3.14159265;
            return float2(u, v);
        }

        // 背景图作为环境图采样；粗糙度越高采样越分散（手写模糊近似 mip）
        float3 SampleEnv(float3 dir, float roughness)
        {
            float blur = roughness * roughness * 0.14;
            float2 uv = DirToEquirect(dir) * EnvUvTransform.xy + EnvUvTransform.zw;
            float3 c = BgTex.SampleLevel(Samp, uv, 0).rgb;
            c += BgTex.SampleLevel(Samp, uv + float2(blur, 0.0), 0).rgb;
            c += BgTex.SampleLevel(Samp, uv - float2(blur, 0.0), 0).rgb;
            c += BgTex.SampleLevel(Samp, uv + float2(0.0, blur), 0).rgb;
            c += BgTex.SampleLevel(Samp, uv - float2(0.0, blur), 0).rgb;
            return c * 0.2;
        }

        // ACES 电影色调映射（Narkowicz 近似）
        float3 AcesTonemap(float3 x)
        {
            return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
        }

        float4 PSMain(PSIn i) : SV_Target
        {
            float3 N = normalize(i.nrm);
            if (Params.y > 0.5)
            {
                // UE 法线贴图（R=X, G=Y），Z 分量由 BC5 重建
                float2 nm = NormalTex.Sample(Samp, i.uv).rg * 2.0 - 1.0;
                float nz = sqrt(saturate(1.0 - dot(nm, nm)));
                float3 T = normalize(i.tan.xyz);
                float3 B = cross(N, T) * i.tan.w;
                N = normalize(T * nm.x + B * nm.y + N * nz);
            }

            float3 baseColor = Params.x > 0.5
                ? BaseColorTex.Sample(Samp, i.uv).rgb
                : float3(0.62, 0.62, 0.65);

            float3 V = normalize(CameraPos - i.wpos);
            float NdotV = saturate(dot(N, V)) + 1e-4;

            // 金属/粗糙工作流：F0 在非金属 0.04 与固有色之间插值；金属吃掉漫反射
            float metallic = Params.z;
            float roughness = clamp(Params.w, 0.045, 1.0);
            float3 F0 = lerp(float3(0.04, 0.04, 0.04), baseColor, metallic);
            float3 diffuseColor = baseColor * (1.0 - metallic);

            // ---- 主光（暖色，Cook-Torrance）----
            float3 L = normalize(LightDir);
            float3 H = normalize(V + L);
            float NdotL = saturate(dot(N, L));
            float NdotH = saturate(dot(N, H));

            float alpha = roughness * roughness;
            float alpha2 = alpha * alpha;
            float dDenom = NdotH * NdotH * (alpha2 - 1.0) + 1.0;
            float D = alpha2 / (3.14159265 * dDenom * dDenom);
            float k = alpha2 * 0.5;
            float G = (NdotL / (NdotL * (1.0 - k) + k)) *
                      (NdotV / (NdotV * (1.0 - k) + k));
            float3 F = F0 + (1.0 - F0) * pow(1.0 - saturate(dot(L, H)), 5.0);

            float3 spec = D * G * F / max(4.0 * NdotL * NdotV, 1e-4);
            float3 keyColor = float3(1.0, 0.96, 0.90) * LightIntensity;
            float3 direct = (diffuseColor / 3.14159265 + spec) * NdotL * keyColor;

            // ---- 冷色补光（仅漫反射，提阴影面）----
            float NdotL2 = saturate(dot(N, normalize(FillDir)));
            float3 fill = diffuseColor * NdotL2 * float3(0.55, 0.63, 0.78) * 0.30;

            // ---- 环境：背景图平均色环境光 + 背景图环境反射（IBL）----
            float3 ambient = diffuseColor * EnvAverage;
            float3 env = HasEnv > 0.5 ? SampleEnv(reflect(-V, N), roughness) : EnvAverage;
            float envBoost = HasEnv > 0.5 ? EnvBoost : 1.0;
            float3 envFresnel = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
            float3 envSpec = env * envFresnel * (1.0 - roughness * 0.35) * envBoost;

            float3 col = direct + fill + ambient + envSpec;
            col = AcesTonemap(col * Exposure);
            col = pow(saturate(col), 1.0 / 2.2);
            return float4(col, 1.0);
        }
        """;

    /// <summary>背景图顶点着色器：SV_VertexID 生成全屏三角形，无顶点缓冲。</summary>
    public const string BgVsSource = """
        struct BGOut
        {
            float4 pos : SV_Position;
            float2 uv  : TEXCOORD0;
        };

        BGOut VSMain(uint vid : SV_VertexID)
        {
            BGOut o;
            float2 p = float2(float((vid << 1) & 2), float(vid & 2)) * 2.0 - 1.0;
            o.pos = float4(p, 0.999, 1.0);
            o.uv = float2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);
            return o;
        }
        """;

    /// <summary>背景图像素着色器：cover 适配的 UV 变换采样。</summary>
    public const string BgPsSource = """
        struct BGOut
        {
            float4 pos : SV_Position;
            float2 uv  : TEXCOORD0;
        };

        cbuffer BgParams : register(b0)
        {
            float2 UvScale;
            float2 UvOffset;
        };

        Texture2D BgTex : register(t0);
        SamplerState Samp : register(s0);

        float4 PSMain(BGOut i) : SV_Target
        {
            float3 c = BgTex.Sample(Samp, i.uv * UvScale + UvOffset).rgb;
            return float4(pow(saturate(c), 1.0 / 2.2), 1.0);
        }
        """;
}
