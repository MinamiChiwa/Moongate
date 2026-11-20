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
    /// <summary>检查之后补上保留计划的下载方式与大小（本机比对）；与检查、下载队列分开运行。</summary>
    Task EstimateAsync(CancellationToken token) => Task.CompletedTask;
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
/// 解压版更新：检查 GitHub Releases 的最新发行；按文件清单分段下载本机缺的内容，用本机文件与增量内容构建新包根并按清单核对，
/// 否则（或增量失败时）下载整包 zip 并核对 SHA-256，解压到工具根暂存目录并核对发行身份；
/// 安装时在维护屏障内备份数据库、写事务日志、启动替换器后交出控制。替换、健康确认与回退由替换器完成。
/// </summary>
internal sealed class PortableUpdateService(PortableInstallation? installation, GitHubReleaseSource source, SqliteStore database,
    OperationStore operations, Func<UpdatePolicy> readPolicy, ActivityBarrier barrier, IHostApplicationLifetime host,
    MaintenanceShutdownContext shutdown, TimeProvider clock, Process? owner = null, Func<HttpMessageHandler>? createHandler = null,
    Func<string, long>? freeSpace = null) : IUpdateCheckService, IUpdateDownloadService, IUpdateInstallService
{
    internal static readonly TimeSpan PlanLifetime = TimeSpan.FromDays(7);
    /// <summary>
    /// 等待替换器就绪的时限。新版 Moongate.exe 没有代码签名，杀毒软件首次运行前会扫描，慢机器上可达数十秒；
    /// 超时或替换器提前退出时结束它并重启一次（扫描结果已缓存，第二次通常很快）。
    /// </summary>
    internal static readonly TimeSpan ReplacerStartup = TimeSpan.FromSeconds(60);
    private const int ReplacerAttempts = 2;
    private readonly Func<string, long> availableBytes = freeSpace ?? AvailableBytes;

    /// <summary>
    /// 工具根所在卷的可用字节：按目录查询（网络共享、挂载到文件夹的卷都按实际所在卷计算）。
    /// 查询不到时报 <c>update.storageUnavailable</c> 并写明原因，不抛出未处理异常。
    /// </summary>
    internal static long AvailableBytes(string path)
    {
        var directory = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(directory)) directory = Path.GetDirectoryName(directory) ?? directory;
            if (GetDiskFreeSpaceEx(directory.EndsWith('\\') ? directory : directory + "\\", out var available, out _, out _)) return (long)Math.Min(available, long.MaxValue);
            throw new UpdatePackageException("update.storageUnavailable",
                $"无法读取“{directory}”所在磁盘的剩余空间（系统错误 {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}）");
        }
        try { return new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { throw new UpdatePackageException("update.storageUnavailable", "无法读取工具根所在磁盘的剩余空间：" + error.Message); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

    public bool IsConfigured => installation is not null;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        var current = Require();
        var installed = ReleaseVersion.Parse(current.Identity.DisplayVersion);
        var candidate = await source.FindLatestAsync(current.Identity.Repository, current.Identity.Channel == "preview", token).ConfigureAwait(false);
        // 只提供比当前版本新的发行；仓库里的最新发行更旧（例如撤回了某个版本）时视为已是最新，不提供降级。
        if (candidate is null || candidate.Version.CompareTo(installed) <= 0)
        {
            await RetainAsync(current, null, token).ConfigureAwait(false);
            return new(false, installed.ToString(), "", null, null, candidate?.Source ?? UpdateSourceKind.GithubDirect);
        }
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
        // 检查只做版本发现：清单记有数据区时只保存清单，本机比对与下载大小由 EstimateAsync 在后台补上（活动期间暂停也不影响检查结论）。
        UpdatePlanRanges? ranges = null;
        if (manifest is { Locates: true })
        {
            var manifestSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestBytes!)).ToLowerInvariant();
            await UpdatePlanStore.SaveManifestAsync(current.ToolRoot, manifestSha, manifestBytes!, token).ConfigureAwait(false);
            ranges = new(manifestSha);
        }
        var now = clock.GetUtcNow();
        var plan = new UpdatePlan(1, Guid.NewGuid().ToString("N"), $"{version.Major}.{version.Minor}.{version.Patch}", version.ToString(),
            version.IsPrerelease ? "preview" : "stable", candidate.Tag, candidate.AssetName, length, sha256,
            candidate.Package.Select(item => new UpdatePlanSource(item.Source.ToString(), item.Uri)).ToArray(),
            candidate.Source.ToString(), now, now + PlanLifetime, ranges);
        // 同一发行再次检查时沿用未过期的原计划：已暂存的“可以安装”与下载缓存里的续传进度保持有效。
        if (await SamePlanAsync(current, plan, now, token).ConfigureAwait(false) is { } saved) plan = saved;
        else await UpdatePlanStore.SaveAsync(current.ToolRoot, plan, token).ConfigureAwait(false);
        await RetainAsync(current, plan.PlanId, token).ConfigureAwait(false);
        // 不能分段时下载方式已知（完整包）；能分段时大小待后台比对后给出，此前不写大小。
        return new(true, plan.DisplayVersion, plan.Sha256, plan.PlanId, plan.ExpiresAt, candidate.Source, plan.AssetLength,
            plan.Ranges is null ? "full" : null, plan.Ranges is null ? plan.AssetLength : null);
    }

    /// <summary>
    /// 为保留的计划补上下载方式：比对本机文件得出按需分段的区间字节合计，缺的超过 80% 时为完整包。本机哈希在游戏资源与房间准备活动期间暂停，
    /// 只推迟这一步；已有估算或计划不能分段时不做。
    /// </summary>
    public async Task EstimateAsync(CancellationToken token)
    {
        if (installation is not { } current) return;
        var directory = UpdateLayout.Plans(current.ToolRoot);
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!UpdateLayout.IsTransactionId(id) || UpdatePlanStore.ReadEstimate(current.ToolRoot, id) is not null) continue;
            UpdatePlan plan;
            try { plan = await UpdatePlanStore.ReadAsync(current.ToolRoot, id, token).ConfigureAwait(false); }
            catch (Exception error) when (error is UpdatePackageException or OperationException or IOException) { continue; }
            if (plan.Ranges is not { } ranges || plan.ExpiresAt <= clock.GetUtcNow()) continue;
            var bytes = await UpdatePlanStore.ReadManifestAsync(current.ToolRoot, ranges.ManifestSha256, token).ConfigureAwait(false);
            if (bytes is null) continue;
            var manifest = UpdateManifest.Parse(bytes, plan.DisplayVersion, plan.AssetName, plan.Sha256);
            var choice = await DeltaUpdate.ChooseAsync(manifest, current.PackageRoot, new UpdateIoBudget(null, barrier), token).ConfigureAwait(false);
            // 估算期间计划可能已被新检查回收：计划还在才写。
            if (!File.Exists(file)) continue;
            await UpdatePlanStore.SaveEstimateAsync(current.ToolRoot, plan.PlanId, choice is null
                ? new("full", plan.AssetLength, 0, 0, 1, 0)
                : new("incremental", choice.DownloadBytes, choice.Segments.Count, choice.ReusedFiles, choice.DownloadedFiles, choice.DeletedFiles), token).ConfigureAwait(false);
        }
    }

    private static async Task<UpdatePlan?> SamePlanAsync(PortableInstallation current, UpdatePlan fresh, DateTimeOffset now, CancellationToken token)
    {
        var directory = UpdateLayout.Plans(current.ToolRoot);
        if (!Directory.Exists(directory)) return null;
        string Key(UpdatePlan plan) => JsonSerializer.Serialize(plan with { PlanId = "", CreatedAt = default, ExpiresAt = default }, ContractJson.Options);
        var wanted = Key(fresh);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!UpdateLayout.IsTransactionId(id)) continue;
            try
            {
                var saved = await UpdatePlanStore.ReadAsync(current.ToolRoot, id, token).ConfigureAwait(false);
                if (saved.ExpiresAt > now && Key(saved) == wanted) return saved;
            }
            catch (Exception error) when (error is UpdatePackageException or OperationException or IOException) { }
        }
        return null;
    }

    private async Task RetainAsync(PortableInstallation current, string? planId, CancellationToken token)
    {
        try { await UpdateRetention.RetainAsync(current.ToolRoot, current.Identity.DisplayVersion, clock.GetUtcNow(), afterCheck: true, planId, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or UpdatePackageException) { }
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
        {
            var previous = operations.LatestUpdate("update.download", "succeeded");
            if (previous is { Phase: "staged", Result: { ValueKind: JsonValueKind.Object } payload } &&
                payload.Deserialize<UpdateDownloadStatus>(ContractJson.Options) is { } saved && saved.PlanId == plan.PlanId && saved.ManifestSha256 == plan.Sha256)
                return saved with { ReusedStage = true, DownloadKind = saved.DownloadKind ?? (saved.RequiredDownloadBytes.Value == (ulong)plan.AssetLength ? "full" : "incremental") };
            var estimate = UpdatePlanStore.ReadEstimate(current.ToolRoot, plan.PlanId);
            var bytes = estimate?.DownloadBytes ?? plan.AssetLength;
            return Status(plan, bytes, reusedStage: true, total: bytes, downloadKind: estimate?.DownloadKind ?? "full")
                with { ReusedFiles = estimate?.ReusedFiles ?? 0, DownloadedFiles = estimate?.DownloadedFiles ?? 1 };
        }
        string? fallbackReason = null;
        if (plan.Ranges is { } ranges)
        {
            try
            {
                if (await DownloadRangesAsync(current, plan, ranges, report, token).ConfigureAwait(false) is { } incremental) return incremental;
            }
            catch (Exception error) when (!token.IsCancellationRequested && DeltaFallback(error))
            {
                fallbackReason = error switch
                {
                    AssetDownloadException download => download.Code,
                    UpdatePackageException package => package.Code,
                    InvalidDataException => "update.assetHashMismatch",
                    _ => "update.storageUnavailable"
                };
                DeleteSegments(current, plan);
            }
        }
        await UpdatePlanStore.SaveEstimateAsync(current.ToolRoot, plan.PlanId, new("full", plan.AssetLength, 0, 0, 1, 0), token).ConfigureAwait(false);
        await report("preparing", Status(plan, 0, fallbackReason: fallbackReason), token).ConfigureAwait(false);
        // zip、解压出的包与暂存期间的旧版本同在工具根所在的卷上，按 zip 的三倍预留。
        if (availableBytes(current.ToolRoot) < plan.AssetLength * 3) throw new UpdatePackageException("update.diskSpaceInsufficient");
        var archive = ResumableAssetDownloader.CachePath(UpdateLayout.Downloads(current.ToolRoot), plan.Sha256);
        var downloaded = await DownloadAssetAsync(plan, plan.Sources, archive, plan.AssetLength, plan.Sha256, report, token, fallbackReason).ConfigureAwait(false);
        var verified = Status(plan, plan.AssetLength, source: downloaded.SourceId, fallbackReason: fallbackReason);
        await report("verifying", verified, token).ConfigureAwait(false);
        await PortableUpdatePackage.ExtractAsync(archive, current.ToolRoot, plan.PlanId, plan.DisplayVersion, token).ConfigureAwait(false);
        // 解压成功后 zip 不再需要；暂存目录就是安装的输入。
        File.Delete(archive);
        return verified with { TotalBytes = new((ulong)plan.AssetLength), DownloadedFiles = 1 };
    }

    /// <summary>
    /// 增量下载（按需分段）：按本机文件重新比对出缺的文件，对完整包逐个区间发 Range 请求写入 <c>downloads/&lt;zip 哈希&gt;.blob.seg-&lt;起点&gt;-&lt;长度&gt;</c>
    /// （每段独立续传），再用本机文件与区间里解压出的内容构建新包根，按清单逐文件核对并核对发行身份。
    /// 服务器不支持 Range、区间或校验值不符、解压后哈希不符时由调用方改用完整包。
    /// </summary>
    private async Task<UpdateDownloadStatus?> DownloadRangesAsync(PortableInstallation current, UpdatePlan plan, UpdatePlanRanges ranges,
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token)
    {
        var bytes = await UpdatePlanStore.ReadManifestAsync(current.ToolRoot, ranges.ManifestSha256, token).ConfigureAwait(false)
            ?? throw new UpdatePackageException("update.manifestInvalid");
        var manifest = UpdateManifest.Parse(bytes, plan.DisplayVersion, plan.AssetName, plan.Sha256);
        var choice = await DeltaUpdate.ChooseAsync(manifest, current.PackageRoot, new UpdateIoBudget(null, barrier), token).ConfigureAwait(false);
        if (choice is null) return null;
        var total = choice.DownloadBytes;
        await UpdatePlanStore.SaveEstimateAsync(current.ToolRoot, plan.PlanId,
            new("incremental", total, choice.Segments.Count, choice.ReusedFiles, choice.DownloadedFiles, choice.DeletedFiles), token).ConfigureAwait(false);
        await report("preparing", Status(plan, 0, total: total, downloadKind: "incremental"), token).ConfigureAwait(false);
        // 区间文件与构建出的新包根同在工具根所在的卷上。
        if (availableBytes(current.ToolRoot) < total + manifest.TotalBytes) throw new UpdatePackageException("update.diskSpaceInsufficient");
        var downloads = UpdateLayout.Downloads(current.ToolRoot);
        var files = choice.Segments.Select(segment => (segment, SegmentPath(downloads, plan.Sha256, segment))).ToArray();
        var budget = new UpdateIoBudget(readPolicy().MaximumBytesPerSecond, barrier);
        var resumed = files.Sum(item => File.Exists(item.Item2) ? Math.Min(new FileInfo(item.Item2).Length, item.segment.Length) : 0);
        var sources = plan.Sources.Select(item => new AssetDownloadSource(item.Id, item.Uri)).ToArray();
        var validatorPath = ResumableAssetDownloader.CachePath(downloads, plan.Sha256) + ".validator";
        var validator = File.Exists(validatorPath) ? File.ReadAllText(validatorPath).Trim() : null;
        // 区间按策略的下载并发同时请求，共用同一个限速桶。
        var concurrency = Math.Clamp(readPolicy().DownloadConcurrency, 1, 4);
        using (var downloader = new ResumableAssetDownloader(createHandler?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false },
            new() { Concurrency = concurrency }, availableBytes, budget: budget))
        using (var progressStop = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var progress = ReportProgressAsync(plan, total, () => Math.Min(total, resumed + budget.DownloadedBytes), report, progressStop.Token, "incremental");
            try
            {
                var received = new System.Collections.Concurrent.ConcurrentBag<string?>();
                await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (item, ct) =>
                    received.Add(await downloader.DownloadRangeAsync(sources, item.Item2, item.segment.Start, item.segment.Length, plan.AssetLength,
                        validator, ct).ConfigureAwait(false))).ConfigureAwait(false);
                // 没有保存的校验值时各段同时请求，事后要求所有段来自同一份资产。
                var validators = received.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                if (validators.Length > 1) throw new AssetDownloadException("update.rangeUnsupported");
                if (validator is null && validators.Length == 1) await File.WriteAllTextAsync(validatorPath, validators[0], token).ConfigureAwait(false);
            }
            finally
            {
                progressStop.Cancel();
                try { await progress.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        var verified = Status(plan, total, source: plan.Sources[0].Id, total: total, downloadKind: "incremental");
        await report("verifying", verified, token).ConfigureAwait(false);
        await DeltaUpdate.StageAsync(manifest, file => DeltaUpdate.OpenFromSegments(file, files), current.PackageRoot, current.ToolRoot,
            plan.PlanId, new UpdateIoBudget(null, barrier), token).ConfigureAwait(false);
        DeleteSegments(current, plan);
        return verified with { TotalBytes = new((ulong)total), ReusedFiles = choice.ReusedFiles, DownloadedFiles = choice.DownloadedFiles };
    }

    internal static string SegmentPath(string downloads, string packageSha256, UpdateRangeSegment segment) =>
        ResumableAssetDownloader.CachePath(downloads, packageSha256) + ".seg-" + segment.Start + "-" + segment.Length;

    private static void DeleteSegments(PortableInstallation current, UpdatePlan plan)
    {
        var downloads = UpdateLayout.Downloads(current.ToolRoot);
        if (!Directory.Exists(downloads)) return;
        var prefix = Path.GetFileName(ResumableAssetDownloader.CachePath(downloads, plan.Sha256));
        foreach (var file in Directory.EnumerateFiles(downloads, prefix + ".seg-*").Append(Path.Combine(downloads, prefix + ".validator")))
            if (File.Exists(file)) File.Delete(file);
    }

    // 磁盘不足与计划失效时改用完整包也无济于事；其余增量失败（不支持 Range、区间不符、下载失败、缺内容、校验不过）改用完整包。
    private static bool DeltaFallback(Exception error) => error switch
    {
        AssetDownloadException download => download.Code != "update.diskSpaceInsufficient",
        UpdatePackageException package => package.Code is not ("update.diskSpaceInsufficient" or "update.planStale" or "update.planInvalid"),
        IOException io => !UpdatePaths.IsDiskFull(io),
        InvalidDataException => true,
        _ => false
    };

    private async Task<AssetDownloadResult> DownloadAssetAsync(UpdatePlan plan, IReadOnlyList<UpdatePlanSource> sources, string path,
        long length, string sha256, Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token, string? fallbackReason = null)
    {
        var resumed = File.Exists(path + ".part") ? new FileInfo(path + ".part").Length : 0;
        var budget = new UpdateIoBudget(readPolicy().MaximumBytesPerSecond, barrier);
        using var downloader = new ResumableAssetDownloader(createHandler?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false },
            new() { Concurrency = 1 }, availableBytes, budget: budget);
        using var progressStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var progress = ReportProgressAsync(plan, length, () => Math.Min(length, resumed + budget.DownloadedBytes), report, progressStop.Token, fallbackReason: fallbackReason);
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
            var start = new ProcessStartInfo(UpdateLayout.Replacer(root, plan.PlanId))
                { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = transaction };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(plan.PlanId);
            start.ArgumentList.Add(current.PackageRoot);
            await StartReplacerAsync(() => replacer = Process.Start(start) ?? throw new UpdatePackageException("update.updaterUnavailable"),
                () => UpdateLayout.ReplacerActive(root), () => replacer!.HasExited,
                async () => { if (!replacer!.HasExited) { replacer.Kill(); await replacer.WaitForExitAsync(token).ConfigureAwait(false); } replacer.Dispose(); replacer = null; },
                ReplacerStartup, token).ConfigureAwait(false);
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

    /// <summary>
    /// 启动替换器并等它持锁就绪：每次至多 <paramref name="timeout"/>；未就绪（超时或提前退出）时结束这一次并重启一次，
    /// 第二次仍不行才以 <c>update.updaterUnavailable</c> 放弃（事务尚未改动任何文件）。
    /// </summary>
    internal static async Task StartReplacerAsync(Action start, Func<bool> active, Func<bool> exited, Func<Task> abandon,
        TimeSpan timeout, CancellationToken token, TimeSpan? poll = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            start();
            var started = Stopwatch.StartNew();
            while (!active() && !exited() && started.Elapsed <= timeout)
                await Task.Delay(poll ?? TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
            if (active()) return;
            await abandon().ConfigureAwait(false);
            if (attempt >= ReplacerAttempts) throw new UpdatePackageException("update.updaterUnavailable");
        }
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
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token,
        string downloadKind = "full", string? fallbackReason = null)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), clock);
        await report("downloading", Status(plan, bytes(), total: total, downloadKind: downloadKind, fallbackReason: fallbackReason), token).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            await report("downloading", Status(plan, bytes(), total: total, downloadKind: downloadKind, fallbackReason: fallbackReason), token).ConfigureAwait(false);
    }

    private static UpdateDownloadStatus Status(UpdatePlan plan, long verified, bool reusedStage = false, string? source = null, long? total = null,
        string downloadKind = "full", string? fallbackReason = null)
    {
        var length = new UInt64String((ulong)(total ?? plan.AssetLength));
        return new(plan.PlanId, plan.DisplayVersion, plan.Sha256, length, length, new(0), new((ulong)plan.AssetLength * 3), 0, 0, reusedStage,
            new((ulong)verified), length, source is null ? null : Enum.TryParse<UpdateSourceKind>(source, out var kind) ? kind : new OpenEnum<UpdateSourceKind>(source),
            DownloadKind: downloadKind, FallbackReason: fallbackReason);
    }
}
