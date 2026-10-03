using System.Globalization;
using System.Text;
using DeltaCore;

namespace DeltaModelViewer.Core;

/// <summary>
/// 把已加载的渲染网格导出为 OBJ / FBX 模型文件。
/// 坐标转换：UE 左手系 Z 轴向上 → 右手系 Y 轴向上：(x, y, z) → (x, z, -y)，绕序同步翻转。
/// OBJ 附带 MTL（可选复制 BaseColor 贴图到同目录）；FBX 为 ASCII 7.4（含法线/UV/材质/贴图引用）。
/// </summary>
public static class ModelExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string F(float x) => x.ToString("0.0#######", Inv);

    // ---------- OBJ ----------

    public static void ExportObj(RenderMesh mesh, string objPath, string? baseColorPath)
    {
        string dir = Path.GetDirectoryName(objPath)!;
        string stem = Path.GetFileNameWithoutExtension(objPath);
        string mtlName = stem + ".mtl";

        var sb = new StringBuilder(1 << 20);
        sb.AppendLine("# Delta Model Viewer - OBJ export");
        sb.AppendLine("# Vertices: " + mesh.Vertices.Length + ", Triangles: " + mesh.Indices.Length / 3);
        sb.AppendLine("mtllib " + mtlName);
        sb.AppendLine("o Model");

        foreach (var v in mesh.Vertices)
            sb.Append("v ").Append(F(v.Position.X)).Append(' ').Append(F(v.Position.Z)).Append(' ').Append(F(-v.Position.Y)).Append('\n');

        foreach (var v in mesh.Vertices)
            sb.Append("vt ").Append(F(v.UV.X)).Append(' ').Append(F(1f - v.UV.Y)).Append('\n');

        foreach (var v in mesh.Vertices)
            sb.Append("vn ").Append(F(v.Normal.X)).Append(' ').Append(F(v.Normal.Z)).Append(' ').Append(F(-v.Normal.Y)).Append('\n');

        sb.AppendLine("usemtl Default");
        sb.AppendLine("s 1");
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            uint a = mesh.Indices[t] + 1;
            uint b = mesh.Indices[t + 1] + 1;
            uint c = mesh.Indices[t + 2] + 1;
            // 反射变换后绕序翻转（a, c, b），保持右手系外表面
            sb.Append("f ").Append(a).Append('/').Append(a).Append('/').Append(a)
              .Append(' ').Append(c).Append('/').Append(c).Append('/').Append(c)
              .Append(' ').Append(b).Append('/').Append(b).Append('/').Append(b).Append('\n');
        }
        File.WriteAllText(objPath, sb.ToString());

        var mtl = new StringBuilder();
        mtl.AppendLine("newmtl Default");
        if (!string.IsNullOrEmpty(baseColorPath))
        {
            string texName = stem + "_baseColor" + Path.GetExtension(baseColorPath);
            mtl.AppendLine("map_Kd " + texName);
            try
            {
                File.Copy(baseColorPath, Path.Combine(dir, texName), true);
            }
            catch
            {
                // 贴图复制失败不影响模型导出
            }
        }
        mtl.AppendLine("Kd 0.8 0.8 0.8");
        mtl.AppendLine("Ks 0.15 0.15 0.15");
        mtl.AppendLine("Ns 24");
        File.WriteAllText(Path.Combine(dir, mtlName), mtl.ToString());
    }

    // ---------- FBX (ASCII 7.4) ----------

    public static void ExportFbx(RenderMesh mesh, string fbxPath, string? baseColorPath)
    {
        long modelId = 1000000001;
        long geoId = 1000000002;
        long matId = 1000000003;
        long texId = 1000000004;

        int vertexCount = mesh.Vertices.Length;
        int triCount = mesh.Indices.Length / 3;

        var sb = new StringBuilder(1 << 20);
        sb.AppendLine("; FBX 7.4.0 project file");
        sb.AppendLine("; Exported by Delta Model Viewer");
        sb.AppendLine("FBXHeaderExtension:  {");
        sb.AppendLine("\tFBXHeaderVersion: 1003");
        sb.AppendLine("\tFBXVersion: 7400");
        sb.AppendLine("\tCreator: \"Delta Model Viewer\"");
        sb.AppendLine("}");
        sb.AppendLine("GlobalSettings:  {");
        sb.AppendLine("\tVersion: 1000");
        sb.AppendLine("\tProperties70:  {");
        sb.AppendLine("\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",2");
        sb.AppendLine("\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0");
        sb.AppendLine("\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",1");
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        sb.AppendLine("Definitions:  {");
        sb.AppendLine("\tVersion: 100");
        sb.AppendLine("\tCount: 3");
        sb.AppendLine("\tObjectType: \"Model\" {");
        sb.AppendLine("\t\tCount: 1");
        sb.AppendLine("\t}");
        sb.AppendLine("\tObjectType: \"Geometry\" {");
        sb.AppendLine("\t\tCount: 1");
        sb.AppendLine("\t}");
        sb.AppendLine("\tObjectType: \"Material\" {");
        sb.AppendLine("\t\tCount: 1");
        sb.AppendLine("\t}");
        sb.AppendLine("}");

        sb.AppendLine("Objects:  {");

        // ---------- Geometry ----------
        sb.AppendLine($"\tGeometry: {geoId}, \"Geometry::Model\", \"Mesh\" {{");
        sb.AppendLine($"\t\tVertices: *{vertexCount * 3} {{");
        AppendNumberArray(sb, "\t\t\ta: ", EnumerateVertices(mesh));
        sb.AppendLine("\t\t}");
        sb.AppendLine($"\t\tPolygonVertexIndex: *{mesh.Indices.Length} {{");
        AppendIndexArray(sb, "\t\t\ta: ", EnumeratePolygonIndices(mesh));
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tGeometryVersion: 124");

        // 法线：ByControlPoint Direct（与控制点一一对应）
        sb.AppendLine("\t\tLayerElementNormal: 0 {");
        sb.AppendLine("\t\t\tVersion: 102");
        sb.AppendLine("\t\t\tName: \"\"");
        sb.AppendLine("\t\t\tMappingInformationType: \"ByControlPoint\"");
        sb.AppendLine("\t\t\tReferenceInformationType: \"Direct\"");
        sb.AppendLine($"\t\t\tNormals: *{vertexCount * 3} {{");
        AppendNumberArray(sb, "\t\t\t\ta: ", EnumerateNormals(mesh));
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t}");

        // UV：ByControlPoint Direct
        sb.AppendLine("\t\tLayerElementUV: 0 {");
        sb.AppendLine("\t\t\tVersion: 101");
        sb.AppendLine("\t\t\tName: \"UVMap\"");
        sb.AppendLine("\t\t\tMappingInformationType: \"ByControlPoint\"");
        sb.AppendLine("\t\t\tReferenceInformationType: \"Direct\"");
        sb.AppendLine($"\t\t\tUV: *{vertexCount * 2} {{");
        AppendNumberArray(sb, "\t\t\t\ta: ", EnumerateUvs(mesh));
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t}");

        sb.AppendLine("\t\tLayer: 0 {");
        sb.AppendLine("\t\t\tVersion: 100");
        sb.AppendLine("\t\t\tLayerElement:  {");
        sb.AppendLine("\t\t\t\tType: \"LayerElementNormal\"");
        sb.AppendLine("\t\t\t\tTypedIndex: 0");
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t\tLayerElement:  {");
        sb.AppendLine("\t\t\t\tType: \"LayerElementUV\"");
        sb.AppendLine("\t\t\t\tTypedIndex: 0");
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t}");

        // ---------- Model ----------
        sb.AppendLine($"\tModel: {modelId}, \"Model::Model\", \"Mesh\" {{");
        sb.AppendLine("\t\tVersion: 232");
        sb.AppendLine("\t\tProperties70:  {");
        sb.AppendLine("\t\t\tP: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",0,0,0");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tShading: T");
        sb.AppendLine("\t\tCulling: \"CullingOff\"");
        sb.AppendLine("\t}");

        // ---------- Material ----------
        sb.AppendLine($"\tMaterial: {matId}, \"Material::Default\", \"\" {{");
        sb.AppendLine("\t\tVersion: 102");
        sb.AppendLine("\t\tShadingModel: \"phong\"");
        sb.AppendLine("\t\tMultiLayer: 0");
        sb.AppendLine("\t\tProperties70:  {");
        sb.AppendLine("\t\t\tP: \"DiffuseColor\", \"Color\", \"\", \"A\",0.8,0.8,0.8");
        sb.AppendLine("\t\t\tP: \"SpecularColor\", \"Color\", \"\", \"A\",0.15,0.15,0.15");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t}");

        // ---------- Texture（可选） ----------
        if (!string.IsNullOrEmpty(baseColorPath))
        {
            string texName = Path.GetFileNameWithoutExtension(fbxPath) + "_baseColor" + Path.GetExtension(baseColorPath);
            sb.AppendLine($"\tTexture: {texId}, \"Texture::BaseColor\", \"\" {{");
            sb.AppendLine("\t\tType: \"TextureVideoClip\"");
            sb.AppendLine("\t\tVersion: 202");
            sb.AppendLine($"\t\tTextureName: \"Texture::BaseColor\"");
            sb.AppendLine("\t\tMedia: \"\"");
            sb.AppendLine($"\t\tFileName: \"{texName}\"");
            sb.AppendLine("\t\tRelativeFilename: \"" + texName + "\"");
            sb.AppendLine("\t\tModelUVTranslation: 0,0");
            sb.AppendLine("\t\tModelUVScaling: 1,1");
            sb.AppendLine("\t\tTexture_Alpha_Source: \"None\"");
            sb.AppendLine("\t\tCropping: 0,0,0,0");
            sb.AppendLine("\t}");
            try
            {
                File.Copy(baseColorPath, Path.Combine(Path.GetDirectoryName(fbxPath)!, texName), true);
            }
            catch
            {
                // 贴图复制失败不影响模型导出
            }
        }

        sb.AppendLine("}");

        // ---------- Connections ----------
        sb.AppendLine("Connections:  {");
        sb.AppendLine($"\tC: \"OO\",{modelId},0");
        sb.AppendLine($"\tC: \"OO\",{geoId},{modelId}");
        sb.AppendLine($"\tC: \"OO\",{matId},{modelId}");
        if (!string.IsNullOrEmpty(baseColorPath))
            sb.AppendLine($"\tC: \"OP\",{texId},{matId},\"DiffuseColor\"");
        sb.AppendLine("}");

        File.WriteAllText(fbxPath, sb.ToString());
    }

    // ---------- 数据枚举（坐标转换：Z 轴上 → Y 轴上） ----------

    private static IEnumerable<double> EnumerateVertices(RenderMesh mesh)
    {
        foreach (var v in mesh.Vertices)
        {
            yield return v.Position.X;
            yield return v.Position.Z;
            yield return -v.Position.Y;
        }
    }

    private static IEnumerable<double> EnumerateNormals(RenderMesh mesh)
    {
        foreach (var v in mesh.Vertices)
        {
            yield return v.Normal.X;
            yield return v.Normal.Z;
            yield return -v.Normal.Y;
        }
    }

    private static IEnumerable<double> EnumerateUvs(RenderMesh mesh)
    {
        foreach (var v in mesh.Vertices)
        {
            yield return v.UV.X;
            yield return 1.0 - v.UV.Y;
        }
    }

    /// <summary>三角形索引（反射变换后绕序翻转 a,c,b；每面末索引取负结束）。</summary>
    private static IEnumerable<long> EnumeratePolygonIndices(RenderMesh mesh)
    {
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            long a = mesh.Indices[t];
            long b = mesh.Indices[t + 1];
            long c = mesh.Indices[t + 2];
            yield return a;
            yield return c;
            yield return -b - 1;
        }
    }

    /// <summary>索引数组输出（整数，长数组分行）。</summary>
    private static void AppendIndexArray(StringBuilder sb, string prefix, IEnumerable<long> numbers)
    {
        sb.Append(prefix);
        int count = 0;
        foreach (long n in numbers)
        {
            if (count > 0)
                sb.Append(',');
            if (count > 0 && count % 64 == 0)
                sb.Append('\n').Append(prefix);
            sb.Append(n.ToString(Inv));
            count++;
        }
        sb.Append('\n');
    }

    /// <summary>数字数组输出（长数组分行，避免单行过长）。</summary>
    private static void AppendNumberArray(StringBuilder sb, string prefix, IEnumerable<double> numbers)
    {
        sb.Append(prefix);
        int count = 0;
        foreach (double n in numbers)
        {
            if (count > 0)
                sb.Append(',');
            if (count > 0 && count % 64 == 0)
                sb.Append('\n').Append(prefix);
            sb.Append(n.ToString("0.0#######", Inv));
            count++;
        }
        sb.Append('\n');
    }
}
