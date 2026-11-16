using MabiTool.Contracts;
using MabiTool.Host.Updates;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class UpdateHostHealthCheckTests
{
    [Fact]
    public async Task StartedPersistentHostResolvesAndRunsItsHealthService()
    {
        var directory = NewDirectory();
        try
        {
            await using var app = BackendApplication.Build(new(0, LocalAuthService.NewSecret(), new HashSet<string>()),
                storage: new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
            await app.StartAsync();
            try
            {
                var root = app.Services.GetRequiredService<DataRootLease>();
                var database = app.Services.GetRequiredService<SqliteStore>();
                var system = app.Services.GetRequiredService<SystemInfo>();
                using var maintenance = database.Activities.TryEnterMaintenance("health");
                Assert.NotNull(maintenance);
                DatabaseMigration[] migrations = [CoreMigrations.Initial, CoreMigrations.Operations,
                    CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode,
                    .. BackendApplication.DatabaseMigrations];
                var target = new UpdateHostHealthTarget(Guid.NewGuid().ToString("N"), system.Version, new(1, 0), new(2, 0),
                    root.Paths.RootId, root.DatabaseId!, "live", migrations.Select(m => new DatabaseMigrationStamp(m.ModuleId, m.Version, m.Checksum)).ToArray(),
                    [new("core", "0.1.0"), new("music-library", "0.1.0")]);
                var result = await app.Services.GetRequiredService<UpdateHostHealthCheck>().VerifyAsync(maintenance, target);
                Assert.Equal(system.InstanceId, result.InstanceId);
                Assert.Equal(2, result.Modules.Count);
                Assert.Equal("health", database.Activities.Snapshot().MaintenanceOwner);
                using var installation = new InstallTransactionTests.Workspace();
                using var transaction = new InstallTransaction(installation.InstallRoot);
                await transaction.InitializePointerAsync(installation.Previous);
                var prepared = await transaction.PrepareForHealthAsync(installation.TransactionId, installation.PlanId,
                    installation.Previous, installation.Candidate, new(target.HostVersion, target.MinimumHostApi,
                        target.MaximumHostApiExclusive, target.RootId, target.DatabaseId, target.BackendMode, target.Migrations, target.Modules));
                var backup = await database.CreateDatabaseBackupAsync(maintenance);
                var switched = await transaction.SwitchPointerAsync(await transaction.RecordDataBackupAsync(prepared, backup));
                var challenge = await transaction.BeginHealthCheckAsync(switched);
                var evidence = await app.Services.GetRequiredService<UpdateHostHealthCheck>().VerifyInstallationAsync(maintenance, challenge);
                var approval = await transaction.RecordHealthyAsync(challenge, evidence);
                Assert.Equal("committed", (await transaction.CommitAsync(approval)).Phase);
                Assert.Equal(system.InstanceId, (await transaction.ReadAsync()).Health!.InstanceId);
                Assert.Equal("health", database.Activities.Snapshot().MaintenanceOwner);
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChecksFixedHostDatabaseAndRequiredModulesWithoutStartingOptionalWorkers()
    {
        await using var fixture = new Fixture();
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        var result = await fixture.Check.VerifyAsync(maintenance, fixture.Target);
        Assert.Equal(fixture.Target.TransactionId, result.TransactionId);
        Assert.Equal(fixture.System.InstanceId, result.InstanceId);
        Assert.Equal(fixture.Root.Paths.RootId, result.Database.RootId);
        Assert.Equal(fixture.Target.Modules, result.Modules);
        Assert.Equal(1, fixture.CoreStarts);
        Assert.Equal(0, fixture.OptionalStarts);
        Assert.All((await fixture.Runtime.SnapshotAsync()).Items, item => Assert.Empty(item.ActiveOperationIds));
        Assert.Equal("health", fixture.Database.Activities.Snapshot().MaintenanceOwner);
        Assert.Empty(fixture.Database.Activities.Snapshot().Activities);
        Assert.Throws<ActivityBarrierException>(() => fixture.Database.Activities.EnterActivity("business", "write"));
    }

    [Fact]
    public async Task WaitsForStartupAndFreezesCallerOwnedCollections()
    {
        await using var fixture = new Fixture(started: false);
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        var migrations = fixture.Target.Migrations.ToArray();
        var modules = fixture.Target.Modules.ToArray();
        var operation = fixture.Check.VerifyAsync(maintenance, fixture.Target with { Migrations = migrations, Modules = modules });
        Assert.False(operation.IsCompleted);
        Assert.Equal(0, fixture.CoreStarts);
        modules[0] = new("missing", "9.0.0");
        migrations[0] = migrations[0] with { Checksum = new string('0', 64) };
        fixture.Lifetime.Start();
        Assert.Equal("core", Assert.Single((await operation).Modules).Id);
    }

    [Fact]
    public async Task RequiredModulesShareOneDependencyDemandAtItsCapacity()
    {
        await using var fixture = new Fixture(coreCapacity: 1);
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        var target = fixture.Target with { Modules = [new("core", "0.1.0"), new("optional", "0.1.0")] };
        var result = await fixture.Check.VerifyAsync(maintenance, target);
        Assert.Equal(target.Modules, result.Modules);
        Assert.Equal(1, fixture.CoreStarts);
        Assert.Equal(1, fixture.OptionalStarts);
        Assert.All((await fixture.Runtime.SnapshotAsync()).Items, item => Assert.Empty(item.ActiveOperationIds));
        Assert.Empty(fixture.Database.Activities.Snapshot().Activities);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("api-low")]
    [InlineData("api-high")]
    [InlineData("api-catalog")]
    [InlineData("root")]
    public async Task RejectsWrongHostContractOrRoot(string change)
    {
        await using var fixture = new Fixture();
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        var target = change switch
        {
            "host" => fixture.Target with { HostVersion = "0.2.0" },
            "api-low" => fixture.Target with { MinimumHostApi = new(1, 1) },
            "api-high" => fixture.Target with { MinimumHostApi = new(0, 9), MaximumHostApiExclusive = new(1, 0) },
            "root" => fixture.Target with { RootId = Guid.NewGuid().ToString("N") },
            _ => fixture.Target
        };
        var check = change == "api-catalog" ? fixture.NewCheck(fixture.System with { ApiVersion = "1.1" }) : fixture.Check;
        Assert.Equal("update.hostHealthMismatch", (await Assert.ThrowsAsync<UpdateHostHealthException>(() => check.VerifyAsync(maintenance, target))).Code);
        Assert.Empty(fixture.Database.Activities.Snapshot().Activities);
        Assert.All((await fixture.Runtime.SnapshotAsync()).Items, item => Assert.Empty(item.ActiveOperationIds));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("disabled")]
    public async Task InvalidRequiredModuleDoesNotStartAnyModule(string change)
    {
        await using var fixture = new Fixture();
        if (change == "disabled") await fixture.Runtime.SetEnabledAsync("core", false);
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        var target = fixture.Target with { Modules = change switch
        {
            "missing" => [new("core", "0.1.0"), new("missing", "0.1.0")],
            "version" => [new("core", "0.2.0")],
            _ => fixture.Target.Modules
        }};
        Assert.Equal("update.moduleHealthMismatch", (await Assert.ThrowsAsync<UpdateHostHealthException>(() => fixture.Check.VerifyAsync(maintenance, target))).Code);
        Assert.Equal(0, fixture.CoreStarts);
        Assert.Empty(fixture.Database.Activities.Snapshot().Activities);
    }

    [Theory]
    [InlineData("core")]
    [InlineData("duplicate")]
    [InlineData("transaction")]
    [InlineData("range")]
    [InlineData("mode")]
    [InlineData("null-migration")]
    public async Task MalformedTargetsAreRejectedBeforeInitialization(string change)
    {
        await using var fixture = new Fixture();
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        var target = change switch
        {
            "core" => fixture.Target with { Modules = [new("optional", "0.1.0")] },
            "duplicate" => fixture.Target with { Modules = [new("core", "0.1.0"), new("core", "0.1.0")] },
            "transaction" => fixture.Target with { TransactionId = "bad" },
            "range" => fixture.Target with { MaximumHostApiExclusive = new(1, 0) },
            "mode" => fixture.Target with { BackendMode = "unknown" },
            _ => fixture.Target with { Migrations = [null!, .. fixture.Target.Migrations] }
        };
        Assert.Equal("update.hostHealthTargetInvalid", (await Assert.ThrowsAsync<UpdateHostHealthException>(() => fixture.Check.VerifyAsync(maintenance, target))).Code);
        Assert.Equal(0, fixture.CoreStarts);
        Assert.Empty(fixture.Database.Activities.Snapshot().Activities);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("timeout")]
    public async Task WaitingForStartupEndsOnCancellationStopOrSixtySecondDeadline(string reason)
    {
        await using var fixture = new Fixture(started: false);
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        using var cancel = new CancellationTokenSource();
        var operation = fixture.Check.VerifyAsync(maintenance, fixture.Target, cancel.Token);
        Assert.False(operation.IsCompleted);
        if (reason == "cancel") cancel.Cancel();
        if (reason == "stop") fixture.Lifetime.StopApplication();
        if (reason == "timeout") fixture.Clock.Expire();
        if (reason == "timeout")
            Assert.Equal("update.hostHealthTimeout", (await Assert.ThrowsAsync<UpdateHostHealthException>(() => operation)).Code);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, fixture.CoreStarts);
        Assert.Empty(fixture.Database.Activities.Snapshot().Activities);
        Assert.Equal("health", fixture.Database.Activities.Snapshot().MaintenanceOwner);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("maintenance")]
    public async Task SlowModuleKeepsBarrierUntilItFinishesAndCannotReturnLateSuccess(string reason)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(factory: async _ =>
        {
            entered.SetResult();
            await release.Task;
            return new EmptyModule();
        });
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        using var cancel = new CancellationTokenSource();
        var operation = fixture.Check.VerifyAsync(maintenance, fixture.Target, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (reason == "cancel") cancel.Cancel();
        if (reason == "timeout") fixture.Clock.Expire();
        maintenance.Dispose();
        Assert.False(operation.IsCompleted);
        Assert.Throws<ActivityBarrierException>(() => fixture.Database.Activities.EnterActivity("business", "write"));
        release.SetResult();
        if (reason == "timeout")
            Assert.Equal("update.hostHealthTimeout", (await Assert.ThrowsAsync<UpdateHostHealthException>(() => operation)).Code);
        else if (reason == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else await Assert.ThrowsAsync<ActivityBarrierException>(() => operation);
        Assert.Null(fixture.Database.Activities.Snapshot().MaintenanceOwner);
        Assert.All((await fixture.Runtime.SnapshotAsync()).Items, item => Assert.Empty(item.ActiveOperationIds));
        using var business = fixture.Database.Activities.EnterActivity("business", "write");
    }

    [Fact]
    public async Task ModuleInitializationFailureReleasesPreviouslyAcquiredDemands()
    {
        await using var fixture = new Fixture(optionalFactory: _ => ValueTask.FromException<IAsyncDisposable>(new InvalidOperationException("failed")));
        using var maintenance = fixture.Database.Activities.TryEnterMaintenance("health")!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Check.VerifyAsync(maintenance,
            fixture.Target with { Modules = [new("core", "0.1.0"), new("optional", "0.1.0")] }));
        Assert.Equal(1, fixture.CoreStarts);
        Assert.Equal(1, fixture.OptionalStarts);
        Assert.Empty(fixture.Database.Activities.Snapshot().Activities);
        Assert.All((await fixture.Runtime.SnapshotAsync()).Items, item => Assert.Empty(item.ActiveOperationIds));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;
        public DataRootLease Root { get; }
        public SqliteStore Database { get; }
        public ModuleRuntime Runtime { get; }
        public ModuleCatalog Catalog { get; }
        public Lifetime Lifetime { get; } = new();
        public DeadlineClock Clock { get; } = new();
        public SystemInfo System { get; } = new("MabiTool", "0.1.0", "1.0", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, ".NET", "Windows");
        public UpdateHostHealthTarget Target { get; }
        public UpdateHostHealthCheck Check => NewCheck(System);
        public int CoreStarts { get; private set; }
        public int OptionalStarts { get; private set; }
        public Fixture(bool started = true, Func<CancellationToken, ValueTask<IAsyncDisposable>>? factory = null,
            Func<CancellationToken, ValueTask<IAsyncDisposable>>? optionalFactory = null, int coreCapacity = 8)
        {
            directory = NewDirectory();
            Root = DataRootLease.Open(new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
            Database = SqliteStore.Open(Root);
            Catalog = new(new(1, 0), [
                new(new("core", new(0, 1, 0), new(1, 0), new(2, 0), [], MaximumConcurrentDemands: coreCapacity), ct =>
                { CoreStarts++; return factory?.Invoke(ct) ?? ValueTask.FromResult<IAsyncDisposable>(new EmptyModule()); }),
                new(new("optional", new(0, 1, 0), new(1, 0), new(2, 0), [new("core", "0.1.0")]), ct =>
                { OptionalStarts++; return optionalFactory?.Invoke(ct) ?? ValueTask.FromResult<IAsyncDisposable>(new EmptyModule()); })]);
            Runtime = new(Catalog);
            Target = new(Guid.NewGuid().ToString("N"), "0.1.0", new(1, 0), new(2, 0), Root.Paths.RootId,
                Root.DatabaseId!, "live", new[] { CoreMigrations.Initial, CoreMigrations.Operations,
                    CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode }
                    .Select(m => new DatabaseMigrationStamp(m.ModuleId, m.Version, m.Checksum)).ToArray(), [new("core", "0.1.0")]);
            if (started) Lifetime.Start();
        }
        public UpdateHostHealthCheck NewCheck(SystemInfo info) => new(Database, Runtime, Catalog, info, Lifetime, Clock);
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Database.DisposeAsync();
            Root.Dispose();
            Lifetime.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private static string NewDirectory()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        var directory = Path.Combine(repository!.FullName, "artifacts", "tmp", "host-health-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class EmptyModule : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new(), stopping = new(), stopped = new();
        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void Start() => started.Cancel();
        public void StopApplication() => stopping.Cancel();
        public void Dispose() { started.Dispose(); stopping.Dispose(); stopped.Dispose(); }
    }
    private sealed class DeadlineClock : TimeProvider
    {
        private Action? expire;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(60), dueTime);
            expire = () => callback(state);
            return new TimerHandle(() => expire = null);
        }
        public void Expire() => expire!();
        private sealed class TimerHandle(Action dispose) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => dispose();
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
