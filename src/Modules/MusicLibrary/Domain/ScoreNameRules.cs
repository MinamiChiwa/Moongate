// 源自 Mobi-Lan/MobiFolio library.py，提交 78d2b2a836ee9931500f72a9af7280018e26d26c。
// Copyright (c) 2026 란님；适用仓库根 LICENSE 中的 MIT 声明。
using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace MabiTool.Modules.MusicLibrary.Domain;

public enum ScoreRegion { KR, TW }
public sealed record ArtistDefinition(string Id, string Name, IReadOnlyList<string> Aliases);
public sealed record ScoreNameOptions(ScoreRegion Region, IReadOnlyList<ArtistDefinition> Artists,
    IReadOnlyDictionary<string, string> Assignments, IReadOnlyList<string> Noise);
public sealed record CleanedTitle(string Text, IReadOnlyList<string> Removed, IReadOnlyList<string> Tags, string Variant);
public sealed record ClassifiedScoreName(int SourceIndex, string OriginalTitle, string Cleaned,
    IReadOnlyList<string> Removed, IReadOnlyList<string> Tags, string Variant, string Artist,
    string ArtistKey, string Song, string Rule, string Initial, string SearchIndex, string RulesVersion);

/// <summary>离线名称规则；不产生库存身份、不访问游戏或修改输入配置。</summary>
public static class ScoreNameRules
{
    public const string Version = "mobifolio-78d2b2a-regional-v1";
    public const int MaxScores = 20_000;
    public const int MaxArtists = 1_000;
    public const int MaxTitleRunes = 300;
    public const int MaxBatchCharacters = 4 * 1024 * 1024;
    private const string TrimChars = " -_·•.,:;/|";
    private const string Separator = "[\\s\\-_·•.,()\\[\\]{}~!?/\\\\|:;\"'`+*&%$#@^=<>【】「」『』〈〉《》]+";
    private const string Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    private const string InitialOrder = "ㄱㄴㄷㄹㅁㅂㅅㅇㅈㅊㅋㅌㅍㅎABCDEFGHIJKLMNOPQRSTUVWXYZ#";
    private static readonly string[] Splitters = [" - ", " – ", " — ", " / ", " | ", " : ", " by ", " BY ", " -", "- ", "-", "–", "—", "_", "/", "|"];
    private static readonly string[] Prefixes = [@"^악보\s*[:：]\s*", @"^[!@#$%*]+\s*", @"^[A-Za-z]_(?=\S)", @"^[!@#]_", @"^\d{1,2}\.\s*(?=\D)"];
    private static readonly string[] DefaultNoise = ["악보", "ver", "ver.", "version", "버전", "cover", "커버", "remix", "리믹스", "편곡", "피아노", "piano", "ost", "full", "풀", "완성", "완성본", "미완", "미완성", "test", "테스트", "연습", "수정", "수정본", "final", "최종", "mr", "inst", "inst.", "원곡", "orig", "original", "short", "숏", "long", "롱", "easy", "쉬움", "hard", "v1", "v2", "v3", "v4", "v5", "1절", "2절", "무료", "공유", "배포", "자작", "자동", "auto", "복사본", "copy"];
    private static readonly string[] Categories = ["동요", "영화", "뮤지컬", "애니", "애니메이션", "게임", "클래식", "jpop", "kpop", "j-pop", "k-pop", "ost", "재즈버전", "재즈", "jazz", "피아노", "piano", "임시", "연습", "테스트", "test"];
    private static readonly string[] TwNoise = ["樂譜", "完成版", "完整版", "未完成", "練習", "測試", "修正版", "最終版", "複製本"];
    private static readonly string[] TwCategories = ["兒歌", "電影", "音樂劇", "動畫", "遊戲", "古典", "爵士", "鋼琴", "練習", "測試"];
    private static readonly string[] Brackets = [@"\[[^\]]*\]", @"\([^)]*\)", "【[^】]*】", "「[^」]*」", "『[^』]*』", "〈[^〉]*〉", "《[^》]*》", @"\{[^}]*\}"];
    private static readonly ConcurrentDictionary<(string, bool), Regex> Patterns = new();
    private static Regex Rx(string pattern, bool ignoreCase = false) => Patterns.GetOrAdd((pattern, ignoreCase), key => new(key.Item1,
        RegexOptions.CultureInvariant | (key.Item2 ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(250)));
    private static readonly Regex Separators = Rx(Separator);
    private static readonly Regex Numeric = Rx(@"^[\d\s.]+$");
    private static readonly Regex Variant = Rx(@"[\s\-_]*(?:-|_|\s)(\d{1,2}|\d+합|test|TEST|테스트|재즈|jazz|rock\.?ver|Rock\.ver)\s*$");
    private static readonly Regex AttachedTag = Rx(@"(?:(?<=[가-힣])|(?<![A-Za-z]))(OST)\s*$");
    private static readonly Regex NoiseVersion = Rx(@"^(?:v\d+|ver\.?\d*|\d+절)$", true);

    public static string Normalize(string value) => Separators.Replace(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), "");
    private static string[] Tokens(string value) => Separators.Split(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()).Where(x => x.Length > 0).ToArray();
    private static string Limit(string title) => string.Concat(title.EnumerateRunes().Take(MaxTitleRunes).Select(r => r.ToString()));
    private static string[] ReadOnlyCopy(IEnumerable<string> values) => values.ToArray();

    public static string Initial(string value)
    {
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            var code = rune.Value;
            if (code is >= 0xAC00 and <= 0xD7A3)
                return Cho[(code - 0xAC00) / 588] switch { 'ㄲ' => "ㄱ", 'ㄸ' => "ㄷ", 'ㅃ' => "ㅂ", 'ㅆ' => "ㅅ", 'ㅉ' => "ㅈ", var c => c.ToString() };
            if (code is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return rune.ToString().ToUpperInvariant();
            if (Rune.IsDigit(rune)) return "#";
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned) continue;
            return "#";
        }
        return "#";
    }

    public static CleanedTitle Clean(string title, ScoreRegion region, IReadOnlyList<string>? customNoise = null)
    {
        if (!Enum.IsDefined(region)) throw new ArgumentException("未知地区策略");
        ValidateText(title, 65_536, "标题");
        var noise = (customNoise ?? []).ToArray();
        if (noise.Length > 256) throw new ArgumentException("自定义噪声词超过 256 项");
        foreach (var word in noise) ValidateText(word, 128, "噪声词");
        return CleanCore(Limit(title), region, NoiseWords(region, noise));
    }

    private static HashSet<string> NoiseWords(ScoreRegion region, IReadOnlyList<string> noise) =>
        DefaultNoise.Concat(noise).Concat(region == ScoreRegion.TW ? TwNoise : [])
            .Select(w => w.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    private static CleanedTitle CleanCore(string title, ScoreRegion region, HashSet<string> words)
    {
        List<string> removed = [], tags = [];
        var variant = "";
        var normalized = title.Normalize(NormalizationForm.FormKC).Trim();
        var t = string.Concat(normalized.EnumerateRunes().Select(r =>
            Rune.GetUnicodeCategory(r) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.Format
            || "★☆♪♫♬♥❤✨".Contains(r.ToString(), StringComparison.Ordinal) ? " " : r.ToString()));
        var prefixes = region == ScoreRegion.TW ? new[] { @"^(?:樂譜|乐谱)\s*[:：]\s*" }.Concat(Prefixes) : Prefixes;
        foreach (var pattern in prefixes)
        {
            var next = Rx(pattern).Replace(t, "");
            if (next != t) { removed.Add(t[..(t.Length - next.Length)]); t = next.Trim(); }
        }
        var basis = t;
        var m = Variant.Match(t);
        if (m.Success && t[..m.Index].EnumerateRunes().Count() >= 2)
        {
            var head = t[..m.Index];
            var tail = m.Value;
            var attached = (tail.Contains('-') || tail.Contains('_')) && !Rx(@"\s[-_]\s").IsMatch(tail);
            if (!m.Groups[1].Value.All(char.IsDigit) || attached || RawLeft(head) != "")
            { variant = m.Groups[1].Value; t = head.Trim(); }
        }
        m = AttachedTag.Match(t);
        if (m.Success && t[..m.Index].EnumerateRunes().Count() >= 2)
        { tags.Add("OST"); t = t[..m.Index].Trim(" -_·•.,:/".ToCharArray()); }
        foreach (var word in region == ScoreRegion.TW ? Categories.Concat(TwCategories) : Categories)
        {
            var pattern = Rx(@"^" + Regex.Escape(word) + @"\s*[_\-:/]+\s*|(?:^|(?<=[\s_\-:/(\[]))" + Regex.Escape(word) + @"\s*$", true);
            if (!pattern.IsMatch(t)) continue;
            var next = pattern.Replace(t, " ").Trim(" -_·•.,:/".ToCharArray());
            if (next.Length > 0) { tags.Add(word.All(char.IsAscii) ? word.ToUpperInvariant() : word); t = next; }
        }
        foreach (var pattern in Brackets)
        {
            List<string> matches = [];
            var next = Rx(pattern).Replace(t, match => { matches.Add(match.Value); return " "; });
            if (!string.IsNullOrWhiteSpace(next)) { removed.AddRange(matches); t = next; }
        }
        var tokens = Rx(@"\s+").Split(t).Where(x => x.Length > 0).ToList();
        while (tokens.Count > 1)
        {
            var core = tokens[^1].Trim(" .-_()[]".ToCharArray());
            if (core.Length == 0 || (!words.Contains(core.ToLowerInvariant()) && !NoiseVersion.IsMatch(core))) break;
            removed.Add(tokens[^1]); tokens.RemoveAt(tokens.Count - 1);
        }
        t = string.Join(' ', tokens).Trim(TrimChars.ToCharArray());
        if (t.Length == 0) t = basis.Trim(TrimChars.ToCharArray());
        if (t.Length == 0) t = Rx(Prefixes[0]).Replace(title.Trim(), "").Trim();
        if (t.Length == 0) t = title.Trim();
        return new(t, removed.AsReadOnly(), tags.AsReadOnly(), variant);
    }

    private static string RawLeft(string title)
    {
        foreach (var sep in Splitters)
        {
            var index = title.IndexOf(sep, StringComparison.Ordinal);
            if (index < 0) continue;
            var a = title[..index].Trim(" -_·•.,".ToCharArray());
            var b = title[(index + sep.Length)..].Trim(" -_·•.,".ToCharArray());
            if (a.Length > 0 && b.Length > 0) return a;
        }
        return "";
    }

    private static (string Artist, string Song) Split(string title, HashSet<string> numericArtists)
    {
        foreach (var sep in Splitters)
        {
            var index = title.IndexOf(sep, StringComparison.Ordinal);
            if (index < 0) continue;
            var a = title[..index].Trim(" -_·•,".ToCharArray());
            var b = title[(index + sep.Length)..].Trim(" -_·•.,".ToCharArray());
            if (a.Length == 0 || b.Length == 0) continue;
            if (sep.Trim().Equals("by", StringComparison.OrdinalIgnoreCase)) (a, b) = (b, a);
            if (Numeric.IsMatch(a) && numericArtists.Contains(Normalize(a))) return (a, b);
            if (Numeric.IsMatch(a) && !Numeric.IsMatch(b)) return (b, a);
            if (Numeric.IsMatch(b) && !Numeric.IsMatch(a)) return (a, b);
            if (Numeric.IsMatch(a) && Numeric.IsMatch(b)) return ("", title);
            if (a.EnumerateRunes().Count() > 24 && b.EnumerateRunes().Count() <= 24) return (b, a);
            if (a.EnumerateRunes().Count() >= 8 && Rx(@"\d").IsMatch(a) && Rx("[가-힣A-Za-z]").IsMatch(a) && !Numeric.IsMatch(a)) return ("", b);
            return (a, b);
        }
        return ("", title);
    }

    public static IReadOnlyList<ClassifiedScoreName> Classify(IReadOnlyList<string> titles, ScoreNameOptions options)
    {
        if (titles.Count > MaxScores) throw new ArgumentException("标题超过 20000 项");
        if (!Enum.IsDefined(options.Region)) throw new ArgumentException("未知地区策略");
        if (options.Artists.Count > MaxArtists || options.Assignments.Count > MaxScores || options.Noise.Count > 256
            || options.Artists.Any(a => a.Aliases.Count > 32)) throw new ArgumentException("名称配置超过容量");
        var input = titles.ToArray();
        if (input.Sum(t => (long)(t?.Length ?? 0)) > MaxBatchCharacters) throw new ArgumentException("标题批次超过总长度预算");
        foreach (var title in input) ValidateText(title, 65_536, "标题");
        var artists = options.Artists.Select(a => new ArtistDefinition(a.Id, a.Name, ReadOnlyCopy(a.Aliases))).ToArray();
        var assignments = options.Assignments.ToArray();
        var noise = options.Noise.ToArray();
        if (artists.Length > MaxArtists || assignments.Length > MaxScores || noise.Length > 256)
            throw new ArgumentException("名称配置超过容量");
        if (assignments.Sum(p => (long)(p.Key?.Length ?? 0)) > MaxBatchCharacters) throw new ArgumentException("指定标题超过总长度预算");
        foreach (var word in noise) ValidateText(word, 128, "噪声词");
        var byId = new Dictionary<string, ArtistDefinition>(StringComparer.Ordinal);
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var artist in artists)
        {
            ValidateText(artist.Id, 128, "歌手 ID"); ValidateText(artist.Name, 128, "歌手名称");
            if (string.IsNullOrWhiteSpace(artist.Id) || Normalize(artist.Name).Length == 0 || !byId.TryAdd(artist.Id, artist) || artist.Aliases.Count > 32)
                throw new ArgumentException("歌手 ID、名称或别名配置无效");
            foreach (var name in artist.Aliases.Prepend(artist.Name))
            {
                ValidateText(name, 128, "歌手别名"); var key = Normalize(name);
                if (key.Length == 0 || (byName.TryGetValue(key, out var other) && other != artist.Id))
                    throw new ArgumentException("歌手别名为空或归属冲突");
                byName[key] = artist.Id;
            }
        }
        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in assignments)
        {
            ValidateText(pair.Key, 65_536, "指定标题");
            if (!byId.ContainsKey(pair.Value)) throw new ArgumentException("手工指定的歌手不存在");
            var key = Normalize(pair.Key);
            if (assigned.TryGetValue(key, out var other) && other != pair.Value) throw new ArgumentException("规范化标题指定冲突");
            assigned[key] = pair.Value;
        }
        var words = NoiseWords(options.Region, noise);
        var cleaned = input.Select(t => CleanCore(Limit(t), options.Region, words)).ToArray();
        var numericArtists = cleaned.Select(c => (Left: RawLeft(c.Text), Text: Normalize(c.Text)))
            .Where(c => c.Left.Length > 0 && Numeric.IsMatch(c.Left)).GroupBy(c => Normalize(c.Left))
            .Where(g => g.Select(c => c.Text).Distinct().Count() >= 2).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var preliminary = cleaned.Select(c => Split(c.Text, numericArtists)).Select(p =>
            p.Artist.Length > 0 && byName.ContainsKey(Normalize(p.Song)) && !byName.ContainsKey(Normalize(p.Artist)) ? (Artist: p.Song, Song: p.Artist) : p).ToArray();
        var automatic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in preliminary)
            if (Normalize(p.Artist).EnumerateRunes().Count() >= 2 && !byName.ContainsKey(Normalize(p.Artist))) automatic.TryAdd(Normalize(p.Artist), p.Artist);
        List<ClassifiedScoreName> result = [];
        for (var i = 0; i < input.Length; i++)
        {
            var clean = cleaned[i]; var (guess, song) = preliminary[i];
            string artist = "", artistKey = "", rule = "none";
            if (assigned.TryGetValue(Normalize(input[i]), out var id))
            { artist = byId[id].Name; artistKey = id; rule = "manual"; }
            else if (byName.TryGetValue(Normalize(guess), out id))
            { artist = byId[id].Name; artistKey = id; rule = "dict"; }
            else if (guess.Length > 0) { artist = automatic.GetValueOrDefault(Normalize(guess), guess); rule = "split"; }
            else
            {
                var hit = FindInTitle(clean.Text, byName, automatic);
                var rest = RemoveSpan(clean.Text, hit.Start, hit.Length);
                if (hit.Name.Length > 0 && rest.Length > 0)
                {
                    artist = hit.IsDictionary ? byId[hit.Name].Name : hit.Name;
                    artistKey = hit.IsDictionary ? hit.Name : "";
                    rule = hit.IsDictionary ? "dict" : "auto"; song = rest;
                }
            }
            if (rule == "manual" && guess.Length == 0 && song == clean.Text)
            {
                var hit = FindInTitle(clean.Text, byName, automatic);
                var rest = RemoveSpan(clean.Text, hit.Start, hit.Length);
                if (hit.Name == artistKey && rest.Length > 0) song = rest;
            }
            if (artistKey.Length == 0 && artist.Length > 0) artistKey = "auto:" + Normalize(artist);
            if (song.Length == 0) song = clean.Text;
            var aliases = byId.TryGetValue(artistKey, out var known) ? known.Aliases : [];
            var search = string.Join(' ', new[] { Normalize(input[i]), Normalize(clean.Text), Normalize(song), Normalize(artist) }
                .Concat(aliases.Select(Normalize)).Concat(clean.Tags.Select(Normalize)));
            result.Add(new(i, input[i], clean.Text, clean.Removed, clean.Tags, clean.Variant,
                artist, artistKey, song, rule, Initial(song.Length > 0 ? song : input[i]), search, Version + ":" + options.Region));
        }
        return result.AsReadOnly();
    }

    private static string RemoveSpan(string title, int start, int length) =>
        (title[..start] + " " + title[(start + length)..]).Trim(" -_·•.,:".ToCharArray());

    private static (string Name, bool IsDictionary, int Start, int Length) FindInTitle(string title,
        Dictionary<string, string> dictionary, Dictionary<string, string> automatic)
    {
        var boundaries = Separators.Matches(title).Select(m => (m.Index, m.Length)).ToList();
        List<(int Start, int End, string Key)> tokens = [];
        var start = 0;
        foreach (var (index, length) in boundaries.Append((title.Length, 0)))
        {
            if (index > start) tokens.Add((start, index, Normalize(title[start..index])));
            start = index + length;
        }
        var candidates = new Dictionary<string, (int Start, int Length)>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var key = "";
            for (var j = i; j < Math.Min(tokens.Count, i + 5); j++)
            { key += tokens[j].Key; candidates.TryAdd(key, (tokens[i].Start, tokens[j].End - tokens[i].Start)); }
        }
        (string Name, bool IsDictionary, int Start, int Length) best = ("", false, 0, 0);
        var bestKeyLength = 0;
        foreach (var (map, isDictionary) in new[] { (dictionary, true), (automatic, false) })
            foreach (var (key, name) in map)
                if (key.EnumerateRunes().Count() > bestKeyLength && candidates.TryGetValue(key, out var span))
                { best = (name, isDictionary, span.Start, span.Length); bestKeyLength = key.EnumerateRunes().Count(); }
        return best;
    }

    public static IReadOnlyList<ClassifiedScoreName> Search(IReadOnlyList<ClassifiedScoreName> items, string query)
    {
        ValidateText(query, 1024, "搜索词");
        var terms = Tokens(query).Select(Normalize).Where(t => t.Length > 0).ToArray();
        return items.Where(item => terms.All(term => item.SearchIndex.Contains(term, StringComparison.Ordinal))).ToArray();
    }

    public static IReadOnlyList<ClassifiedScoreName> Sort(IReadOnlyList<ClassifiedScoreName> items, ScoreRegion region)
    {
        if (!Enum.IsDefined(region)) throw new ArgumentException("未知地区策略");
        return items.OrderBy(i => region == ScoreRegion.KR ? InitialOrder.IndexOf(i.Initial, StringComparison.Ordinal) : 0)
            .ThenBy(i => Normalize(i.Song), StringComparer.Ordinal).ThenBy(i => i.SourceIndex).ToArray();
    }

    private static void ValidateText(string text, int maxLength, string field)
    {
        if (text is null || text.Length > maxLength) throw new ArgumentException(field + "为空或超过长度上限");
        try { _ = text.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { throw new ArgumentException(field + "包含无效 Unicode"); }
    }
}
