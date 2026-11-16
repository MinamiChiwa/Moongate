using System.Text.Json;
using MabiTool.Modules.MusicLibrary.Domain;
using Xunit;

namespace MabiTool.MusicLibrary.Tests;

public sealed class ScoreNameTests
{
    private static ScoreNameOptions Options(ScoreRegion region = ScoreRegion.TW) => new(region, [], new Dictionary<string, string>(), []);

    [Fact]
    public void KoreanRulesMatchNinetyFiveFixedUpstreamCases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/korean-names.json")));
        var root = fixture.RootElement;
        Assert.Equal("78d2b2a836ee9931500f72a9af7280018e26d26c", root.GetProperty("commit").GetString());
        var state = root.GetProperty("state");
        var artists = state.GetProperty("artists").EnumerateArray().Select(a => new ArtistDefinition(
            a.GetProperty("id").GetString()!, a.GetProperty("name").GetString()!,
            a.GetProperty("aliases").EnumerateArray().Select(x => x.GetString()!).ToArray())).ToArray();
        var assign = state.GetProperty("assign").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var result = ScoreNameRules.Classify(cases.Select(c => c.GetProperty("title").GetString()!).ToArray(),
            new(ScoreRegion.KR, artists, assign, state.GetProperty("noise").EnumerateArray().Select(n => n.GetString()!).ToArray()));
        Assert.Equal(95, result.Count);
        for (var i = 0; i < cases.Length; i++)
        {
            var expected = cases[i]; var actual = result[i];
            var fields = new Dictionary<string, string> { ["title"] = actual.OriginalTitle, ["cleaned"] = actual.Cleaned,
                ["variant"] = actual.Variant, ["artist"] = actual.Artist, ["artistKey"] = actual.ArtistKey,
                ["song"] = actual.Song, ["rule"] = actual.Rule, ["initial"] = actual.Initial };
            foreach (var (field, value) in fields)
                Assert.True(expected.GetProperty(field).GetString() == value,
                    $"样本 {i} ({actual.OriginalTitle}) {field}: expected={expected.GetProperty(field).GetString()} actual={value}");
            Assert.Equal(expected.GetProperty("removed").EnumerateArray().Select(x => x.GetString()), actual.Removed);
            Assert.Equal(expected.GetProperty("tags").EnumerateArray().Select(x => x.GetString()), actual.Tags);
        }
    }

    [Fact]
    public void TraditionalPolicyPreservesRawNamesAndMeaningfulInteriorWords()
    {
        string[] titles = ["  樂譜：電影_周杰倫 - 晴天 完成版  ", "周杰倫 - 夜曲 測試", "練習愛情", "鋼琴上的秘密", "【晴天】", "Route 66", "周杰倫 - 22", "樂譜：五月天 - 溫柔-2"];
        var result = ScoreNameRules.Classify(titles, Options());
        Assert.Equal(titles, result.Select(r => r.OriginalTitle));
        Assert.Equal("周杰倫 - 晴天", result[0].Cleaned);
        Assert.Equal("周杰倫", result[0].Artist);
        Assert.Equal("晴天", result[0].Song);
        Assert.Contains("電影", result[0].Tags);
        Assert.Equal("周杰倫 - 夜曲", result[1].Cleaned);
        Assert.Equal("練習愛情", result[2].Cleaned);
        Assert.Equal("鋼琴上的秘密", result[3].Cleaned);
        Assert.Equal("【晴天】", result[4].Cleaned);
        Assert.Equal("Route 66", result[5].Song);
        Assert.Equal("22", result[6].Song);
        Assert.Equal("2", result[7].Variant);
        Assert.All(result, r => Assert.Equal(ScoreNameRules.Version + ":TW", r.RulesVersion));
        Assert.NotEqual(result[0].Cleaned, ScoreNameRules.Classify([titles[0]], Options(ScoreRegion.KR))[0].Cleaned);
    }

    [Fact]
    public void AliasesAssignmentsAndDictionaryBoundariesHaveStablePrecedence()
    {
        var artist = new ArtistDefinition("jay", "周杰倫", ["Jay Chou", "杰倫"]);
        var other = new ArtistDefinition("iu", "아이유", ["IU"]);
        var result = ScoreNameRules.Classify(["Jay Chou - 晴天", "杰倫 夜曲", "海底世界 Aquarium", "Jay Chou", "手動指定", "周杰倫 - 晴天"],
            new(ScoreRegion.TW, [artist, other], new Dictionary<string, string> { ["手動指定"] = "jay", ["周杰倫 - 晴天"] = "iu" }, []));
        Assert.Equal("jay", result[0].ArtistKey);
        Assert.Equal("夜曲", result[1].Song);
        Assert.Equal("none", result[2].Rule);
        Assert.Equal("none", result[3].Rule);
        Assert.Equal("manual", result[4].Rule);
        Assert.Equal("iu", result[5].ArtistKey);
        Assert.Equal("晴天", result[5].Song);
        Assert.Equal([0, 1, 3, 4], ScoreNameRules.Search(result, "Jay Chou").Select(r => r.SourceIndex));
        Assert.Equal([0, 5], ScoreNameRules.Search(result, "晴天 杰倫").Select(r => r.SourceIndex));
        Assert.Equal(result.Count, ScoreNameRules.Search(result, "").Count);
    }

    [Fact]
    public void SearchIncludesTagsAndNormalizationAndSortIsStable()
    {
        var result = ScoreNameRules.Classify(["電影_歌手 - 夜曲", "歌手 - 晴天", "歌手 - 夜曲", "ＡＢＣ - Ｓｏｎｇ"], Options());
        Assert.Equal([0], ScoreNameRules.Search(result, "電影 夜曲").Select(r => r.SourceIndex));
        Assert.Equal([3], ScoreNameRules.Search(result, "abc song").Select(r => r.SourceIndex));
        Assert.Equal([3, 0, 2, 1], ScoreNameRules.Sort(result, ScoreRegion.TW).Select(r => r.SourceIndex));
        var korean = ScoreNameRules.Classify(["Zebra", "나비", "가을", "22", "Apple"], Options(ScoreRegion.KR));
        Assert.Equal([2, 1, 4, 0, 3], ScoreNameRules.Sort(korean, ScoreRegion.KR).Select(r => r.SourceIndex));
    }

    [Fact]
    public void ConfigurationIsSnapshottedAndAliasMergeUsesCanonicalIdentity()
    {
        var aliases = new List<string> { "Old Band", "新樂團" };
        var noise = new List<string> { "自訂詞" };
        var options = new ScoreNameOptions(ScoreRegion.TW, [new("band", "樂團", aliases)], new Dictionary<string, string>(), noise);
        var result = ScoreNameRules.Classify(["Old Band - 歌曲 自訂詞", "新樂團 - 新歌"], options);
        Assert.All(result, r => Assert.Equal("band", r.ArtistKey));
        aliases.Clear(); noise.Clear();
        Assert.Equal(2, ScoreNameRules.Search(result, "Old Band").Count);
        Assert.Equal("歌曲", result[0].Song);
        Assert.Contains("自訂詞", result[0].Removed);
    }

    [Fact]
    public void InvalidAndOversizedInputsAreRejectedWithoutPartialResults()
    {
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify(new string[ScoreNameRules.MaxScores + 1], Options()));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify([new string('a', 65_537)], Options()));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify(Enumerable.Repeat(new string('a', 65_536), 65).ToArray(), Options()));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify(["\uD800"], Options()));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify(["歌"], Options((ScoreRegion)8)));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify(["歌"], new(ScoreRegion.TW,
            [new("a", "歌手A", ["same"]), new("b", "歌手B", ["SAME"])], new Dictionary<string, string>(), [])));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify(["歌"], new(ScoreRegion.TW, [], new Dictionary<string, string> { ["歌"] = "missing" }, [])));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify(["歌"], Options() with { Noise = Enumerable.Repeat("x", 257).ToArray() }));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Classify(["歌"], new(ScoreRegion.TW,
            [new("a", "歌手A", []), new("b", "歌手B", [])],
            new Dictionary<string, string> { ["歌 曲"] = "a", ["歌曲"] = "b" }, [])));
        Assert.Throws<ArgumentException>(() => ScoreNameRules.Search([], new string('a', 1025)));
    }

    [Fact]
    public void NormalizedBudgetCountsUnicodeScalarsAndRetainsTheOriginal()
    {
        var title = string.Concat(Enumerable.Repeat("😀", 299)) + "晴天";
        var result = ScoreNameRules.Classify([title], Options());
        Assert.Equal(title, result[0].OriginalTitle);
        Assert.Equal("晴", result[0].Cleaned);
        Assert.Equal("ㄱ", ScoreNameRules.Initial("★까치"));
        Assert.Equal("#", ScoreNameRules.Initial("😀晴天"));
    }
}
