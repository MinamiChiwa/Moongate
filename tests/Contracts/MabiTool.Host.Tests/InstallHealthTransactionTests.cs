using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using System.Text.Json;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class InstallHealthTransactionTests
{
    [Fact]
    public async Task HealthyAndCommittedArePersistedOnlyWithCurrentChallengeAndCandidate()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var target = Target();
        var prepared = await transaction.PrepareForHealthAsync(workspace.TransactionId, workspace.PlanId,
            workspace.Previous, workspace.Candidate, target);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, InstallTransactionTests.Workspace.Backup);
        var switched = await transaction.SwitchPointerAsync(backedUp);
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        var evidence = Evidence(workspace.TransactionId, challenge.Nonce, target);
        var approval = await transaction.RecordHealthyAsync(challenge, evidence);
        Assert.Equal("healthy", (await transaction.ReadAsync()).Phase);
        var committed = await transaction.CommitAsync(approval);
        Assert.Equal("committed", committed.Phase);
        Assert.Equal(InstallRecoveryAction.Committed, (await transaction.AssessRecoveryAsync()).Action);
        Assert.Equal(committed.Health!.Challenge, (await transaction.ReadAsync()).Health!.Challenge);
        Assert.Equal(committed.Revision, (await transaction.CommitAsync(approval)).Revision);
    }

    [Fact]
    public async Task StaleChallengeAndEvidenceCannotAdvanceHealth()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var target = Target();
        var prepared = await transaction.PrepareForHealthAsync(workspace.TransactionId, workspace.PlanId,
            workspace.Previous, workspace.Candidate, target);
        var switched = await transaction.SwitchPointerAsync(await transaction.RecordDataBackupAsync(prepared, InstallTransactionTests.Workspace.Backup));
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.RecordHealthyAsync(challenge,
            Evidence(workspace.TransactionId, new string('0', 64), target)));
        var fresh = await transaction.BeginHealthCheckAsync(switched);
        await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.RecordHealthyAsync(challenge,
            Evidence(workspace.TransactionId, fresh.Nonce, target)));
        Assert.Equal("pointerSwitched", (await transaction.ReadAsync()).Phase);
    }

    [Fact]
    public async Task CommitRejectsChangedCandidate()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var target = Target();
        var prepared = await transaction.PrepareForHealthAsync(workspace.TransactionId, workspace.PlanId,
            workspace.Previous, workspace.Candidate, target);
        var switched = await transaction.SwitchPointerAsync(await transaction.RecordDataBackupAsync(prepared, InstallTransactionTests.Workspace.Backup));
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        var approval = await transaction.RecordHealthyAsync(challenge, Evidence(workspace.TransactionId, challenge.Nonce, target));
        File.WriteAllText(workspace.PublishedFile, "tampered");
        await Assert.ThrowsAsync<FileTreeException>(() => transaction.CommitAsync(approval));
        Assert.Equal("healthy", (await transaction.ReadAsync()).Phase);
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("instance")]
    [InlineData("host")]
    [InlineData("api")]
    [InlineData("root")]
    [InlineData("database")]
    [InlineData("mode")]
    [InlineData("migration")]
    [InlineData("module")]
    public async Task ChangedEvidenceCannotBePersisted(string change)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var target = Target();
        var switched = await SwitchAsync(workspace, transaction, target);
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        var evidence = Evidence(workspace.TransactionId, challenge.Nonce, target);
        evidence = change switch
        {
            "transaction" => evidence with { TransactionId = Guid.NewGuid().ToString("N") },
            "instance" => evidence with { InstanceId = "bad" },
            "host" => evidence with { HostVersion = "0.2.0" },
            "api" => evidence with { ApiVersion = "2.0" },
            "root" => evidence with { Database = evidence.Database with { RootId = Guid.NewGuid().ToString("N") } },
            "database" => evidence with { Database = evidence.Database with { DatabaseId = Guid.NewGuid().ToString("N") } },
            "mode" => evidence with { Database = evidence.Database with { BackendMode = "mock" } },
            "migration" => evidence with { Database = evidence.Database with { Migrations = target.Migrations.Skip(1).ToArray() } },
            _ => evidence with { Modules = [new("core", "0.2.0")] }
        };
        var journal = File.ReadAllBytes(workspace.Journal);
        await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.RecordHealthyAsync(challenge, evidence));
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
    }

    [Theory]
    [InlineData("missing-core")]
    [InlineData("migration-gap")]
    [InlineData("checksum")]
    [InlineData("api-range")]
    public async Task InvalidHealthTargetCannotPublishTransaction(string invalid)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var target = Target();
        target = invalid switch
        {
            "missing-core" => target with { Modules = [new("optional", "0.1.0")] },
            "migration-gap" => target with { Migrations = [target.Migrations[0], target.Migrations[1], target.Migrations[3], target.Migrations[3] with { Version = 5 }] },
            "checksum" => target with { Migrations = [target.Migrations[0] with { Checksum = "bad" }, .. target.Migrations.Skip(1)] },
            _ => target with { MaximumHostApiExclusive = new(1, 0) }
        };
        await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.PrepareForHealthAsync(workspace.TransactionId, workspace.PlanId,
            workspace.Previous, workspace.Candidate, target));
        Assert.False(File.Exists(workspace.Journal));
    }

    [Fact]
    public async Task CallerCollectionsAndChallengeCopiesCannotChangeFixedTarget()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var target = Target();
        var migrations = target.Migrations.ToArray();
        var modules = target.Modules.ToArray();
        var prepared = await transaction.PrepareForHealthAsync(workspace.TransactionId, workspace.PlanId,
            workspace.Previous, workspace.Candidate, target with { Migrations = migrations, Modules = modules });
        migrations[0] = migrations[0] with { Checksum = new string('0', 64) };
        modules[0] = new("core", "9.0.0");
        var switched = await transaction.SwitchPointerAsync(await transaction.RecordDataBackupAsync(prepared, InstallTransactionTests.Workspace.Backup));
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        var copy = challenge.Record;
        ((System.Collections.Generic.IList<InstallHealthModule>)copy.HealthTarget!.Modules)[0] = new("core", "8.0.0");
        var approval = await transaction.RecordHealthyAsync(challenge, Evidence(workspace.TransactionId, challenge.Nonce, target));
        Assert.Equal("0.1.0", Assert.Single(approval.Record.HealthTarget!.Modules).Version);
        Assert.Equal("committed", (await transaction.CommitAsync(approval)).Phase);
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("pointer")]
    public async Task ChangedInstallationAfterCheckCannotBeRecordedHealthy(string change)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var target = Target();
        var switched = await SwitchAsync(workspace, transaction, target);
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        if (change == "tree") File.WriteAllText(workspace.PublishedFile, "bad");
        else File.WriteAllText(workspace.Pointer, "{}");
        var journal = File.ReadAllBytes(workspace.Journal);
        await Assert.ThrowsAnyAsync<Exception>(() => transaction.RecordHealthyAsync(challenge,
            Evidence(workspace.TransactionId, challenge.Nonce, target)));
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChallengeAndApprovalRemainValidAfterSixtyOneSeconds(bool alreadyHealthy)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var clock = new HealthClock();
        using var transaction = new InstallTransaction(workspace.InstallRoot, clock);
        var target = Target();
        var switched = await SwitchAsync(workspace, transaction, target);
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        var evidence = Evidence(workspace.TransactionId, challenge.Nonce, target);
        var approval = alreadyHealthy ? await transaction.RecordHealthyAsync(challenge, evidence) : null;
        clock.Elapsed = TimeSpan.FromSeconds(61);
        approval ??= await transaction.RecordHealthyAsync(challenge, evidence);
        Assert.Equal("healthy", (await transaction.ReadAsync()).Phase);
        Assert.Equal("committed", (await transaction.CommitAsync(approval)).Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredChallengeOrApprovalDoesNotAdvanceLog(bool afterHealthy)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var clock = new HealthClock();
        using var transaction = new InstallTransaction(workspace.InstallRoot, clock);
        var target = Target();
        var switched = await SwitchAsync(workspace, transaction, target);
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        var evidence = Evidence(workspace.TransactionId, challenge.Nonce, target);
        InstallHealthApproval? approval = null;
        if (afterHealthy)
        {
            clock.Elapsed = TimeSpan.FromSeconds(61);
            approval = await transaction.RecordHealthyAsync(challenge, evidence);
        }
        var journal = File.ReadAllBytes(workspace.Journal);
        clock.Elapsed = TimeSpan.FromSeconds(120);
        var error = afterHealthy
            ? await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.CommitAsync(approval!))
            : await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.RecordHealthyAsync(challenge, evidence));
        Assert.Equal("update.healthExpired", error.Code);
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackwardsMonotonicTimeRejectsChallengeOrApprovalWithoutAdvancingLog(bool afterHealthy)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var clock = new HealthClock { Elapsed = TimeSpan.FromSeconds(20) };
        using var transaction = new InstallTransaction(workspace.InstallRoot, clock);
        var target = Target();
        var switched = await SwitchAsync(workspace, transaction, target);
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        var evidence = Evidence(workspace.TransactionId, challenge.Nonce, target);
        var approval = afterHealthy ? await transaction.RecordHealthyAsync(challenge, evidence) : null;
        var journal = File.ReadAllBytes(workspace.Journal);
        clock.Elapsed = TimeSpan.FromSeconds(19);
        var error = afterHealthy
            ? await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.CommitAsync(approval!))
            : await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.RecordHealthyAsync(challenge, evidence));
        Assert.Equal("update.healthExpired", error.Code);
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
    }

    [Fact]
    public async Task ReopenedHealthyTransactionRequiresFreshCheckAndDoesNotAcceptOldApproval()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var target = Target();
        InstallHealthApproval approval;
        using (var first = new InstallTransaction(workspace.InstallRoot))
        {
            var switched = await SwitchAsync(workspace, first, target);
            var challenge = await first.BeginHealthCheckAsync(switched);
            approval = await first.RecordHealthyAsync(challenge, Evidence(workspace.TransactionId, challenge.Nonce, target));
        }
        using var reopened = new InstallTransaction(workspace.InstallRoot);
        Assert.Equal(InstallRecoveryAction.RecheckCandidateHealth, (await reopened.AssessRecoveryAsync()).Action);
        await Assert.ThrowsAsync<UpdatePlanException>(() => reopened.CommitAsync(approval));
        var fresh = await reopened.BeginHealthCheckAsync(await reopened.ReadAsync());
        var second = await reopened.RecordHealthyAsync(fresh, Evidence(workspace.TransactionId, fresh.Nonce, target));
        Assert.Equal(2, second.Record.HealthChecks);
        Assert.Equal(6, (await reopened.CommitAsync(second)).Revision);
        File.Delete(workspace.PreviousFile);
        Assert.Equal(InstallRecoveryAction.Committed, (await reopened.AssessRecoveryAsync()).Action);
        var journal = File.ReadAllBytes(workspace.Journal);
        using var root = workspace.OpenData();
        var committed = await reopened.ReadAsync();
        await Assert.ThrowsAsync<UpdatePlanException>(() => reopened.RestorePreviousAsync(committed, root));
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
    }

    [Theory]
    [InlineData("Healthy", "healthy")]
    [InlineData("Committed", "committed")]
    public async Task KilledProcessRecoversHealthyOrCommittedWithoutRestoringBusinessData(string boundary, string phase)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using (var initial = new InstallTransaction(workspace.InstallRoot)) await initial.InitializePointerAsync(workspace.Previous);
        var target = Target();
        File.WriteAllText(Path.Combine(workspace.InstallRoot, "probe.json"), JsonSerializer.Serialize(new
        {
            workspace.TransactionId, workspace.PlanId, workspace.Previous, workspace.Candidate,
            Backup = InstallTransactionTests.Workspace.Backup, HealthTarget = target,
            Health = Evidence(workspace.TransactionId, new string('0', 64), target)
        }));
        using var child = workspace.StartProbe(boundary);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(workspace.Marker))
            {
                if (child.HasExited) throw new InvalidOperationException(await errors);
                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await Task.WhenAll(output, errors);
        }
        using var recovered = new InstallTransaction(workspace.InstallRoot);
        Assert.Equal(phase, (await recovered.ReadAsync()).Phase);
        Assert.Equal(boundary == "Healthy" ? InstallRecoveryAction.RecheckCandidateHealth : InstallRecoveryAction.Committed,
            (await recovered.AssessRecoveryAsync()).Action);
        Assert.Equal("new-host", File.ReadAllText(workspace.PublishedFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeCommitPreservesHealthyAndAfterCommitReturnsCommitted(bool after)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var cancel = new CancellationTokenSource();
        using var transaction = new InstallTransaction(workspace.InstallRoot, point =>
        {
            if (after && point == InstallPersistencePoint.Committed) cancel.Cancel();
            return Task.CompletedTask;
        });
        var target = Target();
        var switched = await SwitchAsync(workspace, transaction, target);
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        var approval = await transaction.RecordHealthyAsync(challenge, Evidence(workspace.TransactionId, challenge.Nonce, target));
        if (!after) cancel.Cancel();
        if (after) Assert.Equal("committed", (await transaction.CommitAsync(approval, cancel.Token)).Phase);
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.CommitAsync(approval, cancel.Token));
            Assert.Equal("healthy", (await transaction.ReadAsync()).Phase);
        }
    }

    private static async Task<InstallTransactionRecord> SwitchAsync(InstallTransactionTests.Workspace workspace,
        InstallTransaction transaction, InstallHealthTarget target)
    {
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await transaction.PrepareForHealthAsync(workspace.TransactionId, workspace.PlanId,
            workspace.Previous, workspace.Candidate, target);
        return await transaction.SwitchPointerAsync(await transaction.RecordDataBackupAsync(prepared, InstallTransactionTests.Workspace.Backup));
    }

    [Fact]
    public async Task HealthyRollbackPreservesEvidenceAndAdvancesRevisionsThroughDatabaseRestore()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        using var root = workspace.OpenData();
        DatabaseBackupDescriptor backup;
        DatabaseMigrationStamp[] migrations;
        await using (var database = SqliteStore.Open(root))
        {
            using var maintenance = database.Activities.TryEnterMaintenance("backup")!;
            backup = await database.CreateDatabaseBackupAsync(maintenance);
            migrations = backup.Migrations.ToArray();
        }
        var target = Target() with { RootId = backup.RootId, DatabaseId = backup.DatabaseId, Migrations = migrations };
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await transaction.PrepareForHealthAsync(workspace.TransactionId, workspace.PlanId,
            workspace.Previous, workspace.Candidate, target);
        var switched = await transaction.SwitchPointerAsync(await transaction.RecordDataBackupAsync(prepared, backup));
        var challenge = await transaction.BeginHealthCheckAsync(switched);
        await transaction.RecordHealthyAsync(challenge, Evidence(workspace.TransactionId, challenge.Nonce, target));
        var restored = await transaction.RestorePreviousAsync(await transaction.ReadAsync(), root);
        Assert.Equal("previousPointerRestored", restored.Phase);
        Assert.Equal(7, restored.Revision);
        Assert.Equal(1, restored.HealthChecks);
        Assert.NotNull(restored.Health);
        Assert.Equal(InstallRecoveryAction.PreviousVersionRestored, (await transaction.AssessRecoveryAsync()).Action);
        Assert.Equal(restored.Revision, (await transaction.RestorePreviousAsync(restored, root)).Revision);
    }

    private sealed class HealthClock : TimeProvider
    {
        public TimeSpan Elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }

    private static InstallHealthTarget Target() => new("0.1.0", new(1, 0), new(2, 0),
        new string('2', 32), new string('3', 32), "live", Enumerable.Range(1, 4)
            .Select(version => new DatabaseMigrationStamp("core", version, new string('A', 64))).ToArray(),
        [new("core", "0.1.0")]);

    private static InstallHealthEvidence Evidence(string transactionId, string challenge, InstallHealthTarget target) =>
        new(transactionId, challenge, new string('4', 32), target.HostVersion, "1.0",
            new(target.RootId, target.DatabaseId, target.BackendMode, target.Migrations), target.Modules);
}
