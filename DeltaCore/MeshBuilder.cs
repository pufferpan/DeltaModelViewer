using System.Numerics;

namespace DeltaCore;

/// <summary>
/// 把解析出的 UeModel（LOD0）转换成带切线空间的渲染网格。
/// 坐标系保持 Unreal 原始左手系（Z 轴向上），供 Direct3D 直接使用。
/// </summary>
public static class MeshBuilder
{
    public static RenderMesh Build(UeModel model, int lodIndex = 0)
    {
        if (model.Lods.Count == 0)
            throw new InvalidDataException("模型不包含 LOD 数据");
        var lod = model.Lods[Math.Clamp(lodIndex, 0, model.Lods.Count - 1)];

        var positions = lod.Vertices;
        if (positions.Length == 0)
            throw new InvalidDataException("LOD 缺少顶点数据");
        var indices = lod.Indices;
        if (indices.Length == 0)
            throw new InvalidDataException("LOD 缺少索引数据");
        if (indices.Length % 3 != 0)
            throw new InvalidDataException("索引数量不是 3 的倍数（非三角形网格）");

        int vc = positions.Length;
        if (indices.Any(i => i >= vc))
            throw new InvalidDataException("索引越界（引用了不存在的顶点）");

        // 法线：缺失或数量不符时按面平滑重建
        Vector3[] normals = lod.Normals.Length == vc ? lod.Normals : ComputeSmoothNormals(positions, indices);
        float[] normalW = lod.NormalW.Length == vc ? lod.NormalW : new float[vc];

        // UV：优先取与顶点数一致的通道
        Vector2[]? uv = null;
        if (lod.Uvs.Length > 0)
        {
            uv = lod.Uvs.FirstOrDefault(c => c.Length == vc);
            uv ??= lod.Uvs[0];
            if (uv.Length != vc) uv = null;
        }

        var tangents = ComputeTangents(positions, normals, normalW, uv, indices, lod.Tangents);

        var vertices = new Vertex[vc];
        for (int i = 0; i < vc; i++)
        {
            vertices[i] = new Vertex
            {
                Position = positions[i],
                Normal = normals[i],
                Tangent = tangents[i],
                UV = uv is null ? Vector2.Zero : uv[i],
            };
        }

        // 材质分组 → 索引范围
        var ranges = new List<MeshRange>();
        foreach (var m in lod.Materials)
        {
            int offset = Math.Max(0, m.FirstIndex);
            if (offset >= indices.Length) continue;
            int count = Math.Min(Math.Max(0, m.NumFaces) * 3, indices.Length - offset);
            if (count > 0)
                ranges.Add(new MeshRange { IndexOffset = offset, IndexCount = count, MaterialName = m.Name });
        }
        if (ranges.Count == 0)
            ranges.Add(new MeshRange { IndexOffset = 0, IndexCount = indices.Length, MaterialName = "" });

        // 包围盒
        Vector3 mn = positions[0], mx = positions[0];
        foreach (var p in positions)
        {
            mn = Vector3.Min(mn, p);
            mx = Vector3.Max(mx, p);
        }
        var center = (mn + mx) * 0.5f;
        float radius = MathF.Max((mx - mn).Length() * 0.5f, 1e-3f);

        // NaN 防护：数据损坏时尽早报错而不是渲染出坏图
        if (!IsFinite(mn) || !IsFinite(mx) || !IsFinite(center))
            throw new InvalidDataException("顶点数据包含非法数值");

        return new RenderMesh
        {
            Vertices = vertices,
            Indices = indices,
            Ranges = ranges,
            Min = mn,
            Max = mx,
            Center = center,
            Radius = radius,
            UvChannelCount = lod.Uvs.Length,
            HasNormals = lod.Normals.Length == vc,
            LodName = lod.Name,
        };
    }

    private static Vector3[] ComputeSmoothNormals(Vector3[] positions, uint[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (int t = 0; t < indices.Length; t += 3)
        {
            var a = positions[indices[t]];
            var b = positions[indices[t + 1]];
            var c = positions[indices[t + 2]];
            var n = Vector3.Cross(b - a, c - a);
            normals[indices[t]] += n;
            normals[indices[t + 1]] += n;
            normals[indices[t + 2]] += n;
        }
        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
        }
        return normals;
    }

    private static Vector4[] ComputeTangents(Vector3[] positions, Vector3[] normals, float[] normalW,
        Vector2[]? uv, uint[] indices, Vector3[] fileTangents)
    {
        int vc = positions.Length;
        var tan = new Vector3[vc];
        var btan = new Vector3[vc];
        bool hasFileTangents = fileTangents.Length == vc;

        if (uv is not null)
        {
            for (int t = 0; t < indices.Length; t += 3)
            {
                int i0 = (int)indices[t], i1 = (int)indices[t + 1], i2 = (int)indices[t + 2];
                var p0 = positions[i0];
                var e1 = positions[i1] - p0;
                var e2 = positions[i2] - p0;
                var w0 = uv[i0];
                float du1 = uv[i1].X - w0.X, dv1 = uv[i1].Y - w0.Y;
                float du2 = uv[i2].X - w0.X, dv2 = uv[i2].Y - w0.Y;
                float det = du1 * dv2 - du2 * dv1;
                if (MathF.Abs(det) < 1e-12f) continue;
                float r = 1f / det;
                var tv = r * (dv2 * e1 - dv1 * e2);
                var bv = r * (du1 * e2 - du2 * e1);
                tan[i0] += tv; tan[i1] += tv; tan[i2] += tv;
                btan[i0] += bv; btan[i1] += bv; btan[i2] += bv;
            }
        }

        var result = new Vector4[vc];
        for (int i = 0; i < vc; i++)
        {
            var n = normals[i];
            var t = tan[i];
            if (t.LengthSquared() < 1e-12f && hasFileTangents)
                t = fileTangents[i];
            if (t.LengthSquared() < 1e-12f)
            {
                t = FallbackTangent(n);
            }
            else
            {
                t -= n * Vector3.Dot(n, t);
                if (t.LengthSquared() < 1e-12f)
                    t = FallbackTangent(n);
                else
                    t = Vector3.Normalize(t);
            }

            // 副切线符号：优先用模型自带的 W 通道，否则按重建的副切线计算
            float sign;
            float w = i < normalW.Length ? normalW[i] : 0f;
            if (MathF.Abs(w) > 0.5f)
            {
                sign = MathF.Sign(w);
            }
            else if (btan[i].LengthSquared() > 1e-12f)
            {
                sign = Vector3.Dot(Vector3.Cross(n, t), Vector3.Normalize(btan[i])) >= 0f ? 1f : -1f;
            }
            else
            {
                sign = 1f;
            }
            result[i] = new Vector4(t, sign);
        }
        return result;
    }

    private static Vector3 FallbackTangent(Vector3 n)
    {
        var axis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(n, axis));
    }

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
