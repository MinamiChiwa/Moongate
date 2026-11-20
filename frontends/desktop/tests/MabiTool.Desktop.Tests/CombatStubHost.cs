using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MabiTool.Contracts;

namespace MabiTool.Desktop.Tests;

/// <summary>
/// 桌面刷新用的替身后端：只实现战斗页与常驻刷新读到的接口，其余路由回 404（页面按“未读取”处理）。
/// 战斗状态按检查点推进：每个检查点工作区修订加一，并按真实宿主的写法推送摘要、效果、分段结算与定时测量事件
/// （事件的 resourceRevision 取工作区修订，效果事件只带 changedEntityIds）。按“方法 路由”累计请求次数。
/// 替身自身序列化响应的分配单独累计，便于从进程总分配里扣除。
/// </summary>
internal sealed partial class CombatStubHost : HttpMessageHandler
{
    public static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    public const string ObservationId = "obs-1";
    public const string Epoch = "epoch-1";
    public const string LocalId = "ent-local";
    private static readonly ObservationSource Source = new("stub", "1", "TW", "synthetic");
    private static readonly EntityKey LocalKey = Key(1);

    private readonly Lock gate = new();
    private readonly Channel<byte[]> events = Channel.CreateUnbounded<byte[]>();
    private readonly List<CombatEncounter> history = [];
    private readonly Dictionary<string, ulong> effectRevisions = new(StringComparer.Ordinal);
    private readonly List<CombatEntity> entities = [];
    private ulong workspaceRevision = 1, encounterRevision = 1, historyRevision = 1, eventSequence;
    private int encounterNumber = 1, ordinal = 1;
    private string? encounterId = "enc-1";
    private long damage;
    private double durationMs;
    private CombatMeasurementState measurement = Idle();
    private long stubBytes;

    public CombatStubHost(int monsters = 8, int historyCount = 25)
    {
        entities.Add(Entity(LocalId, LocalKey, "琉特", CombatEntityRole.LocalPlayer));
        for (var i = 0; i < monsters; i++)
            entities.Add(Entity($"ent-m{i}", Key((ulong)(100 + i)), $"巨魔首領{i}", CombatEntityRole.Monster));
        foreach (var entity in entities) effectRevisions[entity.Id] = 1;
        for (var i = 0; i < historyCount; i++)
            history.Add(Encounter($"enc-h{i}", 800_000 + i * 1000, 40_000, final: true, revision: 3));
    }

    /// <summary>按“方法 路由”（ID 段换成 {id}）累计的请求次数。</summary>
    public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, int> Events { get; } = new(StringComparer.Ordinal);
    public int TotalRequests => Requests.Values.Sum();
    public long StubAllocatedBytes => Interlocked.Read(ref stubBytes);
    public ulong WorkspaceRevision { get { lock (gate) return workspaceRevision; } }
    public string? CurrentTargetId => entities.Count > 1 ? entities[1].Id : null;

    public BootstrapResponse Bootstrap => new(1, new string('a', 32), "http://127.0.0.1:18543/",
        new("token", new AuthSession("desktop", "moongate-desktop", [], null, null, true)));

    /// <summary>客户端属性图标目录；为空时接口回 404（旧宿主）。</summary>
    public ClientIconCatalog? IconCatalog { get { lock (gate) return iconCatalog; } set { lock (gate) iconCatalog = value; } }
    private ClientIconCatalog? iconCatalog;

    /// <summary>每个可用键的图标内容（测试用代码生成的纯色 PNG，不含游戏图片）。</summary>
    public byte[] IconPng { get; set; } = [];

    public void ResetCounters()
    {
        Requests.Clear();
        Events.Clear();
        Interlocked.Exchange(ref stubBytes, 0);
    }

    public int Count(string route) => Requests.GetValueOrDefault(route);

    // ---- 战斗推进 ----

    /// <summary>一个检查点：有伤害时推送摘要事件，<paramref name="effects"/> 中的实体推送一条效果事件；修订只加一次。</summary>
    public void Checkpoint(bool hit, params string[] effects)
    {
        CombatSummary? summary;
        ulong revision;
        lock (gate)
        {
            if (!hit && effects.Length == 0) return;
            workspaceRevision++;
            revision = workspaceRevision;
            if (hit)
            {
                if (encounterId is null) { encounterNumber++; ordinal++; encounterId = $"enc-{encounterNumber}"; damage = 0; durationMs = 0; encounterRevision = 1; }
                damage += 3_500 + (long)(revision % 7) * 400;
                durationMs += 200;
                encounterRevision++;
            }
            foreach (var id in effects) effectRevisions[id] = effectRevisions.GetValueOrDefault(id) + 1;
            summary = hit ? Current()!.Summary : null;
        }
        if (summary is not null) Push("combat.summary.changed", ObservationId, revision, summary);
        if (effects.Length > 0)
        {
            var notification = Effects(LocalId).Snapshot with { Complete = false, Effects = [], SnapshotRequired = true, ChangedEntityIds = effects };
            Push("combat.effects.changed", ObservationId, revision, notification);
        }
    }

    /// <summary>
    /// 分段进行中停手：时长增长，与真实宿主一样不推送摘要事件（摘要事件只在内容变化时出现），工作区修订不变；
    /// 快照读到的时长与 DPS 随之变化。
    /// </summary>
    public void Pause(double milliseconds)
    {
        lock (gate) if (encounterId is not null) durationMs += milliseconds;
    }

    /// <summary>再推送一条摘要事件，修订与上一次相同（同一检查点的重复通知）。</summary>
    public void RepeatSummary()
    {
        CombatSummary summary;
        ulong revision;
        lock (gate) { summary = Current()!.Summary; revision = workspaceRevision; }
        Push("combat.summary.changed", ObservationId, revision, summary);
    }

    /// <summary>结算当前分段：放进战斗记录、推送结算事件；下一次伤害开始新分段。</summary>
    public void CloseEncounter()
    {
        CombatEncounter closed;
        ulong historyRev;
        lock (gate)
        {
            if (Current() is not { } current) return;
            closed = current with { Summary = current.Summary with { Final = true, BoundaryReason = "combat-left" }, EndedAt = Start.AddMilliseconds(durationMs) };
            history.Insert(0, closed);
            if (history.Count > 25) history.RemoveAt(history.Count - 1);
            historyRevision++;
            workspaceRevision++;
            historyRev = closed.Revision.Value;
            encounterId = null;
        }
        Push("combat.encounter.closed", closed.Id, historyRev,
            new CombatEncounterClosed("owner", ObservationId, closed.Id, closed.Revision, closed.Summary, "combat-left"));
    }

    /// <summary>
    /// 定时测量：进入等待或计时、计时中累计，结束时把结果写进战斗记录。与真实宿主一样只在阶段变化时推送测量事件（带完整状态），
    /// 计时中的累计（<paramref name="publish"/> 为假）只改读取结果、修订不变。
    /// </summary>
    public void Measure(string phase, string id, double remainingMs = 0, bool finish = false, bool publish = true)
    {
        CombatMeasurementState state;
        lock (gate)
        {
            var revision = new UInt64String(measurement.Revision.Value + 1);
            if (finish)
            {
                var result = new CombatMeasurementResult("timed-measurement-v1", 20_000, 0, new(new BigInteger(damage)), damage / 20.0, 0, 19_800, 19_800,
                    true, [], Start, Start.AddSeconds(20));
                var record = Encounter(id, damage, 20_000, final: true, revision: 2) with
                {
                    Record = new("timed-measurement", null, result, null)
                };
                history.Insert(0, record);
                historyRevision++;
                state = measurement = Idle() with { Revision = revision, LastResult = result, LastResultId = id };
            }
            else
                state = measurement = Idle() with
                {
                    Phase = phase, Revision = publish ? revision : measurement.Revision, MeasurementId = id, RemainingMs = phase == "running" ? remainingMs : null,
                    Damage = new(new BigInteger(damage)), CurrentDps = damage / 20.0
                };
        }
        if (!publish) return;
        Push("combat.measurement.changed", "combat-measurement", state.Revision.Value, state);
        if (finish) Push("combat.encounter.closed", id, 2, new CombatEncounterClosed("owner", ObservationId, id, new(2), Current()?.Summary ?? history[0].Summary, "measurement"));
    }

    private static CombatMeasurementState Idle() => new("idle", new(20_000, 0), new(1), true, null, ObservationId, null, null, null, null, null,
        null, [], null, null, new(1));

    // ---- HTTP ----

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var route = request.Method.Method + " " + IdSegment().Replace(path, "/{id}");
        Requests.AddOrUpdate(route, 1, (_, value) => value + 1);
        if (path == "/api/v1/events")
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChannelStream(events.Reader)) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(response);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        try { return Task.FromResult(Respond(request.Method.Method, path, request.Content)); }
        finally { Interlocked.Add(ref stubBytes, GC.GetAllocatedBytesForCurrentThread() - before); }
    }

    private HttpResponseMessage Respond(string method, string path, HttpContent? content)
    {
        lock (gate)
        {
            switch (method, path)
            {
                case ("GET", "/api/v1/combat/capabilities"): return Json(new CombatCapabilities(false, []));
                case ("GET", "/api/v1/game/input-ownership"): return Json(new GameInputOwnershipSnapshot(Epoch, false, Start, []));
                case ("GET", "/api/v1/game/binding"):
                    return Json(new GameBindingSnapshot("bound", new(new("game", "TW", 4242, Start, new(1), new(1), null), new(0, 0, 1280, 720), 96, 96, false, true),
                        false, Epoch, new(1)));
                case ("GET", "/api/v1/combat/snapshot"):
                    return Json(new CombatSnapshot(Epoch, "cursor", new(workspaceRevision), Workspace(), new(history.Count, new(0), 0, new(0), false, 0, new(0))));
                case ("GET", "/api/v1/combat/encounters"): return Json(new CombatEncounterPage(history.ToArray(), null, new(historyRevision)));
                case ("GET", "/api/v1/combat/measurement"): return Json(measurement);
                case ("GET", "/api/v1/combat/auto-observation"): return Json(new CombatAutoObservationState(true, "observing", null, ObservationId, new(1)));
                case ("POST", "/api/v1/combat/ranges/query"):
                {
                    var query = JsonSerializer.Deserialize<CombatRangeQuery>(content!.ReadAsStream(), ContractJson.Options)!;
                    var source = query.EncounterId is { } id ? Find(id) : Current() ?? history.FirstOrDefault();
                    return source is null ? NotFound() : Json(Range(source, query));
                }
            }
            var parts = path.Split('/');
            if (method == "GET" && path == "/api/v1/client-icons") return iconCatalog is { } catalog ? Json(catalog) : NotFound();
            if (method == "GET" && parts is [_, "api", "v1", "client-icons", var key, "content"])
            {
                if (iconCatalog?.Icons.FirstOrDefault(icon => icon.Key == key) is not { Available: true, Version: { } version }) return NotFound();
                var png = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(IconPng) };
                png.Content.Headers.ContentType = new("image/png");
                png.Headers.Add("X-Content-Sha256", version);
                return png;
            }
            if (method == "GET" && parts is [_, "api", "v1", "combat", "entities", var entity, "effects"])
                return effectRevisions.ContainsKey(entity) ? Json(Effects(entity)) : NotFound();
            if (method == "GET" && parts is [_, "api", "v1", "combat", "encounters", var encounter])
                return Find(encounter) is { } found ? Json(found) : NotFound();
            if (method == "POST" && parts is [_, "api", "v1", "combat", "encounters", var timeline, "timeline", "query"])
                return Find(timeline) is { } source ? Json(Timeline(source)) : NotFound();
            return NotFound();
        }
    }

    // 进行中的定时测量按当前分段的累计给出范围结果（真实宿主在第一个检查点之后即可查询）。
    private CombatEncounter? Find(string id) => id == encounterId ? Current()
        : id == measurement.MeasurementId && Current() is { } live ? live with { Id = id }
        : history.FirstOrDefault(item => item.Id == id);

    private void Push<T>(string type, string resource, ulong revision, T data) where T : notnull
    {
        var sequence = Interlocked.Increment(ref eventSequence);
        var envelope = new EventEnvelope<T>($"{Epoch}:{sequence}", type, 1, "combat-observation", resource, new(revision), Start, null, data);
        var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
        Events.AddOrUpdate(type, 1, (_, value) => value + 1);
        events.Writer.TryWrite(Encoding.UTF8.GetBytes($"id: {envelope.EventId}\nevent: {type}\ndata: {json}\n\n"));
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };

    [GeneratedRegex(@"/(?:enc|ent|op|obs)-[A-Za-z0-9-]+")]
    private static partial Regex IdSegment();

    // ---- 契约对象 ----

    private static EntityKey Key(ulong id) => new("game", new(1), new(id), new(1));
    private static CombatDamageTotals Totals(long value) => new(new(new BigInteger(value)), new(0), new(0), new(0));

    private static CombatEntity Entity(string id, EntityKey key, string name, CombatEntityRole role) =>
        new(id, key, name, name, role, null, false, true, Start, ObservationQuality.Confirmed);

    private static CombatDamageGroup Group(string key, string name, long amount, ulong hits) =>
        new(key, name, Totals(amount), new(new BigInteger(amount)), new(hits), new(hits), new(hits / 4), new(hits / 10),
            MinimumDamage: new(100), MaximumDamage: new(9000), CriticalEligibleCount: new(hits), MainHitEligibleCount: new(hits),
            FirstAttackCount: new(0));

    private CombatEncounter? Current() => encounterId is null ? null : Encounter(encounterId, damage, durationMs, final: false, revision: encounterRevision);

    private CombatEncounter Encounter(string id, long amount, double duration, bool final, ulong revision)
    {
        var skills = Enumerable.Range(0, 14).Select(i => Group($"{1000 + i}", $"技能{i}", amount / (i + 2), (ulong)(amount / 5000 + i))).ToArray();
        var targets = entities.Skip(1).Select((entity, i) => Group(entity.Id, entity.DisplayName, amount / (i + 2), (ulong)(amount / 6000 + i))).ToArray();
        var summary = new CombatSummary(id, LocalKey, "琉特", new(new BigInteger(amount)), duration, duration > 0 ? amount / (duration / 1000) : null,
            null, 5000, "v1", final, final ? "combat-left" : "none", ObservationQuality.Confirmed, [], Revision: new(revision));
        return new(id, "owner", ObservationId, "game", new(1), new(revision), summary, Start, final ? Start.AddMilliseconds(duration) : null, 0,
            final ? duration : null, true, Totals(amount), Totals(amount), Totals(0), Totals(0), skills, targets, [],
            new(entities.Count, 0, 0, 0, new(0), new(0), new(0), new(0)), false, Segment: new(ordinal, "first-damage", 1));
    }

    private CombatWorkspace Workspace()
    {
        var status = new CombatObservationStatus(ObservationId, "owner", "game", "observing", Source, LocalKey, new("game"), new(workspaceRevision),
            Start, Start, durationMs, ObservationQuality.Confirmed, [], new(0));
        return new(status, Current(), entities.ToArray(), Effects(LocalId), new(workspaceRevision));
    }

    private CombatEntityEffects Effects(string entityId)
    {
        var entity = entities.First(item => item.Id == entityId);
        var revision = new UInt64String(effectRevisions[entityId]);
        var views = Enumerable.Range(0, 12).Select(i =>
        {
            var instance = new EffectInstance(entity.Key, new((ulong)(500 + i)), new((ulong)(9000 + i)), LocalKey, Start, Start.AddSeconds(60 + i),
                EffectDurationKind.Finite, 1, null, EffectPresence.Observed, ObservationQuality.Confirmed, revision);
            return new CombatEffectView(instance, 60_000 + i * 1000, Start, false, false, Polarity: i % 3 == 0 ? "buff" : i % 3 == 1 ? "debuff" : null);
        }).ToArray();
        var snapshot = new EffectsSnapshot(entity.Key, true, views.Select(view => view.Instance).ToArray(), Start, ObservationQuality.Confirmed,
            EntityId: entityId, Revision: revision);
        return new(entityId, "owner", ObservationId, revision, snapshot, views);
    }

    private static CombatTimelinePage Timeline(CombatEncounter source) => new(source.Id, source.Revision, [], null,
        Enumerable.Range(0, 20).Select(i => new CombatDamageBucket(i * 1000, (i + 1) * 1000, Totals(5000 + i), new(3))).ToArray(),
        [new("ent-m0", new(500), 0, source.Summary.DurationMs, source.Summary.DurationMs / 2, source.Summary.DurationMs, 0.5, [])], [], false);

    private static CombatRangeSummary Range(CombatEncounter source, CombatRangeQuery query) => new("observed-range-v1", query.Scope, ObservationId,
        [source.Id], 0, source.Summary.DurationMs, query.TargetId, null, source, source.Skills[0], source.Targets,
        source.Summary.AttributedDamage, 0, source.Summary.DurationMs, new(null, null, null, null, null, null, null, ["break-flags-unknown"]), true, []);

    /// <summary>SSE 正文：事件写进通道后才可读，读到通道末尾前保持在线。</summary>
    private sealed class ChannelStream(ChannelReader<byte[]> reader) : Stream
    {
        private byte[] current = [];
        private int offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset >= current.Length)
            {
                current = await reader.ReadAsync(cancellationToken);
                offset = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
    }
}
