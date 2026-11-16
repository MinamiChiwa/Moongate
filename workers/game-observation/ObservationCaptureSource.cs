using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.Observation.Worker;

/// <param name="LocalIdentity">宿主为同一游戏会话（同一进程）记住的最近确认本机身份，Worker 重启后作为跨连接沿用的初值；可省略。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObservationCaptureRequest(string GameSessionId, string Region, int ProcessId,
    DateTimeOffset ProcessStartTime, string ChannelId, string Profile, bool AnyAddress = false, ObservedGameIdentity? LocalIdentity = null)
{
    /// <summary>校验后的初值身份；字段不合规时返回 false。</summary>
    internal bool TryKnownIdentity(out GameIdentity? identity)
    {
        identity = null;
        if (LocalIdentity is not { } value) return true;
        if (value.CharacterId.Value == 0 || value.ActorId.Value == 0 || value.Nickname is not { Length: > 0 and <= 64 } name ||
            string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ||
            !int.TryParse(value.RealmId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var realm) ||
            realm.ToString(System.Globalization.CultureInfo.InvariantCulture) != value.RealmId) return false;
        identity = new(value.CharacterId.Value, realm, name, value.ActorId.Value);
        return true;
    }
}
public interface IObservationCaptureSource
{
    IReadOnlyList<CaptureChannelOption> Channels();
    /// <summary>带游戏进程时为每张网卡统计本机地址落在其上的游戏数据连接数。</summary>
    IReadOnlyList<CaptureChannelOption> Channels(CaptureChannelQuery? query) => Channels();
    Task<ChannelDetectionProgress> DetectChannelsAsync(ChannelDetectionRequest request, Func<ChannelDetectionProgress, Task> progress, CancellationToken ct) =>
        Task.FromException<ChannelDetectionProgress>(new MabiTool.Infrastructure.Workers.WorkerException("worker.methodUnsupported"));
    Task CaptureAsync(ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct);
    IReadOnlyList<ObservationCombatProfile> CombatProfiles => [];
    IReadOnlyList<ObservationCombatProfile> CombatProfilesFor(ObservationCaptureRequest request) => CombatProfiles;
    Task<InventoryClientPrepared> PrepareInventoryAsync(InventoryClientRequest request, CancellationToken ct) =>
        Task.FromException<InventoryClientPrepared>(new WorkerObservationException("inventory.buildUnsupported"));
    Task<InventoryCatalogData> ReadInventoryCatalogAsync(InventoryClientRequest request, CancellationToken ct) =>
        Task.FromException<InventoryCatalogData>(new WorkerObservationException("inventory.buildUnsupported"));
    ClientCaptureLayout? InventoryLayoutFor(ObservationCaptureRequest request) => null;
    ObservationInventoryProfile? InventoryProfileFor(ObservationCaptureRequest request) => null;
    Task CaptureSharedAsync(ObservationCaptureRequest request, ObservationCaptureContext context, CancellationToken ct) =>
        CaptureAsync(request, context.Publish, ct);
}
public sealed class NpcapObservationSource : IObservationCaptureSource
{
    private readonly object inventoryGate = new();
    private (InventoryClientRequest Request, ClientCaptureLayout Layout, ObservationInventoryProfile Profile)? prepared;
    public Task<InventoryClientPrepared> PrepareInventoryAsync(InventoryClientRequest request, CancellationToken ct) => Task.Run(() =>
    {
        if (request.Region != "TW") throw new WorkerObservationException("inventory.buildUnsupported");
        using var game = SelectedGameProcess.Open(request.ProcessId, request.ProcessStartTime);
        var directory = Path.GetDirectoryName(game.ImagePath)!;
        MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "GameAssembly.dll")) MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(path);
        foreach (var path in Directory.EnumerateDirectories(directory, "*_Data"))
            MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(Path.Combine(path, "il2cpp_data", "Metadata", "global-metadata.dat"));
        var layout = ClientLayoutReader.Read(directory, ct);
        ReleaseLayoutBuffers();
        // 钱包货币的名称、说明与补充间隔来自客户端表；读不到时资源照常按变化消息更新，只是没有名称与倒计时。
        try { layout = layout with { Resources = ClientResourceCatalog.Read(ClientItemCatalogLocation.Resolve(game.ImagePath), ct) }; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        if (!SelectedGameProcess.IsRunning(game)) throw new WorkerObservationException("input.bindingChanged");
        var profile = new ObservationInventoryProfile("tw-inventory-" + layout.AssemblyHash[..16] + "-" + layout.MetadataHash[..16],
            new("mabiagent-inventory", "1.0.0", "TW", TwClientFingerprint.GameBuildForInventoryLayout(layout.AssemblyHash, layout.MetadataHash)),
            layout.AssemblyHash, layout.MetadataHash, layout.CreateDecoder().Categories, true, layout.SupportsInventoryMutations);
        ct.ThrowIfCancellationRequested();
        lock (inventoryGate) prepared = (request, layout, profile);
        return new InventoryClientPrepared(profile);
    }, ct);
    public Task<InventoryCatalogData> ReadInventoryCatalogAsync(InventoryClientRequest request, CancellationToken ct) => Task.Run(() =>
    {
        using var game = SelectedGameProcess.Open(request.ProcessId, request.ProcessStartTime);
        var location = ClientItemCatalogLocation.Resolve(game.ImagePath);
        var result = ClientItemCatalogExtractor.Extract(location, ct);
        if (!SelectedGameProcess.IsRunning(game)) throw new WorkerObservationException("input.bindingChanged");
        ct.ThrowIfCancellationRequested(); return result;
    }, ct);
    /// <summary>
    /// 布局解析整读 GameAssembly.dll 与 metadata（当前客户端约 310 + 58 MiB，进入大对象堆）。解析返回后这些数组已无引用，
    /// 但 GC 按此前存活量放大大对象预算，提交内存会在共享捕获期间长期保持数百 MiB；此处做一次压缩回收并归还内存。
    /// 实测（当前客户端）暂停约 70 ms，私有字节由约 410 MiB 回到约 20 MiB。
    /// </summary>
    internal static void ReleaseLayoutBuffers()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
    private bool Matches(ObservationCaptureRequest request, InventoryClientRequest value) => request.GameSessionId == value.GameSessionId &&
        request.ProcessId == value.ProcessId && request.ProcessStartTime == value.ProcessStartTime && request.Region == value.Region;
    public ClientCaptureLayout? InventoryLayoutFor(ObservationCaptureRequest request)
    { lock (inventoryGate) return prepared is { } value && Matches(request, value.Request) ? value.Layout : null; }
    public ObservationInventoryProfile? InventoryProfileFor(ObservationCaptureRequest request)
    { lock (inventoryGate) return prepared is { } value && Matches(request, value.Request) ? value.Profile : null; }
    public IReadOnlyList<ObservationCombatProfile> CombatProfiles => [Tw20261110CombatProtocol.CombatProfile];
    /// <summary>v3 请求取当前（含治疗与本机战斗状态）配置；旧宿主的 v2、v1 请求取对应的旧配置。</summary>
    public IReadOnlyList<ObservationCombatProfile> CombatProfilesFor(ObservationCaptureRequest request) =>
        Tw20261110CombatProtocol.CombatProfileFor(request.Profile) is { } profile ? [profile] : [];
    public IReadOnlyList<CaptureChannelOption> Channels() => Channels(null);
    public IReadOnlyList<CaptureChannelOption> Channels(CaptureChannelQuery? query)
    {
        IReadOnlyList<CaptureConnection>? data = null;
        if (query is not null)
        {
            using var game = SelectedGameProcess.Open(query.ProcessId, query.ProcessStartTime);
            data = CaptureChannels.DataConnections(CaptureChannels.Connections(game.ProcessId));
        }
        return Annotate(CaptureChannels.Devices(), data);
    }
    /// <summary>按游戏数据连接的本机地址（或两端回环）统计每张网卡承载的连接数；未给连接时不统计。</summary>
    internal static IReadOnlyList<CaptureChannelOption> Annotate(IReadOnlyList<CaptureDevice> devices, IReadOnlyList<CaptureConnection>? data) =>
        devices.Take(128).Select(value => new CaptureChannelOption(ChannelId(value.Name), value.Description,
            value.Addresses, value.IsLoopback, data?.Count(connection => NativeCaptureBoundary.MatchesDevice(value, connection)))).ToArray();
    public async Task<ChannelDetectionProgress> DetectChannelsAsync(ChannelDetectionRequest request, Func<ChannelDetectionProgress, Task> progress, CancellationToken ct)
    {
        using var game = SelectedGameProcess.Open(request.ProcessId, request.ProcessStartTime);
        return await CaptureChannels.DetectAsync(game, CaptureChannels.Devices(), request.DurationMs, progress, ct).ConfigureAwait(false);
    }
    internal static string ChannelId(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..32];
    public async Task CaptureAsync(ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct)
        => await CaptureSharedAsync(request, new([], publish, _ => { }), ct).ConfigureAwait(false);
    public async Task CaptureSharedAsync(ObservationCaptureRequest request, ObservationCaptureContext context, CancellationToken ct)
    {
        var layout = request.Profile switch
        {
            TwGameProtocol.CaptureProfile => CombatProtocolLayout.MusicOnly,
            var combatProfile when Tw20261110CombatProtocol.IsCaptureProfile(combatProfile) => CombatProtocolLayout.Tw20261110,
            _ => throw new WorkerObservationException("observation.profileUnavailable")
        };
        using var game = SelectedGameProcess.Open(request.ProcessId, request.ProcessStartTime);
        var device = CaptureChannels.Devices().SingleOrDefault(value => ChannelId(value.Name) == request.ChannelId)
            ?? throw new WorkerObservationException("observation.channelUnavailable");
        request.TryKnownIdentity(out var known);
        await CaptureChannels.ObserveAsync(game, device, request.GameSessionId, context, ct, layout, request.AnyAddress, known).ConfigureAwait(false);
    }
    internal static GameCaptureReport Project(CaptureObservation report, MapLocationResolver? maps = null)
    {
        ObservedGameIdentity Identity(GameIdentity value) => new(new(value.CharacterId), value.RealmId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.Nickname, new(value.ActorId));
        var game = report.Game;
        var concert = game.Concert is { } c ? new ObservedConcert(new(c.Id), new(c.HostCharacterId), null, c.Members.Select(Identity).ToArray()) : null;
        var music = game.Music is { } m ? new ObservedMusic(new(m.ConcertId), new(m.PerformerCharacterId), new(m.HostCharacterId),
            m.Title, m.IsLoop, m.SelectedChannels, m.ScoreSha256, m.WholeScoreDurationSeconds, m.SelectedDurationSeconds,
            m.EstimatedStartLocalMs, m.TimingSource, false, m.Issue) : null;
        var value = new GameObservationValue(game.Generation, game.Identity is { } identity ? Identity(identity) : null,
            concert, music, new(checked((ulong)report.Packets)), game.Detail, maps?.Resolve(game.Channel, game.Identity?.RealmId));
        return new(value, new(checked((ulong)game.Sequence)), report.Running,
            Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency), DateTimeOffset.UtcNow, null,
            new("tw-observation", "1.0.0", "TW", "sha256:" + TwGameProtocol.AssemblySha256 + ":" + TwGameProtocol.MetadataSha256));
    }
}

