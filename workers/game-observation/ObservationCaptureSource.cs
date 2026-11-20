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

/// <param name="ChannelId">指定网卡（只在该设备上抓包）；省略时打开全部设备。</param>
/// <param name="LocalIdentity">宿主为同一游戏进程记住的最近确认本机身份，捕获重启后作为沿用的初值；可省略。</param>
/// <param name="LocalIdentityConnection">该身份的实体 ID 所属的游戏连接；只有同一连接的流沿用实体 ID，其他连接只沿用角色 ID。可省略。</param>
/// <param name="CombatEntities">宿主为同一游戏进程记住的实体延续记录（类型、表 ID、最大 HP、首领标志与 HP 解码信息），只在同一连接的流上补足。可省略。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObservationCaptureRequest(string GameSessionId, string Region, int ProcessId,
    DateTimeOffset ProcessStartTime, string? ChannelId, string Profile, ObservedGameIdentity? LocalIdentity = null,
    ObservedConnection? LocalIdentityConnection = null, CombatEntityCarry? CombatEntities = null)
{
    /// <summary>校验后的实体延续记录：连接字段合规且至多 64 条，否则为空（不延续）。</summary>
    internal CombatEntityCarry? KnownCombatEntities => CombatEntities is { Connection: { LocalPort: > 0 and <= 65535, RemotePort: > 0 and <= 65535 } connection } value &&
        System.Net.IPAddress.TryParse(connection.RemoteAddress, out _) && value.Entities is { Count: <= TwCombatObserver.MaximumCarriedEntities } entities &&
        entities.All(entity => entity is not null) ? value : null;
    /// <summary>校验后的身份连接；字段不合规时为空（只沿用角色 ID）。</summary>
    internal ObservedConnection? KnownConnection => LocalIdentityConnection is { LocalPort: > 0 and <= 65535, RemotePort: > 0 and <= 65535 } value &&
        System.Net.IPAddress.TryParse(value.RemoteAddress, out _) ? value : null;
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
    /// <summary>带游戏进程时为每张网卡统计本机地址落在其上的游戏进程连接数（诊断）。</summary>
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
        var layout = ClientLayoutReader.Read(ClientDirectory(game.ImagePath), ct, cacheDirectory: ObservationCache.Directory("inventory", "client-layouts"));
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
    /// <summary>
    /// 库存布局读取的客户端目录：<paramref name="imagePath"/> 是已解析联接与符号链接的最终路径（<see cref="SelectedGameProcess.ImagePath"/>），
    /// 目录、GameAssembly.dll 与 metadata 仍逐级拒绝重解析点，解析后被换成链接时拒绝。
    /// </summary>
    internal static string ClientDirectory(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath)!;
        MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "GameAssembly.dll")) MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(path);
        foreach (var path in Directory.EnumerateDirectories(directory, "*_Data"))
            MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(Path.Combine(path, "il2cpp_data", "Metadata", "global-metadata.dat"));
        return directory;
    }
    public Task<InventoryCatalogData> ReadInventoryCatalogAsync(InventoryClientRequest request, CancellationToken ct) => Task.Run(() =>
    {
        using var game = SelectedGameProcess.Open(request.ProcessId, request.ProcessStartTime);
        var location = ClientItemCatalogLocation.Resolve(game.ImagePath);
        var result = ClientItemCatalogExtractor.Extract(location, ct);
        if (!SelectedGameProcess.IsRunning(game)) throw new WorkerObservationException("input.bindingChanged");
        ct.ThrowIfCancellationRequested(); return result;
    }, ct);
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
        var devices = CaptureChannels.Devices();
        if (query is not null)
        {
            using var game = SelectedGameProcess.Open(query.ProcessId, query.ProcessStartTime);
            var owned = CaptureChannels.Connections(game.ProcessId);
            data = owned.Where(connection => !NativeCaptureBoundary.SelfConnection(connection, owned)).Take(32).ToArray();
        }
        return Annotate(devices, data);
    }
    /// <summary>全部网卡的本机地址；判断游戏连接是否连向本机代理。</summary>
    internal static IReadOnlySet<string> LocalAddresses(IReadOnlyList<CaptureDevice> devices) =>
        devices.SelectMany(value => value.Addresses).ToHashSet(StringComparer.Ordinal);
    /// <summary>
    /// 诊断：统计每张网卡承载的游戏进程连接数（进程自连除外）：远端是本机地址时计入回环适配器，否则计入本机地址所在的网卡
    /// （见 <see cref="NativeCaptureBoundary.MatchesDevice"/>）；未给连接时不统计。捕获不按它选择网卡。
    /// </summary>
    internal static IReadOnlyList<CaptureChannelOption> Annotate(IReadOnlyList<CaptureDevice> devices, IReadOnlyList<CaptureConnection>? data)
    {
        var locals = LocalAddresses(devices);
        return devices.Take(128).Select(value => new CaptureChannelOption(ChannelId(value.Name), value.Description,
            value.Addresses, value.IsLoopback, data?.Count(connection => NativeCaptureBoundary.MatchesDevice(value, connection, locals)))).ToArray();
    }
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
        var devices = CaptureChannels.Devices();
        // 自动模式打开全部设备；指定网卡时只打开那一张。
        IReadOnlyList<CaptureDevice> selected = request.ChannelId is null ? devices
            : [devices.SingleOrDefault(value => ChannelId(value.Name) == request.ChannelId) ?? throw new WorkerObservationException("observation.channelUnavailable")];
        request.TryKnownIdentity(out var known);
        context.CombatCarry = request.KnownCombatEntities;
        await CaptureChannels.ObserveAsync(game, selected, request.GameSessionId, context, ct, layout, known, request.KnownConnection).ConfigureAwait(false);
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

