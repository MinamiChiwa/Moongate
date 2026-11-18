using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Storage;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.PortableUpdate;
using MabiTool.Providers.GitHubReleases;

namespace MabiTool.Host.Updates;

/// <param name="SizeBytes">完整包字节数。</param>
/// <param name="DownloadKind"><c>incremental</c>（增量包）或 <c>full</c>（完整包）。</param>
/// <param name="DownloadBytes">本次需要下载的字节数。</param>
public sealed record UpdateCheckResult(bool UpdateAvailable, string ReleaseId, string ManifestSha256,
    string? PlanId, DateTimeOffset? PlanExpiresAt, UpdateSourceKind Source, long? SizeBytes = null,
    string? DownloadKind = null, long? DownloadBytes = null);

public interface IUpdateCheckService
{
    bool IsConfigured { get; }
    Task<UpdateCheckResult> CheckAsync(CancellationToken token);
}

public interface IUpdateDownloadService
{
    Task<UpdatePlan> ReadPlanAsync(string planId, CancellationToken token = default);
    Task<UpdateDownloadStatus> DownloadAsync(UpdatePlan frozenPlan,
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token = default);
}

public interface IUpdateInstallService
{
    Task<bool> TransferAsync(string operationId, UpdatePlan plan, ActivityBarrier.MaintenanceLease maintenance, CancellationToken token);
    void RequestShutdown();
}

/// <summary>正在运行的解压版：包根、工具根与包内发行身份。只有目录包布局且带发行身份时才启用更新。</summary>
public sealed class PortableInstallation
{
    internal PortableInstallation(string packageRoot, string toolRoot, ReleaseIdentity identity)
    {
        PackageRoot = packageRoot; ToolRoot = toolRoot; Identity = identity;
    }

    internal string PackageRoot { get; }
    internal string ToolRoot { get; }
    internal ReleaseIdentity Identity { get; }

    internal static PortableInstallation? Detect(string programDirectory)
    {
        if (MabiTool.ModuleSdk.ToolRoot.PackageRoot(programDirectory) is not { } package) return null;
        try { return ReleaseIdentity.Read(package) is { } identity ? new PortableInstallation(package, MabiTool.ModuleSdk.ToolRoot.FromProgramDirectory(programDirectory), identity) : null; }
        catch (InvalidDataException) { return null; }
    }
}

/// <summary>
/// 解压版更新：检查 GitHub Releases 的最新发行；有与本机版本对应的增量包时只下载增量包，用本机文件与增量内容构建新包根并按清单核对，
/// 否则（或增量失败时）下载整包 zip 并核对 SHA-256，解压到工具根暂存目录并核对发行身份；
/// 安装时在维护屏障内备份数据库、写事务日志、启动替换器后交出控制。替换、健康确认与回退由替换器完成。
/// </summary>
internal sealed class PortableUpdateService(PortableInstallation? installation, GitHubReleaseSource source, SqliteStore database,
    OperationStore operations, Func<UpdatePolicy> readPolicy, ActivityBarrier barrier, IHostApplicationLifetime host,
    MaintenanceShutdownContext shutdown, TimeProvider clock, Process? owner = null, Func<HttpMessageHandler>? createHandler = null,
    Func<string, long>? freeSpace = null) : IUpdateCheckService, IUpdateDownloadService, IUpdateInstallService
{
    internal static readonly TimeSpan PlanLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan ReplacerStartup = TimeSpan.FromSeconds(15);
    private readonly Func<string, long> availableBytes = freeSpace ??
        (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);

    public bool IsConfigured => installation is not null;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        var current = Require();
        var installed = ReleaseVersion.Parse(current.Identity.DisplayVersion);
        var candidate = await source.FindLatestAsync(current.Identity.Repository, current.Identity.Channel == "preview", token).ConfigureAwait(false);
        // 只提供比当前版本新的发行；仓库里的最新发行更旧（例如撤回了某个版本）时视为已是最新，不提供降级。
        if (candidate is null || candidate.Version.CompareTo(installed) <= 0)
            return new(false, installed.ToString(), "", null, null, candidate?.Source ?? UpdateSourceKind.GithubDirect);
        var sha256 = await source.FetchChecksumAsync(candidate, token).ConfigureAwait(false);
        var version = candidate.Version;
        // 清单可选：缺失、无效，或与 .sha256、API 报告的长度不一致时按完整包处理。
        var manifestBytes = await source.FetchManifestAsync(candidate, token).ConfigureAwait(false);
        UpdateManifest? manifest = null;
        if (manifestBytes is not null)
        {
            try { manifest = UpdateManifest.Parse(manifestBytes, version.ToString(), candidate.AssetName, sha256); }
            catch (UpdatePackageException) { }
            if (manifest is not null && candidate.AssetLength is { } listed && listed != manifest.Package.Size) manifest = null;
        }
        var length = manifest?.Package.Size ?? candidate.AssetLength ?? await source.FetchLengthAsync(candidate, token).ConfigureAwait(false);
        var choice = manifest is null ? null : await DeltaUpdate.ChooseAsync(manifest, current.PackageRoot, current.Identity.DisplayVersion,
            new UpdateIoBudget(null, barrier), token).ConfigureAwait(false);
        UpdatePlanDelta? delta = null;
        if (choice is not null)
        {
            var manifestSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestBytes!)).ToLowerInvariant();
            await UpdatePlanStore.SaveManifestAsync(current.ToolRoot, manifestSha, manifestBytes!, token).ConfigureAwait(false);
            delta = new(choice.Delta.Name, choice.Delta.Size, choice.Delta.Sha256,
                candidate.Endpoints(choice.Delta.Name).Select(item => new UpdatePlanSource(item.Source.ToString(), item.Uri)).ToArray(),
                manifestSha, choice.ReusedFiles, choice.DownloadedFiles, choice.DeletedFiles);
        }
        var now = clock.GetUtcNow();
        var plan = new UpdatePlan(1, Guid.NewGuid().ToString("N"), $"{version.Major}.{version.Minor}.{version.Patch}", version.ToString(),
            version.IsPrerelease ? "preview" : "stable", candidate.Tag, candidate.AssetName, length, sha256,
            candidate.Package.Select(item => new UpdatePlanSource(item.Source.ToString(), item.Uri)).ToArray(),
            candidate.Source.ToString(), now, now + PlanLifetime, delta);
        await UpdatePlanStore.SaveAsync(current.ToolRoot, plan, token).ConfigureAwait(false);
        await UpdatePlanStore.PruneManifestsAsync(current.ToolRoot, now, token).ConfigureAwait(false);
        return new(true, plan.DisplayVersion, plan.Sha256, plan.PlanId, plan.ExpiresAt, candidate.Source, plan.AssetLength,
            delta is null ? "full" : "incremental", delta?.AssetLength ?? plan.AssetLength);
    }

    public Task<UpdatePlan> ReadPlanAsync(string planId, CancellationToken token = default) =>
        UpdatePlanStore.ReadAsync(Require().ToolRoot, planId, token);

    public async Task<UpdateDownloadStatus> DownloadAsync(UpdatePlan frozenPlan,
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token = default)
    {
        var current = Require();
        using var activity = barrier.EnterActivity("platform-updates", "update.staging");
        var plan = await FrozenAsync(current, frozenPlan, token).ConfigureAwait(false);
        var staged = UpdateLayout.StagedPackage(current.ToolRoot, plan.PlanId);
        if (StagedMatches(staged, plan))
            return Status(plan, plan.AssetLength, reusedStage: true) with { TotalBytes = new((ulong)plan.AssetLength) };
        if (plan.Delta is { } delta)
        {
            try { return await DownloadDeltaAsync(current, plan, delta, report, token).ConfigureAwait(false); }
            catch (Exception error) when (!token.IsCancellationRequested && DeltaFallback(error)) { }
        }
        await report("preparing", Status(plan, 0), token).ConfigureAwait(false);
        // zip、解压出的包与暂存期间的旧版本同在工具根所在的卷上，按 zip 的三倍预留。
        if (availableBytes(current.ToolRoot) < plan.AssetLength * 3) throw new UpdatePackageException("update.diskSpaceInsufficient");
        var archive = ResumableAssetDownloader.CachePath(UpdateLayout.Downloads(current.ToolRoot), plan.Sha256);
        var downloaded = await DownloadAssetAsync(plan, plan.Sources, archive, plan.AssetLength, plan.Sha256, report, token).ConfigureAwait(false);
        var verified = Status(plan, plan.AssetLength, source: downloaded.SourceId);
        await report("verifying", verified, token).ConfigureAwait(false);
        await PortableUpdatePackage.ExtractAsync(archive, current.ToolRoot, plan.PlanId, plan.DisplayVersion, token).ConfigureAwait(false);
        // 解压成功后 zip 不再需要；暂存目录就是安装的输入。
        File.Delete(archive);
        return verified with { TotalBytes = new((ulong)plan.AssetLength), DownloadedFiles = 1 };
    }

    /// <summary>增量下载：取得增量包并核对，用本机文件与增量内容构建新包根，按清单逐文件核对并核对发行身份。</summary>
    private async Task<UpdateDownloadStatus> DownloadDeltaAsync(PortableInstallation current, UpdatePlan plan, UpdatePlanDelta delta,
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token)
    {
        var bytes = await UpdatePlanStore.ReadManifestAsync(current.ToolRoot, delta.ManifestSha256, token).ConfigureAwait(false)
            ?? throw new UpdatePackageException("update.manifestInvalid");
        var manifest = UpdateManifest.Parse(bytes, plan.DisplayVersion, plan.AssetName, plan.Sha256);
        await report("preparing", Status(plan, 0, total: delta.AssetLength), token).ConfigureAwait(false);
        // 增量包与构建出的新包根同在工具根所在的卷上。
        if (availableBytes(current.ToolRoot) < delta.AssetLength + manifest.TotalBytes) throw new UpdatePackageException("update.diskSpaceInsufficient");
        var archive = ResumableAssetDownloader.CachePath(UpdateLayout.Downloads(current.ToolRoot), delta.Sha256);
        try
        {
            var downloaded = await DownloadAssetAsync(plan, delta.Sources, archive, delta.AssetLength, delta.Sha256, report, token).ConfigureAwait(false);
            var verified = Status(plan, delta.AssetLength, source: downloaded.SourceId, total: delta.AssetLength);
            await report("verifying", verified, token).ConfigureAwait(false);
            await DeltaUpdate.StageAsync(manifest, archive, current.PackageRoot, current.ToolRoot, plan.PlanId,
                new UpdateIoBudget(null, barrier), token).ConfigureAwait(false);
            return verified with { TotalBytes = new((ulong)delta.AssetLength), ReusedFiles = delta.ReusedFiles, DownloadedFiles = delta.DownloadedFiles };
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }

    // 磁盘不足与计划失效时改用完整包也无济于事；其余增量失败（增量包 404、下载失败、缺内容、校验不过）改用完整包。
    private static bool DeltaFallback(Exception error) => error switch
    {
        AssetDownloadException download => download.Code != "update.diskSpaceInsufficient",
        UpdatePackageException package => package.Code is not ("update.diskSpaceInsufficient" or "update.planStale" or "update.planInvalid"),
        IOException io => !UpdatePaths.IsDiskFull(io),
        InvalidDataException => true,
        _ => false
    };

    private async Task<AssetDownloadResult> DownloadAssetAsync(UpdatePlan plan, IReadOnlyList<UpdatePlanSource> sources, string path,
        long length, string sha256, Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token)
    {
        var resumed = File.Exists(path + ".part") ? new FileInfo(path + ".part").Length : 0;
        var budget = new UpdateIoBudget(readPolicy().MaximumBytesPerSecond, barrier);
        using var downloader = new ResumableAssetDownloader(createHandler?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false },
            new() { Concurrency = 1 }, availableBytes, budget: budget);
        using var progressStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var progress = ReportProgressAsync(plan, length, () => Math.Min(length, resumed + budget.DownloadedBytes), report, progressStop.Token);
        try
        {
            return await downloader.DownloadAsync(new(sources.Select(item => new AssetDownloadSource(item.Id, item.Uri)).ToArray(),
                path, length, sha256), token).ConfigureAwait(false);
        }
        finally
        {
            progressStop.Cancel();
            try { await progress.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    public async Task<bool> TransferAsync(string operationId, UpdatePlan frozenPlan, ActivityBarrier.MaintenanceLease maintenance, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new UpdatePackageException("update.platformUnsupported");
        var current = Require();
        using var activity = database.Activities.EnterMaintenanceActivity(maintenance, "update.handoff");
        var plan = await FrozenAsync(current, frozenPlan, token).ConfigureAwait(false);
        var root = current.ToolRoot;
        var staged = UpdateLayout.StagedPackage(root, plan.PlanId);
        if (!StagedMatches(staged, plan)) throw new UpdatePackageException("update.stageRequired");
        if (UpdateJournal.Read(root) is { IsFinal: false }) throw new UpdatePackageException("update.recoveryRequired");
        // 上一次安装留下的旧版本与事务文件只保留到这一次安装开始。
        foreach (var stale in new[] { Path.Combine(UpdateLayout.Root(root), "previous"), Path.Combine(UpdateLayout.Root(root), "transactions") })
            UpdatePaths.DeleteOwnedTree(stale);
        var backup = await database.CreateDatabaseBackupAsync(maintenance, token).ConfigureAwait(false);
        var transaction = UpdateLayout.Transaction(root, plan.PlanId);
        Directory.CreateDirectory(Path.GetDirectoryName(UpdateLayout.Replacer(root, plan.PlanId))!);
        await File.WriteAllBytesAsync(UpdateLayout.DatabaseBackup(root, plan.PlanId),
            JsonSerializer.SerializeToUtf8Bytes(backup, new JsonSerializerOptions(JsonSerializerDefaults.Web)), token).ConfigureAwait(false);
        File.Copy(Path.Combine(staged, ReleaseIdentity.EntryName), UpdateLayout.Replacer(root, plan.PlanId), overwrite: true);
        using var self = Process.GetCurrentProcess();
        var processes = new List<KeyValuePair<int, long>> { new(self.Id, self.StartTime.ToUniversalTime().Ticks) };
        if (owner is { HasExited: false }) processes.Add(new(owner.Id, owner.StartTime.ToUniversalTime().Ticks));
        var journal = UpdateJournal.Create(root, plan.PlanId, current.Identity.DisplayVersion, plan.DisplayVersion, operationId, backup.BackupId, processes);
        await File.WriteAllTextAsync(UpdateLayout.Restarting(root), plan.PlanId, token).ConfigureAwait(false);
        Process? replacer = null;
        try
        {
            var start = new ProcessStartInfo(UpdateLayout.Replacer(root, plan.PlanId)) { UseShellExecute = false, WorkingDirectory = transaction };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(plan.PlanId);
            start.ArgumentList.Add(current.PackageRoot);
            replacer = Process.Start(start) ?? throw new UpdatePackageException("update.updaterUnavailable");
            var started = Stopwatch.StartNew();
            while (!UpdateLayout.ReplacerActive(root))
            {
                if (replacer.HasExited || started.Elapsed > ReplacerStartup) throw new UpdatePackageException("update.updaterUnavailable");
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is UpdatePackageException or System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        {
            if (replacer is { HasExited: false }) replacer.Kill();
            journal.Append(UpdateJournal.Aborted, "update.updaterUnavailable");
            File.Delete(UpdateLayout.Restarting(root));
            throw error is UpdatePackageException ? error : new UpdatePackageException("update.updaterUnavailable", error.Message);
        }
        finally { replacer?.Dispose(); }
        await operations.FinishUpdateHandoffAsync(operationId, JsonSerializer.SerializeToElement(
            new { transactionId = plan.PlanId, releaseId = plan.DisplayVersion, recoverySnapshotId = backup.BackupId }, ContractJson.Options), token).ConfigureAwait(false);
        shutdown.Publish(maintenance);
        return true;
    }

    public void RequestShutdown() => host.StopApplication();

    private PortableInstallation Require() => installation ?? throw new UpdatePackageException("update.sourceNotConfigured");

    private static async Task<UpdatePlan> FrozenAsync(PortableInstallation current, UpdatePlan frozen, CancellationToken token)
    {
        var plan = await UpdatePlanStore.ReadAsync(current.ToolRoot, frozen.PlanId, token).ConfigureAwait(false);
        if (JsonSerializer.Serialize(plan, ContractJson.Options) != JsonSerializer.Serialize(frozen, ContractJson.Options))
            throw new UpdatePackageException("update.planStale");
        return plan;
    }

    private static bool StagedMatches(string staged, UpdatePlan plan)
    {
        try { return ReleaseIdentity.Read(staged) is { } identity && identity.DisplayVersion == plan.DisplayVersion && identity.Verify(staged) is null; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    private async Task ReportProgressAsync(UpdatePlan plan, long total, Func<long> bytes,
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), clock);
        await report("downloading", Status(plan, bytes(), total: total), token).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            await report("downloading", Status(plan, bytes(), total: total), token).ConfigureAwait(false);
    }

    private static UpdateDownloadStatus Status(UpdatePlan plan, long verified, bool reusedStage = false, string? source = null, long? total = null)
    {
        var length = new UInt64String((ulong)(total ?? plan.AssetLength));
        return new(plan.PlanId, plan.DisplayVersion, plan.Sha256, length, length, new(0), new((ulong)plan.AssetLength * 3), 0, 0, reusedStage,
            new((ulong)verified), length, source is null ? null : Enum.TryParse<UpdateSourceKind>(source, out var kind) ? kind : new OpenEnum<UpdateSourceKind>(source));
    }
}
