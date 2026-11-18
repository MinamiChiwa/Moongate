using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.Host.Game;

/// <summary>
/// 绑定客户端的构建核对。已登记构建各项直接启用；未登记的台服客户端在后台读取客户端文件逐项核对
/// （<see cref="TwBuildVerification"/>），结果按构建保存在内存，并缓存到数据根 <c>cache/game-access/client-builds</c>。
/// 核对进行中或读不到客户端时没有结果，调用方按“正在核对”或不可用处理，不当作通过。
/// </summary>
public sealed class GameBuildAdaptation(IGameWindowSource? source, string? cacheDirectory, DiagnosticTrace? trace = null)
{
    private readonly object gate = new();
    private readonly Dictionary<string, TwBuildVerdict> verdicts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<TwBuildVerdict?>> running = new(StringComparer.Ordinal);
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    /// <summary>读不到客户端的构建与时点（毫秒计时）；60 秒内不重复读取。</summary>
    private readonly Dictionary<string, long> failed = new(StringComparer.Ordinal);
    private const long RetryMs = 60_000;
    /// <summary>核对完成（结果写入）时触发；处理方须不阻塞。</summary>
    public event Action? Changed;

    private readonly Func<GameSession, Task<TwBuildVerdict?>>? evaluate;
    /// <summary>以给定方式取得未登记构建的核对结果（离线测试不读真实客户端）。</summary>
    internal GameBuildAdaptation(Func<GameSession, Task<TwBuildVerdict?>> evaluate, DiagnosticTrace? trace = null) : this((IGameWindowSource?)null, null, trace) =>
        this.evaluate = evaluate;

    /// <summary>不读客户端的实例：只认已登记构建。</summary>
    public static GameBuildAdaptation RegisteredOnly { get; } = new((IGameWindowSource?)null, null);

    /// <summary>绑定构建的核对结果；未登记构建尚未核对完成或不是台服客户端时为空。</summary>
    public TwBuildVerdict? Verdict(GameSession? binding)
    {
        if (binding is null || binding.Region != "TW") return null;
        if (binding.GameBuild == TwClientFingerprint.GameBuild) return Registered;
        if (!TwClientFingerprint.IsAdaptive(binding.GameBuild)) return null;
        lock (gate) return verdicts.GetValueOrDefault(binding.GameBuild!);
    }

    /// <summary>未登记构建的核对正在进行。</summary>
    public bool Verifying(GameSession? binding)
    {
        if (binding?.GameBuild is not { } build || !TwClientFingerprint.IsAdaptive(build)) return false;
        lock (gate) return running.ContainsKey(build);
    }

    /// <summary>绑定的是可核对的台服客户端：已登记构建，或带未登记构建标识。</summary>
    public static bool Recognized(GameSession? binding) => binding is { Region: "TW" } &&
        (binding.GameBuild == TwClientFingerprint.GameBuild || TwClientFingerprint.IsAdaptive(binding.GameBuild));

    /// <summary>需要时在后台开始核对（读不到客户端后 60 秒内不重试）；返回当前结果（可能为空）。</summary>
    public TwBuildVerdict? Ensure(GameSession? binding)
    {
        var verdict = Verdict(binding);
        if (verdict is not null || binding is null || !Recognized(binding)) { Report(binding, verdict); return verdict; }
        lock (gate)
            if (failed.TryGetValue(binding.GameBuild!, out var at) && Environment.TickCount64 - at < RetryMs) return null;
        _ = Start(binding);
        return null;
    }

    /// <summary>等待核对结果；客户端读不到或进程已变化时为空。</summary>
    public async Task<TwBuildVerdict?> EnsureAsync(GameSession binding, CancellationToken ct)
    {
        if (Verdict(binding) is { } known) { Report(binding, known); return known; }
        if (!Recognized(binding)) return null;
        return await Start(binding).WaitAsync(ct).ConfigureAwait(false);
    }

    private Task<TwBuildVerdict?> Start(GameSession binding)
    {
        var build = binding.GameBuild!;
        lock (gate)
        {
            if (running.TryGetValue(build, out var existing)) return existing;
            var task = Task.Run(() => EvaluateAsync(binding));
            running[build] = task;
            return task;
        }
    }

    private async Task<TwBuildVerdict?> EvaluateAsync(GameSession binding)
    {
        var build = binding.GameBuild!;
        TwBuildVerdict? verdict = null;
        try
        {
            if (evaluate is not null) verdict = await evaluate(binding).ConfigureAwait(false);
            var client = evaluate is not null ? null : source?.PreparedClient(Observation(binding));
            if (client is not null && Hashes(client) is { } hashes &&
                TwClientFingerprint.AdaptiveBuild(hashes.AssemblySha256, hashes.MetadataSha256) == build)
            {
                verdict = await TwBuildVerification.EvaluateAsync(client.ImagePath, hashes, cacheDirectory, CancellationToken.None).ConfigureAwait(false);
                // 读取客户端事实整读两个大文件（进入大对象堆），结束后压缩回收一次，宿主常驻时不长期占用。
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { verdict = null; }
        lock (gate)
        {
            running.Remove(build);
            if (verdict is null) { if (failed.Count >= 16) failed.Remove(failed.Keys.First()); failed[build] = Environment.TickCount64; }
            else if (verdict.Build == build)
            {
                failed.Remove(build);
                if (verdicts.Count >= 16) verdicts.Remove(verdicts.Keys.First());
                verdicts[build] = verdict;
            }
        }
        Report(binding, verdict);
        Changed?.Invoke();
        return verdict;
    }

    private static TwClientHashes? Hashes(GameClientFiles client) =>
        client.Sha256.TryGetValue(TwClientFingerprint.AssemblyFile, out var assembly) &&
        client.Sha256.TryGetValue(TwClientFingerprint.MetadataFile, out var metadata) &&
        client.Sha256.TryGetValue(TwClientFingerprint.UnityFile, out var unity) ? new(assembly, metadata, unity) : null;

    private static GameWindowObservation Observation(GameSession binding) => new(binding.Region, binding.ProcessId, binding.ProcessStartTime,
        binding.WindowIdentity, binding.GameBuild, new(0, 0, 1, 1), 96, 96, false);

    /// <summary>每个游戏会话记录一次构建指纹摘要：三文件哈希前 12 位、是否登记及各项核对结果，不含路径。</summary>
    private void Report(GameSession? binding, TwBuildVerdict? verdict)
    {
        if (trace is null || binding is null || binding.Region != "TW") return;
        var pending = verdict is null && TwClientFingerprint.IsAdaptive(binding.GameBuild) && Verifying(binding);
        if (pending) return;
        lock (gate)
        {
            if (!reported.Add(binding.Id)) return;
            if (reported.Count > 64) reported.Remove(reported.First());
        }
        var client = source?.PreparedClient(Observation(binding));
        var hashes = client is null ? null : Hashes(client);
        var registered = hashes is null ? null : TwClientFingerprint.Registered(hashes.AssemblySha256, hashes.MetadataSha256, hashes.UnitySha256);
        static string Prefix(string? value) => value is { Length: >= 12 } ? value[..12] : "none";
        static string State(TwCapabilityCheck? check) => check is null ? "unknown" : check.Enabled ? "enabled" : check.Reason ?? "disabled";
        trace.Emit(DiagnosticEvents.GameBuildChecked, new DiagnosticFields()
            .Value("build", binding.GameBuild == TwClientFingerprint.GameBuild ? "registered" : TwClientFingerprint.IsAdaptive(binding.GameBuild) ? "adaptive" : "unrecognized")
            .Value("registeredName", registered?.Name ?? (binding.GameBuild == TwClientFingerprint.GameBuild ? "unknown" : "none"))
            .Value("assembly", Prefix(hashes?.AssemblySha256)).Value("metadata", Prefix(hashes?.MetadataSha256)).Value("unity", Prefix(hashes?.UnitySha256))
            .Flag("captureUsable", verdict?.CaptureUsable ?? false)
            .Value("identity", State(verdict?.Identity)).Value("music", State(verdict?.Music)).Value("combat", State(verdict?.Combat)));
    }

    private static readonly TwBuildVerdict Registered = TwBuildVerdict.ForRegistered(TwClientFingerprint.RegisteredBuilds[0]) with { RegisteredName = null };
}
