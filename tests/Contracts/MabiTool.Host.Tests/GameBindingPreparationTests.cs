using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class GameBindingPreparationTests
{
    [Fact]
    public async Task WaitingForPreparationDoesNotHoldTheCandidateLock()
    {
        var area = new Area();
        var pending = area.Select();
        await area.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var candidates = await Task.Run(() => area.Service.Enumerate("owner")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(candidates.Items); Assert.False(pending.IsCompleted);
        }
        finally { area.Source.Complete(area.Source.Current); }
        var bound = await pending;
        Assert.Equal("bound", bound.State);
        Assert.Equal(area.Source.Current.GameBuild, bound.Binding!.Session.GameBuild);
    }

    [Fact]
    public async Task ARevisionChangeWhilePreparingRejectsTheOldRequest()
    {
        var area = new Area(); var pending = area.Select();
        await area.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = area.Bindings.Update(area.Source.Current with { WindowIdentity = new(900) });
        area.Source.Complete(area.Source.Current);
        var error = await Assert.ThrowsAsync<OperationException>(() => pending);
        Assert.Equal("revision.stale", error.Code);
        Assert.Equal(other.Session.Id, area.Bindings.Current!.Session.Id);
        Assert.Equal(900UL, area.Bindings.Current.Session.WindowIdentity.Value);
    }

    [Fact]
    public async Task CompletedIdempotentRetryReturnsTheReceiptWithoutPreparingAgain()
    {
        var area = new Area(); var pending = area.Select();
        await area.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        area.Source.Complete(area.Source.Current);
        var first = await pending;
        var replay = await area.Select();
        Assert.Equal(first, replay); Assert.Equal(1, area.Source.PreparationCalls);
    }

    [Fact]
    public async Task CancelledPreparationLeavesTheGameUnbound()
    {
        var area = new Area(); using var cancel = new CancellationTokenSource();
        var pending = area.Select(cancel.Token);
        await area.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(area.Bindings.Current); Assert.Equal("unbound", area.Service.Snapshot().State);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("restart")]
    [InlineData("window")]
    [InlineData("region")]
    public async Task ChangedIdentityReturnedByPreparationIsRejected(string part)
    {
        var area = new Area(); var pending = area.Select();
        await area.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        area.Source.Complete(Change(area.Source.Current, part));
        var error = await Assert.ThrowsAsync<OperationException>(() => pending);
        Assert.Equal("input.bindingChanged", error.Code); Assert.Null(area.Bindings.Current);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("restart")]
    [InlineData("window")]
    [InlineData("region")]
    public async Task FreshIdentityMustStillMatchThePreparedCandidate(string part)
    {
        var area = new Area(); var pending = area.Select();
        await area.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var prepared = area.Source.Current;
        area.Source.Current = Change(prepared, part);
        area.Source.Complete(prepared);
        var error = await Assert.ThrowsAsync<OperationException>(() => pending);
        Assert.Equal("input.bindingChanged", error.Code); Assert.Null(area.Bindings.Current);
    }

    [Fact]
    public async Task FileEvidenceChangedAfterPreparationRejectsTheBinding()
    {
        var area = new Area(); var pending = area.Select();
        await area.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var prepared = area.Source.Current;
        area.Source.Current = prepared with { GameBuild = null };
        area.Source.Complete(prepared);
        var error = await Assert.ThrowsAsync<OperationException>(() => pending);
        Assert.Equal("input.bindingChanged", error.Code); Assert.Null(area.Bindings.Current);
    }

    private static GameWindowObservation Change(GameWindowObservation value, string part) => part switch
    {
        "process" => value with { ProcessId = value.ProcessId + 1 },
        "restart" => value with { ProcessStartTime = value.ProcessStartTime.AddSeconds(1) },
        "window" => value with { WindowIdentity = new(value.WindowIdentity.Value + 1) },
        "region" => value with { Region = "KR" },
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };

    private sealed class Area
    {
        public WindowSource Source { get; } = new();
        public GameSessionBindingTracker Bindings { get; } = new();
        public GameBindingService Service { get; }
        private readonly GameBindingRequest request;
        private readonly string etag;
        public Area()
        {
            var leases = new ResourceLeaseCoordinator();
            var controls = new GameControlContext(Bindings, leases,
                new GameInputBroker(leases, Bindings, new ReplayGameInputDevice(new(500))), false);
            var info = new SystemInfo("MabiTool", "0.1.0", "1.0", "binding-preparation-test", DateTimeOffset.UtcNow, ".NET", "test");
            Service = new(controls, Source, info, TimeProvider.System);
            request = new(Assert.Single(Service.Enumerate("owner").Items).Id);
            etag = Service.ETag(Service.Snapshot());
        }
        public Task<GameBindingSnapshot> Select(CancellationToken ct = default) =>
            Service.SelectAsync("owner", request, "prepare-game-binding", etag, ct);
    }

    private sealed class WindowSource : IGameWindowSource
    {
        public GameWindowObservation Current { get; set; } = new("TW", 100,
            new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), new(500), "fixture-current-build", new(0, 0, 800, 600), 96, 96, false);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<GameWindowObservation?> prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PreparationCalls { get; private set; }
        public IReadOnlyList<SelectableGameWindow> Enumerate() => [new("测试游戏", "MabinogiMobile", Current)];
        public GameWindowObservation? Refresh(GameWindowObservation selected) => Current;
        public bool TryActivate(GameWindowObservation selected) => true;
        public async ValueTask<GameWindowObservation?> PrepareAsync(GameWindowObservation selected, CancellationToken ct)
        {
            PreparationCalls++; Entered.TrySetResult();
            return await prepared.Task.WaitAsync(ct);
        }
        public void Complete(GameWindowObservation? value) => prepared.TrySetResult(value);
    }
}
