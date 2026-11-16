using System.Text.Json;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;

internal static class InstallTransactionProbe
{
    internal static async Task<int> RollbackAsync(string installationRoot, string pauseAt, string marker)
    {
        var root = Path.GetDirectoryName(installationRoot)!;
        using var lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), false, Path.Combine(root, "data")));
        using var transaction = new InstallTransaction(installationRoot, async point =>
        {
            if (point.ToString() != pauseAt) return;
            await File.WriteAllTextAsync(marker, pauseAt);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        });
        await transaction.RestorePreviousAsync(await transaction.ReadAsync(), lease);
        return 0;
    }

    // 故障注入仅编译进测试探针，进入刷盘后的实际持久化边界后等待外部终止。
    internal static async Task<int> RunAsync(string installationRoot, string pauseAt, string marker)
    {
        var input = JsonSerializer.Deserialize<Input>(await File.ReadAllTextAsync(Path.Combine(installationRoot, "probe.json")))!;
        using var transaction = new InstallTransaction(installationRoot, async point =>
        {
            if (point.ToString() != pauseAt) return;
            await File.WriteAllTextAsync(marker, pauseAt);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        });
        var prepared = input.HealthTarget is null
            ? await transaction.PrepareAsync(input.TransactionId, input.PlanId, input.Previous, input.Candidate)
            : await transaction.PrepareForHealthAsync(input.TransactionId, input.PlanId, input.Previous, input.Candidate, input.HealthTarget);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, input.Backup);
        var switched = await transaction.SwitchPointerAsync(backedUp);
        if (input.HealthTarget is not null)
        {
            var challenge = await transaction.BeginHealthCheckAsync(switched);
            var approval = await transaction.RecordHealthyAsync(challenge, input.Health! with { Challenge = challenge.Nonce });
            await transaction.CommitAsync(approval);
        }
        return 0;
    }

    private sealed record Input(string TransactionId, string PlanId, InstallVersion Previous, InstallVersion Candidate,
        DatabaseBackupDescriptor Backup, InstallHealthTarget? HealthTarget = null, InstallHealthEvidence? Health = null);
}
