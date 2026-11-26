using System.Collections.Concurrent;
using MabiTool.Infrastructure.Updates;
using MabiTool.Infrastructure.Workers;

namespace MabiTool.Host.Color;

/// <summary>
/// 染色引擎的运行目录：引擎在 asset root 下按运行标识建目录，写入截图与核验记录，运行结束后留作排查依据。
/// 只保留创建时间最近的 <see cref="Keep"/> 个；目标色集合图渲染期间登记的目录不删（它在结束时删除自己的目录），
/// 名称不是运行标识的内容与链接不动。
/// </summary>
internal static class ColorRunFolders
{
    public const int Keep = 3;
    private static readonly ConcurrentDictionary<string, byte> inUse = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记一个正在使用、不能被回收的目录，释放后恢复为普通目录。</summary>
    public static IDisposable Use(string folder)
    {
        var full = Path.GetFullPath(folder);
        inUse[full] = 0;
        return new Lease(full);
    }

    /// <summary>删除最近 <see cref="Keep"/> 个以外的运行目录；单个目录删除失败时跳过，返回删除的数量。</summary>
    public static int Prune(string root)
    {
        if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null) return 0;
        var folders = new DirectoryInfo(root).EnumerateDirectories()
            .Where(folder => WorkerProtocol.IsId(folder.Name) && folder.LinkTarget is null && !inUse.ContainsKey(folder.FullName))
            .OrderByDescending(folder => folder.CreationTimeUtc).ThenByDescending(folder => folder.Name, StringComparer.Ordinal)
            .Skip(Keep).ToArray();
        var removed = 0;
        foreach (var folder in folders)
        {
            try { UpdatePaths.DeleteOwnedTree(folder.FullName); removed++; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or UpdatePackageException) { }
        }
        return removed;
    }

    private sealed class Lease(string folder) : IDisposable
    {
        public void Dispose() => inUse.TryRemove(folder, out _);
    }
}
