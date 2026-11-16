using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MabiTool.Host;

public sealed class LocalDiscoveryService(LocalApiOptions options, SystemInfo info, LocalAuthService auth, IServer server)
    : IHostedLifecycleService, IDisposable
{
    private const int PipeListeners = 4;
    private readonly CancellationTokenSource stop = new();
    private readonly string pipeName = "mabitool-" + info.InstanceId;
    private readonly string directory = Path.GetFullPath(options.RuntimeDirectory!);
    private string? apiBaseUri;
    private FileStream? instanceLock;
    private Task[] listeners = [];
    private bool published;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本机发现服务当前支持 Windows");
        try { LocalDiscoverySecurity.EnsureDirectory(directory); }
        catch (UnauthorizedAccessException error)
        {
            throw new MabiTool.Infrastructure.StorageException("storage.permissionDenied",
                ToolRoot.NotWritableMessage(Path.GetDirectoryName(directory) ?? directory), error);
        }
        var lockPath = Path.Combine(directory, "host.lock");
        LocalDiscoverySecurity.RejectLinks(lockPath);
        instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { LocalDiscoverySecurity.ValidateFile(lockPath); }
        catch { instanceLock.Dispose(); instanceLock = null; throw; }
        return Task.CompletedTask;
    }

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(directory, info.InstanceId + ".tmp");
        var target = Path.Combine(directory, "instance.json");
        try
        {
            apiBaseUri = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + "/";
            var startedListeners = new List<Task>();
            try
            {
                for (var i = 0; i < PipeListeners; i++) startedListeners.Add(AcceptLoop(NewPipe()));
            }
            finally { listeners = startedListeners.ToArray(); }
            using var process = Process.GetCurrentProcess();
            var discovery = new LocalDiscoveryInfo(LocalIpcProtocol.Version, info.InstanceId, apiBaseUri, pipeName,
                process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), DateTimeOffset.UtcNow);
            if (OperatingSystem.IsWindows()) LocalDiscoverySecurity.RejectLinks(target);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, discovery, ContractJson.Options, cancellationToken);
                file.Flush(true);
            }
            await PublishAsync(temporary, target, cancellationToken);
            published = true;
        }
        catch { await ReleaseAsync(); throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task PublishAsync(string temporary, string target, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalDiscoverySecurity.EnsureDirectory(Path.GetDirectoryName(target)!);
            LocalDiscoverySecurity.ValidateFile(temporary);
            LocalDiscoverySecurity.RejectLinks(target);
            if (File.Exists(target)) LocalDiscoverySecurity.ValidateFile(target);
            try { File.Move(temporary, target, overwrite: true); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 4 &&
                (error.HResult & 0xffff) is 5 or 32 or 33 && File.Exists(temporary))
            {
                await delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)), cancellationToken);
            }
        }
    }

    private NamedPipeServerStream NewPipe() => new(pipeName, PipeDirection.InOut, PipeListeners,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);

    private async Task AcceptLoop(NamedPipeServerStream initial)
    {
        var pipe = initial;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await using (pipe)
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        var request = await LocalIpcProtocol.ReadAsync<BootstrapRequest>(pipe, deadline.Token);
                        if (request.ProtocolVersion == LocalIpcProtocol.Version && !string.IsNullOrWhiteSpace(request.ClientName) &&
                            request.ClientName.Length <= 80 && !request.ClientName.Any(char.IsControl))
                            await LocalIpcProtocol.WriteAsync(pipe, new BootstrapResponse(LocalIpcProtocol.Version, info.InstanceId, apiBaseUri!, auth.OwnerGrant()), deadline.Token);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
                }
                if (!stop.IsCancellationRequested) pipe = NewPipe();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await pipe.DisposeAsync(); }
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (published)
        {
            File.Delete(Path.Combine(directory, "instance.json"));
            published = false;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await ReleaseAsync();

    private async Task ReleaseAsync()
    {
        await stop.CancelAsync();
        try { await Task.WhenAll(listeners); }
        finally { listeners = []; instanceLock?.Dispose(); instanceLock = null; }
    }

    public void Dispose()
    {
        ReleaseAsync().GetAwaiter().GetResult();
        stop.Dispose();
    }
}
