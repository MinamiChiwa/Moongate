using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Xunit;
using Xunit.Abstractions;
using Op = MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>
/// 本机回放验证（默认不运行）：设置环境变量 MABITOOL_COMBAT_SAMPLES 指向只读的 combat-state-20261115 样本目录时，
/// 逐连接回放六份实采样本，核对首领 HP 解密通过率、HP 下降与伤害飘字对账、boss-b 当前目标切换次数，
/// 并统计正常接入与中段重启（模拟 Worker 重启，有无实体延续记录）下本机命中目标的“未知名称”与“血量未读取”占比。
/// 设置 MABITOOL_TW_CLIENT 指向本机客户端 appdata 目录时按客户端名称表统计名称。样本与客户端只读，结果只写测试输出。
/// </summary>
[Trait("Category", "LocalSample")]
public sealed class MonsterHealthSampleReplayTests(ITestOutputHelper output)
{
    private static readonly string[] Samples = ["combat-state-01-flee", "combat-state-01-flee-b", "combat-state-02-death", "combat-state-03-party",
        "combat-state-04-boss", "combat-state-04-boss-b"];

    [Fact]
    public void SamplesDecryptReconcileAndReduceUnknownTargets()
    {
        if (Environment.GetEnvironmentVariable("MABITOOL_COMBAT_SAMPLES") is not { Length: > 0 } root || !Directory.Exists(root)) return;
        var client = Environment.GetEnvironmentVariable("MABITOOL_TW_CLIENT");
        var names = client is { Length: > 0 } && Directory.Exists(client) ? ClientCombatNameCatalog.Extract(client) : null;
        var report = new Dictionary<string, object>();
        ulong decrypted = 0, failed = 0;
        foreach (var sample in Samples)
        {
            var path = Path.Combine(root, sample, "packets.jsonl");
            if (!File.Exists(path)) continue;
            var normal = Replay(path, restartFraction: null, carry: false);
            var restartedWithout = Replay(path, 0.5, carry: false);
            var restartedWith = Replay(path, 0.5, carry: true);
            decrypted += normal.Decrypted; failed += normal.Failed;
            report[sample] = new
            {
                decryption = new { normal.Decrypted, normal.Failed },
                reconcile = normal.Reconcile,
                switches = new { newRule = normal.TargetSwitches, lastHitChanges = normal.LastHitChanges, mainTargetChanges = normal.MainTargetChanges },
                unknownNames = new
                {
                    normalBefore = Ratio(normal.Hits, hit => !OldNamed(hit, names)), normalAfter = Ratio(normal.Hits, hit => !NewNamed(hit, names)),
                    restartBefore = Ratio(restartedWithout.HitsAfterRestart, hit => !OldNamed(hit, names)),
                    restartAfter = Ratio(restartedWith.HitsAfterRestart, hit => !NewNamed(hit, names))
                },
                healthUnread = new
                {
                    normal = Ratio(normal.Hits, Unread),
                    restartWithoutCarry = Ratio(restartedWithout.HitsAfterRestart, Unread),
                    restartWithCarry = Ratio(restartedWith.HitsAfterRestart, Unread)
                },
                hits = normal.Hits.Count, hitsAfterRestart = restartedWith.HitsAfterRestart.Count, target = NormalDiagnostics(path)
            };
            foreach (var (boss, value) in normal.Reconcile)
                Assert.True(value.Drop <= value.Damage && value.Damage - value.Drop <= value.LargestHit, $"{sample} {boss}: HP 下降 {value.Drop}，伤害 {value.Damage}");
        }
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal(0UL, failed);
        Assert.True(decrypted >= 1482, $"解密 {decrypted} 条");
    }

    private sealed record Hit(CombatEntityObservation? Target, CombatEntityHealth? Health);
    private sealed record Reconciliation(ulong Drop, ulong Damage, ulong LargestHit);
    private sealed record Result(ulong Decrypted, ulong Failed, List<Hit> Hits, List<Hit> HitsAfterRestart, Dictionary<ulong, Reconciliation> Reconcile,
        int TargetSwitches, int LastHitChanges, int MainTargetChanges);

    /// <summary>血量未读取：没有已读的当前值，且不是“由同场首领体现”的加密锁血实体（领域按本机命中把它标为 shared 并改指首领）。</summary>
    private static bool Unread(Hit hit) => hit.Health is not { State: "read" } && hit.Health is not { Encrypted: true, Updated: not true };
    private static string? NormalDiagnostics(string path) { Replay(path, null, false); return Diagnostics; }
    private static double? Ratio(IReadOnlyCollection<Hit> hits, Func<Hit, bool> unknown) =>
        hits.Count == 0 ? null : Math.Round(hits.Count(unknown) / (double)hits.Count, 4);

    /// <summary>改前的规则：只有带出现帧的怪物有名称占位，空名与其他类型都是未知。</summary>
    private static bool OldNamed(Hit hit, CombatNameTables? names) =>
        hit.Target is { Role.KnownValue: CombatEntityRole.Monster } target && CombatNames.MonsterSheetId(target.RawName) is { } id &&
        (names is null || names.Monsters.ContainsKey(id));
    /// <summary>改后的规则：怪物、其他自动体按各自表，空名怪物按首领组名，AI 同伴为通用文案。</summary>
    private static bool NewNamed(Hit hit, CombatNameTables? names)
    {
        if (CombatNames.MonsterLabel(hit.Target) is not { } label) return false;
        if (label.Kind == CombatNames.AiCompanion || names is null) return true;
        var id = (uint)label.SheetId.Value;
        return label.Kind == CombatNames.Monster
            ? names.Monsters.ContainsKey(id) || names.BossGroupNames?.ContainsKey(id) == true
            : names.Actors?.GetValueOrDefault(label.Kind)?.ContainsKey(id) == true;
    }

    private static Result Replay(string packets, double? restartFraction, bool carry)
    {
        var rows = File.ReadLines(packets).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        var span = rows.Count == 0 ? 0 : rows[^1].GetProperty("monotonicMs").GetDouble();
        var restartAt = restartFraction is { } fraction ? span * fraction : double.PositiveInfinity;
        var origin = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        ulong generation = 0, decrypted = 0, failed = 0;
        var frames = new List<(CombatFrame Frame, CombatEntityObservation? Local, bool AfterRestart)>();
        var streams = new Dictionary<string, Connection>(StringComparer.Ordinal);
        var restarted = false;
        Connection Open(string id, bool afterRestart)
        {
            var connection = new Connection();
            connection.Observer = new TwCombatObserver("sample", Op.CombatProfile.Source, frame => frames.Add((frame, connection.Observer!.LocalEntity, afterRestart)), () => ++generation);
            connection.Observer.Configure(new(true, true, true, true, true, true));
            connection.Stream = new((code, body) =>
            {
                if (Op.ProtocolsV4.Contains(code))
                    connection.Observer.Accept(code, body, connection.Stream!.FrameStreamOffset, connection.Stream.FrameWireLength, connection.Now, origin.AddMilliseconds(connection.Now), connection.Stream.FrameEncoding);
            }, reason => connection.Observer.Reset("combat.streamGap", connection.Now, origin.AddMilliseconds(connection.Now)), Op.StreamProfile);
            return connection;
        }
        foreach (var row in rows)
        {
            var id = row.GetProperty("connectionId").GetString()!;
            var now = row.GetProperty("monotonicMs").GetDouble();
            if (!restarted && now >= restartAt)
            {
                // 模拟 Worker 重启：每条连接从下一个包起中途接入，沿用本机身份（宿主下发的身份）；有延续记录时补足实体。
                restarted = true;
                foreach (var key in streams.Keys.ToArray())
                {
                    var previous = streams[key];
                    var exported = previous.Observer!.ExportCarry();
                    var local = previous.Observer.LocalEntity; var character = previous.Observer.LocalCharacterId;
                    (decrypted, failed) = (decrypted + previous.Observer.HealthDecryption.Decrypted, failed + previous.Observer.HealthDecryption.Failed);
                    previous.Observer.Finish(now, origin.AddMilliseconds(now));
                    var fresh = Open(key, true); fresh.Now = now; fresh.Joined = true;
                    if (local is not null && character is { } owner)
                        fresh.Observer!.SeedLocal(owner, local.Key.RawInstanceId.Value, now, origin.AddMilliseconds(now), carried: true);
                    if (carry && exported.Count > 0) fresh.Observer!.SeedEntities(exported, now, origin.AddMilliseconds(now));
                    streams[key] = fresh;
                }
            }
            if (!streams.TryGetValue(id, out var connectionState)) streams[id] = connectionState = Open(id, restarted);
            if (now < connectionState.Now) now = connectionState.Now;
            connectionState.Now = now;
            var bytes = Convert.FromBase64String(row.GetProperty("payloadBase64").GetString()!);
            var sequence = row.GetProperty("tcpSequence").GetUInt32();
            var flags = row.GetProperty("tcpFlags").GetByte();
            if ((flags & 2) != 0) { connectionState.Stream!.Restart("tcp.syn", GameStreamResetCause.ConnectionSyn); sequence = unchecked(sequence + 1); }
            for (var offset = 0; offset < bytes.Length; offset += 65536)
                connectionState.Stream!.Feed(unchecked(sequence + (uint)offset), bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)), now, now);
            connectionState.Observer!.Tick(now, origin.AddMilliseconds(now));
            if ((flags & 5) != 0) connectionState.Stream!.Restart("tcp.closed", GameStreamResetCause.ConnectionClosed);
        }
        foreach (var connection in streams.Values)
        {
            connection.Observer!.Finish(connection.Now + 1501, origin.AddMilliseconds(connection.Now + 1501));
            decrypted += connection.Observer.HealthDecryption.Decrypted; failed += connection.Observer.HealthDecryption.Failed;
        }

        // 本机命中：伤害帧的攻击者为该帧的本机实体，目标取同一帧的实体快照。
        var hits = new List<Hit>(); var after = new List<Hit>();
        var damage = new Dictionary<ulong, (ulong Sum, ulong Max)>(); var lastHit = (ulong?)null; var lastHitChanges = 0;
        var mainChanges = 0; ulong? main = null; var mainKnown = false;
        var healthSeries = new Dictionary<ulong, (ulong First, ulong Last, int Changes)>();
        // 帧只在 HP 变化后附带 health：与宿主一样沿用同一代次实体上次附带的值。
        var known = new Dictionary<(ulong Generation, ulong Id), CombatEntityHealth>();
        foreach (var (frame, local, afterRestart) in frames)
        {
            foreach (var entity in frame.Entities)
                if (entity.Health is { } health) known[(frame.Generation.Value, entity.Key.RawInstanceId.Value)] = health;
            foreach (var entity in frame.Entities)
                if (entity.IsBoss == true || entity.Health?.Encrypted == true)
                    if (entity.Health?.Current is { } value)
                        healthSeries[entity.Key.RawInstanceId.Value] = healthSeries.TryGetValue(entity.Key.RawInstanceId.Value, out var series)
                            ? (series.First, value.Value, series.Changes + (series.Last != value.Value ? 1 : 0)) : (value.Value, value.Value, 0);
            if (frame.MainTarget is { } target && local is not null)
            {
                var next = target.Target?.RawInstanceId.Value;
                if (mainKnown && next != main) mainChanges++;
                main = next; mainKnown = true;
            }
            foreach (var hit in frame.Damage)
            {
                var targetId = hit.TargetEntity.RawInstanceId.Value;
                var current = damage.GetValueOrDefault(targetId);
                damage[targetId] = (current.Sum + hit.Amount.Value, Math.Max(current.Max, hit.Amount.Value));
                if (local is null || hit.SourceEntity != local.Key) continue;
                var entity = frame.Entities.FirstOrDefault(value => value.Key == hit.TargetEntity);
                var record = new Hit(entity, entity?.Health ?? known.GetValueOrDefault((frame.Generation.Value, targetId)));
                hits.Add(record); if (afterRestart) after.Add(record);
                if (lastHit is { } previous && previous != targetId) lastHitChanges++;
                lastHit = targetId;
            }
        }
        // 只对 HP 持续更新的实体对账；只在死亡时归零一次的锁血实体（其血量由同场首领体现）不参与。
        var reconcile = healthSeries.Where(pair => damage.ContainsKey(pair.Key) && pair.Value.First > pair.Value.Last && pair.Value.Changes > 1)
            .ToDictionary(pair => pair.Key, pair => new Reconciliation(pair.Value.First - pair.Value.Last, damage[pair.Key].Sum, damage[pair.Key].Max));

        // 当前目标：本机身份确认后的帧按顺序进入领域会话。
        Diagnostics = null;
        var switches = 0; var displayed = 0; var withTarget = 0; string? shown = null; var bases = new Dictionary<string, int>();
        var first = frames.FindIndex(item => item.Local is not null && !item.AfterRestart);
        if (first >= 0)
        {
            var session = new CombatSession("owner", "replay", Op.CombatProfile.Source, frames[first].Local!, new("sample"), frames[first].Frame.ObservedAt, healing: true);
            ulong lastGeneration = 0, sequence = 0;
            // 与 Worker 只发布主连接一致：只取已确认本机身份的连接发出的帧。
            foreach (var (frame, _, _) in frames.Skip(first).Where(item => item.Local is not null && !item.AfterRestart).OrderBy(item => item.Frame.MonotonicMs))
            {
                if (frame.Generation.Value < lastGeneration) continue;
                if (frame.Generation.Value != lastGeneration) { lastGeneration = frame.Generation.Value; }
                session.Apply(frame with { Sequence = new(++sequence) });
                if (sequence % 5 != 0) continue;
                var current = session.Capture().Workspace.CurrentTarget;
                if (current is not null) { withTarget++; bases[current.Basis] = bases.GetValueOrDefault(current.Basis) + 1; }
                if (current?.EntityId != shown && current is not null && shown is not null) displayed++;
                if (current is not null) shown = current.EntityId;
            }
            switches = session.TargetSwitches;
            Diagnostics = $"captures with target {withTarget}, displayed changes {displayed}, bases {string.Join(",", bases.Select(pair => pair.Key + "=" + pair.Value))}";
        }
        return new(decrypted, failed, hits, after, reconcile, switches, lastHitChanges, mainChanges);
    }

    [ThreadStatic] private static string? Diagnostics;
    private sealed class Connection
    {
        public TwCombatObserver? Observer;
        public GameTcpStream? Stream;
        public double Now;
        public bool Joined;
    }
}
