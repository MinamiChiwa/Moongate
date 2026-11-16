using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.Providers.GitHubReleases;

namespace MabiTool.Host.Updates;

public interface IUpdateDownloadService
{
    Task<UpdatePlan> ReadPlanAsync(string planId, CancellationToken token = default);
    Task<UpdateDownloadStatus> DownloadAsync(UpdatePlan frozenPlan,
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token = default);
}

/// <summary>只执行用户受理的下载；完整组件集合在独立工作树验证后发布，不授权安装。</summary>
public sealed class ReleaseUpdateDownloadService(UpdateInstallationOptions? options, ReleaseUpdateCheckService checker,
    ReleaseIndexSourceProvider sources, Func<UpdatePolicy> readPolicy, ActivityBarrier barrier,
    Func<HttpMessageHandler>? createHandler = null, Func<string, long>? freeSpace = null,
    VolumeSpaceReservations? spaceReservations = null, TimeProvider? timeProvider = null) : IUpdateDownloadService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<string, long> availableBytes = freeSpace ??
        (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);

    public async Task<UpdatePlan> ReadPlanAsync(string planId, CancellationToken token = default)
    {
        if (options is null) throw new UpdateSourceException("update.sourceNotConfigured", []);
        return (await new UpdatePlanRepository(options.StateRoot).ReadAsync(planId, token).ConfigureAwait(false)).Plan;
    }

    public async Task<UpdateDownloadStatus> DownloadAsync(UpdatePlan frozenPlan,
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, CancellationToken token = default)
    {
        if (options is null) throw new UpdateSourceException("update.sourceNotConfigured", []);
        // 受理副本与当前保存计划逐字段比较；计划 ID 相同不能替换请求内容。
        var frozen = JsonSerializer.SerializeToUtf8Bytes(frozenPlan, ContractJson.Options);
        using var activity = barrier.EnterActivity("platform-updates", "update.staging");
        var (plan, release) = await checker.ReadValidatedPlanAsync(frozenPlan.PlanId, token, verifyAvailableSpace: false).ConfigureAwait(false);
        if (!frozen.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan, ContractJson.Options)))
            throw new UpdatePlanException("update.planStale");
        var target = UpdateSignedPlanVerifier.TargetFiles(plan, release);
        var settings = readPolicy();
        var budget = new UpdateIoBudget(settings.MaximumBytesPerSecond, barrier);
        var stage = UpdatePaths.Local(options.StagingRoot, plan.PlanId);
        UpdatePaths.RequireDisjoint(stage, options.CacheRoot, options.CurrentVersionRoot ?? options.VersionsRoot);
        var rootLock = Path.Combine(options.StateRoot, "staging.lock");
        UpdatePaths.RejectLinks(rootLock);
        Directory.CreateDirectory(options.StateRoot);
        await using var ownership = OpenLock(rootLock);
        if (Directory.Exists(stage))
        {
            await report("verifying", Status(plan, 0, 0, 0, 0, 0, 0, true), token).ConfigureAwait(false);
            await FileTreePlanner.VerifyAsync(stage, target, token, budget).ConfigureAwait(false);
            // 最终发布或复用前重验授权，长下载不能延长已过期计划。
            await RevalidateAsync(plan, token).ConfigureAwait(false);
            return Status(plan, 0, 0, 0, 0, target.Count, 0, true) with { TotalBytes = new(0) };
        }
        if (File.Exists(stage)) throw new FileTreeException("update.stageExists");
        var estimate = await EstimateAsync(plan, release, budget, token).ConfigureAwait(false);
        var progress = new DownloadProgress(estimate, report, clock);
        await report("preparing", estimate, token).ConfigureAwait(false);
        if (estimate.PeakAdditionalBytes.Value > (ulong)Math.Max(0,
                Math.Min(options.PlannerContext.AvailableBytes, availableBytes(options.InstallationRoot))))
            throw new UpdatePlanException("update.diskSpaceInsufficient");
        VolumeSpaceReservations.SpaceReservation reservation;
        try
        {
            reservation = (spaceReservations ?? VolumeSpaceReservations.Shared).Reserve("update-download:" + plan.PlanId,
                new(options.StagingRoot, checked((long)(estimate.PeakAdditionalBytes.Value - estimate.MaximumDownloadBytes.Value)),
                    Math.Min(options.PlannerContext.AvailableBytes, availableBytes(options.InstallationRoot))),
                new(options.CacheRoot, checked((long)estimate.MaximumDownloadBytes.Value), availableBytes(options.CacheRoot)));
        }
        catch (StorageException error) when (error.Code == "storage.diskSpaceInsufficient")
        { throw new UpdatePlanException("update.diskSpaceInsufficient"); }
        using var space = reservation;
        var work = stage + ".work-" + Guid.NewGuid().ToString("N");
        UpdatePaths.RequireDisjoint(work, stage, options.CacheRoot, options.CurrentVersionRoot ?? options.VersionsRoot);
        Directory.CreateDirectory(work);
        try
        {
            var candidate = Path.Combine(work, "target");
            Directory.CreateDirectory(Path.Combine(candidate, "components"));
            using var downloader = new ResumableAssetDownloader(createHandler?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false },
                new() { Concurrency = settings.DownloadConcurrency }, availableBytes, budget: budget);
            var stager = new FileTreeStager(downloader, budget);
            var reused = 0;
            var downloaded = 0;
            await report("downloading", estimate, token).ConfigureAwait(false);
            await Parallel.ForEachAsync(plan.Components, new ParallelOptions
            { MaxDegreeOfParallelism = settings.DownloadConcurrency, CancellationToken = token }, async (component, ct) =>
            {
                var artifacts = release.Components[component.ComponentId].Artifacts;
                var current = options.CurrentVersionRoot is { } root
                    ? UpdatePaths.Local(root, "components/" + component.ComponentId) : Path.Combine(work, "empty");
                var part = UpdatePaths.Local(Path.Combine(work, "parts"), component.ComponentId);
                var built = await stager.StageWithFallbackAsync(current, part, options.CacheRoot, artifacts,
                    file => AssetSources(plan.ReleaseId, file.DownloadSha256),
                    package => AssetSources(plan.ReleaseId, package.DownloadSha256), ct,
                    (asset, callbackToken) => progress.VerifiedAsync(component.ComponentId, asset, callbackToken)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Directory.Move(built.StageRoot, UpdatePaths.Local(Path.Combine(candidate, "components"), component.ComponentId));
                Interlocked.Add(ref reused, built.ReusedFiles);
                Interlocked.Add(ref downloaded, built.DownloadedFiles);
            }).ConfigureAwait(false);
            var result = progress.Snapshot(estimate with { ReusedFiles = reused, DownloadedFiles = downloaded }, complete: true);
            await report("verifying", result, token).ConfigureAwait(false);
            await FileTreePlanner.VerifyAsync(candidate, target, token, budget).ConfigureAwait(false);
            await RevalidateAsync(plan, token).ConfigureAwait(false);
            await UpdatePaths.PublishOwnedDirectoryAsync(candidate, stage, token).ConfigureAwait(false);
            return result;
        }
        catch (IOException error) when (UpdatePaths.IsDiskFull(error)) { throw new FileTreeException("update.diskSpaceInsufficient"); }
        finally { UpdatePaths.DeleteOwnedTree(work); }
    }

    private async Task RevalidateAsync(UpdatePlan plan, CancellationToken token)
    {
        var (current, _) = await checker.ReadValidatedPlanAsync(plan.PlanId, token, verifyAvailableSpace: false).ConfigureAwait(false);
        if (JsonSerializer.Serialize(current, ContractJson.Options) != JsonSerializer.Serialize(plan, ContractJson.Options))
            throw new UpdatePlanException("update.planStale");
    }

    private async Task<UpdateDownloadStatus> EstimateAsync(UpdatePlan plan, VerifiedReleaseManifest release,
        UpdateIoBudget budget, CancellationToken token)
    {
        var missing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var repair = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in plan.Components)
        {
            var artifacts = release.Components[component.ComponentId].Artifacts;
            HashSet<string>? reuse = null;
            if (options!.CurrentVersionRoot is { } root)
            {
                try
                {
                    var files = await FileTreePlanner.PlanAsync(UpdatePaths.Local(root, "components/" + component.ComponentId),
                        artifacts.Files.Select(item => item.File).ToArray(), budget, token).ConfigureAwait(false);
                    reuse = files.Changes.Where(item => item.Kind == FileChangeKind.Reuse)
                        .Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (IOException) { /* 旧文件被占用时按完整目标估算；执行复制仍重新验证。 */ }
            }
            foreach (var artifact in artifacts.Files)
            {
                if (reuse?.Contains(UpdatePaths.Normalize(artifact.File.RelativePath)) == true) continue;
                if (await IsCachedAsync(artifact, budget, token).ConfigureAwait(false)) continue;
                if (missing.TryGetValue(artifact.DownloadSha256, out var length) && length != artifact.DownloadLength)
                    throw new UpdatePlanException("update.assetMetadataInvalid");
                missing[artifact.DownloadSha256] = artifact.DownloadLength;
                if (plan.Reused.Any(item => item.ComponentId == component.ComponentId)) repair.Add(artifact.DownloadSha256);
            }
        }
        try
        {
            // 复用检查后旧文件仍可能改变；上界覆盖所有组件文件及完整包回退。
            var maximum = plan.Components.Sum(item => item.DownloadBytes);
            var peak = checked((long)Math.Ceiling(checked(maximum + plan.Components.Sum(item => item.InstalledBytes) +
                options!.PlannerContext.DatabaseBackupBytes +
                (plan.RequiredUpdaterVersion is null ? 0 : plan.Components.Single(item => item.ComponentId == "updater").InstalledBytes)) * 1.2d));
            return Status(plan, missing.Values.Sum(), maximum, repair.Sum(hash => missing[hash]), peak, 0, 0, false);
        }
        catch (OverflowException) { throw new UpdatePlanException("update.manifestInvalid"); }
    }

    private async Task<bool> IsCachedAsync(UpdateFileArtifact artifact, UpdateIoBudget budget, CancellationToken token)
    {
        var path = ResumableAssetDownloader.CachePath(options!.CacheRoot, artifact.DownloadSha256);
        UpdatePaths.RejectLinks(path);
        if (!File.Exists(path)) return false;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                UpdateIoBudget.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != artifact.DownloadLength) return false;
            var actual = await budget.CopyAndHashAsync(input, null, artifact.DownloadLength, token).ConfigureAwait(false);
            return actual.Length == artifact.DownloadLength && actual.Hash.Equals(artifact.DownloadSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    private IReadOnlyList<AssetDownloadSource> AssetSources(string releaseId, string hash) =>
        sources.BuildReleaseAssetSources(options!.Source, releaseId, hash).Select(endpoint =>
            new AssetDownloadSource(endpoint.Source.ToString(), endpoint.Uri, endpoint.Relocate, endpoint.Resolve)).ToArray();

    private static FileStream OpenLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { throw new UpdatePlanException("resource.busy"); }
    }

    private static UpdateDownloadStatus Status(UpdatePlan plan, long required, long maximum, long repair, long peak,
        int reused, int downloaded, bool reusedStage) => new(plan.PlanId, plan.ReleaseId, plan.ManifestSha256,
        new(checked((ulong)required)), new(checked((ulong)maximum)), new(checked((ulong)repair)), new(checked((ulong)peak)),
        reused, downloaded, reusedStage, new(0));

    private sealed class DownloadProgress(UpdateDownloadStatus initial,
        Func<string, UpdateDownloadStatus, CancellationToken, Task> report, TimeProvider clock)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly HashSet<string> counted = new(StringComparer.OrdinalIgnoreCase);
        private ulong bytes;
        private long lastReport;
        private bool reported;
        private OpenEnum<UpdateSourceKind>? source;
        private string? component;

        internal async Task VerifiedAsync(string componentId, AssetDownloadResult asset, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (asset.ReusedCache) return;
                if (counted.Add(asset.Sha256)) bytes = checked(bytes + (ulong)asset.Length);
                source = Enum.TryParse<UpdateSourceKind>(asset.SourceId, out var known) ? (OpenEnum<UpdateSourceKind>)known : new OpenEnum<UpdateSourceKind>(asset.SourceId);
                component = componentId;
                var now = clock.GetTimestamp();
                if (!reported || clock.GetElapsedTime(lastReport, now) >= TimeSpan.FromMilliseconds(250))
                {
                    await report("downloading", Snapshot(initial), token).ConfigureAwait(false);
                    reported = true; lastReport = clock.GetTimestamp();
                }
            }
            finally { gate.Release(); }
        }

        internal UpdateDownloadStatus Snapshot(UpdateDownloadStatus status, bool complete = false) => status with
        { VerifiedBytes = new(bytes), TotalBytes = complete ? new(bytes) : null, Source = source, ComponentId = component };
    }
}
