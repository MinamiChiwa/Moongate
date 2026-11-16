using System.Buffers.Binary;
using System.Text.Json;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>当前地图：进图通知的 ChannelSheetId 经客户端 Channel → GameSpace 表投影为地图名，频道号按永久频道规则处理。</summary>
public sealed class TwMapLocationTests
{
    private static readonly JsonDocument MapFixture = JsonDocument.Parse(typeof(TwMapLocationTests).Assembly
        .GetManifestResourceStream("MabiTool.TwMapChannelFixture")!);
    private static readonly JsonDocument MusicFixture = JsonDocument.Parse(typeof(TwMapLocationTests).Assembly
        .GetManifestResourceStream("MabiTool.TwMusicFixture")!);
    private static readonly TwMusicProtocolMap Map = TwMusicProtocolMap.Tw20261110;
    private static IEnumerable<JsonElement> Examples => MapFixture.RootElement.GetProperty("mapTables").GetProperty("examples").EnumerateArray();
    private static int CapturedSheet => MapFixture.RootElement.GetProperty("protocol").GetProperty("entering").GetProperty("samples")[0].GetProperty("channelSheetId").GetInt32();
    private static JsonElement Example(int sheet) => Examples.Single(e => e.GetProperty("channelSheetId").GetInt32() == sheet);

    /// <summary>进图前缀：ChannelId（表 ID、实例编号、游戏服务器 ID）后接天气与 TransitionTime。</summary>
    private static byte[] Entering(int sheet, long unique)
    {
        var body = new byte[22];
        BinaryPrimitives.WriteInt32LittleEndian(body, sheet);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(4), unique);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(12), 7);
        return body;
    }

    [Fact]
    public void EnteringFrameSetsChannelWithoutMusicInterestAndMapChangeReplacesIt()
    {
        var observer = new TwGameObserver(Map) { MusicEnabled = false };
        observer.Accept(Map.Entering, Entering(CapturedSheet, 123456789012345678));
        Assert.Equal(new GameChannel(CapturedSheet, 123456789012345678, 7), observer.Current.Channel);

        // 本机消失属于同一次切图：角色状态清空，频道保留到下一次进图通知。
        var local = ulong.Parse(MusicFixture.RootElement.GetProperty("localEntity").GetString()!);
        observer.Accept(Map.Personal, BitConverter.GetBytes(ulong.Parse(MusicFixture.RootElement.GetProperty("localCharacter").GetString()!)));
        observer.Accept(Map.Appearing, Convert.FromBase64String(MusicFixture.RootElement.GetProperty("localAppearance").GetString()!));
        Assert.NotNull(observer.Current.Identity);
        observer.Accept(Map.Disappearing, BitConverter.GetBytes(local));
        Assert.Null(observer.Current.Identity);
        Assert.Equal(CapturedSheet, observer.Current.Channel!.SheetId);

        var field = Example(975984897);
        observer.Accept(Map.Entering, Entering(field.GetProperty("channelSheetId").GetInt32(), 30005));
        Assert.Equal(975984897, observer.Current.Channel!.SheetId);

        observer.Accept(Map.Entering, []);
        Assert.Null(observer.Current.Channel);
        observer.Accept(Map.Entering, Entering(975984897, 30005));
        observer.Reset("游戏连接已结束，等待新连接。");
        Assert.Null(observer.Current.Channel);

        // 音乐兴趣开启时同一进图快照照常读取合奏，频道取自同一首字段。
        var body = Convert.FromBase64String(MusicFixture.RootElement.GetProperty("enterMap").GetProperty("body").GetString()!);
        BinaryPrimitives.WriteInt32LittleEndian(body, CapturedSheet);
        observer.MusicEnabled = true;
        observer.Accept(Map.Entering, body, 1000);
        Assert.Equal(CapturedSheet, observer.Current.Channel!.SheetId);
    }

    private static readonly IReadOnlyDictionary<int, ClientMapEntry> Catalog = Examples.ToDictionary(
        e => e.GetProperty("channelSheetId").GetInt32(),
        e => new ClientMapEntry(e.GetProperty("channelSheetId").GetInt32(), e.GetProperty("gameSpaceSheetId").GetInt32(),
            e.GetProperty("displayName").GetString()!, e.GetProperty("isPermanentChannel").GetBoolean()));

    private static MabiTool.Contracts.ObservedLocation Settled(MapLocationResolver resolver, GameChannel channel, int? realm)
    {
        MabiTool.Contracts.ObservedLocation? value = null;
        Assert.True(SpinWait.SpinUntil(() => (value = resolver.Resolve(channel, realm))!.MapReason != MapLocationResolver.CatalogPending, 5000));
        return value!;
    }

    [Fact]
    public void ResolverGivesRawMapNameAndKeepsDisplayChannelUnknown()
    {
        var resolver = new MapLocationResolver(_ => Catalog, CancellationToken.None);
        var dungeon = Settled(resolver, new(CapturedSheet, 123456789012345678, 7), 3);
        Assert.Equal(Example(CapturedSheet).GetProperty("displayName").GetString(), dungeon.MapName);
        Assert.Equal(1015388881, dungeon.MapSheetId);
        Assert.Equal((false, (int?)null, MapLocationResolver.NotPermanent), (dungeon.PermanentChannel!.Value, dungeon.PermanentChannelIndex, dungeon.ChannelReason));

        var field = resolver.Resolve(new(975984897, 3 * 10000 + 5, 7), 3)!;
        Assert.Equal(("杜巴頓", true, (int?)5, MapLocationResolver.DisplayUnverified), (field.MapName, field.PermanentChannel!.Value, field.PermanentChannelIndex, field.ChannelReason));
        Assert.Equal(((int?)null, MapLocationResolver.IndexUnresolved), (resolver.Resolve(new(975984897, 3 * 10000 + 5, 7), 4)!.PermanentChannelIndex, resolver.Resolve(new(975984897, 3 * 10000 + 5, 7), 4)!.ChannelReason));
        Assert.Equal(MapLocationResolver.IndexUnresolved, resolver.Resolve(new(975984897, 123456789012345678, 7), null)!.ChannelReason);

        var unknown = resolver.Resolve(new(42, 1, 7), 3)!;
        Assert.Equal((null, MapLocationResolver.MapUnknown, MapLocationResolver.RuleUnknown), (unknown.MapName, unknown.MapReason, unknown.ChannelReason));
        Assert.Null(resolver.Resolve(null, 3));
    }

    [Fact]
    public void ResolverReportsPendingAndFailedCatalogReadOnce()
    {
        using var gate = new ManualResetEventSlim();
        var reads = 0;
        var resolver = new MapLocationResolver(_ => { Interlocked.Increment(ref reads); gate.Wait(); throw new InvalidDataException("客户端缺少地图名称资源"); }, CancellationToken.None);
        var pending = resolver.Resolve(new(CapturedSheet, 1, 7), 3)!;
        Assert.Equal((CapturedSheet, (string?)null, MapLocationResolver.CatalogPending), (pending.ChannelSheetId, pending.MapName, pending.MapReason));
        gate.Set();
        var failed = Settled(resolver, new(CapturedSheet, 1, 7), 3);
        Assert.Equal((null, MapLocationResolver.CatalogUnavailable, MapLocationResolver.RuleUnknown), (failed.MapName, failed.MapReason, failed.ChannelReason));
        Assert.Equal(MapLocationResolver.CatalogUnavailable, resolver.Resolve(new(975984897, 30005, 7), 3)!.MapReason);
        Assert.Equal(1, reads);
    }

    [InstalledTwClientFact]
    public void InstalledClientTablesMatchOfflineMapProjection()
    {
        var catalog = ClientMapCatalog.Read(InstalledTwClientFactAttribute.ClientDirectory);
        var tables = MapFixture.RootElement.GetProperty("mapTables");
        Assert.Equal(tables.GetProperty("rowCounts").GetProperty("Channel").GetInt32(), catalog.Count);
        Assert.Equal(tables.GetProperty("canonicalMapping").GetProperty("sha256").GetString(), ClientMapCatalog.CanonicalSha256(catalog));
        foreach (var example in Examples)
        {
            var entry = catalog[example.GetProperty("channelSheetId").GetInt32()];
            Assert.Equal((example.GetProperty("gameSpaceSheetId").GetInt32(), example.GetProperty("displayName").GetString(), example.GetProperty("isPermanentChannel").GetBoolean()),
                (entry.GameSpaceSheetId, entry.DisplayName, entry.PermanentChannel));
        }
    }
}

public sealed class InstalledTwClientFactAttribute : FactAttribute
{
    public static string ClientDirectory => Environment.GetEnvironmentVariable("MABITOOL_TEST_TW_CLIENT") ?? @"C:\Nexon\Library\MabinogiMobile_TW\appdata";
    /// <summary>只在客户端资源索引与离线核实时一致（夹具 starterIndexSha256）时运行。</summary>
    public InstalledTwClientFactAttribute()
    {
        var index = Path.Combine(ClientDirectory, "ResourcesPackage", "Starter", "index");
        if (!File.Exists(index)) { Skip = "需要当前台服客户端静态资源；设置 MABITOOL_TEST_TW_CLIENT 指向 appdata"; return; }
        using var fixture = JsonDocument.Parse(typeof(InstalledTwClientFactAttribute).Assembly.GetManifestResourceStream("MabiTool.TwMapChannelFixture")!);
        var expected = fixture.RootElement.GetProperty("provenance").GetProperty("client").GetProperty("starterIndexSha256").GetString();
        using var stream = File.OpenRead(index);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != expected) Skip = "客户端资源索引与离线核实的版本不同";
    }
}
