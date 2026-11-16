using System.Numerics;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using Xunit;

namespace MabiTool.Desktop.Tests;

public sealed class CombatFirstAttackReaderTests
{
    private static readonly EntityKey Local = new("game", new(1), new(1), new(1));
    private static readonly EntityKey Target = new("game", new(1), new(2), new(1));
    private static readonly ObservationSource Source = new("test", "1", "TW", "synthetic");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static CombatEncounter Encounter(string id, ulong? first, string target = "target", bool owned = true)
    {
        var totals = new CombatDamageTotals(new(new BigInteger(1000)), new(0), new(0), new(0));
        CombatDamageGroup Group(string key) => new(key, key == "1000" ? "重擊" : "巨魔", totals, new(new BigInteger(1000)),
            new(10), new(10), null, null, FirstAttackCount: first is { } count ? new UInt64String(count) : null);
        return new(id, "owner", "obs", "game", new(1), new(1),
            new(id, Local, "琉特", new(new BigInteger(1000)), 10000, 100, null, 5000, "v1", true, "none", ObservationQuality.Confirmed, []),
            Now, Now, 0, 10000, owned, totals, totals, totals, totals, [Group("1000")], [Group(target)], [],
            new(0, 0, 0, 0, new(0), new(0), new(0), new(0)), false);
    }

    private static CombatTimelineItem Item(ulong index, double at, string attribution = "local", ulong amount = 100) => new(
        new(index), at, Now, "damage", new("d" + index, Source, Local, Target, new(1000), new(amount), CombatDamageKind.ActualHp,
            Now, at, new(index), 0, false, true, ObservationQuality.Confirmed, FirstAttack: true), null, null, attribution);

    private static CombatTimelinePage Page(string id, IReadOnlyList<CombatTimelineItem> items, string? cursor = null) =>
        new(id, new(1), items, cursor, [], [], [], false);

    private static Task<CombatEncounter> NoMetadata(string id, CancellationToken _) =>
        throw new InvalidOperationException("已有记录摘要，不应重复读取详情：" + id);

    [Fact]
    public async Task WholeRangeReadsTheNewestSegmentWithFirstAttacksAndReusesUnchangedCounts()
    {
        var reader = new LiveBackend.FirstAttackReader();
        var older = Encounter("older", 1); var newest = Encounter("newest", 0);
        var range = new LiveBackend.FirstAttackRange("observation|obs", Encounter("observation:obs", 1), [older.Id, newest.Id]);
        var queries = new List<string>();
        Task<CombatTimelinePage> Read(string id, CombatTimelineQuery query, CancellationToken _)
        { queries.Add(id); return Task.FromResult(Page(id, [Item(1, id == "older" ? 100 : 200)])); }

        Assert.Equal(100, Assert.Single((await reader.ReadAsync(range, [older, newest], NoMetadata, Read, Token))!).MonotonicMs);
        await reader.ReadAsync(range, [older, newest], NoMetadata, Read, Token);
        Assert.Equal(["older"], queries);

        newest = Encounter("newest", 1);
        range = range with { Summary = Encounter("observation:obs", 2) };
        Assert.Equal(200, Assert.Single((await reader.ReadAsync(range, [older, newest], NoMetadata, Read, Token))!).MonotonicMs);
        Assert.Equal(["older", "newest"], queries);
    }

    [Fact]
    public async Task TargetAndRecordSelectionClearTheOldResultAndKeepTheSelectedTimeWindow()
    {
        var reader = new LiveBackend.FirstAttackReader();
        var queries = new List<(string Id, CombatTimelineQuery Query)>();
        Task<CombatTimelinePage> Read(string id, CombatTimelineQuery query, CancellationToken _)
        {
            queries.Add((id, query));
            return Task.FromResult(Page(id, [Item(1, query.EntityId == "a" ? 300 : 600)]));
        }
        var first = new LiveBackend.FirstAttackRange("segment|a", Encounter("enc", 1, "a"), ["enc"], "a", 200, 800);
        Assert.Equal(300, Assert.Single((await reader.ReadAsync(first, [], NoMetadata, Read, Token))!).MonotonicMs);
        var second = first with { Key = "segment|b", Summary = Encounter("enc", 1, "b"), TargetId = "b" };
        Assert.Equal(600, Assert.Single((await reader.ReadAsync(second, [], NoMetadata, Read, Token))!).MonotonicMs);
        var saved = new LiveBackend.FirstAttackRange("measurement:saved|a", Encounter("saved", 1, "a"), ["saved"], "a", 200, 800);
        Assert.Equal(300, Assert.Single((await reader.ReadAsync(saved, [], NoMetadata, Read, Token))!).MonotonicMs);
        Assert.Equal([("enc", "a"), ("enc", "b"), ("saved", "a")], queries.Select(value => (value.Id, value.Query.EntityId)));
        Assert.All(queries, value => { Assert.Equal(200d, value.Query.FromMs); Assert.Equal(800d, value.Query.ToMs); });
        Assert.Equal(1, reader.CachedSources);
    }

    [Fact]
    public async Task PaginationSharesOneBudgetAndResumesWithoutChangingTheCursorQuery()
    {
        var reader = new LiveBackend.FirstAttackReader();
        var queries = new List<CombatTimelineQuery>();
        Task<CombatTimelinePage> Read(string id, CombatTimelineQuery query, CancellationToken _)
        {
            queries.Add(query);
            var page = query.Cursor is null ? 1 : int.Parse(query.Cursor);
            return Task.FromResult(Page(id, [Item((ulong)page, page)], page == 6 ? null : (page + 1).ToString()));
        }
        var range = new LiveBackend.FirstAttackRange("segment|enc", Encounter("enc", 6), ["enc"]);
        Assert.Equal(5, Assert.Single((await reader.ReadAsync(range, [], NoMetadata, Read, Token))!).MonotonicMs);
        Assert.Equal(LiveBackend.FirstAttackPagesPerRefresh, queries.Count);
        Assert.Equal(6, Assert.Single((await reader.ReadAsync(range, [], NoMetadata, Read, Token))!).MonotonicMs);
        await reader.ReadAsync(range, [], NoMetadata, Read, Token);
        Assert.Equal(6, queries.Count);
        Assert.All(queries, query => Assert.Equal(0, query.FromMs));
    }

    [Fact]
    public async Task MissingMetadataAndTimelinePagesUseTheSameRequestBudget()
    {
        var reader = new LiveBackend.FirstAttackReader();
        var metadata = new List<string>(); var pages = 0;
        Task<CombatEncounter> Details(string id, CancellationToken _)
        { metadata.Add(id); return Task.FromResult(Encounter(id, id == "2" ? 1UL : 0UL)); }
        Task<CombatTimelinePage> Read(string id, CombatTimelineQuery query, CancellationToken _)
        { pages++; return Task.FromResult(Page(id, [Item(1, 200)])); }
        var range = new LiveBackend.FirstAttackRange("observation|obs", Encounter("observation:obs", 1), ["1", "2", "3", "4", "5", "6"]);
        Assert.Empty((await reader.ReadAsync(range, [], Details, Read, Token))!);
        Assert.Equal(LiveBackend.FirstAttackPagesPerRefresh, metadata.Count);
        Assert.Equal(0, pages);
        Assert.Equal(200, Assert.Single((await reader.ReadAsync(range, [], Details, Read, Token))!).MonotonicMs);
        Assert.Equal(5, metadata.Count);
        Assert.Equal(1, pages);
    }

    [Fact]
    public async Task UnknownOrZeroCountsAvoidRequestsAndSourceRetentionStaysBounded()
    {
        var reader = new LiveBackend.FirstAttackReader();
        var metadata = new List<string>(); var pages = 0;
        Task<CombatEncounter> Details(string id, CancellationToken _)
        { metadata.Add(id); return Task.FromResult(Encounter(id, id == "1" ? 1UL : 0UL)); }
        Task<CombatTimelinePage> Read(string id, CombatTimelineQuery query, CancellationToken _)
        { pages++; return Task.FromResult(Page(id, [Item(1, 100)])); }
        var range = new LiveBackend.FirstAttackRange("observation|obs", Encounter("observation:obs", null), ["1"]);
        Assert.Null(await reader.ReadAsync(range, [], Details, Read, Token));
        Assert.Empty((await reader.ReadAsync(range with { Summary = Encounter("observation:obs", 0) }, [], Details, Read, Token))!);
        Assert.Empty(metadata); Assert.Equal(0, pages);

        var sources = Enumerable.Range(1, 140).Select(value => value.ToString()).ToArray();
        range = range with { Summary = Encounter("observation:obs", 1), Sources = sources };
        for (var round = 0; round < 30; round++)
        {
            var before = metadata.Count + pages;
            await reader.ReadAsync(range, [], Details, Read, Token);
            Assert.InRange(metadata.Count + pages - before, 0, LiveBackend.FirstAttackPagesPerRefresh);
            Assert.InRange(reader.CachedSources, 0, LiveBackend.FirstAttackReader.MaximumSources);
        }
        Assert.Equal(LiveBackend.FirstAttackReader.MaximumSources, reader.CachedSources);
        Assert.Equal(140, metadata.Count);
        Assert.Contains("1", metadata);
        Assert.Equal(1, pages);
        Assert.Equal(100, Assert.Single((await reader.ReadAsync(range, [], Details, Read, Token))!).MonotonicMs);
    }

    [Fact]
    public async Task AttributionAndSameOffsetRescansKeepOnlyTheLatestCountedEvent()
    {
        var reader = new LiveBackend.FirstAttackReader();
        var queries = 0;
        Task<CombatTimelinePage> Read(string id, CombatTimelineQuery query, CancellationToken _)
        {
            queries++;
            CombatTimelineItem[] items = queries == 1
                ? [Item(1, 100, "unknown"), Item(2, 200, "owned"), Item(3, 300, amount: 300),
                    Item(4, 500) with { Damage = Item(4, 500).Damage! with { FirstAttack = false } }, Item(5, 600, amount: 0)]
                : [Item(3, 300, amount: 300), Item(6, 300, amount: 400)];
            return Task.FromResult(Page(id, items.Where(item => item.OffsetMs >= (query.FromMs ?? 0)).ToArray()));
        }
        var range = new LiveBackend.FirstAttackRange("segment|enc", Encounter("enc", 1, owned: false), ["enc"]);
        Assert.Equal("300", Assert.Single((await reader.ReadAsync(range, [], NoMetadata, Read, Token))!).Amount.ToString());
        range = range with { Summary = Encounter("enc", 2, owned: false) };
        Assert.Equal("400", Assert.Single((await reader.ReadAsync(range, [], NoMetadata, Read, Token))!).Amount.ToString());
        await reader.ReadAsync(range, [], NoMetadata, Read, Token);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task FailedSupplementKeepsTheResultAndRetriesAfterTheInterval()
    {
        var clock = new TestClock();
        var reader = new LiveBackend.FirstAttackReader(clock);
        var calls = 0;
        Task<CombatTimelinePage> Read(string id, CombatTimelineQuery query, CancellationToken _)
        {
            calls++;
            if (calls == 2 || id == "different") throw new MabiToolApiException(409, ApiErrors.Create("combat.planStale", "x", "t"));
            return Task.FromResult(Page(id, [Item((ulong)(calls == 1 ? 1 : 2), calls == 1 ? 100 : 200)]));
        }
        var range = new LiveBackend.FirstAttackRange("segment|enc", Encounter("enc", 1), ["enc"]);
        await reader.ReadAsync(range, [], NoMetadata, Read, Token);
        range = range with { Summary = Encounter("enc", 2) };
        Assert.Equal(100, Assert.Single((await reader.ReadAsync(range, [], NoMetadata, Read, Token))!).MonotonicMs);
        await reader.ReadAsync(range, [], NoMetadata, Read, Token);
        Assert.Equal(2, calls);
        clock.Now += LiveBackend.FirstAttackRetryInterval;
        Assert.Equal(200, Assert.Single((await reader.ReadAsync(range, [], NoMetadata, Read, Token))!).MonotonicMs);
        var different = new LiveBackend.FirstAttackRange("measurement:different", Encounter("different", 1), ["different"]);
        Assert.Empty((await reader.ReadAsync(different, [], NoMetadata, Read, Token))!);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = CombatFirstAttackReaderTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
