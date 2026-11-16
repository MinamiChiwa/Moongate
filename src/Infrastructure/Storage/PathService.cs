using System.Text.RegularExpressions;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed class PathService
{
    public string DataRoot { get; }
    public string RootId { get; }
    public string BootstrapDirectory { get; }
    public bool Portable { get; }
    public string Database => Resolve("store", "main.sqlite");
    public string Assets => Resolve("assets");
    public string Settings => Resolve("settings");
    public string Cache => Resolve("cache");
    public string Logs => Resolve("logs");
    public string Diagnostics => Resolve("diagnostics");
    public string Temporary => Resolve("temp");
    public string Backups => Resolve("backups");
    public string Recovery => Resolve("recovery");

    internal PathService(string root, string rootId, string bootstrapDirectory, bool portable)
    {
        DataRoot = root;
        RootId = rootId;
        BootstrapDirectory = bootstrapDirectory;
        Portable = portable;
    }

    // 各段是平台内部逻辑名称，不接受外部绝对路径或文件选择器结果。
    public string Resolve(params string[] segments)
    {
        if (segments.Length == 0 || segments.Any(s => s.Length is 0 or > 128 ||
            !Regex.IsMatch(s, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\z") || s.EndsWith('.') ||
            Regex.IsMatch(s, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)", RegexOptions.IgnoreCase)))
            throw new StorageException("storage.pathInvalid", "存储子路径必须使用受控的逻辑名称");
        var result = Path.Combine([DataRoot, .. segments]);
        StoragePaths.ValidateLocal(result);
        return result;
    }
}

internal static class StoragePaths
{
    public static string ValidateLocal(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("数据根当前支持 Windows 本地存储");
        if (!Path.IsPathFullyQualified(path) || !Regex.IsMatch(path, @"\A[a-zA-Z]:[\\/]"))
            throw new StorageException("storage.pathInvalid", "数据路径必须为本地磁盘绝对路径");
        var parts = path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') ||
            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Regex.IsMatch(p, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)", RegexOptions.IgnoreCase)))
            throw new StorageException("storage.pathInvalid", "数据路径含有不受支持的名称或路径段");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new StorageException("storage.unsupportedVolume", "数据根需要就绪的本地固定或可移动磁盘");
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var cloud = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(cloud) && Path.IsPathFullyQualified(cloud) && IsWithin(full, Path.GetFullPath(cloud)))
                throw new StorageException("storage.unsupportedVolume", "活动数据根不能位于已知云同步目录");
        }
        LocalDiscoverySecurity.RejectLinks(full);
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.Offline) != 0)
                throw new StorageException("storage.unsupportedVolume", "数据路径不能使用离线占位目录");
        return full;
    }

    public static bool IsWithin(string path, string parent) => string.Equals(path, Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
