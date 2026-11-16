using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;
using Xunit.Abstractions;

namespace MabiTool.Host.Tests;

/// <summary>
/// 常驻抓包的有界状态压力测试：合成 TCP 流在压缩时钟下模拟 6 小时，包括每 10 分钟切图换连接、每 2 秒出现新怪物且旧怪物消失、
/// 未移除的效果累积、两段各 1 小时无战斗。Worker 侧解析器与宿主侧 CombatSession、战斗历史库在同一进程运行，
/// 每模拟 30 分钟记录托管堆、各集合与数据库大小，断言均停在上限内且后半程不再增长。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ResidentCaptureSoakTests(ITestOutputHelper output)
{
    private const string Game = "soak-game";
    private static readonly DateTimeOffset Origin = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SixHoursOfSyntheticTrafficKeepParserSessionAndStorageBounded()
    {
        var root = Path.Combine(GameObservationApiTests.Area.FindRoot(), "artifacts", "tmp", "resident-soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var timer = Stopwatch.StartNew();
        try
        {
            using var lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, PortableRoot: root));
            var clock = new SimulatedClock(Origin);
            await using var database = SqliteStore.Open(lease, CombatHistoryStore.Migrations, mock: true, clock: clock);
            var operations = new OperationStore(database);
            var retention = new CombatRetentionLimits(MaximumEncounters: 12, SoftBytes: 24L * 1024 * 1024);
            var history = new CombatHistoryStore(database, operations, "epoch", clock, retention);
            var frames = new List<CombatFrame>();
            var observer = new TwCombatObserver(Game, SyntheticCombat.Profile.Source, frames.Add);
            double now = 0; uint sequence = 0; GameTcpStream? stream = null;
            void Connect()
            {
                stream = new((code, body) => observer.Accept(code, body, stream!.FrameStreamOffset, stream.FrameWireLength,
                    stream.FrameReceivedLocalMs ?? now, Origin.AddMilliseconds(now), stream.FrameEncoding),
                    reason => observer.Reset(reason, now, Origin.AddMilliseconds(now)), Tw20261110CombatProtocol.StreamProfile);
                sequence = 1000;
            }
            void Send(byte[] bytes) { stream!.Feed(sequence, bytes, now, now); sequence = unchecked(sequence + (uint)bytes.Length); }
            Connect(); Send(Join(SyntheticCombat.Identity()));
            observer.Tick(now + 300, Origin.AddMilliseconds(now + 300));
            var local = observer.LocalEntity ?? throw new InvalidOperationException("合成身份未建立");
            var request = new CombatObservationRequest(Game);
            var admitted = await operations.AdmitCombatAsync("soak", request, Guid.NewGuid().ToString("N"));
            var session = new CombatSession("soak", admitted.Snapshot.Id, SyntheticCombat.Profile.Source, local, request, Origin);
            var initial = session.Capture(); Assert.True(await history.SaveAsync(initial)); session.Acknowledge(initial);
            frames.Clear();

            var dbPath = lease.Paths.Database;
            long Bytes() => new FileInfo(dbPath).Length + (File.Exists(dbPath + "-wal") ? new FileInfo(dbPath + "-wal").Length : 0);
            var samples = new List<(double Hours, long Heap, long Database, int ParserEntities, int ParserEffects, int SessionEntities, long Rows, long Outbox)>();
            void Sample(int sessionEntities)
            {
                var heap = GC.GetTotalMemory(true);
                var rows = Convert.ToInt64(database.ReadScalar("SELECT count(*) FROM combat_entities"));
                var outbox = Convert.ToInt64(database.ReadScalar("SELECT count(*) FROM core_outbox"));
                samples.Add((now / 3_600_000d, heap, Bytes(), observer.Usage.Entities, observer.Usage.EffectInstances, sessionEntities, rows, outbox));
            }
            var sink = new NullSink();
            ulong monster = 10_000, instance = 1;
            var random = new Random(7);
            const double Step = 1000, Hours = 6;
            var lastSessionEntities = 1;
            Sample(1);
            for (var tick = 1; tick <= Hours * 3600; tick++)
            {
                now = tick * Step; clock.Now = Origin.AddMilliseconds(now);
                var minute = tick / 60;
                var idle = minute is >= 120 and < 180 or >= 300 and < 360;
                if (tick % 600 == 0)
                {
                    // 切图：旧连接结束，新连接重新下发身份。
                    observer.Reset("combat.streamGap.connectionRemoved", now, Origin.AddMilliseconds(now));
                    Connect(); Send(Join(SyntheticCombat.Identity()));
                }
                else if (!idle)
                {
                    if (tick % 2 == 0) monster++;
                    var target = monster - (ulong)random.Next(0, 3);
                    var bytes = new List<byte[]>();
                    for (var hit = 0; hit < 3; hit++) bytes.Add(Damage(1111, target, (uint)random.Next(100, 5000)));
                    bytes.Add(Buff(owner: target, instance: instance++, caster: 1111));
                    if (tick % 5 == 0) bytes.Add(Buff(owner: 1111, instance: instance++, caster: 1111));
                    Send(Join([.. bytes]));
                }
                observer.Tick(now + 300, Origin.AddMilliseconds(now + 300));
                foreach (var frame in frames) session.Apply(frame);
                frames.Clear();
                if (tick % 10 == 0)
                {
                    session.Tick(Origin.AddMilliseconds(now + 300), now + 300);
                    var checkpoint = session.Capture();
                    Assert.True(await history.SaveAsync(checkpoint));
                    session.Acknowledge(checkpoint);
                    lastSessionEntities = checkpoint.Workspace.Entities.Count;
                    await database.DispatchPendingAsync(sink);
                }
                // 切图在每 10 分钟整点；采样取地图中段，反映同一地图内的实体与效果累积。
                if (tick % 1800 == 900) Sample(lastSessionEntities);
            }
            foreach (var value in samples)
                output.WriteLine($"{value.Hours:F1} h heap={value.Heap / 1024d / 1024:F1} MiB db={value.Database / 1024d / 1024:F1} MiB " +
                    $"parser={value.ParserEntities}/{value.ParserEffects} session={value.SessionEntities} rows={value.Rows} outbox={value.Outbox}");
            output.WriteLine($"elapsed={timer.Elapsed.TotalSeconds:F1} s monsters={monster - 10_000} effects={instance}");

            Assert.All(samples, value =>
            {
                Assert.InRange(value.ParserEntities, 0, TwCombatObserver.MaximumEntities);
                Assert.InRange(value.ParserEffects, 0, TwCombatObserver.MaximumEffects);
                Assert.InRange(value.SessionEntities, 0, new CombatLimits().MaximumEntities);
                Assert.InRange(value.Rows, 0, value.SessionEntities + 1);
                Assert.InRange(value.Outbox, 0, SqliteStore.MaximumOutboxEvents);
            });
            // 3 小时后进入稳定区间：托管堆与数据库不随时长继续增长。
            var settled = samples.Where(value => value.Hours >= 3).ToArray();
            var peak = samples.Max(value => value.Heap);
            Assert.True(settled[^1].Heap <= settled[0].Heap + 16L * 1024 * 1024, $"堆从 {settled[0].Heap} 增至 {settled[^1].Heap}");
            Assert.True(peak <= samples[0].Heap + 96L * 1024 * 1024, $"堆峰值 {peak}");
            Assert.True(settled.Max(value => value.Database) <= settled[0].Database + 16L * 1024 * 1024, $"数据库从 {settled[0].Database} 增至 {settled[^1].Database}");
            Assert.True(Convert.ToInt64(database.ReadScalar("SELECT count(*) FROM combat_encounters")) <= retention.MaximumEncounters + 1);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class SimulatedClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class NullSink : IStateEventSink
    { public Task PublishAsync(PersistedEvent state, CancellationToken cancellationToken) => Task.CompletedTask; }

    private static byte[] Join(params byte[][] frames) => frames.SelectMany(value => value).ToArray();
    private static byte[] Damage(ulong attacker, ulong target, uint amount)
    {
        var body = new byte[53]; BinaryPrimitives.WriteUInt64LittleEndian(body, attacker); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), target);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), amount);
        return Message(Tw20261110CombatProtocol.DamageFloater, body);
    }
    private static byte[] Buff(ulong owner, ulong instance, ulong caster)
    {
        var body = new byte[36]; BinaryPrimitives.WriteUInt64LittleEndian(body, owner); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), instance);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), 0x12345678); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), 0x40a00000);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), 1); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(28), caster);
        return Message(Tw20261110CombatProtocol.BuffAdd, body);
    }
    private static byte[] Message(int opcode, byte[] body, byte encoding = 0)
    {
        if (encoding == 1)
        {
            using var compressed = new MemoryStream(); using (var compressor = new BrotliStream(compressed, CompressionLevel.Optimal, true)) compressor.Write(body);
            body = compressed.ToArray();
        }
        var result = new byte[9 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(result, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), body.Length); result[8] = encoding; body.CopyTo(result, 9); return result;
    }
}
