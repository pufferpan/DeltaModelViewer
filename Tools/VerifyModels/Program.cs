using System.Diagnostics;
using DeltaCore;

string root = args.Length > 0 ? args[0] : @"E:\deltamodel\DeltaForce\Content\Models\Props\Collect";
if (!Directory.Exists(root))
{
    Console.WriteLine($"目录不存在: {root}");
    return 1;
}

var files = Directory.GetFiles(root, "*.uemodel", SearchOption.AllDirectories);
Console.WriteLine($"扫描到 {files.Length} 个 .uemodel 文件，开始批量解析...");

int ok = 0, fail = 0;
var failures = new List<(string, string)>();
long totalVerts = 0, totalTris = 0;
int noUv = 0, multiUv = 0, noNormals = 0, multiMat = 0, multiLod = 0;
string biggest = ""; long biggestVerts = 0;
string highestPoly = ""; long highestTris = 0;
var sw = Stopwatch.StartNew();

foreach (var file in files)
{
    try
    {
        var bytes = File.ReadAllBytes(file);
        var model = UemodelReader.Read(bytes);
        var mesh = MeshBuilder.Build(model, 0);

        ok++;
        totalVerts += mesh.Vertices.Length;
        totalTris += mesh.Indices.Length / 3;

        if (mesh.UvChannelCount == 0) noUv++;
        if (mesh.UvChannelCount > 1) multiUv++;
        if (!mesh.HasNormals) noNormals++;
        if (mesh.Ranges.Count > 1) multiMat++;
        if (model.Lods.Count > 1) multiLod++;

        if (mesh.Vertices.Length > biggestVerts) { biggestVerts = mesh.Vertices.Length; biggest = file; }
        if (mesh.Indices.Length / 3 > highestTris) { highestTris = mesh.Indices.Length / 3; highestPoly = file; }
    }
    catch (Exception ex)
    {
        fail++;
        if (failures.Count < 30) failures.Add((file, ex.Message));
    }
}

sw.Stop();
Console.WriteLine();
Console.WriteLine($"===== 结果 =====");
Console.WriteLine($"成功: {ok} / {files.Length}, 失败: {fail}, 耗时: {sw.Elapsed.TotalSeconds:F2}s");
Console.WriteLine($"顶点总数: {totalVerts:N0}, 三角形总数: {totalTris:N0}");
Console.WriteLine($"无 UV: {noUv} 个, 多 UV 通道: {multiUv} 个, 无原始法线(重建): {noNormals} 个");
Console.WriteLine($"多材质: {multiMat} 个, 多 LOD: {multiLod} 个");
Console.WriteLine($"最大模型: {Path.GetFileName(biggest)} ({biggestVerts:N0} 顶点)");
Console.WriteLine($"最高面数: {Path.GetFileName(highestPoly)} ({highestTris:N0} 三角形)");
if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"前 {failures.Count} 个失败:");
    foreach (var (f, msg) in failures)
        Console.WriteLine($"  [{Path.GetDirectoryName(f)?.Split(Path.DirectorySeparatorChar).Last()}] {Path.GetFileName(f)}: {msg}");
}
return fail == 0 ? 0 : 2;
