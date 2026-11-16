using System.Diagnostics;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.Host.Updates;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class UpdateHandoffHealthSessionTests
{
    [Fact]
    public async Task AuthenticatedExchangePersistsHealthAndCommitAndKeepsMaintenance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var candidate = UpdateHandoffHealthSession.RunCandidateAsync(fixture.Candidate,
            fixture.Health, fixture.Maintenance, fixture.System, fixture.Timeout.Token);
        var progress = new UpdateHandoffProgress();
        var committed = await UpdateInstallHealthExchange.RunUpdaterAsync(fixture.Updater,
            fixture.Installation, fixture.Switched, fixture.Timeout.Token, progress);
        var notice = await candidate;
        Assert.Equal("committed", committed.Phase);
        Assert.Equal(fixture.System.InstanceId, committed.Health!.InstanceId);
        Assert.Equal(committed.Revision, notice.Revision);
        Assert.Equal(InstallRecoveryAction.Committed, (await fixture.Installation.AssessRecoveryAsync()).Action);
        var timings = progress.Freeze().Timings;
        foreach (var stage in new[] { "challenge.candidateTree", "challenge.previousTree", "recordHealthy.candidateTree",
            "recordHealthy.previousTree", "commit.candidateTree", "commit.previousTree", "commit.persist", "committed" })
            Assert.Contains(timings, timing => timing.Stage == stage);
        fixture.AssertMaintenance();
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("stage")]
    [InlineData("challenge")]
    [InlineData("transaction")]
    [InlineData("database")]
    [InlineData("different-instance")]
    public async Task InvalidCandidateResponseCannotCommit(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var updater = fixture.RunUpdaterAsync();
        var offer = await fixture.Candidate.ReceiveAsync<UpdateHealthOffer>("offer", fixture.Timeout.Token);
        await fixture.Candidate.SendAsync(change == "stage" ? "healthResult" : "accepted",
            new UpdateHealthAccepted(change == "instance" ? "bad" : fixture.System.InstanceId), fixture.Timeout.Token);
        if (change is not ("instance" or "stage"))
        {
            await fixture.Candidate.ReceiveAsync<UpdateHealthRequest>("healthCheck", fixture.Timeout.Token);
            var evidence = await fixture.EvidenceAsync(offer);
            evidence = change switch
            {
                "challenge" => evidence with { Challenge = new('f', 64) },
                "transaction" => evidence with { TransactionId = new('f', 32) },
                "database" => evidence with { Database = evidence.Database with { DatabaseId = new('f', 32) } },
                _ => evidence with { InstanceId = new('f', 32) }
            };
            await fixture.Candidate.SendAsync("healthResult", evidence, fixture.Timeout.Token);
        }
        await Assert.ThrowsAsync<UpdatePlanException>(() => updater);
        Assert.Equal("pointerSwitched", (await fixture.Installation.ReadAsync()).Phase);
        fixture.AssertMaintenance();
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("wrong-ack")]
    public async Task MissingOrWrongAcknowledgementKeepsDurableCommit(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var updater = fixture.RunUpdaterAsync();
        var offer = await fixture.Candidate.ReceiveAsync<UpdateHealthOffer>("offer", fixture.Timeout.Token);
        await fixture.Candidate.SendAsync("accepted", new UpdateHealthAccepted(fixture.System.InstanceId), fixture.Timeout.Token);
        await fixture.Candidate.ReceiveAsync<UpdateHealthRequest>("healthCheck", fixture.Timeout.Token);
        await fixture.Candidate.SendAsync("healthResult", await fixture.EvidenceAsync(offer), fixture.Timeout.Token);
        var notice = await fixture.Candidate.ReceiveAsync<UpdateCommitNotice>("commit", fixture.Timeout.Token);
        Assert.Equal("committed", (await fixture.Installation.ReadAsync()).Phase);
        if (change == "disconnect") fixture.Candidate.Dispose();
        else await fixture.Candidate.SendAsync("committed", notice with { Revision = notice.Revision + 1 }, fixture.Timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => updater);
        Assert.Equal(InstallRecoveryAction.Committed, (await fixture.Installation.AssessRecoveryAsync()).Action);
        fixture.AssertMaintenance();
    }

    [Theory]
    [InlineData("offer-transaction")]
    [InlineData("offer-challenge")]
    [InlineData("offer-revision")]
    [InlineData("request-instance")]
    [InlineData("notice-revision")]
    [InlineData("notice-release")]
    [InlineData("notice-instance")]
    [InlineData("notice-manifest")]
    public async Task CandidateRejectsWrongTargetOrNoticeWithoutOpeningBusiness(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var candidate = UpdateHandoffHealthSession.RunCandidateAsync(fixture.Candidate,
            fixture.Health, fixture.Maintenance, fixture.System, fixture.Timeout.Token);
        var challenge = await fixture.Installation.BeginHealthCheckAsync(fixture.Switched, fixture.Timeout.Token);
        var record = challenge.Record;
        var offer = new UpdateHealthOffer(record.TransactionId, challenge.Nonce, record.Revision,
            record.Candidate.ReleaseId, record.Candidate.ManifestSha256, record.HealthTarget!);
        offer = change switch
        {
            "offer-transaction" => offer with { TransactionId = new('f', 32) },
            "offer-challenge" => offer with { Challenge = "bad" },
            "offer-revision" => offer with { Revision = long.MaxValue },
            _ => offer
        };
        await fixture.Updater.SendAsync("offer", offer, fixture.Timeout.Token);
        if (!change.StartsWith("offer-", StringComparison.Ordinal))
        {
            await fixture.Updater.ReceiveAsync<UpdateHealthAccepted>("accepted", fixture.Timeout.Token);
            await fixture.Updater.SendAsync("healthCheck", new UpdateHealthRequest(
                change == "request-instance" ? new('f', 32) : fixture.System.InstanceId), fixture.Timeout.Token);
            if (change != "request-instance")
            {
                var evidence = await fixture.Updater.ReceiveAsync<InstallHealthEvidence>("healthResult", fixture.Timeout.Token);
                var approval = await fixture.Installation.RecordHealthyAsync(challenge, evidence, fixture.Timeout.Token);
                var committed = await fixture.Installation.CommitAsync(approval, fixture.Timeout.Token);
                var notice = new UpdateCommitNotice(committed.TransactionId, fixture.System.InstanceId,
                    committed.Candidate.ReleaseId, committed.Candidate.ManifestSha256, committed.Revision);
                notice = change switch
                {
                    "notice-revision" => notice with { Revision = notice.Revision + 1 },
                    "notice-release" => notice with { ReleaseId = "other" },
                    "notice-instance" => notice with { InstanceId = new('f', 32) },
                    _ => notice with { ManifestSha256 = new('f', 64) }
                };
                await fixture.Updater.SendAsync("commit", notice, fixture.Timeout.Token);
            }
        }
        await Assert.ThrowsAsync<UpdatePlanException>(() => candidate);
        Assert.Equal(change.StartsWith("notice-", StringComparison.Ordinal) ? "committed" : "pointerSwitched",
            (await fixture.Installation.ReadAsync()).Phase);
        fixture.AssertMaintenance();
    }

    [Fact]
    public async Task DisconnectBeforeHealthResultLeavesTransactionUncommitted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var updater = fixture.RunUpdaterAsync();
        await fixture.Candidate.ReceiveAsync<UpdateHealthOffer>("offer", fixture.Timeout.Token);
        await fixture.Candidate.SendAsync("accepted", new UpdateHealthAccepted(fixture.System.InstanceId), fixture.Timeout.Token);
        await fixture.Candidate.ReceiveAsync<UpdateHealthRequest>("healthCheck", fixture.Timeout.Token);
        fixture.Candidate.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => updater);
        Assert.Equal("pointerSwitched", (await fixture.Installation.ReadAsync()).Phase);
        fixture.AssertMaintenance();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly InstallTransactionTests.Workspace workspace = new();
        private WebApplication? app;
        internal readonly CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(30));
        internal InstallTransaction Installation { get; private set; } = null!;
        internal InstallTransactionRecord Switched { get; private set; } = null!;
        internal UpdateHandoffConnection Updater { get; private set; } = null!;
        internal UpdateHandoffConnection Candidate { get; private set; } = null!;
        internal ActivityBarrier.MaintenanceLease Maintenance { get; private set; } = null!;
        internal UpdateHostHealthCheck Health => app!.Services.GetRequiredService<UpdateHostHealthCheck>();
        internal SystemInfo System => app!.Services.GetRequiredService<SystemInfo>();
        private SqliteStore Database => app!.Services.GetRequiredService<SqliteStore>();

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try { await fixture.InitializeAsync(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task InitializeAsync()
        {
            app = BackendApplication.Build(new(0, LocalAuthService.NewSecret(), new HashSet<string>()),
                storage: new(Path.Combine(workspace.Root, "bootstrap"), true, Path.Combine(workspace.Root, "data")));
            await app.StartAsync(Timeout.Token);
            Maintenance = Database.Activities.TryEnterMaintenance("handoff-health")!;
            Assert.NotNull(Maintenance);
            var root = app.Services.GetRequiredService<DataRootLease>();
            DatabaseMigration[] migrations = [CoreMigrations.Initial, CoreMigrations.Operations,
                CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode,
                .. BackendApplication.DatabaseMigrations];
            var target = new InstallHealthTarget(System.Version, new(1, 0), new(2, 0), root.Paths.RootId,
                root.DatabaseId!, "live", migrations.Select(m => new DatabaseMigrationStamp(m.ModuleId, m.Version, m.Checksum)).ToArray(),
                [new("core", "0.1.0"), new("music-library", "0.1.0")]);
            Installation = new(workspace.InstallRoot);
            await Installation.InitializePointerAsync(workspace.Previous, Timeout.Token);
            var prepared = await Installation.PrepareForHealthAsync(workspace.TransactionId, workspace.PlanId,
                workspace.Previous, workspace.Candidate, target, Timeout.Token);
            var backedUp = await Installation.RecordDataBackupAsync(prepared,
                await Database.CreateDatabaseBackupAsync(Maintenance, Timeout.Token), Timeout.Token);
            Switched = await Installation.SwitchPointerAsync(backedUp, Timeout.Token);
            using var process = Process.GetCurrentProcess();
            var identity = UpdatePeerIdentity.Capture(process);
            using var listener = new UpdateHandoffListener(workspace.TransactionId);
            var accepting = listener.AcceptAsync(identity, Timeout.Token);
            Candidate = await UpdateHandoffConnection.ConnectAsync(listener.PipeName, identity, workspace.TransactionId, Timeout.Token);
            Updater = await accepting;
        }

        internal Task<InstallTransactionRecord> RunUpdaterAsync() => UpdateHandoffHealthSession.RunUpdaterAsync(
            Updater, Installation, Switched, Timeout.Token);

        internal async Task<InstallHealthEvidence> EvidenceAsync(UpdateHealthOffer offer)
        {
            var target = offer.Target;
            var result = await Health.VerifyAsync(Maintenance, new(offer.TransactionId, target.HostVersion,
                target.MinimumHostApi, target.MaximumHostApiExclusive, target.RootId, target.DatabaseId,
                target.BackendMode, target.Migrations, target.Modules), Timeout.Token);
            return new(result.TransactionId, offer.Challenge, result.InstanceId, result.HostVersion,
                result.ApiVersion, result.Database, result.Modules);
        }

        internal void AssertMaintenance()
        {
            Assert.Equal("handoff-health", Database.Activities.Snapshot().MaintenanceOwner);
            Assert.Throws<ActivityBarrierException>(() => Database.Activities.EnterActivity("business", "write"));
        }

        public async ValueTask DisposeAsync()
        {
            Updater?.Dispose(); Candidate?.Dispose();
            Installation?.Dispose(); Maintenance?.Dispose();
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
            Timeout.Dispose(); workspace.Dispose();
        }
    }
}
