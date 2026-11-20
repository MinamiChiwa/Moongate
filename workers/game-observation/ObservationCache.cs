namespace MabiTool.Observation.Worker;

/// <summary>
/// 数据根内的缓存目录：宿主以启动环境 <see cref="Variable"/> 给出数据根下 cache 目录的绝对路径，Worker 读取后移除。
/// 库存布局缓存在 inventory/client-layouts，未登记构建的核对结果在 game-access/client-builds，与宿主同一位置；
/// 宿主没有给出时不缓存，每次照常读取客户端文件。
/// </summary>
internal static class ObservationCache
{
    internal const string Variable = "MABITOOL_OBSERVATION_CACHE_ROOT";
    private static string? root;

    internal static void StartFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, null);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return;
        try
        {
            value = Path.GetFullPath(value);
            System.IO.Directory.CreateDirectory(value);
            MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(value);
            root = value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { }
    }

    /// <summary>缓存子目录的绝对路径（不存在时创建）；没有缓存根、创建失败或该目录及其上级含链接时为空，调用方照常读取客户端文件、不写缓存。</summary>
    internal static string? Directory(params string[] segments)
    {
        if (root is null) return null;
        var path = Path.Combine([root, .. segments]);
        try
        {
            MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(path);
            System.IO.Directory.CreateDirectory(path);
            MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(path);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { return null; }
    }
}
