using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MabiTool.Contracts;
using Microsoft.AspNetCore.Connections;

namespace MabiTool.Host;

/// <summary>
/// 宿主启动失败的分类：原因码、退出码与中文原因。目录包前端只看得到退出码，所以每类失败各有一个退出码，
/// 前端据此显示对应说明（<c>PackagedHost.FailureMessage</c>）；命令行启动时原因写到标准错误。
/// </summary>
public static class HostStartupFailure
{
    /// <summary>数据目录不可用（存储异常、启动阶段的文件访问与权限失败）。</summary>
    public const int StorageExitCode = 3;

    /// <summary>本机 API 端口已被占用，占用者是 Moongate 宿主或无法辨认（最常见的是另一个文件夹里的 Moongate 仍在运行）。</summary>
    public const int PortInUseExitCode = 5;

    /// <summary>
    /// 本机 API 端口被其他程序占用，或系统拒绝监听（端口在系统保留范围内、没有权限）。默认端口出现这类情况时宿主改用空闲端口，
    /// 所以这个退出码只出现在显式指定端口（MABITOOL_API_PORT）或系统分配的端口也无法监听时。
    /// </summary>
    public const int PortUnavailableExitCode = 6;

    /// <summary>无法归类的启动失败或运行期失败；原因写出原始异常类型与消息。</summary>
    public const int UnknownExitCode = 7;

    public sealed record Outcome(string Code, int ExitCode, string Message);

    /// <summary>端口被占用时占用者的辨认结果。</summary>
    public enum Occupant { Moongate, Other, Unknown }

    /// <param name="started">宿主已开始监听；之后的异常属于运行期，不按数据目录或端口问题解释。</param>
    /// <param name="identify">辨认端口占用者；为空时用 <see cref="IdentifyOccupantAsync"/>。</param>
    public static async Task<Outcome> DescribeAsync(Exception error, int port, bool started,
        Func<int, CancellationToken, Task<Occupant>>? identify = null, CancellationToken cancellationToken = default)
    {
        if (started)
            return new("host.runtimeFailed", UnknownExitCode, $"宿主运行中出错（{error.GetType().FullName}）：{error.Message}");
        if (error is MabiTool.Infrastructure.StorageException storage)
            return new(storage.Code, StorageExitCode, storage.Message);
        if (IsAddressInUse(error))
        {
            var occupant = await (identify ?? IdentifyOccupantAsync)(port, cancellationToken).ConfigureAwait(false);
            return occupant == Occupant.Other
                ? new("host.portUnavailable", PortUnavailableExitCode,
                    $"本机端口 {port} 已被其他程序占用，Moongate 后端无法启动。请关闭占用该端口的程序，或改用 MABITOOL_API_PORT 指定其他端口（清除该设置时后端自动选用空闲端口）后重新打开 Moongate；" +
                    $"在命令提示符运行 netstat -ano | findstr :{port} 可查看占用进程的编号。")
                : new("host.portInUse", PortInUseExitCode, PortInUseMessage(port, occupant == Occupant.Moongate));
        }
        if (Find<SocketException>(error) is { } socket)
            return new("host.portUnavailable", PortUnavailableExitCode,
                $"系统拒绝在本机端口 {port} 监听（{socket.SocketErrorCode}），常见原因是该端口落在系统保留的端口范围内。" +
                "可在命令提示符运行 netsh interface ipv4 show excludedportrange protocol=tcp 查看保留范围；重启电脑后保留范围通常会变化。" +
                "改用 MABITOOL_API_PORT 指定其他端口，或清除该设置让后端自动选用空闲端口。");
        if (IsStorage(error))
            return new("storage.unavailable", StorageExitCode, $"程序文件夹里的数据无法访问：{error.Message}");
        return new("host.startupFailed", UnknownExitCode, $"未知启动错误（{error.GetType().FullName}）：{error.Message}");
    }

    /// <summary>端口被占用的说明；<paramref name="confirmed"/> 表示占用者已确认是 Moongate 宿主。</summary>
    public static string PortInUseMessage(int port, bool confirmed) =>
        (confirmed
            ? $"本机端口 {port} 已被另一个正在运行的 Moongate 后端占用，通常是另一个文件夹里的 Moongate 仍在运行。"
            : $"本机端口 {port} 已被占用，通常是另一个文件夹里的 Moongate 仍在运行。") +
        "请先关闭那一份的 Moongate 窗口（后台服务随之退出），或在任务管理器的“详细信息”里结束命令行含 MabiTool.Host 的 dotnet.exe；然后重新打开本程序。";

    /// <summary>默认端口不可用、改用系统分配端口的原因：<c>occupied</c> 为被其他程序占用，<c>refused</c> 为系统拒绝监听。</summary>
    public sealed record PortFallback(int RequestedPort, string Reason);

    /// <summary>
    /// 端口取默认值时先试绑默认端口：能绑定就照用；被确认不是 Moongate 的程序占用、或系统拒绝监听（保留范围、没有权限）时改用端口 0，
    /// 由系统分配空闲回环端口，实际地址照常写入发现文件。占用者是 Moongate 宿主或无法辨认时抛出绑定异常，按 <see cref="DescribeAsync"/>
    /// 以退出码 5 结束：两份宿主同时抓包和向游戏发送输入会互相干扰，无法辨认的占用者可能是正忙或停滞的 Moongate 宿主。
    /// 显式指定的端口不做这一步，绑定失败照常报错。
    /// </summary>
    /// <remarks>试绑只 Bind 不 Listen，关闭后不留连接；试绑与正式监听之间端口被抢占时，正式监听的失败照常分类。</remarks>
    public static async Task<(LocalApiOptions Options, PortFallback? Fallback)> ResolveDefaultPortAsync(LocalApiOptions options,
        Func<int, CancellationToken, Task<Occupant>>? identify = null, CancellationToken cancellationToken = default)
    {
        if (!options.PortFallback || options.Port == 0) return (options, null);
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, options.Port));
            return (options, null);
        }
        catch (SocketException error)
        {
            var outcome = await DescribeAsync(error, options.Port, false, identify, cancellationToken).ConfigureAwait(false);
            if (outcome.ExitCode != PortUnavailableExitCode) throw;
            return (options with { Port = 0 },
                new(options.Port, error.SocketErrorCode == SocketError.AddressAlreadyInUse ? "occupied" : "refused"));
        }
    }

    /// <summary>
    /// 不带凭据访问占用者的健康接口：Moongate 宿主对未认证请求固定回 401 与 <c>auth.required</c> 问题详情。
    /// 回了其他 HTTP 响应、回的不是 HTTP、或回环地址上拒绝连接（Moongate 宿主监听回环地址，总会接受连接）的是其他程序；
    /// 超时、连接被对方直接关闭等无法判定时为无法辨认。
    /// </summary>
    public static async Task<Occupant> IdentifyOccupantAsync(int port, CancellationToken cancellationToken)
    {
        if (port <= 0) return Occupant.Unknown;
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 64 * 1024
            };
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}{ApiContract.Health.Path}"), cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return Occupant.Other;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.String && code.GetString() == ApiErrors.AuthRequired
                ? Occupant.Moongate : Occupant.Other;
        }
        catch (JsonException) { return Occupant.Other; }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.InvalidResponse ||
                                                 Find<SocketException>(error) is { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return Occupant.Other;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException) { return Occupant.Unknown; }
    }

    private static bool IsAddressInUse(Exception error) =>
        Find<AddressInUseException>(error) is not null ||
        Find<SocketException>(error) is { SocketErrorCode: SocketError.AddressAlreadyInUse };

    // 存储类：权限拒绝、路径不存在，以及由文件系统接口或本项目存储与发现代码抛出的 IOException。
    // 其他来源的 IOException（例如网络栈）不归为数据目录问题。
    private static bool IsStorage(Exception error) => error switch
    {
        UnauthorizedAccessException => true,
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException => true,
        IOException io => io.TargetSite?.DeclaringType?.Namespace is { } ns
            ? ns.StartsWith("System.IO", StringComparison.Ordinal) || ns.StartsWith("MabiTool", StringComparison.Ordinal)
            : io.TargetSite?.DeclaringType is not null,
        _ => false
    };

    private static T? Find<T>(Exception? error) where T : Exception
    {
        for (; error is not null; error = error.InnerException)
            if (error is T found) return found;
        return null;
    }
}
