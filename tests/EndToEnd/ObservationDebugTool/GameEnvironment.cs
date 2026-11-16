using System.Diagnostics;
using System.Net;
using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MabiTool.ObservationDebugTool;

public sealed record BoundGame(GameSession Session, UInt64String BindingRevision);

/// <summary>
/// 场景与真实游戏之间的唯一边界。正式实现绑定用户指定客户端目录下的唯一游戏窗口与 loopback 网卡；
/// 离线测试用合成绑定和 fixture Worker 替换，场景代码保持不变。
/// </summary>
public interface IGameEnvironment
{
    string CaptureProfile { get; }
    ObservationCombatProfile CombatProfile { get; }
    /// <param name="memoryTraceDirectory">观察 Worker 的内存轨迹目录（见 WorkerMemoryTrace），只经该 Worker 的启动环境传入。</param>
    WebApplication BuildHost(string ownerToken, string assetRoot, StorageOptions? storage, string? memoryTraceDirectory = null);
    Task<BoundGame> BindAsync(WebApplication app, string owner, CancellationToken ct);
    Task<string> SelectChannelAsync(GameObservationService observations, string owner, CancellationToken ct);
    IReadOnlyDictionary<string, object?> Describe();
    IRawSampleCapture? Samples { get; }
}

public sealed class NativeGameEnvironment(string clientRoot, string workerExecutable, string? channelId, IRawSampleCapture? samples) : IGameEnvironment
{
    public string CaptureProfile => Tw20261110CombatProtocol.CaptureProfile;
    public ObservationCombatProfile CombatProfile => Tw20261110CombatProtocol.CombatProfile;
    public IRawSampleCapture? Samples => samples;

    public WebApplication BuildHost(string ownerToken, string assetRoot, StorageOptions? storage, string? memoryTraceDirectory = null) =>
        BackendApplication.Build(new(0, ownerToken, new HashSet<string>()), storage: storage,
            observationOptions: _ => new WorkerProcessOptions(workerExecutable, Path.GetDirectoryName(workerExecutable)!, assetRoot, "game-observation")
            { EnvironmentVariables = WorkerMemoryTraceFiles.Environment(memoryTraceDirectory) });

    public async Task<BoundGame> BindAsync(WebApplication app, string owner, CancellationToken ct)
    {
        var bindings = app.Services.GetRequiredService<GameBindingService>();
        var candidates = bindings.Enumerate(owner).Items.Where(candidate =>
        {
            try
            {
                using var game = Process.GetProcessById(candidate.ProcessId);
                return !game.HasExited && game.StartTime.ToUniversalTime() == candidate.ProcessStartTime.UtcDateTime &&
                    string.Equals(Path.GetDirectoryName(game.MainModule?.FileName), clientRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { return false; }
        }).ToArray();
        if (candidates.Length != 1) throw new ProbeException("probe.gameSelectionAmbiguous");
        var binding = await bindings.SelectAsync(owner, new(candidates[0].Id), Guid.NewGuid().ToString("N"), bindings.ETag(bindings.Snapshot()), ct);
        if (binding.Binding?.Session.GameBuild != Tw20261110CombatProtocol.GameBuild) throw new ProbeException("probe.gameBuildMismatch");
        return new(binding.Binding.Session, binding.Revision);
    }

    public async Task<string> SelectChannelAsync(GameObservationService observations, string owner, CancellationToken ct)
    {
        var channels = (await observations.ChannelsAsync(owner, ct)).Items.Where(channel =>
            channel.Description.Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
            channel.Addresses.Any(address => IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip))).ToArray();
        if (channelId is not null) channels = channels.Where(value => value.Id == channelId).ToArray();
        if (channels.Length != 1) throw new ProbeException("probe.loopbackSelectionAmbiguous");
        return channels[0].Id;
    }

    public IReadOnlyDictionary<string, object?> Describe() => new Dictionary<string, object?>
    {
        ["mode"] = "native",
        ["profile"] = Tw20261110CombatProtocol.Profile,
        ["gameBuild"] = Tw20261110CombatProtocol.GameBuild,
        // 绑定只接受三文件指纹完全一致的客户端；不一致时场景以 probe.gameBuildMismatch 结束。
        ["clientFingerprint"] = new Dictionary<string, string>
        {
            ["AssemblySha256"] = Tw20261110CombatProtocol.AssemblySha256,
            ["MetadataSha256"] = Tw20261110CombatProtocol.MetadataSha256,
            ["UnitySha256"] = TwClientFingerprint.UnitySha256,
        },
        ["clientFingerprintRequiredAtBinding"] = true,
        ["os"] = Environment.OSVersion.VersionString,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["toolVersion"] = typeof(NativeGameEnvironment).Assembly.GetName().Version?.ToString(),
        ["rawSampleCapture"] = samples?.Unavailable ?? (samples is null ? "sample.pythonNotConfigured" : "available"),
    };
}
