namespace MabiTool.Host;

/// <param name="PortFallback">端口取的是默认值、未经显式指定：默认端口被非 Moongate 程序占用或被系统拒绝时改用系统分配的空闲端口
/// （<see cref="HostStartupFailure.ResolveDefaultPortAsync"/>）。显式指定的端口绑定失败时照常报错，不改用别的端口。</param>
public sealed record LocalApiOptions(int Port, string Token, IReadOnlySet<string> AllowedOrigins, string? RuntimeDirectory = null,
    bool PortFallback = false)
{
    /// <summary>未设置 MABITOOL_API_PORT 时首先尝试的本机端口。</summary>
    public const int DefaultPort = 19998;

    public override string ToString() =>
        $"LocalApiOptions {{ Port = {Port}, PortFallback = {PortFallback}, Token = [redacted], RuntimeDirectory = {RuntimeDirectory} }}";

    public void Validate()
    {
        if (Port is < 0 or > 65535) throw new ArgumentException("端口必须在 0–65535 范围内");
        if (Token.Length < 43 || Token.Length > 256 || Token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("MABITOOL_API_TOKEN 必须是 43–256 个字母、数字、减号或下划线；建议生成 32 字节随机值");
        foreach (var origin in AllowedOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.GetLeftPart(UriPartial.Authority) != origin ||
                !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("允许的 Origin 必须为完整 http/https 源");
        if (RuntimeDirectory is not null && (!Path.IsPathFullyQualified(RuntimeDirectory) ||
            Path.GetFileName(Path.TrimEndingDirectorySeparator(RuntimeDirectory)) != "runtime"))
            throw new ArgumentException("本机发现目录必须为绝对路径，末级目录名为 runtime");
    }

    /// <param name="toolRoot">工具根；为空时由程序目录推出。实例发现目录默认是工具根下的 runtime。</param>
    public static LocalApiOptions FromEnvironment(string? toolRoot = null)
    {
        var rawPort = Environment.GetEnvironmentVariable("MABITOOL_API_PORT");
        var port = string.IsNullOrEmpty(rawPort) ? DefaultPort :
            int.TryParse(rawPort, out var parsed) ? parsed : throw new ArgumentException("MABITOOL_API_PORT 无效");
        return new(port, Environment.GetEnvironmentVariable("MABITOOL_API_TOKEN") ?? LocalAuthService.NewSecret(),
            (Environment.GetEnvironmentVariable("MABITOOL_ALLOWED_ORIGINS") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal),
            Environment.GetEnvironmentVariable("MABITOOL_RUNTIME_DIRECTORY") ??
                MabiTool.ModuleSdk.ToolRoot.Runtime(toolRoot ?? MabiTool.ModuleSdk.ToolRoot.FromProgramDirectory(AppContext.BaseDirectory)),
            PortFallback: string.IsNullOrEmpty(rawPort));
    }
}
