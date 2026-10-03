using System.Numerics;
using System.Runtime.InteropServices;

namespace DeltaCore;

/// <summary>解析后的 .uemodel 模型（UEFormat 二进制格式）。</summary>
public sealed class UeModel
{
    public string Name { get; set; } = "";
    public int FileVersion { get; set; }
    public List<UeLod> Lods { get; } = new();
}

public sealed class UeLod
{
    public string Name { get; set; } = "";
    public Vector3[] Vertices { get; set; } = Array.Empty<Vector3>();
    public uint[] Indices { get; set; } = Array.Empty<uint>();
    /// <summary>逐顶点法线；W 通道（副切线符号）单独存放。</summary>
    public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();
    public float[] NormalW { get; set; } = Array.Empty<float>();
    /// <summary>文件自带的逐顶点切线（3 float）。</summary>
    public Vector3[] Tangents { get; set; } = Array.Empty<Vector3>();
    /// <summary>UV 通道列表，每通道逐顶点。</summary>
    public Vector2[][] Uvs { get; set; } = Array.Empty<Vector2[]>();
    public List<UeMaterial> Materials { get; } = new();
}

public sealed class UeMaterial
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>材质对应的索引缓冲区起始偏移（uint32 条目数）。</summary>
    public int FirstIndex { get; set; }
    /// <summary>材质对应的三角形数量。</summary>
    public int NumFaces { get; set; }
}

/// <summary>渲染用顶点：位置 + 法线 + 切线(含符号) + UV。48 字节。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector4 Tangent;
    public Vector2 UV;
}

public sealed class MeshRange
{
    public int IndexOffset { get; init; }
    public int IndexCount { get; init; }
    public string MaterialName { get; init; } = "";
}

/// <summary>构建完成、可直接提交 GPU 的网格。</summary>
public sealed class RenderMesh
{
    public Vertex[] Vertices { get; init; } = Array.Empty<Vertex>();
    public uint[] Indices { get; init; } = Array.Empty<uint>();
    public List<MeshRange> Ranges { get; init; } = new();
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }
    public Vector3 Center { get; init; }
    public float Radius { get; init; }
    public int UvChannelCount { get; init; }
    public bool HasNormals { get; init; }
    public string LodName { get; init; } = "";
}
