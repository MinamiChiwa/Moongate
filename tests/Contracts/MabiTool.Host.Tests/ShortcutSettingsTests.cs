using MabiTool.Contracts;
using MabiTool.Host.Shortcuts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Platform.Windows;
using System.Runtime.Versioning;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class ShortcutSettingsTests
{
    private static ShortcutSettingsRequest Request(string key, bool control = false) => new([new("emergency-stop", new(key, control, false, false, false))]);

    [Fact]
    public async Task FrontendBindingsKeepEmergencyPriorityAndSurviveRestart()
    {
        await using var area = new Area(); await using var source = new Source();
        var service = new ShortcutSettingsService(area.Store, source);
        var conflict = await service.UpdateAsync("owner", new([
            new("overlay-toggle", new("F9", false, false, false, false)),
            new("overlay-click-through-toggle", new("F7", false, false, false, false))]), "frontend-conflict", ShortcutSettingsStore.ETag(new(0)));
        Assert.Equal("F9", conflict.Registrations.Single(x => x.ActionId == "emergency-stop").Registered!.Key);
        Assert.Equal("shortcut.conflictWithConfiguredAction", conflict.Registrations.Single(x => x.ActionId == "overlay-toggle").FailureCode);
        var saved = await service.UpdateAsync("owner", new([
            new("overlay-toggle", new("F8", false, false, false, false)),
            new("overlay-click-through-toggle", new("F7", false, false, false, false))]), "frontend-register", ShortcutSettingsStore.ETag(conflict.Revision));
        Assert.Equal(3, saved.Bindings.Count);
        Assert.All(saved.Registrations, value => Assert.Equal("registered", value.Status));
        await using var nextSource = new Source();
        var restarted = new ShortcutSettingsService(area.Store, nextSource);
        await restarted.RestoreAsync(default);
        Assert.Equal(saved, await restarted.GetAsync(), SnapshotComparer.Instance);
    }

    [Fact]
    public async Task DefaultCustomCombinationAndRestartKeepModuleAndConfiguration()
    {
        await using var area = new Area(); await using var source = new Source();
        var service = new ShortcutSettingsService(area.Store, source);
        await service.RestoreAsync(default);
        var initial = await service.GetAsync();
        Assert.Equal("core", initial.Actions.Single(value => value.ActionId == "emergency-stop").ModuleId);
        Assert.Equal(2, initial.Actions.Count(value => value.ModuleId == "presentation"));
        Assert.Equal("F9", Assert.Single(initial.Registrations).Registered!.Key);
        var changed = await service.UpdateAsync("owner", Request("k", true), "custom-key-1", ShortcutSettingsStore.ETag(initial.Revision));
        Assert.Equal(1UL, changed.Revision.Value);
        Assert.Equal(new("K", true, false, false, false), Assert.Single(changed.Registrations).Registered);
        await source.StopAsync(default);
        await using var restartedSource = new Source();
        var restarted = new ShortcutSettingsService(area.Store, restartedSource);
        await restarted.RestoreAsync(default);
        Assert.Equal(changed, await restarted.GetAsync(), SnapshotComparer.Instance);
    }

    [Fact]
    public async Task ExtensionCommandsRegisterOnlyWhileAvailableAndKeepTheirBindings()
    {
        await using var area = new Area(); await using var source = new Source();
        const string action = "extension.example.timer.reset";
        string? reason = null;
        var service = new ShortcutSettingsService(area.Store, source)
        {
            ExtensionActions = () => [new(action, "extension.example.timer", new Dictionary<string, string> { ["zh-Hans"] = "计时：清空" }, "extension:example.timer", reason)]
        };
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => service.UpdateAsync("owner",
            new([new(action, new("F8", false, false, false, false))]), "extension-reserved", ShortcutSettingsStore.ETag(new(0))))).Code);

        var saved = await service.UpdateAsync("owner", new([new(action, new("R", true, false, false, false))]), "extension-bind", ShortcutSettingsStore.ETag(new(0)));
        Assert.Equal("registered", saved.Registrations.Single(x => x.ActionId == action).Status);
        Assert.Contains(saved.Actions, x => x.ActionId == action && x.Source == "extension:example.timer");

        // 扩展停用：绑定保留，注册撤下并写明原因；重新可用后按原绑定恢复。
        reason = "extension.disabled";
        await service.RefreshAsync();
        var disabled = await service.GetAsync();
        Assert.Contains(disabled.Bindings, x => x.ActionId == action);
        Assert.Equal(("unavailable", "extension.disabled"), (disabled.Registrations.Single(x => x.ActionId == action).Status,
            disabled.Registrations.Single(x => x.ActionId == action).FailureCode));
        Assert.DoesNotContain(source.GetRegistrations(), x => x.ActionId == action);
        reason = null;
        await service.RefreshAsync();
        Assert.Equal("registered", (await service.GetAsync()).Registrations.Single(x => x.ActionId == action).Status);
    }

    [Fact]
    public async Task OccupiedCustomCombinationReturnsActualFallback()
    {
        await using var area = new Area(); await using var source = new Source();
        source.Registrar.Occupied.Add(new(ShortcutKey.K, Control: true));
        var service = new ShortcutSettingsService(area.Store, source);
        var value = await service.UpdateAsync("owner", Request("K", true), "conflict-key-1", ShortcutSettingsStore.ETag(new(0)));
        var registration = Assert.Single(value.Registrations);
        Assert.Equal("K", registration.Configured.Key); Assert.Equal("F9", registration.Registered!.Key);
        Assert.True(registration.UsedFallback); Assert.Equal("registered", registration.Status);
    }

    [Theory]
    [InlineData("unknown-action")]
    [InlineData("duplicate")]
    [InlineData("invalid-key")]
    public async Task InvalidWholeUpdateKeepsPriorRegistrationAndRevision(string scenario)
    {
        await using var area = new Area(); await using var source = new Source();
        var service = new ShortcutSettingsService(area.Store, source); await service.RestoreAsync(default);
        var previous = await service.GetAsync(); var count = source.ConfigureCount;
        var request = scenario switch
        {
            "unknown-action" => new ShortcutSettingsRequest([new("arbitrary-action", new("F9", false, false, false, false))]),
            "duplicate" => new ShortcutSettingsRequest([.. Request("K").Bindings, .. Request("F9").Bindings]),
            _ => Request("999")
        };
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => service.UpdateAsync("owner", request, "invalid-key-1", ShortcutSettingsStore.ETag(previous.Revision)))).Code);
        Assert.Equal(count, source.ConfigureCount);
        Assert.Equal(previous, await service.GetAsync(), SnapshotComparer.Instance);
    }

    [Fact]
    public async Task ReceiptsRejectBodyReuseAndOldRevisionWithoutRegisteringAgain()
    {
        await using var area = new Area(); await using var source = new Source();
        var service = new ShortcutSettingsService(area.Store, source);
        var first = await service.UpdateAsync("owner", Request("K"), "receipt-key-1", ShortcutSettingsStore.ETag(new(0)));
        var second = await service.UpdateAsync("owner", Request("L"), "receipt-key-2", ShortcutSettingsStore.ETag(first.Revision));
        var count = source.ConfigureCount;
        Assert.Equal(first, await service.UpdateAsync("owner", Request("K"), "receipt-key-1", ShortcutSettingsStore.ETag(new(0))), SnapshotComparer.Instance);
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() => service.UpdateAsync("owner", Request("M"), "receipt-key-1", ShortcutSettingsStore.ETag(new(0))))).Code);
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() => service.UpdateAsync("owner", Request("N"), "receipt-key-3", ShortcutSettingsStore.ETag(new(0))))).Code);
        Assert.Equal(count, source.ConfigureCount);
        Assert.Equal(second, await service.GetAsync(), SnapshotComparer.Instance);
    }

    [Fact]
    public async Task PersistenceFailureRestoresPriorRegistrationAndAllowsRetry()
    {
        await using var area = new Area(); await using var source = new Source();
        var service = new ShortcutSettingsService(area.Store, source); await service.RestoreAsync(default);
        await area.Database.WriteAsync(tx => tx.Execute("CREATE TRIGGER shortcut_write_failure BEFORE INSERT ON platform_shortcut_settings BEGIN SELECT RAISE(ABORT,'injected'); END"));
        await Assert.ThrowsAnyAsync<Exception>(() => service.UpdateAsync("owner", Request("K"), "failed-key-1", ShortcutSettingsStore.ETag(new(0))));
        var prior = await service.GetAsync();
        Assert.Equal(0UL, prior.Revision.Value); Assert.Equal("F9", Assert.Single(prior.Registrations).Registered!.Key);
        await area.Database.WriteAsync(tx => tx.Execute("DROP TRIGGER shortcut_write_failure"));
        var retry = await service.UpdateAsync("owner", Request("K"), "failed-key-1", ShortcutSettingsStore.ETag(new(0)));
        Assert.Equal(1UL, retry.Revision.Value); Assert.Equal("K", Assert.Single(retry.Registrations).Registered!.Key);
    }

    [Fact]
    public async Task MissingDesktopSourceReturnsUnavailableAndKeepsConfiguration()
    {
        await using var area = new Area(); var service = new ShortcutSettingsService(area.Store);
        var value = await service.UpdateAsync("owner", Request("7", true), "headless-key-1", ShortcutSettingsStore.ETag(new(0)));
        Assert.Equal("D7", Assert.Single(value.Bindings).Chord.Key);
        Assert.Equal("unavailable", Assert.Single(value.Registrations).Status);
        Assert.Null(Assert.Single(value.Registrations).Registered);
        Assert.Equal(value, await new ShortcutSettingsService(area.Store).GetAsync(), SnapshotComparer.Instance);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsReconfigurationReleasesOldCombinationAndStopUnregistersNewOne()
    {
        var first = new ShortcutChord(ShortcutKey.F24, Control: true, Alt: true, Shift: true);
        var second = new ShortcutChord(ShortcutKey.F23, Control: true, Alt: true, Shift: true);
        await using var source = new WindowsGlobalShortcutSource([new("emergency-stop", first, [])]);
        await source.StartAsync(default);
        Assert.Equal(first, Assert.Single(source.GetRegistrations()).Registered);
        await using var conflicting = new WindowsGlobalShortcutSource([new("emergency-stop", first, [])]);
        await conflicting.StartAsync(default);
        Assert.NotEqual(first, Assert.Single(conflicting.GetRegistrations()).Registered);
        await source.ReconfigureAsync([new("emergency-stop", second, [])], default);
        await using var oldCombination = new WindowsGlobalShortcutSource([new("emergency-stop", first, [])]);
        await oldCombination.StartAsync(default);
        Assert.Equal(first, Assert.Single(oldCombination.GetRegistrations()).Registered);
        await source.StopAsync(default);
        Assert.Equal(ShortcutRegistrationStatus.Unavailable, Assert.Single(source.GetRegistrations()).Status);
        await using var releasedCombination = new WindowsGlobalShortcutSource([new("emergency-stop", second, [])]);
        await releasedCombination.StartAsync(default);
        Assert.Equal(second, Assert.Single(releasedCombination.GetRegistrations()).Registered);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsReconfigurationIsBoundedFreezesInputsAndReleasesCanceledSlots()
    {
        using var registrar = new BlockingRegistrar();
        await using var source = new WindowsGlobalShortcutSource([], registrar);
        await source.StartAsync(default);
        var first = source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.K), [])], default);
        try
        {
            Assert.True(registrar.Entered.Wait(TimeSpan.FromSeconds(5)));
            var alternatives = new List<ShortcutChord> { new(ShortcutKey.F20) };
            var queuedValues = new[] { new ShortcutRequest("emergency-stop", new(ShortcutKey.L), []), new ShortcutRequest("overlay-toggle", new(ShortcutKey.L), alternatives) };
            var queued = source.ReconfigureAsync(queuedValues, default);
            queuedValues[0] = new("emergency-stop", new(ShortcutKey.M), []);
            alternatives[0] = new(ShortcutKey.F21);
            Assert.Equal(WindowsGlobalShortcutSource.CapacityErrorCode, (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.N), [])], default))).Message);
            registrar.Release.Set();
            await first; var result = await queued;
            Assert.Equal(new(ShortcutKey.L), result.Single(value => value.ActionId == "emergency-stop").Registered);
            Assert.Equal(new(ShortcutKey.F20), result.Single(value => value.ActionId == "overlay-toggle").Registered);

            registrar.BlockKey = ShortcutKey.P; registrar.Release.Reset(); registrar.Entered.Reset();
            var executing = source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.P), [])], default);
            Assert.True(registrar.Entered.Wait(TimeSpan.FromSeconds(5)));
            using var cancellation = new CancellationTokenSource();
            var canceled = source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.Q), [])], cancellation.Token);
            cancellation.Cancel();
            Assert.Equal(WindowsGlobalShortcutSource.CapacityErrorCode, (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.R), [])], default))).Message);
            registrar.Release.Set(); await executing;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            var recovered = await source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.R), [])], default);
            Assert.Equal(new(ShortcutKey.R), Assert.Single(recovered).Registered);
        }
        finally { registrar.Release.Set(); }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsStopDrainsPendingReconfigurationAndReleasesRegistrations()
    {
        using var registrar = new BlockingRegistrar();
        await using var source = new WindowsGlobalShortcutSource([], registrar);
        await source.StartAsync(default);
        var executing = source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.K), [])], default);
        try
        {
            Assert.True(registrar.Entered.Wait(TimeSpan.FromSeconds(5)));
            var queued = source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.L), [])], default);
            var stopped = source.StopAsync(default);
            registrar.Release.Set();
            await executing;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
            await stopped;
            Assert.Empty(registrar.Registered);
            Assert.All(source.GetRegistrations(), value => Assert.Equal(ShortcutRegistrationStatus.Unavailable, value.Status));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.ReconfigureAsync([new("emergency-stop", new(ShortcutKey.M), [])], default));
        }
        finally { registrar.Release.Set(); }
    }

    private sealed class BlockingRegistrar : IGlobalShortcutRegistrar, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ShortcutKey BlockKey { get; set; } = ShortcutKey.K;
        public Dictionary<int, ShortcutChord> Registered { get; } = [];
        public bool TryRegister(int id, ShortcutChord chord, out string? failureCode)
        {
            if (chord.Key == BlockKey)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("shortcut registrar test gate timed out");
            }
            if (Registered.Values.Contains(chord)) { failureCode = "shortcut.inUse"; return false; }
            Registered.Add(id, chord); failureCode = null; return true;
        }
        public void Unregister(int id) => Registered.Remove(id);
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class SnapshotComparer : IEqualityComparer<ShortcutSettingsSnapshot>
    {
        public static SnapshotComparer Instance { get; } = new();
        public bool Equals(ShortcutSettingsSnapshot? x, ShortcutSettingsSnapshot? y) =>
            System.Text.Json.JsonSerializer.Serialize(x, ContractJson.Options) == System.Text.Json.JsonSerializer.Serialize(y, ContractJson.Options);
        public int GetHashCode(ShortcutSettingsSnapshot obj) => 0;
    }
    private sealed class Source : IGlobalShortcutSource
    {
        public Registrar Registrar { get; } = new();
        private readonly GlobalShortcutRegistry registry;
        public Source() => registry = new(Registrar);
        public bool CanReconfigure => true;
        public int ConfigureCount { get; private set; }
        public event Action<string>? ActionReceived;
        public void Emit(string value) => ActionReceived?.Invoke(value);
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { registry.Dispose(); return Task.CompletedTask; }
        public IReadOnlyList<ShortcutRegistrationResult> GetRegistrations() => registry.GetRegistrations();
        public Task<IReadOnlyList<ShortcutRegistrationResult>> ReconfigureAsync(IReadOnlyList<ShortcutRequest> requests, CancellationToken token)
        { token.ThrowIfCancellationRequested(); ConfigureCount++; return Task.FromResult(registry.Configure(requests)); }
        public ValueTask DisposeAsync() { registry.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Registrar : IGlobalShortcutRegistrar
    {
        public HashSet<ShortcutChord> Occupied { get; } = [];
        private readonly Dictionary<int, ShortcutChord> registered = [];
        public bool TryRegister(int id, ShortcutChord chord, out string? failure)
        {
            failure = Occupied.Contains(chord) || registered.Values.Contains(chord) ? "shortcut.inUse" : null;
            if (failure is not null) return false;
            registered.Add(id, chord); return true;
        }
        public void Unregister(int id) => registered.Remove(id);
    }
    private sealed class Area : IAsyncDisposable
    {
        private readonly string root;
        private readonly DataRootLease lease;
        public SqliteStore Database { get; }
        public ShortcutSettingsStore Store { get; }
        public Area()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            root = Path.Combine(repository!.FullName, "artifacts", "tmp", "shortcuts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data")));
            Database = SqliteStore.Open(lease, [ShortcutSettingsStore.Migration]); Store = new(Database);
        }
        public async ValueTask DisposeAsync() { await Database.DisposeAsync(); lease.Dispose(); Directory.Delete(root, true); }
    }
}
