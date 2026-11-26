using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;
using MabiTool.PortableUpdate;
using MabiTool.Providers.GitHubReleases;

namespace MabiTool.Host.Updates;

/// <summary>
/// 计划对应版本的更新说明（简体、繁体各一份）。先从 github.com 直连读公开仓库发行标签下的
/// <c>docs/release-notes-&lt;版本&gt;.md</c> 与 <c>.zh-TW.md</c>；某种语言读不到时改用 Release 正文按语言切分的那一份（REST API）；
/// 都读不到的语言不给，界面写明未能读取更新说明、仍可下载。两种语言都取到才缓存到 <c>update/plans/&lt;计划&gt;.notes.json</c>
/// （随计划回收），否则下次请求再试。检查成功后在后台预取，界面请求时尚未取到则在请求里读取；读取总时长有上限。
/// </summary>
public sealed class UpdateNotesService(PortableInstallation? installation, GitHubReleaseSource source)
{
    /// <summary>一次读取的总时限（含直连与回落）。</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<UpdateReleaseNotes> GetAsync(string planId, CancellationToken token = default)
    {
        var current = installation ?? throw new UpdatePackageException("update.sourceNotConfigured");
        var plan = await UpdatePlanStore.ReadAsync(current.ToolRoot, planId, token).ConfigureAwait(false);
        return await ForPlanAsync(current, plan, token).ConfigureAwait(false);
    }

    /// <summary>检查之后补取保留计划的更新说明；已缓存或已过期的计划不读。</summary>
    public async Task PrefetchAsync(CancellationToken token)
    {
        if (installation is not { } current) return;
        var directory = UpdateLayout.Plans(current.ToolRoot);
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!UpdateLayout.IsTransactionId(id) || UpdatePlanStore.ReadNotes(current.ToolRoot, id) is not null) continue;
            UpdatePlan plan;
            try { plan = await UpdatePlanStore.ReadAsync(current.ToolRoot, id, token).ConfigureAwait(false); }
            catch (Exception error) when (error is UpdatePackageException or MabiTool.Infrastructure.OperationException or IOException) { continue; }
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow) continue;
            await ForPlanAsync(current, plan, token).ConfigureAwait(false);
        }
    }

    private async Task<UpdateReleaseNotes> ForPlanAsync(PortableInstallation current, UpdatePlan plan, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (UpdatePlanStore.ReadNotes(current.ToolRoot, plan.PlanId) is { } cached) return Snapshot(plan, cached);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(Budget);
            // 时限到达时各步骤按“没读到”处理；调用方取消则放弃。
            var entries = await FetchAsync(current.Identity.Repository, plan, budget.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // 两种语言都取到才缓存；读取期间计划已被新检查回收时不再写回。
            if (entries.Count == 2 && File.Exists(Path.Combine(UpdateLayout.Plans(current.ToolRoot), plan.PlanId + ".json")))
                await UpdatePlanStore.SaveNotesAsync(current.ToolRoot, plan.PlanId, entries, token).ConfigureAwait(false);
            return Snapshot(plan, entries);
        }
        finally { gate.Release(); }
    }

    private static UpdateReleaseNotes Snapshot(UpdatePlan plan, IReadOnlyList<UpdateNotesEntry> entries) => new(plan.PlanId, plan.DisplayVersion, entries);

    private async Task<IReadOnlyList<UpdateNotesEntry>> FetchAsync(string repository, UpdatePlan plan, CancellationToken token)
    {
        var blob = $"https://github.com/{repository}/blob/{Uri.EscapeDataString(plan.Tag)}/";
        var simplifiedFile = $"docs/release-notes-{plan.DisplayVersion}.md";
        var traditionalFile = $"docs/release-notes-{plan.DisplayVersion}.zh-TW.md";
        var simplified = RepositoryAsync(repository, plan.Tag, simplifiedFile, new Uri(blob + simplifiedFile), token);
        var traditional = RepositoryAsync(repository, plan.Tag, traditionalFile, new Uri(blob + traditionalFile), token);
        string? hans = await simplified.ConfigureAwait(false), hant = await traditional.ConfigureAwait(false);
        string hansSource = "repository", hantSource = "repository";
        // 公开仓库里读不到的语言：改用 Release 正文里对应语言的那一份。
        if (hans is null || hant is null)
        {
            var split = await ReleaseBodyAsync(repository, plan.Tag, new Uri(blob + "docs/"), token).ConfigureAwait(false);
            if (hans is null && split.Simplified is not null) { hans = split.Simplified; hansSource = "release"; }
            if (hant is null && split.Traditional is not null) { hant = split.Traditional; hantSource = "release"; }
        }
        var entries = new List<UpdateNotesEntry>(2);
        if (hans is not null) entries.Add(new("zh-Hans", hans, hansSource));
        if (hant is not null) entries.Add(new("zh-Hant", hant, hantSource));
        return entries;
    }

    private async Task<string?> RepositoryAsync(string repository, string tag, string path, Uri page, CancellationToken token)
    {
        try
        {
            return await source.FetchRepositoryFileAsync(repository, tag, path, token).ConfigureAwait(false) is { } text
                ? ReleaseNotesText.Normalize(text, page) : null;
        }
        catch (Exception error) when (error is UpdateSourceException or OperationCanceledException) { return null; }
    }

    private async Task<(string? Simplified, string? Traditional)> ReleaseBodyAsync(string repository, string tag, Uri page, CancellationToken token)
    {
        try
        {
            return await source.FetchReleaseBodyAsync(repository, tag, token).ConfigureAwait(false) is { } body
                ? ReleaseNotesText.SplitReleaseBody(body, page) : (null, null);
        }
        catch (Exception error) when (error is UpdateSourceException or OperationCanceledException) { return (null, null); }
    }
}
