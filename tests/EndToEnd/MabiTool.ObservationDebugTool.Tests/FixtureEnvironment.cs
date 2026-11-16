using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MabiTool.ObservationDebugTool.Tests;

/// <summary>合成游戏绑定 + fixture 观察 Worker；场景代码与正式环境相同，不接触真实游戏或网卡。</summary>
public sealed class FixtureEnvironment : IGameEnvironment, IDisposable
{
    private readonly string executable;
    private readonly string mode;
    public FixtureEnvironment(string project, string mode, string gameBuild, string captureProfile, ObservationCombatProfile combatProfile)
    {
        executable = BuildExecutable(project); this.mode = mode; CaptureProfile = captureProfile; CombatProfile = combatProfile;
        Directory.CreateDirectory(Root);
        Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), gameBuild, new(0, 0, 800, 600), 96, 96, false));
    }

    public static FixtureEnvironment CurrentCombat(string mode = "current-combat-stream") => new("MabiTool.ObservationProbe", mode,
        Tw20261110CombatProtocol.GameBuild, Tw20261110CombatProtocol.CaptureProfile, Tw20261110CombatProtocol.CombatProfile);

    /// <summary>当前协议真实共用流 + 完整库存登录体（mode=current-combat-inventory）。</summary>
    public static FixtureEnvironment CurrentCombatInventory() => CurrentCombat("current-combat-inventory");

    public static readonly ObservationSource InventorySource = new("mabiagent-inventory", "1.0.0", "TW", TwClientFingerprint.GameBuild);
    public static FixtureEnvironment SharedInventory(string mode = "normal") => new("MabiTool.InventoryObservationProbe", mode,
        TwClientFingerprint.GameBuild, "tw-music-v1", new("fixture-combat", InventorySource, Simulation: true));

    public string Root { get; } = Path.Combine(RepositoryRoot(), "artifacts", "test-data", "debugtool-" + Guid.NewGuid().ToString("N"));
    public string Reports => Path.Combine(Root, "reports");
    public string Scratch => Path.Combine(Root, "scratch");
    public GameSessionBindingTracker Bindings { get; } = new();
    public string CaptureProfile { get; }
    public ObservationCombatProfile CombatProfile { get; }
    public IRawSampleCapture? RawSamples { get; set; }
    public IRawSampleCapture? Samples => RawSamples;

    public WebApplication BuildHost(string ownerToken, string assetRoot, StorageOptions? storage, string? memoryTraceDirectory = null) =>
        BackendApplication.Build(new(0, ownerToken, new HashSet<string>()), storage: storage,
            gameControls: sp =>
            {
                var leases = sp.GetRequiredService<ResourceLeaseCoordinator>();
                return new(Bindings, leases, new(leases, Bindings, new NoInput()), false);
            },
            observationOptions: _ => new(executable, Path.GetDirectoryName(executable)!, Root, "game-observation")
            { Arguments = [Root, mode], EnvironmentVariables = WorkerMemoryTraceFiles.Environment(memoryTraceDirectory), HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(5) });

    public Task<BoundGame> BindAsync(WebApplication app, string owner, CancellationToken ct)
    {
        var session = Bindings.Current!.Session;
        return Task.FromResult(new BoundGame(session, session.BindingRevision));
    }

    public Task<string> SelectChannelAsync(GameObservationService observations, string owner, CancellationToken ct) =>
        Task.FromResult(new string('1', 32));

    public IReadOnlyDictionary<string, object?> Describe() => new Dictionary<string, object?> { ["mode"] = "fixture", ["fixture"] = mode };

    public void Dispose()
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(Root); attempt++)
            try { Directory.Delete(Root, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
    }

    public static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MabiTool.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("repository.unavailable");
    }

    private static string BuildExecutable(string project)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var bin = output.Parent?.Parent ?? throw new InvalidOperationException("fixture.outputUnavailable");
        var path = Path.Combine(bin.FullName, project, output.Name, project + ".exe");
        if (!File.Exists(path)) throw new FileNotFoundException("缺少同批构建的观察 fixture", path);
        return path;
    }

    private sealed class NoInput : IGameInputDevice
    {
        public UInt64String? ForegroundWindowIdentity => null;
        public void Press(InputControl control) => throw new GameInputException("input.bindingUnavailable");
        public void Release(InputControl control) { }
        public void MovePointer(PhysicalScreenPoint point) => throw new GameInputException("input.bindingUnavailable");
        public void ReleaseAll() { }
    }
}
