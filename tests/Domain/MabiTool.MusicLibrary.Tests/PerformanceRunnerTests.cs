using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;
using MabiTool.Modules.MusicLibrary.Persistence;
using Xunit;

namespace MabiTool.MusicLibrary.Tests;

[Collection(MusicLibraryTestCollection.Name)]
public sealed class PerformanceRunnerTests
{
    private static readonly PerformanceRunnerOptions Fast = new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80), 3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

    private sealed class FakeMusic : IGameMusicControl
    {
        private readonly object gate = new();
        public List<string> Calls { get; } = [];
        public GameMusicAvailability Availability { get; set; } = new(true, "available");
        public List<GameProvidedScore> Scores { get; } = [];
        public List<GameInstrument> Instruments { get; set; } = [];
        public Func<string, GameMusicResult> Play { get; set; } = _ => new(GameMusicOutcome.Accepted);
        public Func<GameMusicResult> Stop { get; set; } = () => new(GameMusicOutcome.Accepted);
        public Func<int, GamePerformanceActivity?> Activity { get; set; } = _ => Playing(100);
        public int Reads;
        public double RecentCallLatencyMs => 0;
        private void Log(string value) { lock (gate) Calls.Add(value); }
        public string[] Snapshot() { lock (gate) return [.. Calls]; }

        public ValueTask<GameScoreListing> ListScoresAsync(TimeSpan maxAge, CancellationToken ct) =>
            ValueTask.FromResult(new GameScoreListing(Availability.Available, Availability.Available ? null : Availability.Reason, Scores));
        public bool? TryIsGameProvided(string title) => Scores.Any(x => GameMusicNames.Title(x.DisplayTitle) == GameMusicNames.Title(title));
        public ValueTask<IReadOnlyList<GameInstrument>?> ListInstrumentsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<GameInstrument>?>(Instruments);
        public ValueTask<GameMusicResult> ChangeInstrumentAsync(GameActivityProof proof, string name, CancellationToken ct) { Log("change:" + name); return ValueTask.FromResult(new GameMusicResult(GameMusicOutcome.Accepted)); }
        public ValueTask<GameMusicResult> PlayScoreAsync(GameActivityProof proof, string title, CancellationToken ct)
        {
            Log("play:" + title); Interlocked.Exchange(ref Reads, 0);
            return ValueTask.FromResult(Play(title));
        }
        public ValueTask<GameMusicResult> StopAsync(GameActivityProof proof, CancellationToken ct) { Log("stop"); return ValueTask.FromResult(Stop()); }
        public ValueTask<GamePerformanceActivity?> ReadActivityAsync(CancellationToken ct) => ValueTask.FromResult(Activity(Interlocked.Increment(ref Reads)));
    }

    private static GamePerformanceActivity Playing(double remaining) => new(true, "鋼琴", "t", false, 100, 100 - remaining, remaining, 1, false, false);
    private static GamePerformanceActivity Idle() => new(false, "鋼琴", null, false, null, null, null, 0, false, false);

    private sealed class FakeLeases : IPerformanceActivityLeases
    {
        public bool Busy { get; set; }
        public int Acquired, Disposed;
        public bool Valid { get; set; } = true;
        public IPerformanceLease Acquire(string ownerId, string operationId, string gameSessionId)
        {
            if (Busy) throw new OperationException("resource.busy", "占用");
            Interlocked.Increment(ref Acquired);
            return new Lease(this);
        }
        private sealed class Lease(FakeLeases owner) : IPerformanceLease
        {
            public GameActivityProof Proof { get; } = new("lease", new(1), "owner", "game-1", new(1));
            public bool IsValid => owner.Valid;
            public bool Renew() => owner.Valid;
            public void Dispose() => Interlocked.Increment(ref owner.Disposed);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string directory;
        private readonly DataRootLease lease;
        private readonly SqliteStore database;
        public FakeMusic Music { get; } = new();
        public FakeLeases Leases { get; } = new();
        public PerformanceSessionService Sessions { get; }
        public PerformanceRunner Runner { get; }
        public Dictionary<string, string> Titles { get; } = new() { ["a"] = "Song A", ["b"] = "Song B", ["custom"] = "My Own" };
        public Rig(PerformanceRunnerOptions? options = null)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
            directory = Path.Combine(root!.FullName, "artifacts", "tmp", "performance-runner-tests", Guid.NewGuid().ToString("N"));
            lease = DataRootLease.Open(new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
            database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
            Sessions = new(new PerformanceSessionStore(database));
            Music.Scores.AddRange([new("Song A", "inventory", false), new("Song B", "inventory", false)]);
            Runner = new(Sessions, Music, Leases, id => Titles.GetValueOrDefault(id),
                id => id == "piano" ? "鋼琴" : null, options ?? Fast);
        }

        public async Task<PerformanceQueueState> StartAsync(string[] scoreIds, string? instrument = null, PerformanceRepeatMode repeat = PerformanceRepeatMode.Off, double gap = 0)
        {
            var items = scoreIds.Select((id, i) => new PlaylistItem("item-" + i, id, instrument, i.ToString("D4"))).ToArray();
            var created = await Sessions.CreateAsync("owner", new(null, "solo", "game-1", items, false, repeat, gap, 2), "idempotency-" + Guid.NewGuid().ToString("N"));
            return await Sessions.CommandAsync("owner", created.Id, new("arm"), PerformanceSessionService.ETag(created.Generation));
        }

        public async Task<PerformanceQueueState> WaitAsync(string id, Func<PerformanceQueueState, bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                var state = Sessions.Get("owner", id);
                if (done(state)) return state;
                await Task.Delay(10);
            }
            throw new TimeoutException("演奏会话没有到达预期状态：" + Sessions.Get("owner", id).Phase);
        }

        public Task<PerformanceQueueState> CommandAsync(string id, PerformanceSessionCommand command) =>
            Sessions.CommandAsync("owner", id, command, PerformanceSessionService.ETag(Sessions.Get("owner", id).Generation));

        public async ValueTask DisposeAsync()
        {
            Runner.Dispose();
            await database.DisposeAsync();
            lease.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private static bool Terminal(PerformanceQueueState state) => state.Phase is PerformancePhase.Stopped or PerformancePhase.Failed or PerformancePhase.Cancelled;

    [Fact]
    public async Task PlaysEachItemStopsNearTheEndAndMovesOnUntilTheQueueIsExhausted()
    {
        await using var rig = new Rig();
        rig.Music.Activity = reads => reads < 3 ? Playing(30) : Playing(1);   // 剩余不超过提前切换边距（2 秒）时切到下一首
        var session = await rig.StartAsync(["a", "b"]);
        var final = await rig.WaitAsync(session.Id, Terminal);
        Assert.Equal((PerformancePhase.Stopped, PerformanceStopReason.QueueExhausted), (final.Phase, final.StopReason));
        var calls = rig.Music.Snapshot();
        Assert.Equal(["play:Song A", "stop", "play:Song B", "stop"], calls);
        Assert.Equal(1, rig.Leases.Acquired);
        await WaitUntilAsync(() => rig.Leases.Disposed == 1);
    }

    [Fact]
    public async Task PlayCommandIsSentOnceAndAGameRefusalFailsTheItemWithItsReason()
    {
        await using var rig = new Rig();
        rig.Music.Play = _ => new(GameMusicOutcome.Rejected, "gameCli.notAvailableOnCombat");
        var session = await rig.StartAsync(["a"]);
        var final = await rig.WaitAsync(session.Id, Terminal);
        Assert.Equal(PerformancePhase.Failed, final.Phase);
        Assert.Equal("gameCli.notAvailableOnCombat", final.LastError);
        Assert.Equal(["play:Song A"], rig.Music.Snapshot());
    }

    [Fact]
    public async Task ScoreThatTheGameDoesNotProvideFailsWithoutCallingTheGame()
    {
        await using var rig = new Rig();
        var session = await rig.StartAsync(["custom"]);
        var final = await rig.WaitAsync(session.Id, Terminal);
        Assert.Equal((PerformancePhase.Failed, "gameCli.userComposedNotAllowed"), (final.Phase, final.LastError));
        Assert.Empty(rig.Music.Snapshot());
    }

    [Fact]
    public async Task InstrumentIsChangedOnlyWhenNeededAndAMissingInstrumentFailsTheItem()
    {
        await using var rig = new Rig();
        rig.Music.Activity = _ => Playing(1);
        rig.Music.Instruments = [new("小提琴", true, null), new("鋼琴", false, null)];
        var first = await rig.StartAsync(["a"], "piano");
        await rig.WaitAsync(first.Id, Terminal);
        Assert.Equal(["change:鋼琴", "play:Song A", "stop"], rig.Music.Snapshot());

        await using var missing = new Rig();
        missing.Music.Instruments = [new("小提琴", true, null)];
        var second = await missing.StartAsync(["a"], "piano");
        var failed = await missing.WaitAsync(second.Id, Terminal);
        Assert.Equal((PerformancePhase.Failed, "gameCli.instrumentNotOwned"), (failed.Phase, failed.LastError));
        Assert.Empty(missing.Music.Snapshot());

        await using var equipped = new Rig();
        equipped.Music.Activity = _ => Playing(1);
        equipped.Music.Instruments = [new("鋼琴", true, null)];
        var third = await equipped.StartAsync(["a"], "piano");
        await equipped.WaitAsync(third.Id, Terminal);
        Assert.DoesNotContain(equipped.Music.Snapshot(), call => call.StartsWith("change:"));
    }

    [Fact]
    public async Task UserStopStopsTheGameAndAnInvalidStateAnswerCountsAsStopped()
    {
        await using var rig = new Rig();
        rig.Music.Stop = () => new(GameMusicOutcome.Rejected, "gameCli.invalidState");
        var session = await rig.StartAsync(["a", "b"]);
        await rig.WaitAsync(session.Id, state => state.Phase == PerformancePhase.Performing);
        await rig.CommandAsync(session.Id, new("stop", StopReason: PerformanceStopReason.ManualStop));
        var final = await rig.WaitAsync(session.Id, Terminal);
        Assert.Equal((PerformancePhase.Stopped, PerformanceStopReason.ManualStop), (final.Phase, final.StopReason));
        Assert.Equal(["play:Song A", "stop"], rig.Music.Snapshot());
        await WaitUntilAsync(() => rig.Leases.Disposed == 1);
    }

    [Fact]
    public async Task NextStopsThePlayingSongBeforeTheNextOneStarts()
    {
        await using var rig = new Rig();
        var session = await rig.StartAsync(["a", "b"]);
        await rig.WaitAsync(session.Id, state => state.Phase == PerformancePhase.Performing);
        await rig.CommandAsync(session.Id, new("advance", 1));
        await WaitUntilAsync(() => rig.Music.Snapshot().Contains("play:Song B"));
        Assert.Equal(["play:Song A", "stop", "play:Song B"], rig.Music.Snapshot());
    }

    [Fact]
    public async Task StoppingFarBeforeTheEndInTheGameStopsTheQueueInsteadOfAdvancing()
    {
        await using var rig = new Rig();
        rig.Music.Activity = reads => reads < 3 ? Playing(60) : Idle();
        var session = await rig.StartAsync(["a", "b"]);
        var final = await rig.WaitAsync(session.Id, Terminal);
        Assert.Equal((PerformancePhase.Stopped, PerformanceStopReason.GameStopped), (final.Phase, final.StopReason));
        Assert.Equal(["play:Song A"], rig.Music.Snapshot());
    }

    [Fact]
    public async Task RepeatedlyUnreadableStateStopsTheQueueAsObservationLost()
    {
        await using var rig = new Rig();
        rig.Music.Activity = _ => null;
        var session = await rig.StartAsync(["a"]);
        var final = await rig.WaitAsync(session.Id, Terminal);
        Assert.Equal((PerformancePhase.Stopped, PerformanceStopReason.ObservationLost, "gameCli.observationLost"), (final.Phase, final.StopReason, final.LastError));
    }

    [Fact]
    public async Task OccupiedActivityLockOrUnavailableConnectorSendsNoCommandAndStopsTheQueue()
    {
        await using var busy = new Rig();
        busy.Leases.Busy = true;
        var session = await busy.StartAsync(["a"]);
        var final = await busy.WaitAsync(session.Id, Terminal);
        Assert.Equal((PerformanceStopReason.ControlFailure, "resource.busy"), (final.StopReason, final.LastError));
        Assert.Empty(busy.Music.Snapshot());

        await using var offline = new Rig();
        offline.Music.Availability = new(false, "gameCli.expired");
        var second = await offline.StartAsync(["a"]);
        var stopped = await offline.WaitAsync(second.Id, Terminal);
        Assert.Equal((PerformanceStopReason.ControlFailure, "gameCli.expired"), (stopped.StopReason, stopped.LastError));
        Assert.Empty(offline.Music.Snapshot());
    }

    [Fact]
    public async Task EmergencyStopCancelsTheDriverReleasesTheLeaseAndStopsTheSessionWithoutNewGameCommands()
    {
        await using var rig = new Rig();
        var session = await rig.StartAsync(["a", "b"]);
        await rig.WaitAsync(session.Id, state => state.Phase == PerformancePhase.Performing);
        var before = rig.Music.Snapshot();
        rig.Runner.EmergencyStop();
        var final = await rig.WaitAsync(session.Id, Terminal);
        Assert.Equal(PerformancePhase.Stopped, final.Phase);
        Assert.Equal(1, rig.Leases.Disposed);
        await Task.Delay(200);
        Assert.Equal(before, rig.Music.Snapshot());
    }

    [Fact]
    public async Task RecoverCancelsSessionsLeftOverFromAPreviousRunWithoutTouchingTheGame()
    {
        await using var rig = new Rig();
        rig.Runner.Dispose();
        var session = await rig.StartAsync(["a"]);
        Assert.Equal(PerformancePhase.Armed, rig.Sessions.Get("owner", session.Id).Phase);
        using var restarted = new PerformanceRunner(rig.Sessions, rig.Music, rig.Leases, id => rig.Titles.GetValueOrDefault(id), _ => null, Fast);
        await restarted.RecoverAsync();
        var final = rig.Sessions.Get("owner", session.Id);
        Assert.Equal((PerformancePhase.Cancelled, "gameCli.hostRestarted"), (final.Phase, final.LastError));
        Assert.Empty(rig.Music.Snapshot());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition());
    }
}
