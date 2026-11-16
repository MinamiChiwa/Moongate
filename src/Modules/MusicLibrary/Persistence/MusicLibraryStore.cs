using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;

namespace MabiTool.Modules.MusicLibrary.Persistence;

public sealed class MusicLibraryStore(SqliteStore database, string hostEpoch, TimeProvider? timeProvider = null)
{
    public const int MaximumSnapshotBytes = 8 * 1024 * 1024;
    public const int MaximumReceipts = 10_000;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim editing = new(16, 16);
    public static DatabaseMigration Migration { get; } = new("music-library", 2, """
        CREATE TABLE music_library_workspace (
            id INTEGER PRIMARY KEY CHECK(id=1), revision TEXT NOT NULL, snapshot TEXT NOT NULL
        ) STRICT;
        CREATE TABLE music_library_receipts (
            principal TEXT NOT NULL, key TEXT NOT NULL, hash TEXT NOT NULL,
            result TEXT NOT NULL, expires_at INTEGER NOT NULL,
            PRIMARY KEY(principal,key)
        ) STRICT;
        """);
    public static IReadOnlyList<DatabaseMigration> Migrations { get; } =
        [.. ScoreCatalogStore.Migrations, Migration, PerformanceSessionStore.Migration, PlaybackHistoryStore.Migration, ScoreTimelineStore.Migration, MusicImportStore.Migration, MusicStorageData.DeletionMigration];
    public static string ETag(UInt64String revision) => "\"music-library-" + revision + "\"";

    public MusicLibraryState Read()
    {
        var json = database.ReadScalar("SELECT snapshot FROM music_library_workspace WHERE id=1") as string;
        return json is null ? MusicLibraryState.Empty : JsonSerializer.Deserialize<MusicLibraryState>(json, ContractJson.Options)!;
    }

    public MusicLibraryInfo Info()
    {
        var json = database.ReadScalar("""
            SELECT json_group_array(json_object('scopeKey',scope_key,'region',json_extract(snapshot,'$.scope.region'),
            'realmId',json_extract(snapshot,'$.scope.realmId'),'characterId',json_extract(snapshot,'$.scope.characterId'),
            'provider',json_extract(snapshot,'$.scope.provider'),'kind',json_extract(snapshot,'$.scope.kind'),
            'revision',revision,'refreshStatus',json_extract(snapshot,'$.status'),
            'epoch',json_extract(snapshot,'$.hostEpoch'),'updatedAt',json_extract(snapshot,'$.updatedAt')))
            FROM (SELECT * FROM music_library_catalogs ORDER BY scope_key)
            """) as string ?? "[]";
        using var rows = JsonDocument.Parse(json);
        var now = clock.GetUtcNow();
        var sources = rows.RootElement.EnumerateArray().Select(x =>
        {
            var at = x.GetProperty("updatedAt").GetDateTimeOffset();
            var status = x.GetProperty("refreshStatus").GetString()!;
            return new MusicScoreSource(x.GetProperty("scopeKey").GetString()!, x.GetProperty("region").GetString()!,
                x.GetProperty("realmId").GetString()!, x.GetProperty("characterId").GetString()!, x.GetProperty("provider").GetString()!,
                x.GetProperty("kind").GetString()!, UInt64String.Parse(x.GetProperty("revision").GetString()!), status,
                x.GetProperty("epoch").GetString() != hostEpoch || status != "complete" || now < at || now - at > TimeSpan.FromMinutes(1));
        }).ToArray();
        return Read().Info with { Sources = sources };
    }

    public async Task<MusicEditResult> EditAsync(string principal, MusicEditBatch request, string? key, string? ifMatch,
        CancellationToken ct = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "曲库编辑需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "幂等键格式无效");
        if (ifMatch is null) throw new OperationException("revision.required", "曲库编辑需要 If-Match");
        if (!Regex.IsMatch(ifMatch, @"\A""music-library-(0|[1-9][0-9]{0,19})""\z")) throw new OperationException("request.invalid", "曲库 ETag 格式无效");
        MusicLibraryEditor.Text(principal, 128);
        if (!await editing.WaitAsync(0, ct)) throw new OperationException("resource.capacityExceeded", "曲库编辑并发达到上限");
        try
        {
            // 序列化/反序列化隔离调用方后续修改，并给嵌入式调用施加与 HTTP 相同的请求预算。
            var body = JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options);
            if (body.Length > 64 * 1024) throw new OperationException("request.bodyTooLarge", "单次曲库编辑超过 64 KiB");
            var frozen = JsonSerializer.Deserialize<MusicEditBatch>(body, ContractJson.Options)!;
            var hash = Convert.ToHexString(SHA256.HashData(body.Concat(Encoding.UTF8.GetBytes("\n" + ifMatch)).ToArray()));
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var receipt = ReadReceipt(principal, key, hash, now);
            if (receipt is not null) return receipt;
            var current = Read();
            if (ETag(current.Revision) != ifMatch)
            {
                receipt = ReadReceipt(principal, key, hash, now);
                if (receipt is not null) return receipt;
                throw Stale();
            }
            var references = current.Playlists.SelectMany(p => p.Items).Concat(frozen.PutPlaylists.SelectMany(p => p.Items)).Select(i => i.ScoreId)
                .Concat((frozen.NameSettings ?? current.NameSettings).Assignments.Keys).Distinct(StringComparer.Ordinal).ToArray();
            var knownScores = KnownScoreIds(references);
            MusicLibraryState next;
            MusicEditResult result;
            try { (next, result) = MusicLibraryEditor.Apply(current, frozen, knownScores); }
            catch (ArgumentException error) { throw new OperationException("request.invalid", error.Message); }
            catch (OverflowException) { throw new OperationException("resource.capacityExceeded", "曲库修订已耗尽"); }
            var json = JsonSerializer.Serialize(next, ContractJson.Options);
            if (Encoding.UTF8.GetByteCount(json) > MaximumSnapshotBytes) throw new OperationException("resource.capacityExceeded", "曲库快照超过 8 MiB");
            var resultJson = JsonSerializer.Serialize(result, ContractJson.Options);
            return await database.WriteAsync(tx =>
            {
                var previous = tx.Scalar("SELECT json_object('hash',hash,'result',json(result)) FROM music_library_receipts WHERE principal=$p AND key=$key AND expires_at>$now",
                    ("$p", principal), ("$key", key), ("$now", now)) as string;
                if (previous is not null) return DecodeReceipt(previous, hash);
                var revision = tx.Scalar("SELECT revision FROM music_library_workspace WHERE id=1") as string ?? "0";
                if (revision != current.Revision.ToString()) throw Stale();
                tx.Execute("DELETE FROM music_library_receipts WHERE expires_at<=$now", ("$now", now));
                if ((long)tx.Scalar("SELECT count(*) FROM music_library_receipts")! >= MaximumReceipts)
                    throw new OperationException("resource.capacityExceeded", "曲库幂等记录达到上限");
                tx.Execute("INSERT INTO music_library_workspace VALUES(1,$revision,$snapshot) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
                    ("$revision", next.Revision.ToString()), ("$snapshot", json));
                tx.Execute("INSERT INTO music_library_receipts VALUES($p,$key,$hash,$result,$expiry) ON CONFLICT(principal,key) DO UPDATE SET hash=excluded.hash,result=excluded.result,expires_at=excluded.expires_at",
                    ("$p", principal), ("$key", key), ("$hash", hash), ("$result", resultJson), ("$expiry", now + 86_400_001));
                SqliteStore.AppendEvent(tx, new("music-library-" + next.Revision, "music-library", "music-library", next.Revision,
                    "music.library.changed", JsonSerializer.SerializeToElement(result, ContractJson.Options)));
                return result;
            }, ct);
        }
        finally { editing.Release(); }
    }

    private MusicEditResult? ReadReceipt(string principal, string key, string hash, long now)
    {
        var json = database.ReadScalar("SELECT json_object('hash',hash,'result',json(result)) FROM music_library_receipts WHERE principal=$p AND key=$key AND expires_at>$now",
            ("$p", principal), ("$key", key), ("$now", now)) as string;
        return json is null ? null : DecodeReceipt(json, hash);
    }
    private static MusicEditResult DecodeReceipt(string json, string hash)
    {
        using var value = JsonDocument.Parse(json);
        if (value.RootElement.GetProperty("hash").GetString() != hash) throw new OperationException("idempotency.conflict", "幂等键已对应其他请求或修订");
        return value.RootElement.GetProperty("result").Deserialize<MusicEditResult>(ContractJson.Options)!;
    }
    private IReadOnlySet<string> KnownScoreIds(IReadOnlyList<string> references)
    {
        if (references.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var json = database.ReadScalar("SELECT json_group_array(DISTINCT json_extract(e.value,'$.id')) FROM music_library_catalogs c, json_each(c.snapshot,'$.entries') e WHERE json_extract(e.value,'$.id') IN (SELECT value FROM json_each($references))",
            ("$references", JsonSerializer.Serialize(references))) as string ?? "[]";
        return JsonSerializer.Deserialize<string[]>(json)!.ToHashSet(StringComparer.Ordinal);
    }
    private static OperationException Stale() => new("revision.stale", "曲库已由其他客户端编辑，请重新读取");

    public MusicLibraryPage Query(MusicQuery query)
    {
        if (!Enum.IsDefined(query.Collection) || query.PageSize is < 1 or > 200) throw new OperationException("request.invalid", "曲库查询类型或页大小无效");
        MusicLibraryEditor.Text(query.Search, 1024, allowEmpty: true);
        if (query.ParentId is not null) MusicLibraryEditor.Id(query.ParentId);
        if (query.PlaylistId is not null) MusicLibraryEditor.Id(query.PlaylistId);
        if (query.ScopeKey is not null && !Regex.IsMatch(query.ScopeKey, @"\A[A-F0-9]{64}\z")) throw new OperationException("request.invalid", "来源范围键无效");
        if (query.Collection != MusicCollection.Scores && query.ScopeKey is not null ||
            query.Collection != MusicCollection.PlaylistItems && query.PlaylistId is not null ||
            query.Collection is MusicCollection.Scores or MusicCollection.PlaylistItems && query.ParentId is not null)
            throw new OperationException("request.invalid", "曲库查询包含不适用的范围字段");
        var state = Read();
        var offset = 0;
        ScoreCatalogSnapshot? source = null;
        if (query.Collection == MusicCollection.Scores)
        {
            if (query.ScopeKey is null) throw new OperationException("request.invalid", "乐谱查询需要来源范围键");
            var json = database.ReadScalar("SELECT snapshot FROM music_library_catalogs WHERE scope_key=$key", ("$key", query.ScopeKey)) as string;
            source = json is null ? throw new OperationException("resource.notFound", "乐谱来源不存在") : JsonSerializer.Deserialize<ScoreCatalogSnapshot>(json, ContractJson.Options)!;
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(query with { Cursor = null }, ContractJson.Options)));
        if (query.Cursor is not null) offset = DecodeCursor(query.Cursor, state.Revision, source?.Revision, fingerprint);
        var folders = Array.Empty<MusicFolder>(); var playlists = Array.Empty<MusicPlaylist>();
        var items = Array.Empty<PlaylistItem>(); var scores = Array.Empty<ScoreEntry>();
        var total = 0;
        bool Match(string text) => query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        switch (query.Collection)
        {
            case MusicCollection.Folders:
                var folderRows = state.Folders.Where(x => x.ParentId == query.ParentId && Match(x.Name)).ToArray();
                total = folderRows.Length; folders = folderRows.Skip(offset).Take(query.PageSize).ToArray(); break;
            case MusicCollection.Playlists:
                var listRows = state.Playlists.Where(x => x.FolderId == query.ParentId && Match(x.Name + " " + x.Notes)).ToArray();
                total = listRows.Length;
                // 列表内容独立分页，列表目录响应不复制大数组。
                playlists = listRows.Skip(offset).Take(query.PageSize).Select(x => x with { Items = [] }).ToArray(); break;
            case MusicCollection.PlaylistItems:
                var playlist = state.Playlists.SingleOrDefault(x => x.Id == query.PlaylistId) ?? throw new OperationException("resource.notFound", "播放列表不存在");
                var itemRows = playlist.Items.Where(x => Match(x.ScoreId)).ToArray();
                total = itemRows.Length; items = itemRows.Skip(offset).Take(query.PageSize).ToArray(); break;
            case MusicCollection.Scores:
                var entries = source!.Entries;
                var settings = state.NameSettings;
                var artistDefinitions = settings.Artists.Select(x => new ArtistDefinition(x.Id, x.Name, x.Aliases)).ToArray();
                var options = new ScoreNameOptions(source.Scope.Region == "TW" ? ScoreRegion.TW : ScoreRegion.KR, artistDefinitions,
                    new Dictionary<string, string>(), settings.Noise);
                var classified = ScoreNameRules.Classify(entries.Select(x => x.RawTitle).ToArray(), options);
                var artists = settings.Artists.ToDictionary(x => x.Id, StringComparer.Ordinal);
                var projected = entries.Select((entry, index) =>
                {
                    var name = classified[index];
                    var artist = settings.Assignments.TryGetValue(entry.Id, out var artistId) ? artists[artistId] : null;
                    var inferred = artists.Values.FirstOrDefault(a => a.Name == name.Artist);
                    var search = name.SearchIndex + " " + (artist is null ? "" : artist.Name + " " + string.Join(' ', artist.Aliases));
                    var score = new ScoreEntry(entry.Id, entry.RawTitle, name.Cleaned, artist?.Id ?? inferred?.Id, name.Tags,
                        [new(source.Scope.Provider, source.Scope.Region, source.Scope.RealmId + ":" + source.Scope.CharacterId,
                            entry.StableSourceId, null, source.Scope.Kind is ScoreSourceKind.ImportedLibrary or ScoreSourceKind.ImportedMml
                                ? "none" : entry.StableSourceId is null ? "ambiguous" : "stable-id", entry.Ambiguous)],
                        entry.CandidateGroup, entry.Availability.ToString().ToLowerInvariant(), new(source.Revision));
                    return (Score: score, Search: search);
                }).Where(x => Match(x.Search)).OrderBy(x => x.Score.DisplayTitle, StringComparer.Ordinal).ThenBy(x => x.Score.Id, StringComparer.Ordinal).ToArray();
                total = projected.Length; scores = projected.Skip(offset).Take(query.PageSize).Select(x => x.Score).ToArray(); break;
        }
        if (offset > total) throw new OperationException("request.invalid", "曲库游标超出结果范围");
        var next = offset + query.PageSize < total ? EncodeCursor(offset + query.PageSize, state.Revision, source?.Revision, fingerprint) : null;
        return new(state.Revision, source is null ? null : new UInt64String(source.Revision), folders, playlists, items, scores, next);
    }

    private string EncodeCursor(int offset, UInt64String revision, ulong? sourceRevision, string fingerprint)
    {
        var data = Encoding.UTF8.GetBytes(string.Join('|', hostEpoch, revision, sourceRevision?.ToString(CultureInfo.InvariantCulture) ?? "", fingerprint, offset.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToBase64String(data) + "." + Convert.ToHexString(HMACSHA256.HashData(cursorKey, data));
    }
    private int DecodeCursor(string cursor, UInt64String revision, ulong? sourceRevision, string fingerprint)
    {
        if (cursor.Length > 1024) throw new OperationException("request.invalid", "曲库游标无效");
        try
        {
            var split = cursor.Split('.');
            if (split.Length != 2) throw new FormatException();
            var data = Convert.FromBase64String(split[0]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(cursorKey, data), Convert.FromHexString(split[1]))) throw new FormatException();
            var fields = Encoding.UTF8.GetString(data).Split('|');
            if (fields.Length != 5 || fields[0] != hostEpoch || fields[1] != revision.ToString() || fields[2] != (sourceRevision?.ToString(CultureInfo.InvariantCulture) ?? ""))
                throw new OperationException("music.cursorStale", "曲库或来源已更新，请从第一页重新查询");
            if (fields[3] != fingerprint || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) throw new FormatException();
            return offset;
        }
        catch (FormatException) { throw new OperationException("request.invalid", "曲库游标与查询不匹配或已失效"); }
    }
}
