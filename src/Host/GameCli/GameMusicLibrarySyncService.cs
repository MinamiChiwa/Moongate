using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;

namespace MabiTool.Host.GameCli;

/// <summary>
/// 乐谱库的游戏乐谱来源：把连接器读到的 <c>get_music_scores</c> 列表同步为乐谱库的一个“游戏列表”来源（范围固定为台服连接器当前角色）。
/// 条目按规范化标题生成，同标题多份各占一条（来源标识为标题加出现序号），乐谱换存放位置不改变条目身份；
/// 本次列表里没有的既有条目由目录规则标为已不在游戏里。连接器、存储不可用或读取失败时不改动来源。
/// 两次实际读取至少相隔 <see cref="MinimumGap"/>，间隔内返回上次结果。
/// </summary>
public sealed class GameMusicLibrarySyncService(GameConnectorService connector, IGameMusicControl music, ScoreCatalogStore? catalogs,
    string backendEpoch, TimeProvider? time = null)
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(3);
    private const int KeyLimit = 200;

    /// <summary>来源范围：台服、连接器提供方、不区分角色（列表就是当前登录角色的乐谱）。</summary>
    public static ScoreScope Scope { get; } = new("TW", "game-cli", "0", GameConnectorCapabilities.Provider, ScoreSourceKind.GameList);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long lastRead;
    private bool hasRead;
    private GameMusicLibrarySync? last;
    private string? lastConnectorKey;

    public async Task<GameMusicLibrarySync> SyncAsync(CancellationToken ct)
    {
        if (catalogs is null) return Result(GameMusicSyncStatus.Unavailable, "storage.unavailable", 0);
        var state = connector.Current;
        if (state.Status.KnownValue != GameConnectorStatus.Connected)
            return Result(GameMusicSyncStatus.Unavailable, connector.MusicAvailability().Reason, CurrentCount());
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var connectorKey = state.BackendEpoch + "|" + state.Revision;
            if (hasRead && last is { Status.KnownValue: GameMusicSyncStatus.Synced } && lastConnectorKey == connectorKey && clock.GetElapsedTime(lastRead) < MinimumGap)
                return last;
            var listing = await music.ListScoresAsync(TimeSpan.FromTicks(-1), ct).ConfigureAwait(false);
            lastRead = clock.GetTimestamp(); hasRead = true;
            if (!listing.Available)
                return last = Result(GameMusicSyncStatus.Failed, listing.Reason ?? "gameCli.failed", CurrentCount());
            var items = Items(listing.Scores);
            await RefreshAsync(items, ct).ConfigureAwait(false);
            lastConnectorKey = connectorKey;
            return last = Result(GameMusicSyncStatus.Synced, null, items.Count, clock.GetUtcNow());
        }
        catch (ScoreCatalogConflictException)
        {
            return last = Result(GameMusicSyncStatus.Failed, "music.catalogConflict", CurrentCount());
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or StorageException)
        {
            return last = Result(GameMusicSyncStatus.Failed, "storage.unavailable", CurrentCount());
        }
        finally { gate.Release(); }
    }

    private async Task RefreshAsync(IReadOnlyList<ScoreSourceItem> items, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var previous = catalogs!.Read(Scope);
            try { await catalogs.RefreshAsync(Scope, previous?.Revision ?? 0, items, ScoreRefreshStatus.Complete, ct).ConfigureAwait(false); return; }
            catch (ScoreCatalogConflictException) when (attempt == 0) { }
        }
    }

    private int CurrentCount() =>
        catalogs?.Read(Scope) is { } snapshot ? snapshot.Entries.Count(entry => entry.Availability != ScoreAvailability.Missing) : 0;

    private GameMusicLibrarySync Result(GameMusicSyncStatus status, string? reason, int count, DateTimeOffset? syncedAt = null) =>
        new(backendEpoch, status, reason, count, syncedAt ?? catalogs?.Read(Scope)?.LastCompleteAt);

    /// <summary>
    /// 列表转来源条目：标题含控制字符、过长或不是有效 Unicode 的行跳过；来源标识取规范化标题（超过 200 字取其哈希），
    /// 规范化后相同的标题按出现顺序加序号，序号只由标题决定，与存放位置无关。
    /// </summary>
    internal static IReadOnlyList<ScoreSourceItem> Items(IReadOnlyList<GameProvidedScore> scores)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<ScoreSourceItem>(scores.Count);
        foreach (var score in scores.OrderBy(x => x.DisplayTitle, StringComparer.Ordinal))
        {
            if (!Usable(score.DisplayTitle)) continue;
            var key = GameMusicNames.Title(score.DisplayTitle);
            if (key.Length == 0) continue;
            if (key.Length > KeyLimit) key = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            var occurrence = counts[key] = counts.GetValueOrDefault(key) + 1;
            items.Add(new(score.DisplayTitle, occurrence == 1 ? key : key + "#" + occurrence));
        }
        return items;
    }

    private static bool Usable(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 4096 || title.Any(char.IsControl)) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(title); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}
