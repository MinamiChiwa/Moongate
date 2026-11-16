using System.Runtime.Versioning;

namespace MabiTool.Infrastructure.Updates;

internal sealed record UpdateHealthOffer(string TransactionId, string Challenge, long Revision,
    string ReleaseId, string ManifestSha256, InstallHealthTarget Target);
internal sealed record UpdateHealthAccepted(string InstanceId);
internal sealed record UpdateHealthRequest(string InstanceId);
internal sealed record UpdateCommitNotice(string TransactionId, string InstanceId, string ReleaseId,
    string ManifestSha256, long Revision);

[SupportedOSPlatform("windows")]
internal static class UpdateInstallHealthExchange
{
    internal static async Task<InstallTransactionRecord> RunUpdaterAsync(UpdateHandoffConnection connection,
        InstallTransaction installation, InstallTransactionRecord expected, CancellationToken token = default,
        UpdateHandoffProgress? progress = null)
    {
        var stage = "challenge";
        progress?.SetStage(stage);
        try
        {
            if (expected is null || connection.TransactionId != expected.TransactionId) throw UpdateHandoffProtocol.Invalid();
            var challenge = await installation.BeginHealthCheckAsync(expected, token, progress).ConfigureAwait(false);
            var record = challenge.Record;
            stage = "offer";
            progress?.SetStage(stage);
            await connection.SendAsync("offer", new UpdateHealthOffer(record.TransactionId, challenge.Nonce,
                record.Revision, record.Candidate.ReleaseId, record.Candidate.ManifestSha256, record.HealthTarget!), token).ConfigureAwait(false);
            stage = "accepted";
            progress?.SetStage(stage);
            var accepted = await connection.ReceiveAsync<UpdateHealthAccepted>("accepted", token).ConfigureAwait(false);
            if (!UpdateHandoffProtocol.Id(accepted.InstanceId)) throw UpdateHandoffProtocol.Invalid();
            await connection.SendAsync("healthCheck", new UpdateHealthRequest(accepted.InstanceId), token).ConfigureAwait(false);
            stage = "healthResult";
            progress?.SetStage(stage);
            var evidence = await connection.ReceiveAsync<InstallHealthEvidence>("healthResult", token).ConfigureAwait(false);
            if (evidence.InstanceId != accepted.InstanceId) throw UpdateHandoffProtocol.Invalid();
            stage = "recordHealthy";
            progress?.SetStage(stage);
            var approval = await installation.RecordHealthyAsync(challenge, evidence, token, progress).ConfigureAwait(false);
            stage = "commitPersistence";
            progress?.SetStage(stage);
            var committed = await installation.CommitAsync(approval, token, progress).ConfigureAwait(false);
            stage = "commit";
            progress?.SetStage(stage);
            var notice = new UpdateCommitNotice(committed.TransactionId, accepted.InstanceId,
                committed.Candidate.ReleaseId, committed.Candidate.ManifestSha256, committed.Revision);
            await connection.SendAsync("commit", notice, token).ConfigureAwait(false);
            stage = "committed";
            progress?.SetStage(stage);
            if (await connection.ReceiveAsync<UpdateCommitNotice>("committed", token).ConfigureAwait(false) != notice)
                throw UpdateHandoffProtocol.Invalid();
            return committed;
        }
        catch (Exception error)
        {
            if (progress is null) error.Data["handoffStage"] = stage;
            else progress.Freeze().Apply(error);
            throw;
        }
        finally { connection.Dispose(); }
    }
}
