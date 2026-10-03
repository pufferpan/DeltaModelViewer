namespace DeltaModelViewer.Core;

/// <summary>根据用户输入的名称定位模型文件与贴图（递归搜索所有子文件夹）。</summary>
public static class PathResolver
{
    /// <summary>在模型根目录下递归查找与输入匹配的模型文件夹。</summary>
    public static string? FindModelFolder(string modelsRoot, string input)
    {
        var name = input.Trim();
        if (name.Length == 0) return null;
        if (name.EndsWith(".uemodel", StringComparison.OrdinalIgnoreCase))
            name = name[..^".uemodel".Length];

        if (!Directory.Exists(modelsRoot)) return null;

        // 1. 直接路径（支持相对路径定位子文件夹）
        var exact = Path.Combine(modelsRoot, name);
        if (Directory.Exists(exact)) return exact;

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(modelsRoot, "*", SearchOption.AllDirectories).ToList();
        }
        catch
        {
            return null;
        }

        // 2. 相对路径精确匹配（忽略大小写）
        string? rel = dirs.FirstOrDefault(d =>
            string.Equals(Path.GetRelativePath(modelsRoot, d), name, StringComparison.OrdinalIgnoreCase));
        if (rel is not null) return rel;

        // 3. 文件夹名精确匹配（忽略大小写），多个同名取路径最短
        var byName = dirs
            .Where(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Length)
            .ToList();
        if (byName.Count > 0) return byName[0];

        // 4. 文件夹名包含输入
        var contains = dirs
            .Where(d => Path.GetFileName(d).Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Length)
            .ToList();
        if (contains.Count > 0) return contains[0];

        // 5. 文件夹内的 .uemodel 文件名包含输入
        foreach (var dir in dirs)
        {
            try
            {
                if (Directory.EnumerateFiles(dir, "*.uemodel")
                    .Any(f => Path.GetFileName(f).Contains(name, StringComparison.OrdinalIgnoreCase)))
                    return dir;
            }
            catch
            {
                // 跳过无权限/异常目录
            }
        }
        return null;
    }

    /// <summary>
    /// 在贴图根目录下按相对路径查找 BaseColor 与法线贴图。
    /// 相对路径与模型目录结构同名（支持子文件夹）。
    /// </summary>
    public static (string? BaseColor, string? Normal) FindTextures(string texturesRoot, string relativePath)
    {
        if (!Directory.Exists(texturesRoot)) return (null, null);

        string? texFolder = null;
        var direct = Path.Combine(texturesRoot, relativePath);
        if (Directory.Exists(direct))
        {
            texFolder = direct;
        }
        else
        {
            string fileName = Path.GetFileName(relativePath);
            try
            {
                texFolder = Directory.EnumerateDirectories(texturesRoot, "*", SearchOption.AllDirectories)
                    .OrderBy(d => d.Length)
                    .FirstOrDefault(d =>
                        string.Equals(Path.GetFileName(d), fileName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                texFolder = null;
            }
        }
        if (texFolder is null) return (null, null);

        // FModel 导出的贴图一般在 Texture/ 或 Textures/ 子文件夹里，也可能直接在根下
        var searchDirs = new List<string> { texFolder };
        foreach (var sub in new[] { "Texture", "Textures" })
        {
            var candidate = Path.Combine(texFolder, sub);
            if (Directory.Exists(candidate)) searchDirs.Add(candidate);
        }

        var files = searchDirs
            .SelectMany(d =>
            {
                try { return Directory.EnumerateFiles(d, "*.png"); }
                catch { return Enumerable.Empty<string>(); }
            })
            .ToList();

        return (
            Pick(files, "BaseColorMap", "BaseColor", "Diffuse"),
            Pick(files, "NRMMap", "NRMap", "NormalMap"));
    }

    private static string? Pick(List<string> files, params string[] patterns)
    {
        var candidates = files
            .Select(f => (Path: f, Score: BestScore(Path.GetFileName(f), patterns)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.Count > 0 ? candidates[0].Path : null;
    }

    private static int BestScore(string fileName, string[] patterns)
    {
        int best = 0;
        foreach (var p in patterns)
        {
            if (fileName.Contains(p, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, p.Length);
        }
        return best;
    }
}
