// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Combat;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Modules.CombatObservation.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class SharedInventoryCaptureTests
{
    private static readonly ObservationSource Source = new("mabiagent-inventory", "1.0.0", "TW", TwClientFingerprint.GameBuild);

    [Theory]
    [InlineData("host-old-fingerprint")]
    [InlineData("host-mismatched-fingerprint")]
    public async Task IncompatibleInventoryFingerprintRejectsInventoryAndKeepsMusic(string mode)
    {
        using var area = Area(); await using var app = Build(area, mode);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        var music = await GameObservationApiTests.BeginCapture(client, area, "inventory-fingerprint-music");
        var operation = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-fingerprint");
        await Wait(async () => SqliteStore.IsTerminal((await sdk.GetInventoryObservationAsync(operation.Id)).State.KnownValue));
        var rejected = await sdk.GetInventoryObservationAsync(operation.Id);
        Assert.True(rejected.State.KnownValue == OperationState.Failed, JsonSerializer.Serialize(rejected, ContractJson.Options));
        Assert.Null((await sdk.GetInventorySnapshotAsync()).Data);
        var service = app.Services.GetRequiredService<GameObservationService>();
        await Wait(() => service.Runtime().State == "running");
        Assert.Equal(1, service.Runtime().SubscriberCount); Assert.Equal("running", service.Runtime().State);
        await GameObservationApiTests.EndCapture(client, music.Id); await app.StopAsync();
    }

    [Fact]
    public async Task MusicCombatAndInventoryShareCaptureAndReleaseIndependently()
    {
        using var area = Area(); await using var app = Build(area, "host-late");
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        using var sdk = Sdk(client);
        var music = await GameObservationApiTests.BeginCapture(client, area, "inventory-music");
        var service = app.Services.GetRequiredService<GameObservationService>();
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        await using var combat = await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(auth.OwnerGrant().Session.ClientId,
            "inventory-combat", new(area.Bindings.Current!.Session.Id), CancellationToken.None);
        var operation = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-shared");
        await Current(sdk, operation.Id, service);
        Assert.Equal(3, service.Runtime().SubscriberCount); var pid = service.Runtime().WorkerProcessId;
        var workspace = (await sdk.GetInventorySnapshotAsync()).Data!;
        Assert.Equal(Source, workspace.Source); Assert.Equal(area.Bindings.Current.Session.BindingRevision, workspace.BindingRevision);
        await sdk.StopInventoryObservationAsync(operation.Id);
        await Wait(() => File.Exists(Path.Combine(area.Root, "late-attempt")));
        Assert.False((await sdk.GetInventorySnapshotAsync()).Data!.Current);
        Assert.Equal(2, service.Runtime().SubscriberCount); Assert.Equal(pid, service.Runtime().WorkerProcessId);
        await combat.CloseAsync(); Assert.Equal(1, service.Runtime().SubscriberCount);
        await GameObservationApiTests.EndCapture(client, music.Id);
        await Wait(() => service.Runtime().WorkerProcessId is null);
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*")); Assert.Empty(area.Device.Commands);
        await app.StopAsync();
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
    }

    [Fact]
    public async Task RetiredGenerationCannotOverwriteTheNewCurrentSnapshot()
    {
        using var area = Area(); await using var app = Build(area, "host-old-generation");
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        var music = await GameObservationApiTests.BeginCapture(client, area, "inventory-generation-music");
        var operation = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-generation");
        var service = app.Services.GetRequiredService<GameObservationService>();
        await Current(sdk, operation.Id, service);
        // 窗口几何变化只递增绑定修订：共享捕获、库存观察与当前背包保持，之后的帧照常处理。
        var pid = service.Runtime().WorkerProcessId;
        area.ChangeWindowGeometry();
        await Task.Delay(1000);
        Assert.Equal(pid, service.Runtime().WorkerProcessId);
        Assert.True((await sdk.GetInventorySnapshotAsync()).Data!.Current);
        Assert.False(SqliteStore.IsTerminal((await sdk.GetInventoryObservationAsync(operation.Id)).State.KnownValue));
        await File.WriteAllTextAsync(Path.Combine(area.Root, "inject-fault"), "1");
        await Wait(async () => (await sdk.GetInventorySnapshotAsync()).Data?.Items.FirstOrDefault()?.Item.Quantity?.Value == 2);
        var snapshot = (await sdk.GetInventorySnapshotAsync()).Data!;
        Assert.True(snapshot.Current); Assert.DoesNotContain(snapshot.Items, i => i.Item.Quantity?.Value == 999999);
        await sdk.StopInventoryObservationAsync(operation.Id); await GameObservationApiTests.EndCapture(client, music.Id); await app.StopAsync();
    }

    [Fact]
    public async Task InvalidSourceStopsCaptureAndInvalidatesCurrentInventory()
    {
        using var area = Area(); await using var app = Build(area, "host-source-fault");
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        await GameObservationApiTests.BeginCapture(client, area, "inventory-source-music");
        var operation = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-source");
        await Current(sdk, operation.Id, app.Services.GetRequiredService<GameObservationService>());
        await File.WriteAllTextAsync(Path.Combine(area.Root, "inject-fault"), "1");
        var service = app.Services.GetRequiredService<GameObservationService>();
        await Wait(() => service.Runtime().WorkerProcessId is null);
        await Wait(async () => (await sdk.GetInventorySnapshotAsync()).Data?.Current == false);
        Assert.Equal("worker.frameInvalid", service.Runtime().Reason); Assert.Single(Directory.GetFiles(area.Root, "capture-start-*"));
        await app.StopAsync();
    }

    /// <summary>读模型拒绝单帧时只丢弃该帧：工作区失效，观察任务不结束，下一次完整同步恢复当前。</summary>
    [Fact]
    public async Task RejectedFrameInvalidatesWithoutEndingTheObservation()
    {
        using var area = Area(); await using var app = Build(area, "host-rejected-frame");
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        var music = await GameObservationApiTests.BeginCapture(client, area, "inventory-rejected-music");
        var operation = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-rejected");
        await Current(sdk, operation.Id, app.Services.GetRequiredService<GameObservationService>());
        await File.WriteAllTextAsync(Path.Combine(area.Root, "inject-fault"), "1");
        await Wait(async () => (await sdk.GetInventorySnapshotAsync()).Data?.Current == false);
        Assert.False(SqliteStore.IsTerminal((await sdk.GetInventoryObservationAsync(operation.Id)).State.KnownValue));
        File.Delete(Path.Combine(area.Root, "inject-fault"));
        await Current(sdk, operation.Id, app.Services.GetRequiredService<GameObservationService>());
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*"));
        await sdk.StopInventoryObservationAsync(operation.Id); await GameObservationApiTests.EndCapture(client, music.Id); await app.StopAsync();
    }

    /// <summary>
    /// 常驻抓包运行时，库存作为共享抓包的兴趣方自动同步，不需要开始，也不占用更新活动屏障；
    /// 此时手动开始返回同一个常驻观察，不报 resource.busy。
    /// </summary>
    [Fact]
    public async Task ResidentCaptureSyncsInventoryAutomaticallyAndManualStartJoins()
    {
        using var area = new GameObservationApiTests.Area();
        var current = area.Bindings.Current!;
        area.Bindings.Update(new("TW", current.Session.ProcessId, current.Session.ProcessStartTime, current.Session.WindowIdentity,
            TwClientFingerprint.GameBuild, current.Bounds, current.DpiX, current.DpiY, false));
        // 登录数据（含完整库存）等库存兴趣建立后才发送。
        await File.WriteAllTextAsync(Path.Combine(area.Root, "identity-hold"), "waiting");
        await using var app = area.Build("current-combat-resident-inventory", combatProfile: Tw20261110CombatProtocol.CombatProfile, automation: true,
            storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: area.Root));
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        await Wait(() => InventoryInterest(area));
        File.Delete(Path.Combine(area.Root, "identity-hold"));
        await Wait(async () => (await sdk.GetInventorySnapshotAsync()).Data?.Current == true);
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
        // 名称目录随常驻抓包自动从客户端提取（不需要用户操作）：条目显示原文名，目录没有的类型仍是未知。
        await Wait(async () => (await sdk.GetInventorySnapshotAsync()).Catalog.Entries == 500);
        var named = (await sdk.GetInventorySnapshotAsync()).Data!.Items;
        Assert.Equal(("合成裝備1000", InventoryNameState.ClientCatalog),
            named.Where(i => i.Item.Category == "Equipment" && i.Item.TypeId.Value == 1000).Select(i => (i.Name.RawName, i.Name.State.KnownValue!.Value)).First());
        Assert.All(named.Where(i => i.Item.Category == "Consumable"), i => Assert.Equal(InventoryNameState.Unknown, i.Name.State.KnownValue));
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);

        var joined = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-manual-join");
        Assert.False(SqliteStore.IsTerminal(joined.State.KnownValue));
        Assert.Equal(joined.Id, (await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-manual-again")).Id);
        Assert.True((await sdk.GetInventorySnapshotAsync()).Data!.Current);
        await app.StopAsync();
    }
    private static bool InventoryInterest(GameObservationApiTests.Area area)
    {
        try
        {
            var path = Path.Combine(area.Root, "interests");
            return File.Exists(path) && JsonSerializer.Deserialize<ObservationCaptureInterests>(File.ReadAllText(path), ContractJson.Options)?.Inventory is not null;
        }
        catch (Exception error) when (error is IOException or JsonException) { return false; }
    }

    private static GameObservationApiTests.Area Area()
    {
        var area = new GameObservationApiTests.Area();
        area.Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), Source.GameBuild!, new(0, 0, 800, 600), 96, 96, false));
        return area;
    }
    private static WebApplication Build(GameObservationApiTests.Area area, string mode)
    {
        var executable = GameObservationApiTests.Area.BuildExecutable("MabiTool.InventoryObservationProbe");
        return BackendApplication.Build(new(0, "observation-owner-token-123456789012345678901234567890", new HashSet<string>()),
            storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: Path.Combine(area.Root, "portable")),
            gameControls: sp => { var leases = sp.GetRequiredService<ResourceLeaseCoordinator>(); return new(area.Bindings, leases, new(leases, area.Bindings, area.Device), false); },
            observationOptions: _ => new(executable, Path.GetDirectoryName(executable)!, area.Root, "game-observation")
            { Arguments = [area.Root, mode], HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(2) },
            combatSource: sp => new SharedCaptureCombatSource(sp.GetRequiredService<GameObservationService>(), sp.GetRequiredService<GameControlContext>(), new("fixture-combat", Source, Simulation: true)));
    }
    private sealed class StopApplication(WebApplication app) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(app.StopAsync()); }
    private static MabiToolClient Sdk(HttpClient client) => new(client.BaseAddress!, _ => ValueTask.FromResult(client.DefaultRequestHeaders.Authorization!.Parameter!));
    private static Task Current(MabiToolClient sdk, string operationId, GameObservationService service) => Wait(async () =>
    {
        if ((await sdk.GetInventorySnapshotAsync()).Data?.Current == true) return true;
        var operation = await sdk.GetInventoryObservationAsync(operationId);
        Assert.False(SqliteStore.IsTerminal(operation.State.KnownValue), JsonSerializer.Serialize(new { operation, runtime = service.Runtime() }, ContractJson.Options));
        return false;
    });
    private static Task Wait(Func<bool> condition) => Wait(() => Task.FromResult(condition()));
    private static async Task Wait(Func<Task<bool>> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, timeout.Token); }
}
