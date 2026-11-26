using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;

namespace MabiTool.Modules.MusicLibrary.Persistence;

public sealed class ScoreCatalogConflictException(ulong currentRevision) : InvalidOperationException("乐谱来源修订已改变，请重新读取后刷新")
{
    public ulong CurrentRevision { get; } = currentRevision;
}

public sealed class ScoreCatalogStore(SqliteStore database, string hostEpoch, TimeProvider? timeProvider = null)
{
    public const int MaximumScopes = 256;
    public const int MaximumSnapshotBytes = 8 * 1024 * 1024;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public static IReadOnlyList<DatabaseMigration> Migrations { get; } = [new("music-library", 1, """
        CREATE TABLE music_library_catalogs (
            scope_key TEXT PRIMARY KEY,
            revision TEXT NOT NULL,
            snapshot TEXT NOT NULL
        ) STRICT;
        """)];

    public ScoreCatalogSnapshot? Read(ScoreScope scope)
    {
        ScoreCatalog.ValidateScope(scope);
        var json = database.ReadScalar("SELECT snapshot FROM music_library_catalogs WHERE scope_key=$key", ("$key", scope.Key)) as string;
        return json is null ? null : JsonSerializer.Deserialize<ScoreCatalogSnapshot>(json, ContractJson.Options)!;
    }

    public ScoreCatalogSnapshot? ReadByKey(string scopeKey)
    {
        if (scopeKey is null || scopeKey.Length != 64 || scopeKey.Any(ch => ch is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new OperationException("request.invalid", "乐谱来源范围键无效");
        var json = database.ReadScalar("SELECT snapshot FROM music_library_catalogs WHERE scope_key=$key", ("$key", scopeKey)) as string;
        return json is null ? null : JsonSerializer.Deserialize<ScoreCatalogSnapshot>(json, ContractJson.Options)!;
    }

    /// <summary>按乐谱 ID 取各来源里的原始标题（ID 为全局唯一 GUID）；找不到的 ID 不在结果里。</summary>
    public IReadOnlyDictionary<string, string> FindRawTitles(IReadOnlyCollection<string> scoreIds)
    {
        var wanted = scoreIds.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted.Count == 0) return result;
        var json = database.ReadScalar("SELECT json_group_array(snapshot) FROM music_library_catalogs") as string ?? "[]";
        using var document = JsonDocument.Parse(json);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var snapshot = JsonSerializer.Deserialize<ScoreCatalogSnapshot>(item.GetString()!, ContractJson.Options)!;
            foreach (var entry in snapshot.Entries)
                if (wanted.Contains(entry.Id)) result[entry.Id] = entry.RawTitle;
        }
        return result;
    }

    /// <summary>
    /// 导入、游戏列表与抓包（库存）来源按最后一次完整同步的内容视为当前，不随时间过期；
    /// 其余来源是实时观察，宿主代次变化、局部或失败、超过一分钟都不再当前。
    /// </summary>
    public bool IsCurrent(ScoreCatalogSnapshot snapshot) => snapshot.Scope.Kind is ScoreSourceKind.ImportedMml or ScoreSourceKind.ImportedLibrary or ScoreSourceKind.GameList or ScoreSourceKind.Inventory
        ? snapshot.Status == ScoreRefreshStatus.Complete
        : !snapshot.IsStale(hostEpoch, clock.GetUtcNow(), TimeSpan.FromMinutes(1));

    public async Task<ScoreCatalogSnapshot> RefreshAsync(ScoreScope scope, ulong expectedRevision,
        IReadOnlyList<ScoreSourceItem> items, ScoreRefreshStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = Read(scope);
        if ((previous?.Revision ?? 0) != expectedRevision) throw new ScoreCatalogConflictException(previous?.Revision ?? 0);
        // 分类、关联和编码在事务外完成；写队列仅执行修订条件、短写入与 outbox。
        var next = ScoreCatalog.Refresh(scope, previous, items, status, hostEpoch, clock.GetUtcNow());
        var json = JsonSerializer.Serialize(next, ContractJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumSnapshotBytes) throw new ArgumentException("乐谱来源快照超过 8 MiB");
        var key = scope.Key;
        await database.WriteAsync(tx =>
        {
            var current = tx.Scalar("SELECT revision FROM music_library_catalogs WHERE scope_key=$key", ("$key", key)) as string;
            var revision = current is null ? 0 : UInt64String.Parse(current).Value;
            if (revision != expectedRevision) throw new ScoreCatalogConflictException(revision);
            if (current is null && (long)tx.Scalar("SELECT count(*) FROM music_library_catalogs")! >= MaximumScopes)
                throw new ArgumentException("乐谱来源范围达到 256 个上限");
            tx.Execute("INSERT INTO music_library_catalogs VALUES($key,$revision,$snapshot) ON CONFLICT(scope_key) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
                ("$key", key), ("$revision", new UInt64String(next.Revision).ToString()), ("$snapshot", json));
            SqliteStore.AppendEvent(tx, new("music-catalog-" + key + "-" + next.Revision, "music-catalog", key,
                new(next.Revision), "music.catalog.changed", JsonSerializer.SerializeToElement(
                    new MusicCatalogChanged(key, new(next.Revision), next.Status.ToString().ToLowerInvariant(), next.Entries.Count), ContractJson.Options)));
            return true;
        }, cancellationToken);
        return next;
    }
}
