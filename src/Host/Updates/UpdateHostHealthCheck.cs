using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Updates;

internal sealed record UpdateHostHealthTarget(string TransactionId, string HostVersion,
    Version MinimumHostApi, Version MaximumHostApiExclusive, string RootId, string DatabaseId,
    string BackendMode, IReadOnlyList<DatabaseMigrationStamp> Migrations, IReadOnlyList<InstallHealthModule> Modules);
internal sealed record UpdateHostHealthResult(string TransactionId, string InstanceId, string HostVersion,
    string ApiVersion, DatabaseHealthResult Database, IReadOnlyList<InstallHealthModule> Modules);
internal sealed class UpdateHostHealthException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>维护屏障内的宿主检查步骤；结果由后续认证握手和安装事务消费。</summary>
internal sealed class UpdateHostHealthCheck(SqliteStore database, ModuleRuntime modules, ModuleCatalog catalog,
    SystemInfo system, IHostApplicationLifetime lifetime, TimeProvider time)
{
    internal async Task<InstallHealthEvidence> VerifyInstallationAsync(ActivityBarrier.MaintenanceLease maintenance,
        InstallHealthChallenge challenge, CancellationToken token = default)
    {
        if (challenge is null) throw new UpdateHostHealthException("update.hostHealthTargetInvalid");
        var record = challenge.Record;
        var target = record.HealthTarget ?? throw new UpdateHostHealthException("update.hostHealthTargetInvalid");
        var result = await VerifyAsync(maintenance, new(record.TransactionId, target.HostVersion,
            target.MinimumHostApi, target.MaximumHostApiExclusive, target.RootId, target.DatabaseId,
            target.BackendMode, target.Migrations, target.Modules), token).ConfigureAwait(false);
        return new(result.TransactionId, challenge.Nonce, result.InstanceId, result.HostVersion, result.ApiVersion, result.Database, result.Modules);
    }

    internal async Task<UpdateHostHealthResult> VerifyAsync(ActivityBarrier.MaintenanceLease maintenance,
        UpdateHostHealthTarget target, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var frozen = Freeze(target);
        using var activity = database.Activities.EnterMaintenanceActivity(maintenance, "update.hostHealth");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token, lifetime.ApplicationStopping);
        var ct = cancellation.Token;
        var demands = new List<IAsyncDisposable>();
        DatabaseHealthResult data;
        try
        {
            try
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
                await started.Task.WaitAsync(ct).ConfigureAwait(false);
                if (system.Version != frozen.HostVersion || !Version.TryParse(system.ApiVersion, out var api) ||
                    api < frozen.MinimumHostApi || api >= frozen.MaximumHostApiExclusive || api != catalog.HostApi)
                    throw new UpdateHostHealthException("update.hostHealthMismatch");

                // 在初始化任何模块前核对整个目标，不因次序先启动一部分错误版本。
                var snapshot = await modules.SnapshotAsync(ct).ConfigureAwait(false);
                foreach (var expected in frozen.Modules)
                {
                    var actual = snapshot.Items.SingleOrDefault(item => item.Id == expected.Id);
                    if (actual is null || actual.Version != expected.Version || !actual.Enabled || actual.Problems.Count != 0)
                        throw new UpdateHostHealthException("update.moduleHealthMismatch");
                }
                demands.Add(await modules.AcquireManyAsync(frozen.Modules.Select(item => item.Id).ToArray(),
                    "update-health-" + frozen.TransactionId, ct).ConfigureAwait(false));

                data = await database.VerifyDatabaseHealthAsync(maintenance, frozen.DatabaseId,
                    frozen.BackendMode, frozen.Migrations, ct).ConfigureAwait(false);
                if (data.RootId != frozen.RootId) throw new UpdateHostHealthException("update.hostHealthMismatch");
            }
            finally
            {
                // 模块工厂即使延迟响应取消，也须结束后才释放维护子活动。
                for (var index = demands.Count - 1; index >= 0; index--)
                    await demands[index].DisposeAsync().ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            // 外层维护权提前释放时，子活动继续阻挡业务，但不能再产生成功结果。
            using var finalOwnership = database.Activities.EnterMaintenanceActivity(maintenance, "update.hostHealthResult");
            return new(frozen.TransactionId, system.InstanceId, system.Version, system.ApiVersion, data, frozen.Modules);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested &&
            !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            throw new UpdateHostHealthException("update.hostHealthTimeout");
        }
    }

    private static UpdateHostHealthTarget Freeze(UpdateHostHealthTarget target)
    {
        if (target is null || !Id(target.TransactionId) || !Id(target.RootId) || !Id(target.DatabaseId) ||
            !Version.TryParse(target.HostVersion, out _) || target.MinimumHostApi is null || target.MaximumHostApiExclusive is null ||
            target.MinimumHostApi >= target.MaximumHostApiExclusive || target.BackendMode is not ("live" or "mock") ||
            target.Migrations is not { Count: >= 4 and <= 4096 } || target.Modules is not { Count: > 0 and <= 128 })
            throw new UpdateHostHealthException("update.hostHealthTargetInvalid");
        var migrations = target.Migrations.ToArray();
        var required = target.Modules.ToArray();
        if (migrations.Any(item => item is null) || required.Any(item => item is null || item.Id is null ||
                !Regex.IsMatch(item.Id, @"\A[a-z][a-z0-9-]{0,63}\z") || !Version.TryParse(item.Version, out _)) ||
            required.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != required.Length ||
            required.All(item => item.Id != "core"))
            throw new UpdateHostHealthException("update.hostHealthTargetInvalid");
        return target with { Migrations = Array.AsReadOnly(migrations), Modules = Array.AsReadOnly(required) };
    }

    private static bool Id(string? value) => value is { Length: 32 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
