using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Updates;

/// <summary>认证连接上的单次健康交换；提交落盘先于通知，双方均不在此释放维护权。</summary>
[SupportedOSPlatform("windows")]
internal static class UpdateHandoffHealthSession
{
    internal static Task<InstallTransactionRecord> RunUpdaterAsync(UpdateHandoffConnection connection,
        InstallTransaction installation, InstallTransactionRecord expected, CancellationToken token = default)
        => UpdateInstallHealthExchange.RunUpdaterAsync(connection, installation, expected, token);

    // 返回的是提交通知；候选启动协调器仍须核验持久日志后再决定业务准入。
    internal static async Task<UpdateCommitNotice> RunCandidateAsync(UpdateHandoffConnection connection,
        UpdateHostHealthCheck health, ActivityBarrier.MaintenanceLease maintenance, SystemInfo system,
        CancellationToken token = default)
    {
        var stage = "offer";
        try
        {
            var offer = await connection.ReceiveAsync<UpdateHealthOffer>("offer", token).ConfigureAwait(false);
            if (offer.TransactionId != connection.TransactionId || !Hash(offer.Challenge) || offer.Revision is < 1 or > long.MaxValue - 2 ||
                string.IsNullOrWhiteSpace(offer.ReleaseId) || !Hash(offer.ManifestSha256) || offer.Target is null) throw Invalid();
            await connection.SendAsync("accepted", new UpdateHealthAccepted(system.InstanceId), token).ConfigureAwait(false);
            stage = "healthCheck";
            var request = await connection.ReceiveAsync<UpdateHealthRequest>("healthCheck", token).ConfigureAwait(false);
            if (request.InstanceId != system.InstanceId) throw Invalid();
            var target = offer.Target;
            stage = "hostHealth";
            var result = await health.VerifyAsync(maintenance, new(offer.TransactionId, target.HostVersion,
                target.MinimumHostApi, target.MaximumHostApiExclusive, target.RootId, target.DatabaseId,
                target.BackendMode, target.Migrations, target.Modules), token).ConfigureAwait(false);
            var evidence = new InstallHealthEvidence(result.TransactionId, offer.Challenge, result.InstanceId,
                result.HostVersion, result.ApiVersion, result.Database, result.Modules);
            await connection.SendAsync("healthResult", evidence, token).ConfigureAwait(false);
            stage = "commit";
            var notice = await connection.ReceiveAsync<UpdateCommitNotice>("commit", token).ConfigureAwait(false);
            if (notice != new UpdateCommitNotice(offer.TransactionId, system.InstanceId, offer.ReleaseId,
                offer.ManifestSha256, offer.Revision + 2)) throw Invalid();
            await connection.SendAsync("committed", notice, token).ConfigureAwait(false);
            return notice;
        }
        catch (Exception error) { error.Data["handoffStage"] = stage; throw; }
        finally { connection.Dispose(); }
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static UpdatePlanException Invalid() => UpdateHandoffProtocol.Invalid();
}
