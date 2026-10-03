using System.Numerics;

namespace DeltaModelViewer.Rendering;

/// <summary>
/// 轨迹球轨道相机：以四元数维护朝向，绕目标点可旋转到任意角度（含上下翻转），无俯仰限制。
/// 相机局部坐标系：右 = +X，上 = +Y，视线方向（眼 → 目标）= +Z（与左手系视图矩阵一致）。
/// </summary>
public sealed class OrbitCamera
{
    private const float DefaultYaw = -0.8f;
    private const float DefaultPitch = 0.35f;
    private const float RotateSensitivity = 0.008f;

    public Vector3 Target;
    public float Distance = 4f;
    public float SceneRadius = 1f;

    private Quaternion _orientation = Quaternion.Identity;

    /// <summary>相机局部上方向（世界坐标）。</summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, _orientation);

    /// <summary>视线方向（眼 → 目标）。</summary>
    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, _orientation);

    /// <summary>相机局部右方向（世界坐标）。</summary>
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, _orientation);

    public Vector3 Eye => Target - Forward * Distance;

    public void Fit(Vector3 center, float radius)
    {
        Target = center;
        SceneRadius = MathF.Max(radius, 1e-3f);
        Distance = SceneRadius * 2.8f;
        ResetOrientation();
    }

    /// <summary>轨迹球旋转（屏幕拖动像素增量）：绕相机局部轴连续旋转，可达任意角度。</summary>
    public void Rotate(float deltaX, float deltaY)
    {
        var horizontal = Quaternion.CreateFromAxisAngle(Up, -deltaX * RotateSensitivity);
        var vertical = Quaternion.CreateFromAxisAngle(Right, deltaY * RotateSensitivity);
        _orientation = Quaternion.Normalize(vertical * horizontal * _orientation);
    }

    public void Zoom(float factor)
    {
        Distance = Math.Clamp(Distance * factor, SceneRadius * 0.02f, SceneRadius * 100f);
    }

    public void Pan(float dx, float dy, float viewportHeight)
    {
        var right = Right;
        var up = Up;
        float k = 2f * Distance * MathF.Tan(MathF.PI / 8f) / MathF.Max(viewportHeight, 1f);
        Target += right * dx * k;
        Target -= up * dy * k;
    }

    /// <summary>恢复默认视角（与原固定俯仰/偏航相机的初始视角完全一致）。</summary>
    private void ResetOrientation()
    {
        var eye = Target + Distance * new Vector3(
            MathF.Cos(DefaultPitch) * MathF.Sin(DefaultYaw),
            MathF.Sin(DefaultPitch),
            MathF.Cos(DefaultPitch) * MathF.Cos(DefaultYaw));

        var forward = Vector3.Normalize(Target - eye);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var up = Vector3.Cross(forward, right);

        // 列为主：列 = (right, up, forward)，即 q·UnitX = right、q·UnitY = up、q·UnitZ = forward
        _orientation = QuaternionFromBasis(right, up, forward);
    }

    /// <summary>标准旋转矩阵（列为主：列 = x/y/z 基向量）→ 四元数。</summary>
    private static Quaternion QuaternionFromBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        float m11 = x.X, m12 = y.X, m13 = z.X;
        float m21 = x.Y, m22 = y.Y, m23 = z.Y;
        float m31 = x.Z, m32 = y.Z, m33 = z.Z;
        float trace = m11 + m22 + m33;

        float w, qx, qy, qz;
        if (trace > 0f)
        {
            float s = MathF.Sqrt(trace + 1f) * 2f;
            w = 0.25f * s;
            qx = (m32 - m23) / s;
            qy = (m13 - m31) / s;
            qz = (m21 - m12) / s;
        }
        else if (m11 > m22 && m11 > m33)
        {
            float s = MathF.Sqrt(1f + m11 - m22 - m33) * 2f;
            w = (m32 - m23) / s;
            qx = 0.25f * s;
            qy = (m12 + m21) / s;
            qz = (m13 + m31) / s;
        }
        else if (m22 > m33)
        {
            float s = MathF.Sqrt(1f + m22 - m11 - m33) * 2f;
            w = (m13 - m31) / s;
            qx = (m12 + m21) / s;
            qy = 0.25f * s;
            qz = (m23 + m32) / s;
        }
        else
        {
            float s = MathF.Sqrt(1f + m33 - m11 - m22) * 2f;
            w = (m21 - m12) / s;
            qx = (m13 + m31) / s;
            qy = (m23 + m32) / s;
            qz = 0.25f * s;
        }
        return Quaternion.Normalize(new Quaternion(qx, qy, qz, w));
    }
}
