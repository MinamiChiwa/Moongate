using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;

namespace MabiTool.Modules.MusicLibrary.Application;

/// <summary>
/// 乐谱库的抓包来源：把库存读模型（背包抓包）里的玩家自制与复制乐谱同步为当前角色的“库存”来源，
/// 归属规则见 <see cref="OwnedScoreSource"/>。库存不是当前、读取失败或存储不可用时不改动来源；
/// 三个容器覆盖都完整时是完整刷新（本次没有的条目标为已不在游戏里），否则是局部刷新。
/// 两次实际读取至少相隔 <see cref="MinimumGap"/>，间隔内返回上次结果。
/// </summary>
public sealed class OwnedMusicLibrarySyncService(OwnedScoreProvider scores, ScoreCatalogStore? catalogs, Func<GameSession?> binding,
    string backendEpoch, TimeProvider? time = null)
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(3);
    private const int PageSize = 1000;
    private const int MaximumPages = 20;

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long lastRead;
    private MusicOwnedSync? last;
    private string? lastScopeKey;

    public async Task<MusicOwnedSync> SyncAsync(CancellationToken ct)
    {
        if (catalogs is null) return Result(MusicOwnedSyncStatus.Unavailable, "storage.unavailable");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (last is { Status.KnownValue: MusicOwnedSyncStatus.Synced } && clock.GetElapsedTime(lastRead) < MinimumGap) return last;
            MusicOwnedScorePage? first = null;
            var rows = new List<MusicOwnedScore>();
            try
            {
                string? cursor = null;
                for (var pages = 0; pages < MaximumPages; pages++)
                {
                    var page = await scores.QueryAsync(new(PageSize: PageSize, Cursor: cursor), binding, ct).ConfigureAwait(false);
                    first ??= page;
                    // 来源范围由当前库存身份决定：工作区必须当前有效、绑定匹配，没有就保持上次内容。
                    if (!page.Current || page.Identity is not { } identity || !ValidCharacter(identity.CharacterId))
                        return last = Result(MusicOwnedSyncStatus.Unavailable, page.Reasons.FirstOrDefault() ?? "inventory.observationStopped");
                    rows.AddRange(page.Items.Where(row => row.Current));
                    cursor = page.NextCursor;
                    if (cursor is null) break;
                }
            }
            catch (OperationException error) { return last = Result(MusicOwnedSyncStatus.Failed, error.Code); }
            if (first is null) return last = Result(MusicOwnedSyncStatus.Failed, "inventory.sourceUnavailable");
            var scope = OwnedScoreSource.ScopeFor(first.Identity!.CharacterId);
            var selection = OwnedScoreSource.Select(rows);
            // 完整刷新要求读取范围内的每个容器与实例都完整当前；否则只新增与更新。
            var status = first.Complete ? ScoreRefreshStatus.Complete : ScoreRefreshStatus.Partial;
            try
            {
                var snapshot = await RefreshAsync(scope, selection.Items, status, ct).ConfigureAwait(false);
                lastRead = clock.GetTimestamp(); lastScopeKey = scope.Key;
                return last = new(backendEpoch, MusicOwnedSyncStatus.Synced, null, scope.Key,
                    snapshot.Entries.Count(entry => entry.Availability != ScoreAvailability.Missing), selection.GameProvided, selection.Unusable,
                    status == ScoreRefreshStatus.Complete, snapshot.LastCompleteAt ?? snapshot.UpdatedAt);
            }
            catch (ScoreCatalogConflictException) { return last = Result(MusicOwnedSyncStatus.Failed, "music.catalogConflict"); }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or StorageException)
            { return last = Result(MusicOwnedSyncStatus.Failed, "storage.unavailable"); }
        }
        finally { gate.Release(); }
    }

    private async Task<ScoreCatalogSnapshot> RefreshAsync(ScoreScope scope, IReadOnlyList<ScoreSourceItem> items, ScoreRefreshStatus status, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var previous = catalogs!.Read(scope);
            // 内容与上次相同时不写入：每次写入都会推进来源修订并发出变更通知，页面每分钟同步一次不应因此重读。
            if (previous is not null && Unchanged(previous, items, status)) return previous;
            try { return await catalogs.RefreshAsync(scope, previous?.Revision ?? 0, items, status, ct).ConfigureAwait(false); }
            catch (ScoreCatalogConflictException) when (attempt == 0) { }
        }
    }

    /// <summary>
    /// 本次刷新不会改变来源：状态相同，且完整刷新时仍在游戏里的条目恰好是本次条目（标题也相同），
    /// 局部刷新时本次条目都已是在游戏里的条目（标题相同）；局部刷新不动其他条目。
    /// </summary>
    internal static bool Unchanged(ScoreCatalogSnapshot previous, IReadOnlyList<ScoreSourceItem> items, ScoreRefreshStatus status)
    {
        if (previous.Status != status) return false;
        var present = previous.Entries.Where(entry => entry.Availability != ScoreAvailability.Missing && entry.StableSourceId is not null)
            .ToDictionary(entry => entry.StableSourceId!, entry => entry.RawTitle, StringComparer.Ordinal);
        var incoming = items.ToDictionary(item => item.StableSourceId!, item => item.RawTitle, StringComparer.Ordinal);
        if (incoming.Any(pair => !present.TryGetValue(pair.Key, out var title) || title != pair.Value)) return false;
        return status != ScoreRefreshStatus.Complete || present.Count == incoming.Count;
    }

    private static bool ValidCharacter(string value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
        && value == id.ToString(CultureInfo.InvariantCulture);

    private MusicOwnedSync Result(MusicOwnedSyncStatus status, string reason)
    {
        // 不可用与失败：来源保持上次内容，数量取上次同步的结果（没有时为 0）。
        var previous = last;
        return new(backendEpoch, status, reason, previous?.ScopeKey ?? lastScopeKey, previous?.ScoreCount ?? 0, previous?.GameProvidedSkipped ?? 0,
            previous?.UnusableSkipped ?? 0, previous?.Complete ?? false, previous?.SyncedAt);
    }
}
