using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.MusicLibrary.Domain;

public sealed record MobiFolioImportSource(string SourceId, string SourceLabel, ScoreScope Scope, string SourceHash,
    string RequestHash, IReadOnlyList<ImportFileSnapshot> Files, IReadOnlyDictionary<string, JsonElement> Documents,
    IReadOnlyDictionary<string, string> ArtistMappings);
public sealed record MobiFolioImportPlan(ScoreCatalogSnapshot Catalog, MusicLibraryState Library,
    MusicEditResult LibraryChange, IReadOnlyList<MusicPlaybackHistoryItem> History,
    IReadOnlyList<ScoreTimelineEvidence> Durations, long ExpectedHistoryRevision);

public static class MobiFolioImport
{
    public const int MaximumRequestBytes = 12 * 1024 * 1024;
    public const int MaximumSourceBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> FileNames = new(StringComparer.Ordinal)
        { "settings.json", "scores.json", "instruments.json", "artists.json", "playlists.json", "recent.json", "durations.json" };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
    private static OperationException Invalid(string file, string message) => new("import.invalidInput", message,
        new Dictionary<string, JsonElement> { ["file"] = JsonSerializer.SerializeToElement(file) });

    public static MobiFolioImportSource Read(MusicImportRequest request, string owner)
    {
        if (request is null || !Regex.IsMatch(request.SourceId ?? "", @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,63}\z"))
            throw Invalid("", "导入来源标识无效");
        MusicLibraryEditor.Text(request.SourceLabel, 200);
        MusicLibraryEditor.Text(owner, 128);
        if (request.Files is null || request.Files.Count is < 1 or > 7 || request.Files.Any(x => x is null))
            throw Invalid("", "导入须包含一至七个已登记文件");
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var snapshots = new List<ImportFileSnapshot>();
        long sourceBytes = 0, encodedBytes = 0;
        foreach (var file in request.Files.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (file.Name is null || !FileNames.Contains(file.Name) || documents.ContainsKey(file.Name))
                throw Invalid(file.Name ?? "", "文件名须为七类已登记 JSON 名称且不能重复");
            if (file.ContentBase64 is null || (encodedBytes += file.ContentBase64.Length) > MaximumRequestBytes)
                throw new OperationException("request.bodyTooLarge", "导入请求超过 12 MiB");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(file.ContentBase64); }
            catch (FormatException) { throw Invalid(file.Name, "文件内容不是有效 base64"); }
            if ((sourceBytes += bytes.Length) > MaximumSourceBytes)
                throw new OperationException("request.bodyTooLarge", "导入原文件总量超过 8 MiB");
            snapshots.Add(new(file.Name, Hash(bytes), new((ulong)bytes.Length)));
            try
            {
                var content = bytes.AsSpan();
                if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
                var json = Utf8.GetString(content);
                using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
                CheckJson(document.RootElement, file.Name);
                documents.Add(file.Name, document.RootElement.Clone());
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or InvalidOperationException)
            { throw Invalid(file.Name, "文件须为有效 UTF-8 JSON，深度上限为 32"); }
        }
        var choices = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (request.ArtistMappings is not null)
        {
            if (request.ArtistMappings.Count > ScoreNameRules.MaxArtists) throw Invalid("artists.json", "歌手映射超过上限");
            foreach (var pair in request.ArtistMappings)
            { MusicLibraryEditor.Text(pair.Key, 128); MusicLibraryEditor.Id(pair.Value); choices.Add(pair.Key, pair.Value); }
        }
        var sourceHash = Hash(JsonSerializer.SerializeToUtf8Bytes(snapshots, ContractJson.Options));
        // 来源身份固定于主体、来源标识和原字节版本；新的缓存版本不会复用旧顺序键作为物品身份。
        var provider = "mobifolio:" + Hash(JsonSerializer.SerializeToUtf8Bytes(new[] { owner, request.SourceId, sourceHash }));
        var scope = new ScoreScope(request.Region, request.RealmId, request.CharacterId.ToString(), provider, ScoreSourceKind.ImportedLibrary);
        try { ScoreCatalog.ValidateScope(scope); }
        catch (ArgumentException e) { throw Invalid("", e.Message); }
        var requestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request.SourceId, request.SourceLabel, scope, sourceHash, choices }, ContractJson.Options));
        return new(request.SourceId!, request.SourceLabel, scope, sourceHash, requestHash, snapshots, documents, choices);
    }

    private static void CheckJson(JsonElement node, string file)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in node.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw Invalid(file, "JSON 对象包含重复属性");
                _ = Utf8.GetByteCount(field.Name);
                CheckJson(field.Value, file);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) CheckJson(child, file);
        else if (node.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(node.GetString()!);
    }

    public static (MusicImportPreview Preview, MobiFolioImportPlan Plan) Plan(MobiFolioImportSource source,
        MusicLibraryState current, IReadOnlySet<string> existingScoreIds, long historyRevision,
        string hostEpoch, DateTimeOffset now, CancellationToken ct = default)
        => new Planner(source, current, existingScoreIds, historyRevision, hostEpoch, now, ct).Build();

    private sealed class Planner(MobiFolioImportSource source, MusicLibraryState current,
        IReadOnlySet<string> existingScoreIds, long historyRevision, string hostEpoch, DateTimeOffset now, CancellationToken ct)
    {
        private readonly List<CatalogScore> scores = [];
        private readonly Dictionary<string, List<CatalogScore>> scoreKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CatalogScore>> scoreTitles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> groups = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Key, string Title), string> unresolved = [];
        private readonly HashSet<string> unresolvedIds = new(StringComparer.Ordinal);
        private readonly List<ImportIdMapping> mappings = [];
        private readonly List<ImportIssue> issues = [];
        private readonly List<MusicImportInstrument> instruments = [];
        private readonly List<MusicPlaybackHistoryItem> history = [];
        private readonly List<ScoreTimelineEvidence> durations = [];
        private readonly Dictionary<string, double> durationByTitle = new(StringComparer.Ordinal);
        private readonly List<MusicFolder> folders = [];
        private readonly List<MusicPlaylist> playlists = [];
        private readonly List<MusicArtist> artists = [.. current.NameSettings.Artists];
        private readonly Dictionary<string, string> assignments = new(current.NameSettings.Assignments, StringComparer.Ordinal);
        private readonly Dictionary<string, string> artistIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> artistNames = new(StringComparer.Ordinal);
        private MusicPreferences? preferences;
        private bool conflict;
        private int importedArtists;
        private string file = "";

        public (MusicImportPreview, MobiFolioImportPlan) Build()
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                LoadScores(); LoadInstruments(); LoadDurations(); LoadArtists(); LoadPlaylists(); LoadHistory(); LoadPreferences();
                if (source.ArtistMappings.Keys.Any(x => !artistIds.ContainsKey(x))) throw Invalid("artists.json", "歌手映射包含未知旧 ID");
                if (scores.Count > ScoreCatalog.MaximumEntries || scores.Sum(x => (long)x.RawTitle.Length) > 1024 * 1024)
                    throw new OperationException("resource.capacityExceeded", "导入乐谱及未解决引用超过来源容量");
                var noise = current.NameSettings.Noise.Concat(Noise()).Distinct(StringComparer.Ordinal).ToArray();
                MusicLibraryState next = current;
                MusicEditResult changed = new(current.Revision, [], [], false);
                try
                {
                    (next, changed) = MusicLibraryEditor.MergeImported(current, folders, playlists, new(artists, assignments, noise),
                        existingScoreIds.Concat(scores.Select(x => x.Id)).ToHashSet(StringComparer.Ordinal));
                }
                catch (Exception e) when (e is ArgumentException || e is OperationException { Code: "resource.notFound" or "request.invalid" })
                { Issue("import.structureConflict", "playlists.json", "", e.Message, blocking: true); }
                var catalog = new ScoreCatalogSnapshot(source.Scope, 1, hostEpoch, now, null, ScoreRefreshStatus.Complete, scores.ToArray(),
                    scores.GroupBy(x => x.RawTitle, StringComparer.Ordinal).Select(g => new ScoreCandidateGroup(g.First().CandidateGroup,
                        g.Key, scoreTitles.GetValueOrDefault(g.Key)?.Count ?? 0, false, g.Select(x => x.Id).ToArray())).ToArray());
                var counts = new MusicImportCounts(scores.Count, folders.Count, playlists.Count, playlists.Sum(x => x.Items.Count),
                    importedArtists, history.Count, durations.Count, unresolved.Count);
                var preview = new MusicImportPreview(Id(), source.SourceId, source.SourceLabel, source.SourceHash, source.Scope.Key,
                    current.Revision, now + PreviewLifetime, conflict ? "conflict" : "ready", source.Files, counts,
                    mappings.ToArray(), issues.ToArray(), preferences, instruments.ToArray());
                return (preview, new(catalog, next, changed, history.ToArray(), durations.ToArray(), historyRevision));
            }
            catch (Exception e) when (e is ArgumentException or OverflowException or InvalidOperationException)
            { throw Invalid(file, "导入字段格式或容量无效：" + e.Message); }
        }

        private bool Document(string name, out JsonElement value)
        { file = name; ct.ThrowIfCancellationRequested(); return source.Documents.TryGetValue(name, out value); }
        private static JsonElement Property(JsonElement value, string name)
            => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
        private string Text(JsonElement value, int maximum, bool empty = false, bool multiline = false)
        {
            if (value.ValueKind != JsonValueKind.String) throw Invalid(file, "字段须为字符串");
            var text = value.GetString()!;
            MusicLibraryEditor.Text(text, maximum, empty, multiline); return text;
        }
        private string? OptionalText(JsonElement value, int maximum = 128)
            => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : Text(value, maximum, true) is { Length: > 0 } text ? text : null;
        private JsonElement[] Array(JsonElement value, int maximum)
        {
            if (value.ValueKind != JsonValueKind.Array) throw Invalid(file, "字段须为数组");
            if (value.GetArrayLength() > maximum) throw new OperationException("resource.capacityExceeded", "导入数组超过条目上限");
            return value.EnumerateArray().ToArray();
        }
        private string Name(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String) return Text(value, 4096);
            foreach (var key in new[] { "Name", "DisplayName", "Title" })
                if (Property(value, key) is { ValueKind: JsonValueKind.String } field && !string.IsNullOrWhiteSpace(field.GetString()))
                    return Text(field, 4096);
            throw Invalid(file, "缓存条目缺少 Name、DisplayName 或 Title");
        }
        private string Group(string title)
        { if (!groups.TryGetValue(title, out var id)) groups.Add(title, id = Id()); return id; }
        private void Issue(string code, string name, string oldId, string message, bool blocking = false)
        { issues.Add(new(code, name, oldId, message)); conflict |= blocking; }
        private void LoadScores()
        {
            if (!Document("scores.json", out var document)) return;
            var names = Array(Property(document, "items"), ScoreCatalog.MaximumEntries).Select(Name).ToArray();
            var totals = names.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var title in names)
            {
                ct.ThrowIfCancellationRequested();
                var occurrence = occurrences[title] = occurrences.GetValueOrDefault(title) + 1;
                var key = totals[title] > 1 ? title + "#" + occurrence.ToString(CultureInfo.InvariantCulture) : title;
                var score = new CatalogScore(Id(), title, null, Group(title), totals[title] > 1, ScoreAvailability.Imported);
                scores.Add(score);
                if (!scoreKeys.TryGetValue(key, out var keys)) scoreKeys.Add(key, keys = []);
                keys.Add(score);
                if (!scoreTitles.TryGetValue(title, out var titles)) scoreTitles.Add(title, titles = []);
                titles.Add(score);
            }
            foreach (var pair in scoreKeys)
                foreach (var score in pair.Value)
                    mappings.Add(new("score", pair.Key, score.Id, score.RawTitle,
                        pair.Value.Count > 1 ? "legacy-key-collision" : "snapshot-key", pair.Value.Select(x => x.Id).ToArray()));
        }
        private string Resolve(string key, string title, string sourceFile, bool titleOnly = false)
        {
            MusicLibraryEditor.Text(key, 4352); MusicLibraryEditor.Text(title, 4096);
            var matches = (scoreKeys.GetValueOrDefault(key) ?? []).Where(x => x.RawTitle == title).ToArray();
            if (!titleOnly && matches.Length == 1) return matches[0].Id;
            if (unresolved.TryGetValue((key, title), out var previous)) return previous;
            var candidates = (scoreTitles.GetValueOrDefault(title) ?? []).Select(x => x.Id).ToArray();
            var id = Id(); unresolved.Add((key, title), id); unresolvedIds.Add(id);
            scores.Add(new(id, title, null, Group(title), true, ScoreAvailability.Uncertain));
            var reason = titleOnly ? "title-only" : candidates.Length > 0 ? "ambiguous" : "missing";
            mappings.Add(new("score", key, id, title, reason, candidates));
            Issue("import.scoreUnresolved", sourceFile, key, "保留未解决引用（" + reason + "）");
            return id;
        }
        private string? Instrument(string? raw, string sourceFile, string oldId)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var normalized = ScoreNameRules.Normalize(raw);
            var matched = MusicLibraryEditor.InstrumentCatalog.Where(x => x.Aliases.Append(x.Id)
                .Any(a => ScoreNameRules.Normalize(a) == normalized)).Select(x => x.Id).ToArray();
            var id = matched.Length == 1 ? matched[0] : null;
            mappings.Add(new("instrument", oldId, id, raw, id is null ? "unmapped" : "catalog-alias", matched));
            if (id is null) Issue("import.instrumentUnmapped", sourceFile, oldId, "原乐器名称尚未关联标准目录：" + raw);
            return id;
        }
        private void LoadInstruments()
        {
            if (!Document("instruments.json", out var document)) return;
            var at = Timestamp(Property(document, "fetched_at"), optional: true);
            var rows = Array(Property(document, "items"), 2000);
            for (var index = 0; index < rows.Length; index++)
            {
                var name = Name(rows[index]);
                instruments.Add(new(Id(), name, Instrument(name, file, index.ToString(CultureInfo.InvariantCulture)), at, rows[index]));
            }
        }
        private void LoadDurations()
        {
            if (!Document("durations.json", out var document)) return;
            if (document.ValueKind != JsonValueKind.Object) throw Invalid(file, "时长文件须为标题到秒数的对象");
            foreach (var pair in document.EnumerateObject())
            {
                if (durationByTitle.Count >= ScoreCatalog.MaximumEntries) throw new OperationException("resource.capacityExceeded", "时长条目超过上限");
                MusicLibraryEditor.Text(pair.Name, 4096);
                if (!pair.Value.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds is <= 0 or > 86400)
                    throw Invalid(file, "时长须为一日以内的有限正秒数");
                durationByTitle.Add(pair.Name, seconds * 1000);
                var targets = scoreTitles.GetValueOrDefault(pair.Name)?.Select(x => x.Id).ToArray()
                    ?? [Resolve(pair.Name, pair.Name, file, titleOnly: true)];
                foreach (var target in targets)
                    durations.Add(new(target, null, seconds * 1000, null, 0, TimelineEvidenceKind.ImportedHistoryEstimate,
                        ObservationQuality.Estimated, source.Scope.Provider, "mobifolio-import-v1", source.SourceHash, now));
            }
        }
        private void LoadArtists()
        {
            if (!Document("artists.json", out var document)) return;
            foreach (var existing in artists)
                foreach (var name in existing.Aliases.Prepend(existing.Name)) artistNames[ScoreNameRules.Normalize(name)] = existing.Id;
            var rows = Array(Property(document, "artists"), ScoreNameRules.MaxArtists);
            foreach (var row in rows)
            {
                var old = Text(Property(row, "id"), 128);
                var name = Text(Property(row, "name"), 128);
                var aliases = Property(row, "aliases").ValueKind == JsonValueKind.Undefined ? [] :
                    Array(Property(row, "aliases"), 32).Select(x => Text(x, 128)).ToArray();
                string id;
                if (source.ArtistMappings.TryGetValue(old, out var target))
                {
                    if (!current.NameSettings.Artists.Any(x => x.Id == target)) throw Invalid(file, "歌手映射目标不存在");
                    id = target;
                }
                else
                {
                    id = Id();
                    var names = aliases.Prepend(name).Select(ScoreNameRules.Normalize).ToHashSet(StringComparer.Ordinal);
                    var conflicts = names.Where(artistNames.ContainsKey).Select(x => artistNames[x]).Distinct(StringComparer.Ordinal).ToArray();
                    if (conflicts.Length > 0)
                    { Issue("import.artistConflict", file, old, "歌手名称或别名已有其他归属", true); mappings.Add(new("artist-conflict", old, null, name, "conflict", conflicts)); }
                    artists.Add(new(id, name, aliases));
                    foreach (var normalized in names) artistNames.TryAdd(normalized, id);
                }
                if (!artistIds.TryAdd(old, id)) throw Invalid(file, "歌手旧 ID 重复");
                importedArtists++;
                mappings.Add(new("artist", old, id, name, source.ArtistMappings.ContainsKey(old) ? "explicit-mapping" : "new-local-id", []));
            }
            var assigned = Property(document, "assign");
            if (assigned.ValueKind == JsonValueKind.Undefined) return;
            if (assigned.ValueKind != JsonValueKind.Object) throw Invalid(file, "歌手指定须为对象");
            var count = 0;
            foreach (var pair in assigned.EnumerateObject())
            {
                if (++count > ScoreNameRules.MaxScores) throw new OperationException("resource.capacityExceeded", "歌手指定超过上限");
                var old = Text(pair.Value, 128);
                if (!artistIds.TryGetValue(old, out var artist))
                { Issue("import.artistConflict", file, old, "指定引用的旧歌手不存在", true); continue; }
                var targets = scoreTitles.GetValueOrDefault(pair.Name)?.Select(x => x.Id).ToArray() ?? [Resolve(pair.Name, pair.Name, file, true)];
                foreach (var score in targets) assignments[score] = artist;
            }
        }
        private string[] Noise()
        {
            if (!source.Documents.TryGetValue("artists.json", out var document) || Property(document, "noise").ValueKind == JsonValueKind.Undefined) return [];
            file = "artists.json"; return Array(Property(document, "noise"), 256).Select(x => Text(x, 128)).ToArray();
        }
        private void LoadPlaylists()
        {
            if (!Document("playlists.json", out var document)) return;
            var folderRows = Array(Property(document, "folders"), MusicLibraryEditor.MaximumFolders);
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in folderRows)
            { var old = Text(Property(row, "id"), 128); if (!ids.TryAdd(old, Id())) throw Invalid(file, "文件夹旧 ID 重复"); }
            for (var index = 0; index < folderRows.Length; index++)
            {
                var row = folderRows[index]; var old = Text(Property(row, "id"), 128);
                var parent = OptionalText(Property(row, "parent"));
                if (parent is not null && !ids.ContainsKey(parent)) throw Invalid(file, "文件夹父级旧 ID 不存在");
                var name = Text(Property(row, "name"), 200);
                folders.Add(new(ids[old], parent is null ? null : ids[parent], name, index.ToString("D6", CultureInfo.InvariantCulture)));
                mappings.Add(new("folder", old, ids[old], name, "new-local-id", []));
            }
            var rows = Array(Property(document, "playlists"), MusicLibraryEditor.MaximumPlaylists);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index]; var old = Text(Property(row, "id"), 128);
                if (!seen.Add(old)) throw Invalid(file, "列表旧 ID 重复");
                var id = Id(); var name = Text(Property(row, "name"), 200); var folder = OptionalText(Property(row, "folder"));
                if (folder is not null && !ids.ContainsKey(folder)) throw Invalid(file, "列表所属文件夹旧 ID 不存在");
                var items = Array(Property(row, "items"), MusicLibraryEditor.MaximumItems);
                if (playlists.Sum(x => x.Items.Count) + items.Length > MusicLibraryEditor.MaximumItems)
                    throw new OperationException("resource.capacityExceeded", "列表条目总量超过上限");
                var memo = Property(row, "memo");
                playlists.Add(new(id, folder is null ? null : ids[folder], name, memo.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : Text(memo, 4000, true, true), null,
                    index.ToString("D6", CultureInfo.InvariantCulture), items.Select((item, i) => Item(item, old + "/" + i)).ToArray()));
                mappings.Add(new("playlist", old, id, name, "new-local-id", []));
            }
        }
        private (string Key, string Title, string? Instrument, bool TitleOnly) Reference(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String) { var legacyTitle = Text(value, 4096); return (legacyTitle, legacyTitle, null, true); }
            var titleField = Property(value, "title");
            var title = Text(titleField, 4096);
            var key = OptionalText(Property(value, "key"), 4352);
            return (key ?? title, title, OptionalText(Property(value, "inst"), 4096), key is null);
        }
        private PlaylistItem Item(JsonElement value, string old)
        {
            var reference = Reference(value); var id = Id();
            var score = Resolve(reference.Key, reference.Title, file, reference.TitleOnly);
            var instrument = Instrument(reference.Instrument, file, old);
            mappings.Add(new("playlist-item", old, id, reference.Title, unresolvedIds.Contains(score) ? "unresolved-score" : "snapshot-key", [score]));
            return new(id, score, instrument, old[(old.LastIndexOf('/') + 1)..].PadLeft(6, '0'));
        }
        private DateTimeOffset? Timestamp(JsonElement value, bool optional = false)
        {
            if (optional && value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < 0 ||
                seconds > now.AddDays(1).ToUnixTimeMilliseconds() / 1000d) throw Invalid(file, "时间戳须为有效 Unix 秒数");
            return DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(seconds * 1000)));
        }
        private void LoadHistory()
        {
            if (!Document("recent.json", out var document)) return;
            var rows = Array(document, 2000);
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i]; var reference = Reference(row); var id = Id();
                var score = Resolve(reference.Key, reference.Title, file, reference.TitleOnly);
                history.Add(new(id, score, null, Instrument(reference.Instrument, file, i.ToString(CultureInfo.InvariantCulture)), Timestamp(Property(row, "ts"))!.Value,
                    durationByTitle.TryGetValue(reference.Title, out var duration) ? duration : null,
                    TimelineEvidenceKind.ImportedHistoryEstimate, ObservationQuality.Estimated, source.Scope.Provider));
                mappings.Add(new("history", i.ToString(CultureInfo.InvariantCulture), id, reference.Title, "imported-estimate", [score]));
            }
        }
        private void LoadPreferences()
        {
            if (!Document("settings.json", out var document)) return;
            if (document.ValueKind != JsonValueKind.Object) throw Invalid(file, "设置须为对象");
            double Number(string key, double fallback, double maximum)
            {
                var field = Property(document, key); if (field.ValueKind == JsonValueKind.Undefined) return fallback;
                double number = 0;
                var valid = field.ValueKind == JsonValueKind.Number ? field.TryGetDouble(out number) :
                    field.ValueKind == JsonValueKind.String && double.TryParse(field.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                if (!valid || double.IsNaN(number))
                { Issue("import.settingAdjusted", file, key, "沿用旧设置规则恢复默认值"); return fallback; }
                var value = Math.Clamp(number, 0, maximum);
                if (value != number) Issue("import.settingAdjusted", file, key, "沿用旧设置范围裁剪数值");
                return value;
            }
            bool Boolean(string key, bool fallback)
            {
                var field = Property(document, key); return field.ValueKind switch
                { JsonValueKind.Undefined => fallback, JsonValueKind.True => true, JsonValueKind.False => false,
                    JsonValueKind.Number => field.GetDouble() != 0,
                    JsonValueKind.String => field.GetString()!.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on",
                    _ => fallback };
            }
            var presentation = new Dictionary<string, JsonElement>();
            foreach (var key in new[] { "ov_perf", "ov_nearby", "ov_env", "ov_scale", "ov_alpha", "ov_banner_sec" })
                if (Property(document, key).ValueKind != JsonValueKind.Undefined) presentation.Add(key, Property(document, key));
            preferences = new(Number("gap_sec", 2, 60), Number("advance_margin", 2, 30),
                Instrument(OptionalText(Property(document, "default_inst"), 4096), file, "default_inst"), Boolean("auto_sync", true),
                Boolean("stop_before_play", true), Boolean("ensemble", true), JsonSerializer.SerializeToElement(presentation));
            foreach (var key in new[] { "cli_exe", "update_url", "update_check", "ov_alter" })
                if (Property(document, key).ValueKind != JsonValueKind.Undefined)
                    Issue("import.legacySettingReview", file, key, "旧设置已列入迁移提示：" + key);
        }
    }
}
