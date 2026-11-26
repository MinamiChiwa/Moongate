using MabiTool.Contracts;
using MabiTool.Host.GameCli;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>乐谱库的游戏乐谱来源：把 <c>get_music_scores</c> 同步成“游戏列表”来源；用替身命令行与临时数据库，不碰真实游戏。</summary>
[Collection("Persistent backend")]
public sealed class GameMusicLibrarySyncTests
{
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    private sealed class Area : IDisposable
    {
        private readonly string root;
        public StorageOptions Storage => new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data"));
        public Area()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "MabiTool.slnx"))) repo = repo.Parent;
            root = Path.Combine(repo!.FullName, "artifacts", "tmp", "game-music-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }
        public void Dispose() => Directory.Delete(root, true);
    }

    private static string Scores(params (string Title, string Location)[] rows) =>
        "[" + string.Join(",", rows.Select(row => $"{{\"Location\":\"{row.Location}\",\"DisplayTitle\":\"{row.Title}\",\"IsCopyingAllowed\":false,\"IsLocked\":false}}")) + "]";

    private static GameMusicLibrarySyncService Sync(ConnectedRig rig, ScoreCatalogStore? store, Clock clock) =>
        new(rig.Service, new TwCliMusicControl(rig.Service.Cli, rig.Service.MusicAvailability, _ => null, clock), store, "epoch", clock);

    [Fact]
    public async Task TheGameListBecomesOneSourceWhoseEntriesKeepTheirIdentityAcrossLocationChangesAndShrinkingLists()
    {
        using var rig = new ConnectedRig();
        using var area = new Area(); using var lease = DataRootLease.Open(area.Storage);
        await using var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations);
        var store = new ScoreCatalogStore(database, "epoch");
        var clock = new Clock();
        await rig.ConnectAsync("get_music_scores");
        var sync = Sync(rig, store, clock);

        rig.Runner.Handlers["get_music_scores"] = _ => DataRunner.Ok(Scores(("月光", "inventory"), ("月光", "account_storage"), ("夜曲", "inventory")));
        var first = await sync.SyncAsync(default);
        Assert.Equal((GameMusicSyncStatus.Synced, (string?)null, 3), (first.Status.KnownValue!.Value, first.Reason, first.ScoreCount));
        var snapshot = store.Read(GameMusicLibrarySyncService.Scope)!;
        Assert.Equal(ScoreSourceKind.GameList, snapshot.Scope.Kind);
        Assert.All(snapshot.Entries, entry => { Assert.Equal(ScoreAvailability.Present, entry.Availability); Assert.False(entry.Ambiguous); });
        var moonlight = snapshot.Entries.Where(entry => entry.RawTitle == "月光").ToList();
        Assert.Equal(2, moonlight.Count);
        Assert.Single(moonlight.Select(entry => entry.CandidateGroup).Distinct());
        var ids = snapshot.Entries.ToDictionary(entry => entry.StableSourceId!, entry => entry.Id);

        // 乐谱换了存放位置、夜曲不在了：条目身份不变，夜曲标为已不在游戏里，仍在曲库里。
        clock.Milliseconds += 10_000;
        rig.Runner.Handlers["get_music_scores"] = _ => DataRunner.Ok(Scores(("月光", "character_storage"), ("月光", "inventory")));
        var second = await sync.SyncAsync(default);
        Assert.Equal((GameMusicSyncStatus.Synced, 2), (second.Status.KnownValue!.Value, second.ScoreCount));
        var after = store.Read(GameMusicLibrarySyncService.Scope)!;
        Assert.Equal(ids.Keys.Order(), after.Entries.Select(entry => entry.StableSourceId!).Order());
        Assert.All(after.Entries, entry => Assert.Equal(ids[entry.StableSourceId!], entry.Id));
        Assert.Equal(ScoreAvailability.Missing, after.Entries.Single(entry => entry.RawTitle == "夜曲").Availability);
        Assert.Equal(2, after.Entries.Count(entry => entry.Availability == ScoreAvailability.Present));
        Assert.Equal(2UL, after.Revision);
    }

    [Fact]
    public async Task UnavailableConnectorAndFailedReadsLeaveTheLastSourceUntouchedAndAreNotRepeatedWithinTheGap()
    {
        using var rig = new ConnectedRig();
        using var area = new Area(); using var lease = DataRootLease.Open(area.Storage);
        await using var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations);
        var store = new ScoreCatalogStore(database, "epoch");
        var clock = new Clock();
        var sync = Sync(rig, store, clock);

        // 连接器未启用：不调用命令行，原因是连接器的原因码，没有来源。
        await rig.Service.RefreshAsync(true);
        var disabled = await sync.SyncAsync(default);
        Assert.Equal((GameMusicSyncStatus.Unavailable, "gameCli.disabled", 0), (disabled.Status.KnownValue!.Value, disabled.Reason, disabled.ScoreCount));
        Assert.Empty(rig.Runner.Commands);
        Assert.Null(store.Read(GameMusicLibrarySyncService.Scope));

        await rig.ConnectAsync("get_music_scores");
        rig.Runner.Handlers["get_music_scores"] = _ => DataRunner.Ok(Scores(("月光", "inventory")));
        Assert.Equal(GameMusicSyncStatus.Synced, (await sync.SyncAsync(default)).Status.KnownValue);
        var revision = store.Read(GameMusicLibrarySyncService.Scope)!.Revision;

        // 间隔内再次同步返回上次结果，不再读取。
        var reads = rig.Runner.CountOf("get_music_scores");
        Assert.Equal(GameMusicSyncStatus.Synced, (await sync.SyncAsync(default)).Status.KnownValue);
        Assert.Equal(reads, rig.Runner.CountOf("get_music_scores"));

        // 读取失败：来源与条目保持，返回失败原因。
        clock.Milliseconds += 10_000;
        rig.Runner.Handlers["get_music_scores"] = _ => new(1, "", "boom", false, TimeSpan.Zero);
        var failed = await sync.SyncAsync(default);
        Assert.Equal(GameMusicSyncStatus.Failed, failed.Status.KnownValue);
        Assert.NotNull(failed.Reason);
        Assert.Equal(1, failed.ScoreCount);
        Assert.Equal(revision, store.Read(GameMusicLibrarySyncService.Scope)!.Revision);

        // 存储不可用的宿主：不可用并写明原因。
        var withoutStorage = Sync(rig, null, clock);
        var none = await withoutStorage.SyncAsync(default);
        Assert.Equal((GameMusicSyncStatus.Unavailable, "storage.unavailable"), (none.Status.KnownValue!.Value, none.Reason));
    }

    [Fact]
    public void ItemsSkipUnusableTitlesAndNumberRepeatedOnesByTitleOnly()
    {
        var items = GameMusicLibrarySyncService.Items(
        [
            new("<color=#fff>月光</color>", "inventory", false), new("月光", "account_storage", false), new("坏\u0007标题", "inventory", false),
            new("   ", "inventory", false), new(new string('长', 300), "inventory", false)
        ]);
        Assert.Equal(["月光", "月光#2"], items.Where(item => item.StableSourceId!.StartsWith("月光", StringComparison.Ordinal)).Select(item => item.StableSourceId!).Order(StringComparer.Ordinal));
        // 过长的标题以其哈希作来源标识（不超过来源标识上限）；控制字符与空白标题被跳过。
        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.True(item.StableSourceId!.Length <= 200 + 4));
    }
}
