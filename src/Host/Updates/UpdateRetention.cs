using MabiTool.Infrastructure.Updates;
using MabiTool.PortableUpdate;
using MabiTool.Providers.GitHubReleases;

namespace MabiTool.Host.Updates;

/// <summary>
/// 工具根 <c>update/</c> 的残留回收。计划只留一份：高于当前运行版本、未过期的（检查后为本次检查的计划，没有新版本时不留）；
/// 暂存、下载缓存与清单只留这份计划引用的。存在事务日志时只回收计划与清单，事务的计划、暂存、下载与上一版文件留给替换器与下一次启动的收尾。
/// </summary>
internal static class UpdateRetention
{
    /// <param name="checkedPlanId">检查完成后调用时为本次检查的计划（没有新版本为空），此时只留这一份；宿主启动时不传，留最新一份有效计划。</param>
    internal static async Task<UpdatePlan?> RetainAsync(string toolRoot, string installedVersion, DateTimeOffset now,
        bool afterCheck = false, string? checkedPlanId = null, CancellationToken token = default)
    {
        if (!ReleaseVersion.TryParse(installedVersion, out var installed)) return null;
        var journal = UpdateJournal.Read(toolRoot);
        var plans = UpdateLayout.Plans(toolRoot);
        var valid = new List<UpdatePlan>();
        if (Directory.Exists(plans))
            foreach (var file in Directory.EnumerateFiles(plans, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!UpdateLayout.IsTransactionId(id)) continue;
                try
                {
                    var plan = await UpdatePlanStore.ReadAsync(toolRoot, id, token).ConfigureAwait(false);
                    if (plan.ExpiresAt > now && ReleaseVersion.TryParse(plan.DisplayVersion, out var version) && version.CompareTo(installed) > 0)
                        valid.Add(plan);
                }
                catch (Exception error) when (error is UpdatePackageException or MabiTool.Infrastructure.OperationException or IOException) { }
            }
        var keep = afterCheck ? valid.FirstOrDefault(plan => plan.PlanId == checkedPlanId)
            : valid.OrderByDescending(plan => plan.CreatedAt).FirstOrDefault();
        var transaction = journal is { IsFinal: false } ? journal.TransactionId : null;

        if (Directory.Exists(plans))
            foreach (var file in Directory.EnumerateFiles(plans))
            {
                var name = Path.GetFileName(file);
                if (keep is not null && (name == keep.PlanId + ".json" || name == keep.PlanId + ".estimate.json" || name == keep.PlanId + ".notes.json" ||
                    keep.Ranges is { } ranges && name == "manifest-" + ranges.ManifestSha256 + ".json")) continue;
                if (transaction is not null && name == transaction + ".json") continue;
                File.Delete(file);
            }
        // 事务日志还在：暂存、下载与上一版文件归替换器和下一次启动的收尾处理。
        if (journal is not null) return keep;

        var staging = Path.Combine(UpdateLayout.Root(toolRoot), "staging");
        if (Directory.Exists(staging))
            foreach (var directory in Directory.EnumerateDirectories(staging))
                if (Path.GetFileName(directory) != keep?.PlanId) UpdatePaths.DeleteOwnedTree(directory);
        var downloads = UpdateLayout.Downloads(toolRoot);
        if (Directory.Exists(downloads))
        {
            // 下载缓存（整包、续传部分与按需分段的区间）都以 zip 的 SHA-256 命名。
            var wanted = keep is null ? null : keep.Sha256 + ".blob";
            foreach (var file in Directory.EnumerateFiles(downloads))
                if (wanted is null || !Path.GetFileName(file).StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        }
        foreach (var directory in new[] { "previous", "transactions" })
            UpdatePaths.DeleteOwnedTree(Path.Combine(UpdateLayout.Root(toolRoot), directory));
        return keep;
    }

    /// <summary>计划中断的下载已保存、可续传的字节数（按需分段的区间文件或整包的 <c>.part</c>）；没有时为空。</summary>
    internal static long? ResumableBytes(string toolRoot, UpdatePlan plan)
    {
        var downloads = UpdateLayout.Downloads(toolRoot);
        if (!Directory.Exists(downloads)) return null;
        var cache = Path.GetFileName(ResumableAssetDownloader.CachePath(downloads, plan.Sha256));
        var bytes = Directory.EnumerateFiles(downloads, cache + ".seg-*").Append(Path.Combine(downloads, cache + ".part"))
            .Where(File.Exists).Sum(file => new FileInfo(file).Length);
        return bytes > 0 ? bytes : null;
    }
}
