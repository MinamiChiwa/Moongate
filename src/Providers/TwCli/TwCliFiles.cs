using System.Diagnostics;
using System.Security.Cryptography;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Providers.TwCli;

public static class TwCliFile
{
    public const string FileName = "MabinogiMobile_CLI.exe";
    public const string GameExecutable = "MabinogiMobile.exe";
    /// <summary>登记的命令行工具 SHA-256（版本 0.1.0+b587207e73fabc7e8993d2e4d38c803f87e013f7）。</summary>
    public const string RegisteredSha256 = "913a4c38a0275ced1b5925a2d8e14dbefb0595c045d2590e5c04295ca8748cc8";
}

public sealed class TwCliFileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record TwCliSourceInfo(GameConnectorCliSource Source, GameConnectorCliFileState State, string? Path, string? Version);

/// <summary>命令行工具在台服目录里的状态：<see cref="Verified"/> 为部署后的文件哈希与登记值相同，<see cref="Differs"/> 为存在但哈希不同。</summary>
public sealed record TwCliDeployment(bool Verified, bool Differs, string? Path);

/// <summary>
/// 命令行工具文件：来源按“发行包组件、本机韩服目录”的优先级核对哈希，部署到台服目录时目标不存在则创建、相同跳过、不同拒绝。
/// 路径及其上级不得是联接或符号链接；哈希核对按文件长度与修改时间缓存。
/// </summary>
public sealed class TwCliFiles(Func<string?> packagePath, Func<IReadOnlyList<string>> koreanCandidates, string expectedSha256 = TwCliFile.RegisteredSha256)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (long Length, long Written, bool Match)> cache = new(StringComparer.OrdinalIgnoreCase);

    public TwCliSourceInfo Resolve()
    {
        var mismatch = default(TwCliSourceInfo);
        foreach (var (source, path) in Candidates())
        {
            var state = Check(path);
            if (state == GameConnectorCliFileState.Verified) return new(source, state, path, Version(path));
            if (state == GameConnectorCliFileState.HashMismatch) mismatch ??= new(source, state, path, null);
        }
        return mismatch ?? new(GameConnectorCliSource.None, GameConnectorCliFileState.Missing, null, null);
    }

    public TwCliDeployment Inspect(string? gameDirectory)
    {
        if (gameDirectory is null) return new(false, false, null);
        var target = Path.Combine(gameDirectory, TwCliFile.FileName);
        return Check(target) switch
        {
            GameConnectorCliFileState.Verified => new(true, false, target),
            GameConnectorCliFileState.HashMismatch => new(false, true, target),
            _ => new(false, false, null)
        };
    }

    /// <summary>把已核对的来源部署到台服目录。目标已存在且相同跳过；不同拒绝（不覆盖用户文件）。</summary>
    public void Deploy(string gameDirectory)
    {
        if (!File.Exists(Path.Combine(gameDirectory, TwCliFile.GameExecutable)))
            throw new TwCliFileException("gameCli.directoryInvalid", "所选目录里没有台服游戏程序");
        var source = Resolve();
        var target = Path.Combine(gameDirectory, TwCliFile.FileName);
        if (File.Exists(target))
        {
            if (Check(target) == GameConnectorCliFileState.Verified) return;
            throw new TwCliFileException("gameCli.deployedDiffers", "台服目录已有不同的命令行工具，已保留该文件");
        }
        if (source.State != GameConnectorCliFileState.Verified || source.Path is null)
            throw new TwCliFileException("gameCli.cliUnavailable", "命令行工具文件不可用");
        try
        {
            using (var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TwCliFileException("gameCli.writeFailed", "部署命令行工具失败：" + error.Message);
        }
        lock (gate) cache.Remove(target);
        if (Check(target) != GameConnectorCliFileState.Verified)
        {
            try { File.Delete(target); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw new TwCliFileException("gameCli.verifyFailed", "命令行工具部署后校验失败");
        }
    }

    private IEnumerable<(GameConnectorCliSource Source, string Path)> Candidates()
    {
        if (packagePath() is { Length: > 0 } package && Path.IsPathFullyQualified(package)) yield return (GameConnectorCliSource.Package, package);
        foreach (var path in koreanCandidates()) yield return (GameConnectorCliSource.LocalKorean, path);
    }

    private GameConnectorCliFileState Check(string path)
    {
        try
        {
            if (!File.Exists(path)) return GameConnectorCliFileState.Missing;
            if (OperatingSystem.IsWindows()) LocalDiscoverySecurity.RejectLinks(path);
            var info = new FileInfo(path);
            lock (gate)
                if (cache.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc.Ticks)
                    return known.Match ? GameConnectorCliFileState.Verified : GameConnectorCliFileState.HashMismatch;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var match = string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expectedSha256, StringComparison.OrdinalIgnoreCase);
            lock (gate) cache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, match);
            return match ? GameConnectorCliFileState.Verified : GameConnectorCliFileState.HashMismatch;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return GameConnectorCliFileState.Missing;
        }
    }

    private static string? Version(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
