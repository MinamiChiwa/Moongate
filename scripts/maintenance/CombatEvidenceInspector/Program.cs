using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;

// 授权本地样本的离线检查；报告只包含布局与计数，不输出角色身份或原始负载。
if (args.Length is < 1 or > 3 || args.Length == 3 && args[2] != "--export-sanitized")
    throw new ArgumentException("需要采集目录、可选的当前协议枚举 JSON，第三参数仅接受 --export-sanitized");
var currentProtocols = args.Length >= 2;
var exportSanitized = args.Length == 3;
var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
if (repository is null) throw new InvalidOperationException("无法定位仓库");
var temporary = Path.GetFullPath(Path.Combine(repository.FullName, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
var folder = Path.GetFullPath(args[0]);
if (!folder.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("样本必须位于 artifacts/tmp");
for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("样本路径不能包含链接");
string Input(string name)
{
    var path = Path.Combine(folder, name);
    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("样本文件不能是链接");
    return path;
}
var summaryPath = Input("summary.json");
if (new FileInfo(summaryPath).Length > 1024 * 1024) throw new InvalidDataException("摘要过长");
using var summary = JsonDocument.Parse(File.ReadAllBytes(summaryPath));
var raw = Input("packets.jsonl");
if (new FileInfo(raw).Length > 32 * 1024 * 1024) throw new InvalidDataException("样本超过字节预算");
using (var content = File.OpenRead(raw))
    if (!Convert.ToHexStringLower(SHA256.HashData(content)).Equals(summary.RootElement.GetProperty("rawSha256").GetString(), StringComparison.Ordinal))
        throw new InvalidDataException("样本哈希与摘要不符");
// 战斗帧按 tw-20261110 线上协议号解析；可选的协议枚举 JSON（名称 → 编号）只决定分帧可识别的编号集合与报告中的名称。
var protocols = Tw20261110CombatProtocol.StreamProfile.KnownProtocols.ToHashSet();
var names = new Dictionary<int, string>();
if (currentProtocols)
{
    if (new FileInfo(args[1]).Length > 1024 * 1024) throw new InvalidDataException("协议枚举过长");
    using var table = JsonDocument.Parse(File.ReadAllBytes(args[1]));
    protocols.Clear();
    foreach (var field in table.RootElement.EnumerateObject())
    {
        var code = field.Value.GetInt32();
        if (code > 0) { protocols.Add(code); names.TryAdd(code, field.Name); }
    }
}
var profile = new GameStreamProfile(protocols, protocols);
var start = summary.RootElement.GetProperty("startedAt").GetDateTimeOffset();
var frames = new Dictionary<int, Shape>();
var gaps = new Dictionary<string, int>(StringComparer.Ordinal);
var protocolFailures = new Dictionary<(int? Code, string Reason), long>();
var projection = exportSanitized ? new SanitizedProjection() : null;
var streams = new Dictionary<string, Connection>(StringComparer.Ordinal);
var source = new ObservationSource("local-diagnostic", "1.0.0", "TW", "unverified-current-client");
long records = 0, damage = 0, effects = 0, identityFrames = 0;
long localDamage = 0, selfEffects = 0, monsterEffects = 0;
var observedMonsters = new HashSet<EntityKey>();
var identityResolved = false;
void Gap(string reason) => gaps[reason] = gaps.GetValueOrDefault(reason) + 1;
void ProtocolFailure(int? code, string reason) => protocolFailures[(code, reason)] = protocolFailures.GetValueOrDefault((code, reason)) + 1;
void Accept(CombatFrame value, int? opcode)
{
    damage += value.Damage.Count; effects += value.Effects.Count;
    identityFrames += value.Entities.Count(x => x.Role.KnownValue == CombatEntityRole.LocalPlayer);
    identityResolved |= value.Entities.Any(x => x.Role.KnownValue == CombatEntityRole.LocalPlayer);
    var local = value.Entities.FirstOrDefault(x => x.Role.KnownValue == CombatEntityRole.LocalPlayer)?.Key;
    var monsters = value.Entities.Where(x => x.Role.KnownValue == CombatEntityRole.Monster).Select(x => x.Key).ToHashSet();
    observedMonsters.UnionWith(monsters);
    localDamage += value.Damage.Count(x => x.SourceEntity == local && x.Chain != true);
    selfEffects += value.Effects.Count(x => x.EntityKey == local);
    monsterEffects += value.Effects.Count(x => monsters.Contains(x.EntityKey));
    foreach (var gap in value.Gaps)
    {
        Gap(gap.Reason);
        if (gap.Reason == "combat.protocolInvalid") ProtocolFailure(opcode, gap.Reason);
    }
    projection?.Accept(value);
}
foreach (var line in File.ReadLines(raw))
{
    if (++records > 100000 || line.Length > 1024 * 1024) throw new InvalidDataException("样本记录超限");
    using var document = JsonDocument.Parse(line);
    var row = document.RootElement;
    if (row.GetProperty("index").GetInt64() != records) throw new InvalidDataException("样本索引不连续");
    var id = row.GetProperty("connectionId").GetString()!;
    if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("连接 ID 无效");
    if (!streams.TryGetValue(id, out var connection))
    {
        if (streams.Count >= 32) throw new InvalidDataException("连接超限");
        int? activeOpcode = null;
        connection = new(new("diagnostic-" + id, source, frame => Accept(frame, activeOpcode)));
        var current = connection;
        current.Stream = new((code, body) =>
        {
            if (!frames.TryGetValue(code, out var shape)) frames.Add(code, shape = new());
            shape.Count++; shape.MinimumLength = Math.Min(shape.MinimumLength, body.Length);
            shape.MaximumLength = Math.Max(shape.MaximumLength, body.Length);
            if (current.Stream!.FrameEncoding == 1) shape.Compressed++;
            if (Tw20261110CombatProtocol.ProtocolsV3.Contains(code))
            {
                activeOpcode = code;
                try
                {
                    current.Observer.Accept(code, body, current.Stream.FrameStreamOffset, current.Stream.FrameWireLength,
                        current.Now, start.AddMilliseconds(current.Now), current.Stream.FrameEncoding);
                }
                finally { activeOpcode = null; }
            }
        }, reason => { Gap(reason); current.Observer.Reset("combat.streamGap", current.Now, start.AddMilliseconds(current.Now)); },
            profile, rejectFrame: (code, _) => { Gap("frame.expandFailed"); ProtocolFailure(code, "frame.expandFailed"); });
        streams.Add(id, current);
    }
    var now = row.GetProperty("monotonicMs").GetDouble();
    if (!double.IsFinite(now) || now < connection.Now || now > 181000) throw new InvalidDataException("样本时点无效");
    connection.Now = now;
    var bytes = Convert.FromBase64String(row.GetProperty("payloadBase64").GetString()!);
    if (bytes.Length > 262144) throw new InvalidDataException("样本负载过长");
    var sequence = row.GetProperty("tcpSequence").GetUInt32();
    var flags = row.GetProperty("tcpFlags").GetByte();
    if ((flags & 2) != 0) { connection.Stream!.Restart("tcp.syn"); sequence = unchecked(sequence + 1); connection.EndSequence = null; }
    // RFC 9293 保活可能携带窗口前一字节的任意值，原始记录保留但不进入重组。
    if ((flags & 16) != 0 && bytes.Length == 1 && connection.EndSequence is { } end && sequence == unchecked(end - 1)) continue;
    var nextSequence = unchecked(sequence + (uint)bytes.Length);
    if (connection.EndSequence is null || unchecked((int)(nextSequence - connection.EndSequence.Value)) > 0)
        connection.EndSequence = nextSequence;
    for (var offset = 0; offset < bytes.Length; offset += 65536)
        connection.Stream!.Feed(unchecked(sequence + (uint)offset), bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)), now, now);
    connection.Observer.Tick(now, start.AddMilliseconds(now));
    if ((flags & 5) != 0) connection.Stream!.Restart("tcp.closed");
}
foreach (var connection in streams.Values)
{
    connection.Stream!.CheckGap(connection.Now + 1501);
    connection.Observer.Finish(connection.Now + 1501, start.AddMilliseconds(connection.Now + 1501));
}
var report = new
{
    schemaVersion = 1, sampleKind = "local-live-diagnostic", productCapabilitiesEnabled = false,
    currentFieldsVerified = false, parsingLayout = nameof(CombatProtocolLayout.Tw20261110), inputPackets = records, connections = streams.Count,
    timingSource = "local-capture-receive-monotonic",
    captureIntegrity = new
    {
        rawHashVerified = true,
        reason = summary.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
        statisticsStatus = summary.RootElement.TryGetProperty("statisticsStatus", out var status) ? status.GetString() : null,
        droppedPackets = summary.RootElement.TryGetProperty("droppedPackets", out var dropped) && dropped.ValueKind == JsonValueKind.Number ? dropped.GetInt64() : (long?)null,
        rejectedPackets = summary.RootElement.TryGetProperty("rejectedPackets", out var rejected) ? rejected.GetInt64() : (long?)null
    },
    combatObservation = new { protocolTableSupplied = currentProtocols, localIdentityResolved = identityResolved,
        identityFrames, damageEvents = damage, effectEvents = effects, localDamageEvents = localDamage,
        selfEffectEvents = selfEffects, monsterEffectEvents = monsterEffects, observedMonsterInstances = observedMonsters.Count },
    protocolShapes = frames.OrderBy(x => x.Key).Select(x => new { code = x.Key, name = names.GetValueOrDefault(x.Key), count = x.Value.Count,
        minimumBodyBytes = x.Value.MinimumLength, maximumBodyBytes = x.Value.MaximumLength, compressed = x.Value.Compressed }).ToArray(),
    protocolFailures = protocolFailures.OrderBy(x => x.Key.Code).ThenBy(x => x.Key.Reason).Select(x => new
        { code = x.Key.Code, name = x.Key.Code is { } code ? names.GetValueOrDefault(code) : null, reason = x.Key.Reason, count = x.Value }).ToArray(),
    gaps
};
var output = Path.Combine(folder, exportSanitized ? "analysis.current-protocols.sanitized.json" : currentProtocols ? "analysis.current-protocols.json" : "analysis.json");
using (var target = new FileStream(output, FileMode.CreateNew)) JsonSerializer.Serialize(target, report, new JsonSerializerOptions { WriteIndented = true });
if (projection is not null)
    using (var target = new FileStream(Path.Combine(folder, "projection.sanitized.json"), FileMode.CreateNew))
        JsonSerializer.Serialize(target, projection.Document(), new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(JsonSerializer.Serialize(new { inputPackets = records, connections = streams.Count,
    protocolCount = frames.Count, identityResolved, damageEvents = damage, effectEvents = effects, output }));

sealed class Shape { public long Count, Compressed; public int MinimumLength = int.MaxValue, MaximumLength; }
sealed class Connection(TwCombatObserver observer)
{
    public TwCombatObserver Observer { get; } = observer;
    public GameTcpStream? Stream { get; set; }
    public double Now { get; set; }
    public uint? EndSequence { get; set; }
}

sealed class SanitizedProjection
{
    private const int MaximumCases = 24;
    private readonly Dictionary<EntityKey, string> entities = [];
    private readonly Dictionary<(string Game, UInt64String Generation), string> sessions = [];
    private readonly Dictionary<(EntityKey Entity, UInt64String Instance), string> instances = [];
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private readonly List<object> cases = [];
    private string Entity(EntityKey key)
    {
        if (!entities.TryGetValue(key, out var alias)) entities.Add(key, alias = "entity-" + (entities.Count + 1));
        return alias;
    }
    private string Session(EntityKey key)
    {
        var identity = (key.GameSessionId, key.ObservationGeneration);
        if (!sessions.TryGetValue(identity, out var alias)) sessions.Add(identity, alias = "session-" + (sessions.Count + 1));
        return alias;
    }
    private string Instance(EntityKey entity, UInt64String instance)
    {
        var identity = (entity, instance);
        if (!instances.TryGetValue(identity, out var alias)) instances.Add(identity, alias = "effect-instance-" + (instances.Count + 1));
        return alias;
    }
    private bool Select(string kind) => cases.Count < MaximumCases && selected.Add(kind);
    public void Accept(CombatFrame frame)
    {
        var local = frame.Entities.FirstOrDefault(x => x.Role.KnownValue == CombatEntityRole.LocalPlayer);
        if (local is not null && Select("local-identity"))
            cases.Add(new { kind = "local-identity", session = Session(local.Key), entity = Entity(local.Key),
                role = "localPlayer", monotonicMs = frame.MonotonicMs, quality = local.Quality.RawValue });
        foreach (var hit in frame.Damage)
        {
            var attribution = hit.Chain == true ? "chain" : local is null ? "unresolved" :
                hit.SourceEntity == local.Key ? "local" : "other";
            if (!Select("damage." + attribution)) continue;
            cases.Add(new { kind = "damage", attribution, session = Session(hit.SourceEntity),
                sourceEntity = Entity(hit.SourceEntity), targetEntity = Entity(hit.TargetEntity),
                skillCasterEntity = hit.SkillCasterKey is { } caster ? Entity(caster) : null,
                skillId = hit.SkillId.ToString(), amount = hit.Amount.ToString(), amountKind = hit.AmountKind.RawValue,
                monotonicMs = hit.MonotonicMs, critical = hit.Critical, hit = hit.Hit,
                skillResolved = hit.SkillResolved, chain = hit.Chain, quality = hit.Quality.RawValue });
        }
        var monsters = frame.Entities.Where(x => x.Role.KnownValue == CombatEntityRole.Monster).ToDictionary(x => x.Key,
            x => x.RawName.Length == 8 && x.RawName.All(char.IsAsciiHexDigit) ? x.RawName : null);
        foreach (var effect in frame.Effects)
        {
            var scope = effect.EntityKey == local?.Key ? "self" : monsters.ContainsKey(effect.EntityKey) ? "monster" : null;
            if (scope is null || !Select("effect." + scope + "." + effect.Kind.RawValue)) continue;
            var item = new Dictionary<string, object?>
            {
                ["kind"] = "effect", ["scope"] = scope, ["change"] = effect.Kind.RawValue,
                ["session"] = Session(effect.EntityKey), ["entity"] = Entity(effect.EntityKey),
                ["effectId"] = effect.EffectId.ToString(), ["effectInstance"] = Instance(effect.EntityKey, effect.InstanceId),
                ["casterEntity"] = effect.CasterKey is { } caster ? Entity(caster) : null,
                ["monotonicMs"] = effect.MonotonicMs, ["durationKind"] = effect.DurationKind.RawValue,
                ["durationMs"] = effect.DurationMs, ["stacks"] = effect.Stacks, ["aggregated"] = effect.Aggregated,
                ["category"] = effect.Category, ["quality"] = effect.Quality.RawValue
            };
            if (scope == "monster" && monsters[effect.EntityKey] is { } typeCode) item["monsterTypeCode"] = typeCode;
            cases.Add(item);
        }
    }
    public object Document() => new { schemaVersion = 1, sourceEvidenceKind = "live-current-field-projection",
        parsingLayout = nameof(CombatProtocolLayout.Tw20261110),
        currentFieldsVerified = false, timingSource = "capture-relative-monotonic-ms", maximumCases = MaximumCases, cases };
}
