using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Observation.Fixtures;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;

// repeat=true 时在战斗兴趣建立后每 200 ms 追加一条本机伤害与效果刷新，供调试工具离线跑完整采集窗口。
// inventoryItems>0 时同时提供库存布局：战斗兴趣建立时发送的个人数据 20590 为含库存的完整登录体（战斗与库存解析同一帧），
// 随后发送身份关联 170631，复现台服切图/登录时的共用解析负载。
sealed class CurrentCombatFixtureSource(string root, bool repeat = false, int inventoryItems = 0, bool residentIdentity = false) : IObservationCaptureSource
{
    private volatile bool inventoryPrepared;
    public IReadOnlyList<ObservationCombatProfile> CombatProfiles => [Tw20261110CombatProtocol.CombatProfile];
    public IReadOnlyList<CaptureChannelOption> Channels() => [new(new string('1', 32), "合成当前协议通道", [])];
    public IReadOnlyList<CaptureChannelOption> Channels(CaptureChannelQuery? query) => residentIdentity
        ? [new(new string('1', 32), "合成当前协议通道", ["127.0.0.1"], false, 1)] : Channels();
    public ClientCaptureLayout? InventoryLayoutFor(ObservationCaptureRequest request) =>
        inventoryItems > 0 && inventoryPrepared ? CoexistenceInventoryFixture.Layout : null;
    public ObservationInventoryProfile? InventoryProfileFor(ObservationCaptureRequest request) =>
        inventoryItems > 0 && inventoryPrepared ? CoexistenceInventoryFixture.InventoryProfile : null;
    public Task<InventoryClientPrepared> PrepareInventoryAsync(InventoryClientRequest request, CancellationToken ct)
    {
        if (inventoryItems <= 0) return Task.FromException<InventoryClientPrepared>(new InvalidDataException("inventory.buildUnsupported"));
        inventoryPrepared = true; return Task.FromResult(new InventoryClientPrepared(CoexistenceInventoryFixture.InventoryProfile));
    }
    /// <summary>名称目录：只收录装备表（登录体里的装备表编号 1000–1499），消耗品不收录，用于核对目录自动提取后条目显示原文名、未收录的仍按编号。</summary>
    public Task<InventoryCatalogData> ReadInventoryCatalogAsync(InventoryClientRequest request, CancellationToken ct)
    {
        if (inventoryItems <= 0) return Task.FromException<InventoryCatalogData>(new InvalidDataException("inventory.buildUnsupported"));
        File.WriteAllText(Path.Combine(root, "catalog-reads"), "read");
        return Task.FromResult(new InventoryCatalogData("fixture-coexist-catalog", "TW", new string('c', 64), DateTimeOffset.UtcNow, [],
            Enumerable.Range(1000, 500).Select(id => new InventoryCatalogEntry(new("Equipment", new((ulong)id)), "合成裝備" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "合成裝備" + id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Equipment", new((ulong)id), new(0), new(0), new(0))).ToArray(), [],
            [new("Equipment", "Equipment", true, 500)]));
    }
    public Task CaptureAsync(ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct) =>
        CaptureSharedAsync(request, new(CombatProfiles, publish, _ => { }), ct);
    public async Task CaptureSharedAsync(ObservationCaptureRequest request, ObservationCaptureContext context, CancellationToken ct)
    {
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatFixtureSource).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var entity = ulong.Parse(fixture.RootElement.GetProperty("entityId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        using var handle = new FileStream(Path.Combine(root, "capture-handle"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(Path.Combine(root, "handles"), "1");
        ulong generation = 0, statusSequence = 0, ticks = 0; uint sequence = 0; string? interest = null;
        SharedGameStream Open() => new(request.GameSessionId, context, () => Interlocked.Increment(ref generation), layout: CombatProtocolLayout.Tw20261110);
        var stream = Open();
        var captureGeneration = Guid.NewGuid().ToString("N");
        var personal = Body("personalBodyBase64");
        var identitySent = false;
        var identityFeeds = 0;
        var damageFeeds = 0;
        using var interestLog = residentIdentity ? context.OnChanging((_, next) =>
            File.WriteAllText(Path.Combine(root, "interests"), JsonSerializer.Serialize(next, ContractJson.Options))) : null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // 测试放置 switch-connection 时模拟游戏换连接：旧连接的共享流释放，新连接从序号 0 重新发送登录数据。
                var switchFile = Path.Combine(root, "switch-connection");
                if (File.Exists(switchFile))
                {
                    File.Delete(switchFile);
                    if (residentIdentity)
                        stream.Restart("connection removed", Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency), DateTimeOffset.UtcNow,
                            GameStreamResetCause.ConnectionRemoved);
                    else { stream.Dispose(); stream = Open(); }
                    sequence = 0; interest = null; identitySent = false;
                }
                var mono = Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency); var utc = DateTimeOffset.UtcNow;
                context.Execute(() =>
                {
                    if (residentIdentity && !identitySent && !File.Exists(Path.Combine(root, "identity-hold"))) { Login(); identitySent = true; }
                    if (context.Current.Combat is { } current && current.Id != interest)
                    {
                        interest = current.Id;
                        if (!residentIdentity) Login();
                        Combat();
                    }
                    else if (repeat && interest is not null && context.Current.Combat is not null && ++ticks % 4 == 0) Combat();
                    void Login()
                    {
                        var character = CoexistenceInventoryFixture.PersonalCharacter(personal);
                        var withInventory = context.Current.Inventory is not null && context.InventoryLayout is not null;
                        Feed(Frame(20590, withInventory ? CoexistenceInventoryFixture.LoginBody(character, inventoryItems) : personal, true));
                        if (withInventory) Feed(Frame((int)CoexistenceInventoryFixture.Profile, CoexistenceInventoryFixture.ProfileBody(character)));
                        Feed(Frame(20054, Body("characterBodyBase64"), true));
                        if (residentIdentity) File.WriteAllText(Path.Combine(root, "identity-feeds"), (++identityFeeds).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    void Combat()
                    {
                        if (residentIdentity && File.Exists(Path.Combine(root, "pause-damage")))
                        {
                            var paused = Path.Combine(root, "damage-paused");
                            if (!File.Exists(paused)) File.WriteAllText(paused, "paused");
                            return;
                        }
                        if (residentIdentity && !identitySent) return;
                        var skill = new byte[35]; BinaryPrimitives.WriteUInt64LittleEndian(skill, entity);
                        BinaryPrimitives.WriteUInt64LittleEndian(skill.AsSpan(8), entity + 1);
                        BinaryPrimitives.WriteUInt32LittleEndian(skill.AsSpan(16), 77); Feed(Frame(20526, skill));
                        var damage = new byte[53]; BinaryPrimitives.WriteUInt64LittleEndian(damage, entity);
                        BinaryPrimitives.WriteUInt64LittleEndian(damage.AsSpan(8), entity + 1);
                        BinaryPrimitives.WriteInt64LittleEndian(damage.AsSpan(16), 4294967395); Feed(Frame(21196, damage));
                        if (residentIdentity) File.WriteAllText(Path.Combine(root, "damage-feeds"), (++damageFeeds).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        var buff = new byte[36]; BinaryPrimitives.WriteUInt64LittleEndian(buff, entity);
                        BinaryPrimitives.WriteUInt64LittleEndian(buff.AsSpan(8), 9); BinaryPrimitives.WriteUInt32LittleEndian(buff.AsSpan(16), 7);
                        BinaryPrimitives.WriteSingleLittleEndian(buff.AsSpan(20), 5); BinaryPrimitives.WriteInt32LittleEndian(buff.AsSpan(24), 1);
                        BinaryPrimitives.WriteUInt64LittleEndian(buff.AsSpan(28), entity); Feed(Frame(170069, buff));
                    }
                    stream.Tick(mono, utc);
                    var game = stream.Music.Current;
                    var identity = residentIdentity && game.Identity is { } local
                        ? new ObservedGameIdentity(new(local.CharacterId), local.RealmId.ToString(System.Globalization.CultureInfo.InvariantCulture), local.Nickname, new(local.ActorId)) : null;
                    context.Publish(new GameCaptureReport(new(residentIdentity ? game.Generation : captureGeneration, identity, null, null, new(0), "合成当前战斗流"),
                        new(++statusSequence), true, mono, utc, null, Tw20261110CombatProtocol.CombatProfile.Source));
                    // 真实捕获按 TCP 段喂入；大帧按 1460 字节分段，小帧沿用 17 字节分段覆盖跨段帧头。
                    void Feed(byte[] bytes)
                    {
                        var segment = bytes.Length > 4096 ? 1460 : 17;
                        for (var at = 0; at < bytes.Length; at += segment)
                            stream.Feed(unchecked(sequence + (uint)at), bytes.AsMemory(at, Math.Min(segment, bytes.Length - at)), mono, mono, utc);
                        sequence += (uint)bytes.Length;
                    }
                });
                await Task.Delay(50, ct);
            }
        }
        finally { stream.Dispose(); File.WriteAllText(Path.Combine(root, "handles"), "0"); }
        byte[] Body(string property) => Convert.FromBase64String(fixture.RootElement.GetProperty(property).GetString()!);
    }
    private static byte[] Frame(int code, byte[] body, bool compressed = false)
    {
        if (compressed)
        {
            using var memory = new MemoryStream(); using (var encoder = new BrotliStream(memory, CompressionLevel.Fastest, true)) encoder.Write(body);
            body = memory.ToArray();
        }
        var bytes = new byte[9 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(bytes, code);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), body.Length); bytes[8] = compressed ? (byte)1 : (byte)0; body.CopyTo(bytes, 9); return bytes;
    }
}
