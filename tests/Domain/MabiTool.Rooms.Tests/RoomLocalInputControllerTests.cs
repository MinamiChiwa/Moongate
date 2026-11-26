using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Rooms.Domain;
using Xunit;

namespace MabiTool.Rooms.Tests;

public sealed class RoomLocalInputControllerTests
{
    [Fact]
    public void MusicLeaseCoversStartAndPreparedStopAndFreezesStopBinding()
    {
        var f = new Fixture();
        var capabilities = f.Arm();
        Assert.True(capabilities.CanStart); Assert.True(capabilities.CanStop); Assert.Empty(f.Device.Commands);
        var start = f.Action("start"); var stop = f.Action("stop");
        Assert.True(f.Input.Prepare(start).Ready); Assert.True(f.Input.Prepare(stop).Ready);
        Assert.Equal("sent", f.Input.Execute(start, CancellationToken.None).Status);
        Assert.False(f.Available());
        f.Preferences = f.Preferences with { Revision = new(2), Bindings = new Dictionary<string, ModeInputPreferences>
        { ["ensemble"] = new(new(LocalBindingKind.Key, 65), new(LocalBindingKind.Key, 66), false) } };
        f.Input.KeepAlive();
        Assert.Equal("sent", f.Input.Execute(stop, CancellationToken.None).Status);
        Assert.Equal(["down:Space", "up:Space", "down:Escape", "up:Escape"], f.Device.Commands);
        Assert.True(f.Available());
        Assert.Equal(EffectStatus.InputDispatched, f.Input.Snapshot.EffectStatus.KnownValue);
        Assert.Equal("room.planStale", f.Input.Execute(stop, CancellationToken.None).Detail);
        Assert.Equal(4, f.Device.Commands.Count);
    }

    [Fact]
    public void ChordAndClientClickUseFrozenPhysicalGeometry()
    {
        var f = new Fixture();
        f.Preferences = f.Preferences with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["emote"] = new(new(LocalBindingKind.Key, 65, Control: true, Shift: true), null, false) } };
        f.Arm("emote"); var key = f.Action("start", "emote");
        Assert.True(f.Input.Prepare(key).Ready); f.Input.Execute(key, CancellationToken.None);
        Assert.Equal(["down:273", "down:272", "down:321", "up:321", "up:272", "up:273"], f.Device.Commands);
        f.Preferences = f.Preferences with { Revision = new(1), Bindings = new Dictionary<string, ModeInputPreferences>
        { ["emote"] = new(new(LocalBindingKind.Button, X: 40, Y: 50, ClientWidth: 800, ClientHeight: 600, Dpi: 96), null, false) } };
        f.Device.Commands.Clear(); f.Arm("emote"); var click = f.Action("start", "emote");
        Assert.True(f.Input.Prepare(click).Ready); f.Input.Execute(click, CancellationToken.None);
        Assert.Equal(["move:140,250", "down:MouseLeft", "up:MouseLeft"], f.Device.Commands);
        f.Preferences = f.Preferences with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["emote"] = new(new(LocalBindingKind.Button, X: 40, Y: 50, ClientWidth: 801, ClientHeight: 600, Dpi: 96), null, false) } };
        Assert.Equal("input.bindingGeometryMismatch", Assert.Throws<RoomInputException>(() => f.Arm("emote")).Code);
        Assert.True(f.Available());
    }

    [Fact]
    public void CancelDuringChordReleasesOnlyPressedControls()
    {
        var f = new Fixture();
        f.Preferences = f.Preferences with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["emote"] = new(new(LocalBindingKind.Key, 65, Control: true), null, false) } };
        f.Arm("emote"); var action = f.Action("start", "emote"); f.Input.Prepare(action);
        using var stop = new CancellationTokenSource(); f.Device.AfterPress = stop.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => f.Input.Execute(action, stop.Token));
        Assert.Equal(["down:273", "up:273"], f.Device.Commands);
        Assert.Empty(f.Device.Held); Assert.True(f.Available());
        Assert.Equal(EffectStatus.Unknown, f.Input.Snapshot.EffectStatus.KnownValue);
    }

    [Theory]
    [InlineData("restart", "input.bindingChanged")]
    [InlineData("geometry", "input.bindingChanged")]
    [InlineData("permission", "permission.denied")]
    [InlineData("preferences", "room.planStale")]
    public void InvalidatedPreparationRejectsInputAndReleasesLease(string change, string code)
    {
        var f = new Fixture(); f.Arm(); var action = f.Action("start"); f.Input.Prepare(action);
        if (change is "restart" or "geometry") f.Bindings.Update(new("TW", 100,
            change == "restart" ? f.Time.GetUtcNow().AddSeconds(1) : f.Time.GetUtcNow(), new(500), "test",
            new(change == "geometry" ? 101 : 100, 200, 800, 600), 96, 96, false));
        if (change == "permission") f.Authorized = false;
        if (change == "preferences") f.Preferences = f.Preferences with { Revision = new(3) };
        Assert.Equal(code, f.Input.Execute(action, CancellationToken.None).Detail);
        Assert.Empty(f.Device.Commands); Assert.True(f.Available());
    }

    [Fact]
    public void ExpiredAndOldPreparationCannotAffectNewOwnerOrPreparation()
    {
        var f = new Fixture(); f.Arm(); var oldId = f.Input.Snapshot.Id!; var old = f.Action("start"); f.Input.Prepare(old);
        f.Time.Milliseconds = 31000;
        var next = f.Leases.Acquire(new("color", "color", f.Binding.Session.Id, f.Binding.Session.BindingRevision,
            ["foreground-input", "activity-control:" + f.Binding.Session.Id], TimeSpan.FromSeconds(30)));
        Assert.Equal("resource.leaseExpired", f.Input.Execute(old, CancellationToken.None).Detail);
        Assert.True(f.Leases.Validate(next.Id, "foreground-input", next.FencingToken, next.GameSessionId, next.BindingRevision));
        f.Leases.Release(next.Id); f.RefreshClock(); f.Arm();
        Assert.NotEqual(oldId, f.Input.Snapshot.Id);
        Assert.Equal("room.planStale", Assert.Throws<RoomInputException>(() => f.Input.Release(oldId)).Code);
        Assert.False(f.Available());
    }

    [Fact]
    public void FocusChangeReleasesChordAndInvalidatesRemainingInput()
    {
        var f = new Fixture(); f.Arm(); var action = f.Action("start"); f.Input.Prepare(action);
        f.Device.AfterPress = () => f.Device.ForegroundWindowIdentity = new(999);
        Assert.Equal("input.focusChanged", f.Input.Execute(action, CancellationToken.None).Detail);
        Assert.Empty(f.Device.Held); Assert.True(f.Available());
        Assert.Equal(EffectStatus.Unknown, f.Input.Snapshot.EffectStatus.KnownValue);
    }

    [Fact]
    public void ChatDraftClockAndShortcutArePreparationGates()
    {
        var f = new Fixture();
        f.Preferences = f.Preferences with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["emote"] = new(new(LocalBindingKind.ChatDraft), null, false) } };
        Assert.Equal("room.chatDraftUnconfirmed", Assert.Throws<RoomInputException>(() => f.Arm("emote")).Code);
        f.Arm("emote", true); Assert.Empty(f.Device.Commands);
        var action = f.Action("start", "emote"); f.Input.Prepare(action); f.Input.Execute(action, CancellationToken.None);
        Assert.Equal(["down:Enter", "up:Enter"], f.Device.Commands);
        f.Preferences = f.Preferences with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["emote"] = new(new(LocalBindingKind.Key, 0x78), null, false) } };
        Assert.Equal("input.shortcutConflict", Assert.Throws<RoomInputException>(() => f.Arm("emote")).Code);
        f.Time.Milliseconds = 4000;
        Assert.Equal("room.clockQualityInsufficient", Assert.Throws<RoomInputException>(() => f.Arm("emote")).Code);
        Assert.True(f.Available());
    }

    [Fact]
    public void ManualJoinerKeepsStopLeaseAndOldSessionCancellationCannotReleaseNewRound()
    {
        var f = new Fixture();
        var result = f.Input.Arm(new(true, false, true, true, MusicRole: "joiner"), "ensemble");
        Assert.False(result.CanStart); Assert.True(result.CanStop);
        var stop = f.Action("stop"); Assert.True(f.Input.Prepare(stop).Ready); f.Input.Forget(stop.Id);
        f.Input.WithdrawReadiness(); Assert.False(f.Available());
        f.Input.ReleaseSession(stop.SessionId); Assert.True(f.Available());
        f.Arm(); var next = f.Action("start") with { SessionId = "new-round" }; f.Input.Prepare(next);
        f.Input.ReleaseSession(stop.SessionId); Assert.False(f.Available());
    }

    [Fact]
    public async Task ImmediateStopPreservesPreparedMusicAuthorityUntilStopDispatch()
    {
        var f = new Fixture(); using var scheduler = new ActionScheduler(f.Time, f.Clock, f.Input);
        scheduler.ChangeEpoch("epoch"); f.RefreshClock(); f.Arm();
        var start = f.Action("start"); f.Input.Prepare(start); f.Input.Execute(start, CancellationToken.None);
        Assert.True(scheduler.StopNow("manual-stop", start.SessionId, "epoch", "ensemble"));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!f.Available()) await Task.Delay(10, limit.Token);
        Assert.Equal(["down:Space", "up:Space", "down:Escape", "up:Escape"], f.Device.Commands);
        Assert.Equal(EffectStatus.InputDispatched, f.Input.Snapshot.EffectStatus.KnownValue);
    }

    [Fact]
    public void LateStopIgnoresClockAgeAndRefocusesTheGameBeforeSending()
    {
        // 自动停止在演奏一段时间后才到点：校时样本早已过期、用户也可能切到了别的窗口，停止仍要送达。
        var f = new Fixture(); f.Arm();
        var start = f.Action("start"); f.Input.Prepare(start); f.Input.Execute(start, CancellationToken.None);
        f.Time.Milliseconds = 10_000;
        f.Device.ForegroundWindowIdentity = new(999);
        var stop = f.Action("stop");
        Assert.True(f.Input.Prepare(stop).Ready);
        f.Device.ForegroundWindowIdentity = new(999);
        Assert.Equal("sent", f.Input.Execute(stop, CancellationToken.None).Status);
        Assert.Equal(["down:Space", "up:Space", "down:Escape", "up:Escape"], f.Device.Commands);
        Assert.True(f.Available());
    }

    [Fact]
    public void ImmediateStopThatCannotReachTheGameReleasesSoTheNextRoundCanStart()
    {
        var f = new Fixture(); f.Arm();
        var start = f.Action("start"); f.Input.Prepare(start); f.Input.Execute(start, CancellationToken.None);
        f.CanActivate = false;
        var stopNow = new ScheduledAction("stop-now", "round", "stop", 0, "epoch") { Mode = "ensemble" };
        Assert.Equal("input.focusChanged", f.Input.Prepare(stopNow).Error);
        Assert.Equal("released", f.Input.Snapshot.State);
        Assert.True(f.Available());
        f.CanActivate = true;
        Assert.True(f.Arm().CanStart);
    }

    [Fact]
    public void KeyIsHeldLikeTheOriginalToolBeforeItIsReleased()
    {
        // 原工具 MabiSync GameInput.Execute：按下后 WaitOne(40) 再全部松开。按下在到点时刻，回执的发送时刻取按下时。
        var f = new Fixture(); f.Arm();
        var start = f.Action("start"); f.Input.Prepare(start);
        var outcome = f.Input.Execute(start, CancellationToken.None);
        Assert.Equal(["down:Space", "up:Space"], f.Device.Commands);
        var held = System.Diagnostics.Stopwatch.GetElapsedTime(f.Device.Times[0], f.Device.Times[1]).TotalMilliseconds;
        Assert.True(held >= RoomLocalInputController.KeyHoldMs - 2, held.ToString());
        Assert.Equal(0, outcome.SentLocalMs);
    }

    [Fact]
    public void ToggleStopRequiresFreshMatchingPlaybackAndCanRetryAfterMissingEvidence()
    {
        var f = new Fixture();
        f.Preferences = f.Preferences with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["ensemble"] = new(new(LocalBindingKind.Key, 32), new(LocalBindingKind.Key, 32), true) } };
        f.Arm(); var start = f.Action("start"); f.Input.Prepare(start); f.Input.Execute(start, CancellationToken.None);
        var stop = f.Action("stop"); f.Input.Prepare(stop);
        f.Playback = new(f.Binding.Session.Id, true, ObservationQuality.Confirmed, f.Time.GetUtcNow().AddSeconds(-3));
        Assert.Equal("input.stopEvidenceRequired", f.Input.Execute(stop, CancellationToken.None).Detail);
        Assert.False(f.Available()); Assert.Equal(2, f.Device.Commands.Count);
        f.Playback = f.Playback with { ObservedAt = f.Time.GetUtcNow() };
        var retry = f.Action("stop"); f.Input.Prepare(retry);
        Assert.Equal("sent", f.Input.Execute(retry, CancellationToken.None).Status);
        Assert.True(f.Available()); Assert.Equal(4, f.Device.Commands.Count);
    }

    [Fact]
    public void GameOperationOffSimulatesWithoutBindingLeaseOrInput()
    {
        var f = new Fixture(); f.OperateGame = false;
        Assert.True(f.Input.Snapshot.Simulation);
        Assert.Equal("room.simulationUnavailable", Assert.Throws<RoomInputException>(() => f.Arm()).Code);
        var capabilities = f.Input.Arm(new(true, true, false, false), "ensemble");
        Assert.True(capabilities.Simulation); Assert.True(capabilities.CanStart); Assert.True(capabilities.CanStop);
        var start = f.Action("start"); var stop = f.Action("stop");
        Assert.True(f.Input.Prepare(start).Ready); Assert.True(f.Input.Prepare(stop).Ready);
        var started = f.Input.Execute(start, CancellationToken.None);
        Assert.Equal("sent", started.Status); Assert.True(started.Simulated); Assert.Equal("performing", f.Input.Snapshot.State); Assert.True(f.Available());
        Assert.Equal("sent", f.Input.Execute(stop, CancellationToken.None).Status);
        Assert.Empty(f.Device.Commands); Assert.Equal("released", f.Input.Snapshot.State);
        f.Input.Arm(new(true, true, false, false), "ensemble"); f.Input.WithdrawReadiness();
        Assert.Equal("released", f.Input.Snapshot.State);
    }

    private sealed class FakeMusic : IRoomGameMusic
    {
        public bool Available { get; set; } = true;
        public double StartLeadMs { get; set; } = 120;
        public bool? Playable { get; set; } = true;
        public GameMusicResult StartResult { get; set; } = new(GameMusicOutcome.Accepted);
        public GameMusicResult StopResult { get; set; } = new(GameMusicOutcome.Accepted);
        public List<string> Calls { get; } = [];
        public GameActivityProof? LastProof { get; private set; }
        public ValueTask<GameInstrumentPreparation> PrepareInstrumentAsync(GameActivityProof proof, string? instrumentName, CancellationToken ct) => ValueTask.FromResult(new GameInstrumentPreparation(null));
        public ValueTask<bool?> WarmAsync(string? title, CancellationToken ct) => ValueTask.FromResult(Playable);
        public bool? TryIsPlayable(string title) => Playable;
        public ValueTask<GameMusicResult> StartAsync(GameActivityProof proof, string title, CancellationToken ct) { LastProof = proof; Calls.Add("start:" + title); return ValueTask.FromResult(StartResult); }
        public ValueTask<GameMusicResult> StopAsync(GameActivityProof proof, CancellationToken ct) { LastProof = proof; Calls.Add("stop"); return ValueTask.FromResult(StopResult); }
    }

    [Fact]
    public void GameProvidedScoreStartsAndStopsThroughTheConnectorWithTheRoundLeaseAndNoKeysOrWindowActivation()
    {
        var music = new FakeMusic();
        var f = new Fixture(music);
        f.Input.ScoreTitleSource = () => "Song A";
        f.Arm();
        var activations = f.Activations;
        var start = f.Action("start"); var stop = f.Action("stop");
        Assert.True(f.Input.Prepare(start).Ready); Assert.True(f.Input.Prepare(stop).Ready);
        Assert.Equal(activations, f.Activations);
        Assert.Equal(120, f.Input.ExecutionLeadMs(start));
        Assert.Equal(0, f.Input.ExecutionLeadMs(stop));
        var started = f.Input.Execute(start, CancellationToken.None);
        Assert.Equal(("sent", null), (started.Status, started.Detail));
        Assert.Equal("performing", f.Input.Snapshot.State);
        Assert.Equal(f.Input.ActivityProof, music.LastProof);
        Assert.Equal("sent", f.Input.Execute(stop, CancellationToken.None).Status);
        Assert.Equal(["start:Song A", "stop"], music.Calls);
        Assert.Empty(f.Device.Commands);
        Assert.Equal("released", f.Input.Snapshot.State);
        Assert.True(f.Available());
    }

    [Fact]
    public void UserComposedOrUnknownScoreKeepsKeyStartWhileStopStillUsesTheConnector()
    {
        foreach (bool? playable in new bool?[] { false, null })
        {
            var music = new FakeMusic { Playable = playable };
            var f = new Fixture(music);
            f.Input.ScoreTitleSource = () => "My Own";
            f.Arm();
            var start = f.Action("start"); var stop = f.Action("stop");
            f.Input.Prepare(start); f.Input.Prepare(stop);
            Assert.Equal(0, f.Input.ExecutionLeadMs(start));
            Assert.Equal("sent", f.Input.Execute(start, CancellationToken.None).Status);
            Assert.Equal(["down:Space", "up:Space"], f.Device.Commands);
            Assert.Equal("sent", f.Input.Execute(stop, CancellationToken.None).Status);
            Assert.Equal(["stop"], music.Calls);
            Assert.Equal(2, f.Device.Commands.Count);
        }
    }

    [Fact]
    public void StopFallsBackToTheKeyOnlyWhenTheCommandWasDefinitelyNotSentAndNeverAfterAnUnknownResult()
    {
        var music = new FakeMusic { StopResult = new(GameMusicOutcome.Unavailable, "gameCli.notConnected") };
        var f = new Fixture(music);
        f.Input.ScoreTitleSource = () => "Song A";
        f.Arm();
        var start = f.Action("start"); f.Input.Prepare(start); f.Input.Execute(start, CancellationToken.None);
        var stop = f.Action("stop"); f.Input.Prepare(stop);
        Assert.Equal("sent", f.Input.Execute(stop, CancellationToken.None).Status);
        Assert.Equal(["start:Song A", "stop"], music.Calls);
        Assert.Equal(["down:Escape", "up:Escape"], f.Device.Commands);

        var unknown = new FakeMusic { StopResult = new(GameMusicOutcome.Interrupted, "gameCli.interrupted") };
        var g = new Fixture(unknown);
        g.Input.ScoreTitleSource = () => "Song A";
        g.Arm();
        var begin = g.Action("start"); g.Input.Prepare(begin); g.Input.Execute(begin, CancellationToken.None);
        var halt = g.Action("stop"); g.Input.Prepare(halt);
        var outcome = g.Input.Execute(halt, CancellationToken.None);
        Assert.Equal(("rejected", "gameCli.interrupted"), (outcome.Status, outcome.Detail));
        Assert.Empty(g.Device.Commands);
        Assert.Equal(EffectStatus.Unknown, g.Input.Snapshot.EffectStatus.KnownValue);
    }

    [Fact]
    public void NothingToStopIsTreatedAsStoppedAndARefusedStartIsReportedWithoutPressingKeys()
    {
        var music = new FakeMusic { StopResult = new(GameMusicOutcome.Rejected, "gameCli.invalidState") };
        var f = new Fixture(music);
        f.Input.ScoreTitleSource = () => "Song A";
        f.Arm();
        var start = f.Action("start"); f.Input.Prepare(start); f.Input.Execute(start, CancellationToken.None);
        var stop = f.Action("stop"); f.Input.Prepare(stop);
        Assert.Equal("sent", f.Input.Execute(stop, CancellationToken.None).Status);

        var refusing = new FakeMusic { StartResult = new(GameMusicOutcome.Rejected, "gameCli.notAvailableOnCombat") };
        var g = new Fixture(refusing);
        g.Input.ScoreTitleSource = () => "Song A";
        g.Arm();
        var begin = g.Action("start"); g.Input.Prepare(begin);
        var outcome = g.Input.Execute(begin, CancellationToken.None);
        Assert.Equal(("rejected", "gameCli.notAvailableOnCombat"), (outcome.Status, outcome.Detail));
        Assert.Empty(g.Device.Commands);
        Assert.Equal(["start:Song A"], refusing.Calls);
    }

    [Fact]
    public void ConnectorIsNotUsedForEmoteOrWhenUnavailableAndStopNeedsNoKeyMappingWhenItIs()
    {
        var music = new FakeMusic();
        var f = new Fixture(music);
        f.Input.ScoreTitleSource = () => "Song A";
        f.Preferences = f.Preferences with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["emote"] = new(new(LocalBindingKind.Key, 65), null, false) } };
        f.Arm("emote");
        var emote = f.Action("start", "emote"); f.Input.Prepare(emote);
        Assert.Equal(0, f.Input.ExecutionLeadMs(emote));
        f.Input.Execute(emote, CancellationToken.None);
        Assert.Empty(music.Calls);

        var offline = new FakeMusic { Available = false };
        var g = new Fixture(offline);
        g.Input.ScoreTitleSource = () => "Song A";
        g.Arm();
        var begin = g.Action("start"); g.Input.Prepare(begin); g.Input.Execute(begin, CancellationToken.None);
        Assert.Empty(offline.Calls);
        Assert.Equal(["down:Space", "up:Space"], g.Device.Commands);

        var noStopKey = new Fixture(new FakeMusic());
        noStopKey.Preferences = noStopKey.Preferences with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["ensemble"] = new(new(LocalBindingKind.Key, 32), null, false) } };
        noStopKey.Arm();
        var withoutConnector = new Fixture();
        withoutConnector.Preferences = noStopKey.Preferences;
        Assert.Equal("input.actionMappingUnavailable", Assert.Throws<RoomInputException>(() => withoutConnector.Arm()).Code);
    }

    /// <summary>开始命令一直不返回，直到完成源给出结果或取消令牌取消（取消时与命令行服务一致，以 Interrupted 返回）。</summary>
    private sealed class HangingMusic : IRoomGameMusic
    {
        private readonly TaskCompletionSource<GameMusicResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Available => true;
        public double StartLeadMs => 0;
        public ManualResetEventSlim Entered { get; } = new();
        public void Complete(GameMusicResult result) => release.TrySetResult(result);
        public ValueTask<GameInstrumentPreparation> PrepareInstrumentAsync(GameActivityProof proof, string? instrumentName, CancellationToken ct) => ValueTask.FromResult(new GameInstrumentPreparation(null));
        public ValueTask<bool?> WarmAsync(string? title, CancellationToken ct) => ValueTask.FromResult<bool?>(true);
        public bool? TryIsPlayable(string title) => true;
        public async ValueTask<GameMusicResult> StartAsync(GameActivityProof proof, string title, CancellationToken ct)
        {
            Entered.Set();
            try { return await release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { return new(GameMusicOutcome.Interrupted, "gameCli.interrupted"); }
        }
        public ValueTask<GameMusicResult> StopAsync(GameActivityProof proof, CancellationToken ct) => ValueTask.FromResult(new GameMusicResult(GameMusicOutcome.Accepted));
    }

    [Fact]
    public async Task HangingConnectorCallKeepsTheControllerResponsiveAndInterruptingItReturnsInterrupted()
    {
        var music = new HangingMusic();
        var f = new Fixture(music);
        f.Input.ScoreTitleSource = () => "Song A";
        f.Arm();
        var start = f.Action("start"); var stop = f.Action("stop");
        Assert.True(f.Input.Prepare(start).Ready); Assert.True(f.Input.Prepare(stop).Ready);
        using var interrupt = new CancellationTokenSource();
        var running = Task.Run(() => f.Input.Execute(start, interrupt.Token));
        Assert.True(music.Entered.Wait(TimeSpan.FromSeconds(5)));

        // 命令行调用挂起期间控制器锁已释放：状态读取、租约证明、续租与后续停止的准备都不被挡住。
        var responsive = Task.Run(() =>
        {
            _ = f.Input.Snapshot; _ = f.Input.ActivityProof; f.Input.KeepAlive();
            return f.Input.Prepare(f.Action("stop")).Ready;
        });
        Assert.True(await responsive.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(running.IsCompleted);

        // 紧急停止中断命令行进程：挂起的调用以 Interrupted 返回，动作按已发出且效果未知处理。
        interrupt.Cancel();
        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("rejected", "gameCli.interrupted"), (outcome.Status, outcome.Detail));
        Assert.Equal(EffectStatus.Unknown, f.Input.Snapshot.EffectStatus.KnownValue);
        Assert.Empty(f.Device.Commands);
    }

    [Fact]
    public async Task ConnectorResultThatArrivesAfterTheLeaseWasReleasedIsLateAndChangesNoState()
    {
        var music = new HangingMusic();
        var f = new Fixture(music);
        f.Input.ScoreTitleSource = () => "Song A";
        f.Arm();
        var start = f.Action("start"); f.Input.Prepare(start);
        var running = Task.Run(() => f.Input.Execute(start, CancellationToken.None));
        Assert.True(music.Entered.Wait(TimeSpan.FromSeconds(5)));
        f.Input.ReleaseAll();
        Assert.Equal("released", f.Input.Snapshot.State);
        var revision = f.Input.Snapshot.Revision;
        music.Complete(new(GameMusicOutcome.Accepted));
        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("rejected", "room.planStale"), (outcome.Status, outcome.Detail));
        Assert.Equal("released", f.Input.Snapshot.State);
        Assert.Equal(revision, f.Input.Snapshot.Revision);
        Assert.True(f.Available());
    }

    private sealed class Fixture
    {
        public bool OperateGame { get; set; } = true;
        public ManualTime Time { get; } = new();
        public GameSessionBindingTracker Bindings { get; } = new();
        public ResourceLeaseCoordinator Leases { get; }
        public Device Device { get; } = new();
        public RoomClock Clock { get; }
        public RoomLocalInputController Input { get; }
        public GameWindowBinding Binding => Bindings.Current!;
        public bool Authorized { get; set; } = true;
        public GamePlaybackEvidence? Playback { get; set; }
        public RoomPreferences Preferences { get; set; } = RoomPreferences.Default with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["ensemble"] = new(new(LocalBindingKind.Key, 32), new(LocalBindingKind.Key, 27), false) } };
        public Fixture(IRoomGameMusic? music = null)
        {
            Bindings.Update(new("TW", 100, Time.GetUtcNow(), new(500), "test", new(100, 200, 800, 600), 96, 96, false));
            Device.ForegroundWindowIdentity = new(500); Leases = new(Time); Clock = new(Time); RefreshClock();
            Input = new("rooms:client", new(Bindings, Leases, new(Leases, Bindings, Device, Time), Activate, () => Playback, Music: music), Time, Clock, () => Preferences, () => Authorized, Time,
                operateGame: () => OperateGame);
        }
        /// <summary>窗口激活能否把游戏切到前台；成功时设备的前台窗口变为游戏窗口。</summary>
        public bool CanActivate { get; set; } = true;
        public int Activations { get; private set; }
        private bool Activate(GameWindowBinding binding)
        {
            Activations++;
            if (CanActivate) Device.ForegroundWindowIdentity = binding.Session.WindowIdentity;
            return CanActivate;
        }
        public void RefreshClock() => Clock.AddSample(Time.NowMs, Time.NowMs, Time.NowMs, Time.NowMs);
        public LocalRoomCapabilities Arm(string mode = "ensemble", bool chat = false) => Input.Arm(new(true, false, false, false, ChatDraftConfirmed: chat), mode);
        public ScheduledAction Action(string kind, string mode = "ensemble") => new(Guid.NewGuid().ToString("N"), "round", kind, Time.NowMs + 1000, "epoch") { Mode = mode };
        public bool Available()
        {
            try { var lease = Leases.Acquire(new("probe", "probe", Binding.Session.Id, Binding.Session.BindingRevision,
                ["foreground-input"], TimeSpan.FromSeconds(5))); Leases.Release(lease.Id); return true; }
            catch (ResourceLeaseException) { return false; }
        }
    }
    private sealed class ManualTime : TimeProvider, IMonotonicClock
    {
        public long Milliseconds { get; set; }
        public double NowMs => Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(Milliseconds);
    }
    private sealed class Device : IGameInputDevice
    {
        public UInt64String? ForegroundWindowIdentity { get; set; }
        public List<string> Commands { get; } = [];
        public List<long> Times { get; } = [];
        public HashSet<InputControl> Held { get; } = [];
        public Action? AfterPress { get; set; }
        public void Press(InputControl control) { Times.Add(System.Diagnostics.Stopwatch.GetTimestamp()); Commands.Add("down:" + control); Held.Add(control); AfterPress?.Invoke(); }
        public void Release(InputControl control) { Times.Add(System.Diagnostics.Stopwatch.GetTimestamp()); Commands.Add("up:" + control); Held.Remove(control); }
        public void MovePointer(PhysicalScreenPoint point) => Commands.Add($"move:{point.X},{point.Y}");
        public void ReleaseAll() { foreach (var control in Held.ToArray()) Release(control); }
    }
}
