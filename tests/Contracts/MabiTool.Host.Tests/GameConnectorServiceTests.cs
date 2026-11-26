using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Host.GameCli;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class GameConnectorServiceTests
{
    private static readonly byte[] CliBytes = Encoding.UTF8.GetBytes("test command line tool");
    private const string Base = "BOOL\nother_flag : True\n";

    private sealed class FakeProbe : IGameProcessProbe
    {
        public GameProcessInfo Info { get; set; } = new(GameProcessState.NotRunning);
        public GameProcessInfo Probe(string gameDirectory) => Info;
    }

    private sealed class FakeRunner : ITwCliRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, TwCliRun> Respond { get; set; } = _ => new(0, "{\"pipe\":\"connected\"}", "", false, TimeSpan.FromMilliseconds(3));
        /// <summary>为真时调用一直挂起，直到被取消（与命令行进程被杀一致，以中断结果返回）。</summary>
        public volatile bool Hang;
        public ManualResetEventSlim Entered { get; } = new();
        public async Task<TwCliRun> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            lock (Commands) Commands.Add(arguments[0]);
            if (Hang)
            {
                Entered.Set();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { return new(-1, "", "", true, TimeSpan.Zero); }
            }
            return Respond(arguments[0]);
        }
        public string[] Calls { get { lock (Commands) return [.. Commands]; } }
    }

    private sealed class Rig : IDisposable
    {
        private readonly string root;
        public string GameDirectory { get; }
        public string Prefs { get; }
        public FakeProbe Probe { get; } = new();
        public FakeRunner Runner { get; } = new();
        public GameConnectorSettingsStore Settings { get; } = new(null);
        public GameConnectorService Service { get; }
        public Rig(bool withCli = true)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            root = Path.Combine(directory!.FullName, "artifacts", "tmp", "game-connector-tests", Guid.NewGuid().ToString("N"));
            GameDirectory = Path.Combine(root, "game"); Prefs = Path.Combine(root, "prefs");
            Directory.CreateDirectory(GameDirectory); Directory.CreateDirectory(Prefs);
            File.WriteAllText(Path.Combine(GameDirectory, TwCliFile.GameExecutable), "game");
            File.WriteAllText(Path.Combine(Prefs, "BaseMMPlayerPrefs.txt"), Base);
            var cli = Path.Combine(root, "package", TwCliFile.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
            if (withCli) File.WriteAllBytes(cli, CliBytes);
            var environment = new GameConnectorEnvironment(Path.Combine(root, "state"), Prefs, [GameDirectory], Probe, _ => null, Runner,
                () => cli, () => [], null, Convert.ToHexStringLower(SHA256.HashData(CliBytes)));
            var time = TimeProvider.System;
            var controls = MabiTool.Host.Game.GameControlContext.Create(new ResourceLeaseCoordinator(time), time, true);
            Service = new(controls, new("Moongate", "0.1.0", "1.0", "epoch", DateTimeOffset.UtcNow, "test", "test"), Settings, environment, time);
        }
        public void Dispose() { Service.Dispose(); Directory.Delete(root, true); }
        public string Changed => Path.Combine(Prefs, "ChangedMMPlayerPrefs.txt");
    }

    private static string Catalog(params string[] commands) => ScriptedCatalog(commands);
    private static string ScriptedCatalog(string[] commands) =>
        "{\"commands\":[" + string.Join(",", commands.Select(c => "{\"Command\":\"" + c + "\"}")) + "]}";

    private static readonly string[] AllCommands =
        ["get_music_scores", "play_music_score", "get_instruments", "change_instrument", "get_activity", "stop_action"];

    [Fact]
    public async Task EnableNeedsTheRiskConfirmationThenDeploysWritesPreferencesAndRemembersTheConfirmation()
    {
        using var rig = new Rig();
        var first = await rig.Service.RefreshAsync(true);
        Assert.Equal((GameConnectorStatus.Disabled, false, true), (first.Status.KnownValue, first.RiskAcknowledged, first.Actions.CanEnable));
        Assert.Equal(GameConnectorDirectorySource.Default, first.Game.DirectorySource.KnownValue);

        var refused = await Assert.ThrowsAsync<OperationException>(() => rig.Service.CommandAsync("p", new("enable"), "key-000001"));
        Assert.Equal("gameCli.riskNotAcknowledged", refused.Code);
        Assert.False(File.Exists(Path.Combine(rig.GameDirectory, TwCliFile.FileName)));
        Assert.False(File.Exists(rig.Changed));

        var enabled = await rig.Service.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-000002");
        Assert.True(enabled.RiskAcknowledged);
        Assert.Equal((true, true), (enabled.PreferenceEnabled, enabled.Cli.Deployed));
        Assert.Equal(GameConnectorStatus.GameNotRunning, enabled.Status.KnownValue);
        Assert.Contains("+4 : game_use_cli_for_ai : True", File.ReadAllText(rig.Changed));
        Assert.Equal(CliBytes, File.ReadAllBytes(Path.Combine(rig.GameDirectory, TwCliFile.FileName)));
        Assert.Empty(rig.Runner.Calls);

        var disabled = await rig.Service.CommandAsync("p", new("disable"), "key-000003");
        Assert.Equal((GameConnectorStatus.Disabled, false), (disabled.Status.KnownValue, disabled.PreferenceEnabled));
        var again = await rig.Service.CommandAsync("p", new("enable"), "key-000004");
        Assert.True(again.PreferenceEnabled);
        Assert.True(rig.Settings.Read().RiskAcknowledgedAt is not null);
    }

    [Fact]
    public async Task WritesAreBlockedWithAReasonWhileTheGameRunsAndRestoreNeedsABackup()
    {
        using var rig = new Rig();
        rig.Probe.Info = new(GameProcessState.Running, 100, 1);
        var state = await rig.Service.RefreshAsync(true);
        Assert.Equal("gameCli.gameRunning", state.Actions.EnableBlocked);
        Assert.Equal("gameCli.notEnabled", state.Actions.DisableBlocked);
        Assert.Equal("gameCli.noBackup", state.Actions.RestoreBlocked);
        var error = await Assert.ThrowsAsync<OperationException>(() => rig.Service.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-000001"));
        Assert.Equal("gameCli.gameRunning", error.Code);
        Assert.False(File.Exists(rig.Changed));
        Assert.False(File.Exists(Path.Combine(rig.GameDirectory, TwCliFile.FileName)));
        Assert.False(rig.Settings.Read().RiskAcknowledgedAt is not null);
    }

    [Fact]
    public async Task MissingCommandLineToolMakesTheConnectorUnavailableAndBlocksEnabling()
    {
        using var rig = new Rig(withCli: false);
        var state = await rig.Service.RefreshAsync(true);
        Assert.Equal((GameConnectorStatus.Unavailable, "gameCli.cliMissing", "gameCli.cliUnavailable"), (state.Status.KnownValue, state.Reason, state.Actions.EnableBlocked));
        var capability = GameConnectorCapabilities.Snapshots(rig.Service, DateTimeOffset.UtcNow).Single(x => x.Id == "performance.play");
        Assert.Equal((CapabilityStatus.Unavailable, "gameCli.cliMissing"), (capability.Status.KnownValue, capability.ReasonCode));
    }

    [Fact]
    public async Task ConnectedStateAndCapabilitiesFollowStatusAndTheCatalogAndExpiryIsReported()
    {
        using var rig = new Rig();
        await rig.Service.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-000001");
        rig.Probe.Info = new(GameProcessState.Running, 100, 1);
        rig.Runner.Respond = command => command == "capabilities"
            ? new(0, Catalog(AllCommands), "", false, TimeSpan.Zero) : new(0, "{\"pipe\":\"connected\"}", "", false, TimeSpan.Zero);
        var connected = await rig.Service.RefreshAsync(true);
        Assert.Equal((GameConnectorStatus.Connected, "available", true), (connected.Status.KnownValue, connected.Reason, connected.Catalog.Loaded));
        Assert.Equal(AllCommands.Length, connected.Catalog.CommandCount);
        Assert.Equal("gameCli.gameRunning", connected.Actions.EnableBlocked);
        Assert.All(GameConnectorCapabilities.Snapshots(rig.Service, DateTimeOffset.UtcNow), item => Assert.Equal(CapabilityStatus.Available, item.Status.KnownValue));
        Assert.Equal(["status", "capabilities"], rig.Runner.Calls);

        // 同一游戏会话内目录已缓存，状态在间隔内不重复调用命令行。
        await rig.Service.RefreshAsync(false);
        Assert.Equal(["status", "capabilities"], rig.Runner.Calls);

        rig.Runner.Respond = command => command == "capabilities"
            ? new(0, Catalog(AllCommands.Where(x => x != "stop_action").ToArray()), "", false, TimeSpan.Zero) : new(0, "{\"pipe\":\"connected\"}", "", false, TimeSpan.Zero);
        rig.Probe.Info = new(GameProcessState.Running, 200, 2);
        await rig.Service.RefreshAsync(true);
        var snapshots = GameConnectorCapabilities.Snapshots(rig.Service, DateTimeOffset.UtcNow);
        var play = snapshots.Single(x => x.Id == "performance.play");
        Assert.Equal((CapabilityStatus.Unavailable, "gameCli.commandMissing", "stop_action"), (play.Status.KnownValue, play.ReasonCode, Assert.Single(play.Limitations)));
        Assert.Equal(CapabilityStatus.Available, snapshots.Single(x => x.Id == "performance.instrument").Status.KnownValue);
        Assert.Equal(CapabilityStatus.Available, snapshots.Single(x => x.Id == "game.connector").Status.KnownValue);

        rig.Runner.Respond = _ => new(5, "", "{\"reason\":\"option_off\"}", false, TimeSpan.Zero);
        var expired = await rig.Service.RefreshAsync(true);
        Assert.Equal((GameConnectorStatus.Expired, "gameCli.expired"), (expired.Status.KnownValue, expired.Reason));
        Assert.True(expired.Actions.EnableBlocked == "gameCli.gameRunning");
        Assert.All(GameConnectorCapabilities.Snapshots(rig.Service, DateTimeOffset.UtcNow), item =>
            Assert.Equal((CapabilityStatus.Unavailable, "gameCli.expired"), (item.Status.KnownValue, item.ReasonCode)));
        Assert.False(rig.Service.MusicAvailability().Available);

        // 游戏进程刚启动（尚未进入游戏）时同样返回 option_off：3 分钟内为“游戏启动中”，不报已失效。
        rig.Probe.Info = new(GameProcessState.Running, 300, DateTime.UtcNow.AddSeconds(-20).Ticks);
        var starting = await rig.Service.RefreshAsync(true);
        Assert.Equal((GameConnectorStatus.Checking, "gameCli.gameStarting"), (starting.Status.KnownValue, starting.Reason));

        rig.Probe.Info = new(GameProcessState.NotRunning);
        var closed = await rig.Service.RefreshAsync(true);
        Assert.Equal(GameConnectorStatus.GameNotRunning, closed.Status.KnownValue);
        Assert.True(closed.Actions.CanEnable);
    }

    [Fact]
    public async Task EmergencyStopInterruptsAHangingAndAQueuedCommandWithoutWaitingForAnyLock()
    {
        using var rig = new Rig();
        await rig.Service.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-000001");
        rig.Probe.Info = new(GameProcessState.Running, 100, 1);
        rig.Runner.Respond = command => command == "capabilities"
            ? new(0, Catalog(AllCommands), "", false, TimeSpan.Zero) : new(0, "{\"pipe\":\"connected\"}", "", false, TimeSpan.Zero);
        Assert.Equal(GameConnectorStatus.Connected, (await rig.Service.RefreshAsync(true)).Status.KnownValue);

        rig.Runner.Hang = true;
        var hanging = rig.Service.Cli.ExecuteAsync("play_music_score", TwCliBody.Plain("x"));
        Assert.True(rig.Runner.Entered.Wait(TimeSpan.FromSeconds(5)));
        var queued = rig.Service.Cli.ExecuteAsync("stop_action", null);
        // 紧急停止直接中断命令行服务：不经房间、演奏队列或任何控制器的锁。
        rig.Service.EmergencyStop();
        var results = await Task.WhenAll(hanging, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal(TwCliResultKind.Interrupted, result.Kind));
        Assert.Equal(["status", "capabilities", "play_music_score"], rig.Runner.Calls);
        // 中断后通道恢复可用，之后的调用正常执行。
        rig.Runner.Hang = false;
        Assert.True((await rig.Service.Cli.ExecuteAsync("get_activity", null)).IsOk);
        await Task.Delay(700);
    }

    [Fact]
    public async Task DisabledConnectorNeverCallsTheCommandLineAndReportsDisabledCapabilities()
    {
        using var rig = new Rig();
        rig.Probe.Info = new(GameProcessState.Running, 100, 1);
        var state = await rig.Service.RefreshAsync(true);
        Assert.Equal(GameConnectorStatus.Disabled, state.Status.KnownValue);
        Assert.Empty(rig.Runner.Calls);
        var play = GameConnectorCapabilities.Snapshots(rig.Service, DateTimeOffset.UtcNow).Single(x => x.Id == "performance.play");
        Assert.Equal((CapabilityStatus.Unavailable, "gameCli.disabled"), (play.Status.KnownValue, play.ReasonCode));
    }

    [Fact]
    public async Task SameKeyReplaysTheResultWithoutRepeatingTheActionAndADifferentBodyConflicts()
    {
        using var rig = new Rig();
        var first = await rig.Service.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-000001");
        File.Delete(rig.Changed);
        var replay = await rig.Service.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-000001");
        Assert.Equal(first, replay);
        Assert.False(File.Exists(rig.Changed));
        var conflict = await Assert.ThrowsAsync<OperationException>(() => rig.Service.CommandAsync("p", new("disable"), "key-000001"));
        Assert.Equal("idempotency.conflict", conflict.Code);
        var missing = await Assert.ThrowsAsync<OperationException>(() => rig.Service.CommandAsync("p", new("restore"), null));
        Assert.Equal("idempotency.required", missing.Code);
        var invalid = await Assert.ThrowsAsync<OperationException>(() => rig.Service.CommandAsync("p", new("explode"), "key-000009"));
        Assert.Equal("request.invalid", invalid.Code);
    }

    [Fact]
    public async Task RevisionOnlyAdvancesWhenTheContentChanges()
    {
        using var rig = new Rig();
        var one = await rig.Service.RefreshAsync(true);
        var two = await rig.Service.RefreshAsync(true);
        Assert.Equal(one.Revision, two.Revision);
        await rig.Service.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-000001");
        Assert.NotEqual(one.Revision, rig.Service.Current.Revision);
        Assert.Equal("\"game-connector-epoch-" + rig.Service.Current.Revision + "\"", GameConnectorService.ETag(rig.Service.Current));
    }

    [Fact]
    public void HostWithoutAConnectorEnvironmentReportsUnavailableImmediately()
    {
        var time = TimeProvider.System;
        var controls = MabiTool.Host.Game.GameControlContext.Create(new ResourceLeaseCoordinator(time), time, true);
        using var service = new GameConnectorService(controls, new("Moongate", "0.1.0", "1.0", "epoch", DateTimeOffset.UtcNow, "t", "t"),
            new(null), GameConnectorEnvironment.Unsupported("platform.unsupported"), time);
        Assert.Equal((GameConnectorStatus.Unavailable, "platform.unsupported"), (service.Current.Status.KnownValue, service.Current.Reason));
        Assert.All(GameConnectorCapabilities.Snapshots(service, DateTimeOffset.UtcNow), item => Assert.Equal(CapabilityStatus.Unavailable, item.Status.KnownValue));
    }
}
