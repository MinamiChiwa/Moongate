using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MabiTool.Providers.Tunnels;

public sealed record TunnelComponentOptions(string ExecutablePath, string Sha256, string Version)
{
    public static TunnelComponentOptions? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("MABITOOL_CLOUDFLARED_PATH");
        var hash = Environment.GetEnvironmentVariable("MABITOOL_CLOUDFLARED_SHA256");
        var version = Environment.GetEnvironmentVariable("MABITOOL_CLOUDFLARED_VERSION");
        return path is null && hash is null && version is null ? null : new(path ?? "", hash ?? "", version ?? "");
    }

    internal async Task<FileStream> VerifyAndPinAsync(CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(ExecutablePath) || ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            Uri.TryCreate(ExecutablePath, UriKind.Absolute, out var uri) && uri.IsUnc ||
            !Regex.IsMatch(Sha256, @"\A[0-9a-fA-F]{64}\z") || !Regex.IsMatch(Version, @"\A[0-9]{4}\.[0-9]{1,2}\.[0-9]{1,3}\z"))
            throw new TunnelException("room.tunnelComponentInvalid", "隧道组件路径、哈希或版本配置无效");
        FileStream? pin = null;
        try
        {
            var path = Path.GetFullPath(ExecutablePath);
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new TunnelException("room.tunnelComponentInvalid", "隧道组件路径包含链接");
            pin = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (pin.Length is < 1 or > 128 * 1024 * 1024)
                throw new TunnelException("room.tunnelComponentInvalid", "隧道组件大小超出预算");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(pin, ct));
            if (!string.Equals(hash, Sha256, StringComparison.OrdinalIgnoreCase))
                throw new TunnelException("room.tunnelComponentInvalid", "隧道组件内容哈希不匹配");
            return pin;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (pin is not null) await pin.DisposeAsync();
            throw new TunnelException("room.tunnelComponentInvalid", "隧道组件不可读取或身份不匹配");
        }
        catch { if (pin is not null) await pin.DisposeAsync(); throw; }
    }
}

public sealed class TunnelException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
