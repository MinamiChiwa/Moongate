using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MabiTool.ModuleSdk;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>
/// 前端文件位置：全部位于工具根（程序所在文件夹，目录包为包根下的 userdata，或 <c>--portable-root</c> 指定的目录）下。偏好、浮窗版面、背包备注、
/// Buff 收藏与默认素材包目录在 <c>desktop/</c>，宿主实例发现文件在 <c>runtime/instance.json</c>；与宿主按同一规则判定工具根。
/// </summary>
public static class DesktopFiles
{
    private static string? root;

    /// <summary>工具根；启动时由 <see cref="Configure"/> 按命令行确定，未设置时由程序目录推出。</summary>
    public static string Root => root ??= ToolRoot.FromProgramDirectory(AppContext.BaseDirectory);
    public static string Directory => ToolRoot.Desktop(Root);
    public static string DiscoveryFile => ToolRoot.DiscoveryFile(Root);
    public static string File(string name) => Path.Combine(Directory, name);

    /// <summary>
    /// 保存到偏好里的路径：位于工具根内时存为相对工具根的路径，程序文件夹整体搬移或改名后仍指向同一位置；工具根外的路径原样保存。
    /// </summary>
    public static string ToToolRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        var relative = Path.GetRelativePath(Root, path);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? path : relative;
    }

    /// <summary>读取偏好里的路径：相对路径按当前工具根解析，绝对路径原样使用。</summary>
    public static string FromToolRoot(string path) => Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(Root, path));

    /// <summary>工具根不可写时的说明（启动时检查）；为空表示可写。</summary>
    public static string? Problem { get; private set; }

    /// <summary>启动阶段发现的其他问题（例如随包宿主未能启动）；已有问题时保留先发现的那一条。</summary>
    internal static void ReportProblem(string message) => Problem ??= message;

    /// <summary>旧版前端文件目录 %LOCALAPPDATA%\Moongate\desktop；只在工具根还没有 desktop 目录时检查。</summary>
    public static string LegacyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moongate", "desktop");

    /// <summary>按命令行确定工具根，首次以工具根布局启动时迁入旧位置的前端文件，并检查工具根是否可写。</summary>
    public static void Configure(IReadOnlyList<string> args)
    {
        try { root = ToolRoot.FromArguments(args); }
        catch (ArgumentException error)
        {
            // --portable-root 缺少绝对路径：按程序所在文件夹启动，并在连接状态里写明参数错误。
            root = ToolRoot.FromProgramDirectory(AppContext.BaseDirectory); Problem = error.Message;
            return;
        }
        // 与宿主一致：只有未显式指定工具根时才迁入旧默认位置。迁入失败保留旧目录，前端使用默认偏好照常启动。
        if (!args.Contains(ToolRoot.Argument))
            try { ImportLegacy(LegacyDirectory, Directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        Problem = Writable(Directory) ? null : ToolRoot.NotWritableMessage(Root);
    }

    /// <summary>
    /// 目标目录不存在且旧目录存在时，把旧目录的文件逐个复制并校验 SHA-256，全部一致后改名发布为目标目录；旧目录保持原样。
    /// 偏好中指向旧目录内的素材包路径改到新目录。返回迁入的文件数，未迁入时返回 0。
    /// </summary>
    internal static int ImportLegacy(string legacy, string target)
    {
        if (System.IO.Directory.Exists(target) || !System.IO.Directory.Exists(legacy)) return 0;
        // 不跟随联接与符号链接，只复制旧目录内的实际文件。
        var files = System.IO.Directory.EnumerateFiles(legacy, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).ToArray();
        var staging = target + ".import";
        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
        try
        {
            foreach (var source in files)
            {
                var destination = Path.Combine(staging, Path.GetRelativePath(legacy, source));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                System.IO.File.Copy(source, destination);
                using var original = System.IO.File.OpenRead(source);
                using var copy = System.IO.File.OpenRead(destination);
                if (!SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(copy)))
                    throw new IOException("迁入的前端文件校验不一致");
            }
            System.IO.Directory.CreateDirectory(staging);
            RebaseAssetPack(Path.Combine(staging, "preferences.json"), legacy, target);
            System.IO.Directory.Move(staging, target);
        }
        catch { if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true); throw; }
        return files.Length;
    }

    private static void RebaseAssetPack(string preferences, string legacy, string target)
    {
        if (!System.IO.File.Exists(preferences) || JsonNode.Parse(System.IO.File.ReadAllBytes(preferences)) is not JsonObject document) return;
        var key = document.Select(pair => pair.Key).FirstOrDefault(name => name.Equals("AssetPack", StringComparison.OrdinalIgnoreCase));
        if (key is null || document[key]?.GetValueKind() != System.Text.Json.JsonValueKind.String) return;
        var pack = document[key]!.GetValue<string>();
        var relative = Path.IsPathFullyQualified(pack) ? Path.GetRelativePath(legacy, pack) : null;
        if (relative is null || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) return;
        document[key] = Path.Combine(target, relative);
        System.IO.File.WriteAllText(preferences, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool Writable(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-check-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
