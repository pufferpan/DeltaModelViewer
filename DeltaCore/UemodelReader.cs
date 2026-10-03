using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace DeltaCore;

/// <summary>
/// 基于 UEFormat（https://github.com/h4lfheart/UEFormat）二进制规范的 .uemodel 解析器。
/// 文件头：magic "UEFORMAT" + 对象类型(FString) + 版本(1字节) + 对象名(FString) + 压缩标志(1字节)。
/// </summary>
public static class UemodelReader
{
    private const string Magic = "UEFORMAT";

    public static UeModel Read(byte[] fileBytes)
    {
        var ar = new ByteReader(fileBytes);
        string magic = Encoding.ASCII.GetString(ar.ReadBytes(Magic.Length));
        if (magic != Magic)
            throw new InvalidDataException("不是 UEFormat 文件（魔数错误）");

        string objectType = ar.ReadString();
        int version = ar.ReadByte();
        string objectName = ar.ReadString();
        bool compressed = ar.ReadByte() != 0;

        if (compressed)
        {
            string compressionType = ar.ReadString();
            int uncompressedSize = ar.ReadInt32();
            int _ = ar.ReadInt32(); // compressed size
            byte[] payload = ar.ReadToEnd();
            payload = compressionType switch
            {
                "GZIP" => DecompressGzip(payload),
                _ => throw new NotSupportedException($"不支持的压缩类型: {compressionType}"),
            };
            ar = new ByteReader(payload);
        }

        return objectType switch
        {
            "UEMODEL" => ReadModelBody(ar, objectName, version),
            _ => throw new NotSupportedException($"不支持的对象类型: {objectType}"),
        };
    }

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static UeModel ReadModelBody(ByteReader ar, string name, int version)
    {
        var model = new UeModel { Name = name, FileVersion = version };
        while (!ar.AtEnd)
        {
            string section = ar.ReadString();
            int count = ar.ReadInt32();
            int byteSize = ar.ReadInt32();
            CheckSectionHeader(section, count, byteSize, ar);

            if (section == "LODS")
            {
                for (int i = 0; i < count; i++)
                    model.Lods.Add(ReadLod(ar));
            }
            else
            {
                ar.Skip(byteSize); // SKELETON / COLLISION 等，直接跳过
            }
        }
        return model;
    }

    private static UeLod ReadLod(ByteReader ar)
    {
        string lodName = ar.ReadString();
        int lodSize = ar.ReadInt32();
        var chunk = ar.ReadChunk(lodSize);

        var lod = new UeLod { Name = lodName };
        while (!chunk.AtEnd)
        {
            string section = chunk.ReadString();
            int count = chunk.ReadInt32();
            int byteSize = chunk.ReadInt32();
            CheckSectionHeader(section, count, byteSize, chunk);

            switch (section)
            {
                case "VERTICES":
                    lod.Vertices = ReadVector3Array(chunk, count);
                    break;
                case "INDICES":
                    lod.Indices = new uint[count];
                    for (int i = 0; i < count; i++)
                        lod.Indices[i] = chunk.ReadUInt32();
                    break;
                case "NORMALS":
                    // 布局为 W XYZ（W = 副切线符号），逐顶点 4 个 float
                    var normals = new Vector3[count];
                    var normalW = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        float w = chunk.ReadSingle();
                        normalW[i] = w;
                        normals[i] = chunk.ReadVector3();
                    }
                    lod.Normals = normals;
                    lod.NormalW = normalW;
                    break;
                case "TANGENTS":
                    // 逐顶点 3 float（切线 XYZ，副切线符号在 NORMALS 的 W 通道）
                    var tangents = new Vector3[count];
                    for (int i = 0; i < count; i++)
                        tangents[i] = chunk.ReadVector3();
                    lod.Tangents = tangents;
                    break;
                case "VERTEXCOLORS":
                    for (int i = 0; i < count; i++)
                    {
                        _ = chunk.ReadString(); // 通道名
                        int n = chunk.ReadInt32();
                        chunk.Skip(n * 4);
                    }
                    break;
                case "TEXCOORDS":
                    var uvs = new Vector2[count][];
                    for (int c = 0; c < count; c++)
                    {
                        int n = chunk.ReadInt32();
                        var channel = new Vector2[n];
                        for (int i = 0; i < n; i++)
                            channel[i] = chunk.ReadVector2();
                        uvs[c] = channel;
                    }
                    lod.Uvs = uvs;
                    break;
                case "MATERIALS":
                    for (int i = 0; i < count; i++)
                    {
                        lod.Materials.Add(new UeMaterial
                        {
                            Name = chunk.ReadString(),
                            Path = chunk.ReadString(),
                            FirstIndex = chunk.ReadInt32(),
                            NumFaces = chunk.ReadInt32(),
                        });
                    }
                    break;
                case "WEIGHTS":
                case "MORPHTARGETS":
                default:
                    chunk.Skip(byteSize); // 蒙皮/形变等数据本程序不使用
                    break;
            }
        }
        return lod;
    }

    private static Vector3[] ReadVector3Array(ByteReader ar, int count)
    {
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
            result[i] = ar.ReadVector3();
        return result;
    }

    private static void CheckSectionHeader(string section, int count, int byteSize, ByteReader ar)
    {
        if (count < 0 || byteSize < 0 || count > 100_000_000)
            throw new InvalidDataException($"数据段 {section} 大小非法 (count={count}, size={byteSize})");
        if (byteSize > ar.Remaining)
            throw new InvalidDataException($"数据段 {section} 越界 (size={byteSize}, remaining={ar.Remaining})");
    }
}

/// <summary>带边界检查的小端二进制读取器。</summary>
public sealed class ByteReader
{
    private readonly byte[] _data;
    private int _pos;

    public ByteReader(byte[] data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Length => _data.Length;
    public int Remaining => _data.Length - _pos;
    public bool AtEnd => _pos >= _data.Length;

    private void Ensure(int size)
    {
        if (_pos + size > _data.Length)
            throw new EndOfStreamException("读取超出文件末尾");
    }

    public byte[] ReadBytes(int count)
    {
        Ensure(count);
        var result = _data.AsSpan(_pos, count).ToArray();
        _pos += count;
        return result;
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _data[_pos++];
    }

    public int ReadInt32()
    {
        Ensure(4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        Ensure(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    public float ReadSingle()
    {
        Ensure(4);
        int bits = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return BitConverter.Int32BitsToSingle(bits);
    }

    public Vector2 ReadVector2() => new(ReadSingle(), ReadSingle());

    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());

    public string ReadString()
    {
        int len = ReadInt32();
        if (len < 0 || len > Remaining)
            throw new InvalidDataException($"字符串长度非法: {len}");
        string s = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len;
        return s;
    }

    public ByteReader ReadChunk(int size)
    {
        Ensure(size);
        var chunk = new ByteReader(_data.AsSpan(_pos, size).ToArray());
        _pos += size;
        return chunk;
    }

    public void Skip(int count)
    {
        Ensure(count);
        _pos += count;
    }

    public byte[] ReadToEnd() => _data.AsSpan(_pos).ToArray();
}
