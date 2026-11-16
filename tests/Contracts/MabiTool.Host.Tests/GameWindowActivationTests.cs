using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class GameWindowActivationTests
{
    [Fact]
    public async Task ReportsTheReadBackWindowStateWithoutSendingInput()
    {
        var area = await Area.Create();
        area.Source.Result = new(true, true, false);
        var receipt = area.Service.Activate("owner");
        Assert.True(receipt.Attempted); Assert.True(receipt.Restored); Assert.False(receipt.Foreground);
        Assert.Equal(area.Bindings.Current!.Session.Id, receipt.GameSessionId);
        Assert.Equal(1, area.Source.Calls);
        Assert.Equal(0, area.Input.HeldControls);
        Assert.Empty(area.Leases.Snapshot());
    }

    [Fact]
    public async Task RejectsWhileAnotherOperationHoldsForegroundInputAndKeepsItsLease()
    {
        var area = await Area.Create();
        var binding = area.Bindings.Current!;
        var held = area.Leases.Acquire(new("color", "op-color", binding.Session.Id, binding.Session.BindingRevision,
            ["foreground-input", "activity-control:" + binding.Session.Id], TimeSpan.FromSeconds(30)));
        var error = Assert.Throws<OperationException>(() => area.Service.Activate("owner"));
        Assert.Equal("resource.busy", error.Code);
        Assert.Equal(0, area.Source.Calls);
        Assert.True(area.Leases.Validate(held.Id, "foreground-input", held.FencingToken, held.GameSessionId, held.BindingRevision, "color"));
    }

    [Fact]
    public async Task RejectsWhenTheBindingIsGone()
    {
        var area = await Area.Create();
        area.Source.Gone = true;
        Assert.Equal("input.bindingChanged", Assert.Throws<OperationException>(() => area.Service.Activate("owner")).Code);
        Assert.Equal(0, area.Source.Calls);
    }

    private sealed class Area
    {
        public Source Source { get; } = new();
        public GameSessionBindingTracker Bindings { get; } = new();
        public ResourceLeaseCoordinator Leases { get; } = new();
        public GameInputBroker Input { get; }
        public GameBindingService Service { get; }
        private Area()
        {
            Input = new(Leases, Bindings, new Device());
            var info = new SystemInfo("MabiTool", "0.1.0", "1.0", "activation-test", DateTimeOffset.UtcNow, ".NET", "test");
            Service = new(new GameControlContext(Bindings, Leases, Input, false), Source, info, TimeProvider.System);
        }
        public static async Task<Area> Create()
        {
            var area = new Area();
            var candidate = Assert.Single(area.Service.Enumerate("owner").Items);
            await area.Service.SelectAsync("owner", new(candidate.Id), "activation-bind-key",
                area.Service.ETag(area.Service.Snapshot()), default);
            area.Source.Calls = 0;
            return area;
        }
    }

    private sealed class Source : IGameWindowSource
    {
        private readonly GameWindowObservation window = new("TW", 100, new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero),
            new(500), "fixture-build", new(0, 0, 800, 600), 96, 96, true);
        public GameWindowActivation Result { get; set; } = new(true, true, true);
        public bool Gone { get; set; }
        public int Calls { get; set; }
        public IReadOnlyList<SelectableGameWindow> Enumerate() => [new("测试游戏", "MabinogiMobile", window)];
        public GameWindowObservation? Refresh(GameWindowObservation selected) => Gone ? null : window;
        public bool TryActivate(GameWindowObservation selected) => true;
        public GameWindowActivation Activate(GameWindowObservation selected) { Calls++; return Result; }
    }

    private sealed class Device : IGameInputDevice
    {
        public UInt64String? ForegroundWindowIdentity => new(500);
        public void Press(InputControl control) { }
        public void Release(InputControl control) { }
        public void MovePointer(PhysicalScreenPoint point) { }
        public void ReleaseAll() { }
    }
}
