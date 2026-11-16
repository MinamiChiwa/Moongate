using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Infrastructure;
using MabiTool.Providers.Tunnels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;

if (args.SequenceEqual(["--host-child"]))
{
    var root = Environment.GetEnvironmentVariable("MABITOOL_ROOM_PROBE_ROOT") ?? throw new ArgumentException("缺少测试数据根");
    var token = Environment.GetEnvironmentVariable("MABITOOL_ROOM_PROBE_TOKEN") ?? throw new ArgumentException("缺少测试凭据");
    await using var application = BackendApplication.Build(new(0, token, new HashSet<string>()),
        storage: new(Path.Combine(root, "bootstrap"), Environment.GetEnvironmentVariable("MABITOOL_ROOM_PROBE_INITIALIZE") == "true",
            DataRoot: Path.Combine(root, "data")));
    await application.StartAsync();
    Console.WriteLine(JsonSerializer.Serialize(new ProbeHost(
        application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(),
        application.Services.GetRequiredService<SystemInfo>().InstanceId, Environment.ProcessId), ContractJson.Options));
    await Console.In.ReadLineAsync(); await application.StopAsync(); return;
}
if (args.Length != 3 || args[0] != "--component" || !OperatingSystem.IsWindows())
    throw new ArgumentException("实测用法：--component <已验证 cloudflared 绝对路径> <版本>；需要 Windows");
var repository = FindRoot();
var scratch = Path.Combine(repository, "artifacts", "tmp", "room-server-live", Guid.NewGuid().ToString("N"));
var evidence = Path.Combine(repository, "artifacts", "test-results", "room-server", "live-probe.json");
Directory.CreateDirectory(scratch); Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
var component = new TunnelComponentOptions(Path.GetFullPath(args[1]),
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[1]))), args[2]);
var accessToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
var checks = new List<object>();
var report = new Dictionary<string, object?> { ["schemaVersion"] = 1, ["startedAt"] = DateTimeOffset.UtcNow,
    ["os"] = RuntimeInformation.OSDescription, ["runtime"] = RuntimeInformation.FrameworkDescription,
    ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(), ["componentVersion"] = component.Version,
    ["componentSha256"] = component.Sha256, ["checks"] = checks };
Process? child = null;
try
{
    child = await StartHost(true);
    var firstHost = await ReadHost(child);
    using var sdk = new MabiToolClient(new(firstHost.ApiBaseUri), _ => ValueTask.FromResult(accessToken));
    var (initial, initialTag) = await ReadServer(sdk);
    Ensure(initial.State.KnownValue == RoomServerState.Stopped, "initial-stopped");
    child.Refresh(); report["idleHostWorkingSetBytes"] = child.WorkingSet64;
    var loopback = await sdk.StartRoomServerAsync(new(RoomServerMode.Loopback), "live-loopback-start", initialTag);
    Ensure((await Terminal(sdk, loopback.Id)).State.KnownValue == OperationState.Succeeded, "loopback-start");
    var (local, localTag) = await ReadServer(sdk);
    Ensure(new Uri(local.LocalEndpoint!).Port != new Uri(firstHost.ApiBaseUri).Port, "separate-control-port");
    Ensure(await new RoomTunnelProbe().VerifyAsync(new(local.LocalEndpoint!), local.ServerEpoch!, CancellationToken.None), "loopback-handshake");
    checks.Add(new { name = "loopback", status = "passed", epoch = local.ServerEpoch, endpoint = local.LocalEndpoint });
    var stopLocal = await sdk.StopRoomServerAsync(new(), "live-loopback-stop", localTag);
    Ensure((await Terminal(sdk, stopLocal.Id)).State.KnownValue == OperationState.Succeeded, "loopback-stop");
    Ensure(!await new RoomTunnelProbe().VerifyAsync(new(local.LocalEndpoint!), local.ServerEpoch!, CancellationToken.None), "loopback-port-release");

    var lanStart = await sdk.StartRoomServerAsync(new(RoomServerMode.Lan), "live-lan-start", (await ReadServer(sdk)).ETag);
    Ensure((await Terminal(sdk, lanStart.Id)).State.KnownValue == OperationState.Succeeded, "lan-start");
    var (lan, lanTag) = await ReadServer(sdk);
    Ensure(lan.LanEndpoints.Count != 0, "lan-invitation");
    Ensure(await new RoomTunnelProbe().VerifyAsync(new(lan.LanEndpoints[0]), lan.ServerEpoch!, CancellationToken.None), "lan-handshake");
    using (var remote = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) })
    {
        var target = new UriBuilder(firstHost.ApiBaseUri) { Host = new Uri(lan.LanEndpoints[0]).Host, Path = ApiContract.SystemInfo.Path }.Uri;
        try { using var response = await remote.GetAsync(target); throw new InvalidOperationException("control-api-remote-listener"); }
        catch (HttpRequestException) { }
    }
    checks.Add(new { name = "lan", status = "passed", endpoints = lan.LanEndpoints, controlApiRemoteConnection = "refused" });
    var stopLan = await sdk.StopRoomServerAsync(new(), "live-lan-stop", lanTag);
    Ensure((await Terminal(sdk, stopLan.Id)).State.KnownValue == OperationState.Succeeded, "lan-stop");

    var publicTag = (await ReadServer(sdk)).ETag;
    var grant = await Pair(sdk, "实测建房客户端");
    using var paired = new MabiToolClient(new(firstHost.ApiBaseUri), _ => ValueTask.FromResult(grant.Token));
    var timer = Stopwatch.StartNew();
    var publicStart = await paired.StartRoomServerAsync(new(RoomServerMode.Public), "live-public-start", publicTag);
    var publicOperation = await Terminal(paired, publicStart.Id);
    report["publicStartOperation"] = publicOperation; report["publicServerSnapshot"] = (await ReadServer(paired)).Snapshot;
    if (publicOperation.State.KnownValue != OperationState.Succeeded)
        throw new InvalidOperationException(publicOperation.Problem?.Code ?? "public-start-failed");
    var (network, _) = await ReadServer(sdk); var endpoint = new Uri(network.Tunnel.Endpoint!);
    Ensure(await new RoomTunnelProbe().VerifyAsync(endpoint, network.ServerEpoch!, CancellationToken.None), "public-handshake");
    var unavailablePaths = new Dictionary<string, int>();
    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        foreach (var path in new[] { ApiContract.SystemInfo.Path, ApiContract.AuthSession.Path, ApiContract.FileCapabilities.Path })
        {
            using var response = await http.GetAsync(new UriBuilder(endpoint) { Scheme = "https", Path = path }.Uri,
                HttpCompletionOption.ResponseHeadersRead);
            unavailablePaths[path] = (int)response.StatusCode; Ensure(response.StatusCode == HttpStatusCode.NotFound, "public-control-api-isolation");
        }
    checks.Add(new { name = "public", status = "passed", epoch = network.ServerEpoch,
        endpoint = network.Tunnel.Endpoint, startupMilliseconds = timer.Elapsed.TotalMilliseconds, rejectedPaths = unavailablePaths });
    var owned = OwnedChildren(child.Id).Select(id => Process.GetProcessById(id)).ToArray();
    try
    {
        report["ownedProcessIdentities"] = owned.Select(process => new { id = process.Id, executable = process.MainModule!.FileName }).ToArray();
        var tunnelProcesses = owned.Where(process => string.Equals(Path.GetFullPath(process.MainModule!.FileName),
            component.ExecutablePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        var consolePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "conhost.exe");
        Ensure(tunnelProcesses.Length == 1 && owned.Length <= 2 && owned.All(process => ReferenceEquals(process, tunnelProcesses[0]) ||
            string.Equals(Path.GetFullPath(process.MainModule!.FileName), consolePath, StringComparison.OrdinalIgnoreCase)), "owned-tunnel-process");
        foreach (var process in owned) { _ = process.Handle; process.Refresh(); }
        child.Refresh();
        report["activeHostWorkingSetBytes"] = child.WorkingSet64;
        report["tunnelWorkingSetBytes"] = tunnelProcesses[0].WorkingSet64;
        report["consoleWorkingSetBytes"] = owned.Where(process => !ReferenceEquals(process, tunnelProcesses[0])).Sum(process => process.WorkingSet64);
        report["activeProcessTreeWorkingSetBytes"] = child.WorkingSet64 + owned.Sum(process => process.WorkingSet64);
        child.Kill(entireProcessTree: false); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var process in owned) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(!await new RoomTunnelProbe().VerifyAsync(new(network.LocalEndpoint!), network.ServerEpoch!, CancellationToken.None), "crash-listener-release");
        checks.Add(new { name = "host-termination", status = "passed", tunnelExited = tunnelProcesses[0].HasExited,
            ownedProcessTreeReleased = owned.All(process => process.HasExited), listenerReleased = true });
    }
    finally { foreach (var process in owned) process.Dispose(); }
    child.Dispose(); child = await StartHost(false); var restarted = await ReadHost(child);
    Ensure(restarted.BackendEpoch != firstHost.BackendEpoch, "new-backend-epoch");
    using var recovered = new MabiToolClient(new(restarted.ApiBaseUri), _ => ValueTask.FromResult(accessToken));
    Ensure((await ReadServer(recovered)).Snapshot.State.KnownValue == RoomServerState.Stopped, "restart-stopped");
    using var stale = new MabiToolClient(new(restarted.ApiBaseUri), _ => ValueTask.FromResult(grant.Token));
    try { await stale.GetOperationAsync(publicStart.Id); throw new InvalidOperationException("restart-old-pairing-accepted"); }
    catch (MabiToolApiException error) when (error.Status == 401) { }
    var reconnectedGrant = await Pair(recovered, "实测建房客户端");
    Ensure(reconnectedGrant.Session.ClientId != grant.Session.ClientId, "restart-new-principal");
    using var reconnected = new MabiToolClient(new(restarted.ApiBaseUri), _ => ValueTask.FromResult(reconnectedGrant.Token));
    var historical = await reconnected.StartRoomServerAsync(new(RoomServerMode.Public), "live-public-start", publicTag);
    Ensure(historical.Id == publicStart.Id && historical.State.KnownValue == OperationState.Succeeded, "historical-idempotency");
    var afterRestart = OwnedChildren(child.Id).Select(id => Process.GetProcessById(id)).ToArray();
    try
    {
        Ensure((await ReadServer(recovered)).Snapshot.State.KnownValue == RoomServerState.Stopped &&
            afterRestart.All(process => string.Equals(Path.GetFullPath(process.MainModule!.FileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "conhost.exe"), StringComparison.OrdinalIgnoreCase)), "restart-no-replay");
    }
    finally { foreach (var process in afterRestart) process.Dispose(); }
    checks.Add(new { name = "restart", status = "passed", originalOperation = historical.Id, serverState = "stopped", tunnelCount = 0,
        oldCredentialRejected = true, newPrincipal = reconnectedGrant.Session.ClientId });
    report["status"] = "passed";
}
catch (Exception error)
{
    report["status"] = "failed"; report["failure"] = error.Message; Environment.ExitCode = 1;
}
finally
{
    if (child is not null)
    {
        try
        {
            if (!child.HasExited) { await child.StandardInput.WriteLineAsync("stop"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        }
        catch { if (!child.HasExited) { child.Kill(entireProcessTree: false); await child.WaitForExitAsync(); } }
        child.Dispose();
    }
    report["completedAt"] = DateTimeOffset.UtcNow;
    File.WriteAllText(evidence, JsonSerializer.Serialize(report, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
    var intended = Path.GetFullPath(Path.Combine(repository, "artifacts", "tmp", "room-server-live")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(scratch).StartsWith(intended, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("测试清理路径超出私有目录");
    Directory.Delete(scratch, true);
    Console.WriteLine($"房间服务实测：{report["status"]}；证据：{evidence}");
}

async Task<Process> StartHost(bool initialize)
{
    var dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../..", "dotnet.exe"));
    var options = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    options.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); options.ArgumentList.Add("--host-child");
    options.Environment["MABITOOL_ROOM_PROBE_ROOT"] = scratch; options.Environment["MABITOOL_ROOM_PROBE_TOKEN"] = accessToken;
    options.Environment["MABITOOL_ROOM_PROBE_INITIALIZE"] = initialize ? "true" : "false";
    options.Environment["MABITOOL_CLOUDFLARED_PATH"] = component.ExecutablePath; options.Environment["MABITOOL_CLOUDFLARED_SHA256"] = component.Sha256;
    options.Environment["MABITOOL_CLOUDFLARED_VERSION"] = component.Version;
    options.Environment.Remove("MABITOOL_UPDATE_CONFIGURATION");
    options.Environment.Remove("MABITOOL_COLOR_OPTIONS");
    var process = Process.Start(options) ?? throw new InvalidOperationException("测试宿主启动失败");
    await Task.CompletedTask; return process;
}
static async Task<ProbeHost> ReadHost(Process child)
{
    var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    if (line is null || line.Length > 4096) throw new InvalidOperationException("测试宿主未发布有效地址");
    var value = JsonSerializer.Deserialize<ProbeHost>(line, ContractJson.Options)!;
    Ensure(value.ProcessId == child.Id, "host-process-identity"); return value;
}
static async Task<(RoomServerSnapshot Snapshot, string ETag)> ReadServer(MabiToolClient sdk)
{
    string? tag = null;
    var value = await sdk.GetRoomServerAsync(onResponse: metadata => tag = metadata.ETag);
    return (value, tag ?? throw new InvalidOperationException("建房状态缺少 ETag"));
}
static async Task<OperationSnapshot> Terminal(MabiToolClient sdk, string id)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
    while (true)
    {
        var value = await sdk.GetOperationAsync(id, deadline.Token);
        if (SqliteStore.IsTerminal(value.State.KnownValue)) return value;
        await Task.Delay(50, deadline.Token);
    }
}
static async Task<AccessGrant> Pair(MabiToolClient owner, string name)
{
    var challenge = await owner.CreatePairingChallengeAsync(new(name, null, [ApiPermissions.SystemRead, ApiPermissions.RoomsCoordinate]));
    return await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
}
static void Ensure(bool value, string check) { if (!value) throw new InvalidOperationException(check); }
static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("缺少项目根");
}
static IReadOnlyList<int> OwnedChildren(int parent)
{
    using var snapshot = NativeMethods.CreateToolhelp32Snapshot(2, 0);
    if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    var entry = new NativeMethods.ProcessEntry { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry>(), Executable = "" };
    var result = new List<int>();
    if (NativeMethods.Process32FirstW(snapshot, ref entry))
        do { if (entry.Parent == parent) result.Add(checked((int)entry.Id)); } while (NativeMethods.Process32NextW(snapshot, ref entry));
    return result;
}
sealed record ProbeHost(string ApiBaseUri, string BackendEpoch, int ProcessId);
static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry
    {
        public uint Size, Usage, Id; public nuint Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
