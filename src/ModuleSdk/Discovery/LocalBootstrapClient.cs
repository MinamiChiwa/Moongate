using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public static class LocalBootstrapClient
{
    public static async Task<BootstrapResponse> ConnectAsync(string discoveryPath, string clientName,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本机发现握手当前支持 Windows");
        LocalDiscoverySecurity.ValidateFile(discoveryPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 宿主刚启动、机器繁忙或杀毒软件扫描时管道握手可能较慢；只限定一次握手，不影响前端的重连节奏。
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var file = new FileStream(discoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            LocalIpcProtocol.MaximumFrameBytes, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > LocalIpcProtocol.MaximumFrameBytes) throw new InvalidDataException("本机发现文件大小无效");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, deadline.Token);
        var discovery = JsonSerializer.Deserialize<LocalDiscoveryInfo>(bytes, ContractJson.Options) ?? throw new JsonException("本机发现文件为空");
        if (discovery.ProtocolVersion != LocalIpcProtocol.Version ||
            !Regex.IsMatch(discovery.InstanceId, @"\A[a-f0-9]{32}\z") ||
            discovery.PipeName != "mabitool-" + discovery.InstanceId ||
            !Uri.TryCreate(discovery.ApiBaseUri, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.IsDefaultPort ||
            endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
            throw new InvalidDataException("本机发现协议或端点无效");
        try
        {
            using var process = Process.GetProcessById(discovery.ProcessId);
            if (process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != discovery.ProcessStartTime)
                throw new IOException("本机发现对应进程已失效");
        }
        catch (ArgumentException error) { throw new IOException("本机发现对应进程不存在", error); }
        await using var pipe = new NamedPipeClientStream(".", discovery.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await LocalIpcProtocol.WriteAsync(pipe, new BootstrapRequest(LocalIpcProtocol.Version, clientName), deadline.Token);
        var response = await LocalIpcProtocol.ReadAsync<BootstrapResponse>(pipe, deadline.Token);
        if (response.ProtocolVersion != LocalIpcProtocol.Version || response.InstanceId != discovery.InstanceId ||
            response.ApiBaseUri != discovery.ApiBaseUri || !response.Grant.Session.Owner)
            throw new InvalidDataException("本机握手与发现实例不一致");
        return response;
    }
}
