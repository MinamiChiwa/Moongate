using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Observation.Fixtures;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;
using Xunit.Abstractions;

namespace MabiTool.Host.Tests;

/// <summary>战斗与库存共用当前协议流：库存协议号可达、完整库存解析不占用捕获线程、写出侧超龄帧按预算校正。</summary>
[SupportedOSPlatform("windows")]
public sealed class SharedObservationCoexistenceTests(ITestOutputHelper output)
{
    private static readonly ObservationCombatProfile Combat = new(Tw20261110CombatProtocol.Profile,
        new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);

    [Fact]
    public async Task SharedLoginFrameGivesCombatIdentityAndFullInventoryWithoutStallingCapture()
    {
        var combat = new ConcurrentQueue<CombatCaptureReport>(); var inventory = new ConcurrentQueue<InventoryCaptureReport>();
        var context = new ObservationCaptureContext([Combat], _ => { }, combat.Enqueue,
            CoexistenceInventoryFixture.InventoryProfile, CoexistenceInventoryFixture.Layout, inventory.Enqueue);
        context.Configure(new(false, new(new string('a', 32), Combat.Id, true, true, true), new(new string('b', 32), CoexistenceInventoryFixture.InventoryProfile.Id)));
        ulong generation = 0;
        using var stream = new SharedGameStream("fixture-current", context, () => Interlocked.Increment(ref generation), layout: CombatProtocolLayout.Tw20261110);
        using var fixture = JsonDocument.Parse(typeof(SharedObservationCoexistenceTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var personal = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var character = CoexistenceInventoryFixture.PersonalCharacter(personal);
        var login = CoexistenceInventoryFixture.LoginBody(character, 4000);
        var bytes = Frame(20590, login, true).Concat(Frame((int)CoexistenceInventoryFixture.Profile, CoexistenceInventoryFixture.ProfileBody(character)))
            .Concat(Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true)).ToArray();
        var at = DateTimeOffset.UtcNow; var mono = Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
        var watch = Stopwatch.StartNew(); var cpuStart = ThreadCpuMs();
        for (var offset = 0; offset < bytes.Length; offset += 1460)
            stream.Feed((uint)offset, bytes.AsMemory(offset, Math.Min(1460, bytes.Length - offset)), mono, mono, at);
        var captureMs = watch.Elapsed.TotalMilliseconds; var captureCpuMs = ThreadCpuMs() - cpuStart;
        Assert.NotNull(stream.LocalEntity);
        await stream.InventoryIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var full = Assert.Single(inventory, value => value.Frame.Kind.KnownValue == InventoryFrameKind.Full);
        Assert.Equal(8000, full.Frame.Items.Count);
        Assert.Equal(character.ToString(System.Globalization.CultureInfo.InvariantCulture), full.Frame.Identity!.CharacterId);
        Assert.Contains(combat, value => value.LocalEntity is not null);
        var lane = stream.InventoryStatistics;
        output.WriteLine($"loginBytes={login.Length} wireBytes={bytes.Length} captureThreadMs={captureMs:0.0} captureThreadCpuMs={captureCpuMs:0.0} inventoryLongestMs={lane.LongestMs:0.0} completed={lane.Completed}");
        // 捕获线程只做重组、解压和战斗分发（空闲约 136 ms），完整库存解码（约 400 ms）在解析通道执行；
        // 按捕获线程自身 CPU 时间判定，排除机器负载下的抢占等待与解析通道并发解码争用。
        Assert.True(captureCpuMs < 250, $"captureThreadCpuMs={captureCpuMs} captureThreadMs={captureMs}");
        Assert.Equal(0, lane.Rejected);
    }

    [Fact]
    public void QueueLagCorrectorRestampsStaleCombatFramesWithOneCaptureLagGapPerSegment()
    {
        var source = Combat.Source;
        var entity = new CombatEntityObservation(new("game", new(3), new(11), null), "self", "self", CombatEntityRole.LocalPlayer,
            null, false, true, ObservationQuality.Confirmed);
        CombatCaptureReport Report(ulong sequence, double mono, DateTimeOffset at) => new(1, new string('a', 32), entity,
            new(1, source, "game", new(3), new(sequence), at, mono, [entity],
                [new($"dmg-3-{sequence}", source, entity.Key, entity.Key, new(0), new(5), CombatDamageKind.Unclassified, at, mono - 100,
                    new(sequence), 0, false, true, ObservationQuality.Confirmed)], [], [], []));
        var corrector = new QueueLagCorrector();
        var at = DateTimeOffset.UtcNow; const double now = 100_000;
        var stale1 = corrector.Combat(Report(1, now - 4000, at), now);
        var stale2 = corrector.Combat(Report(2, now - 3500, at), now + 10);
        var fresh = corrector.Combat(Report(3, now - 200, at), now + 20);
        Assert.Equal(now - ObservationCaptureRun.MaximumQueueLagMs, stale1.Frame.MonotonicMs);
        var lag = Assert.Single(stale1.Frame.Gaps);
        Assert.Equal(("combat.captureLag", now - 4000, now - ObservationCaptureRun.MaximumQueueLagMs, 0UL), (lag.Reason, lag.FromMs, lag.ToMs!.Value, lag.DroppedEvents.Value));
        Assert.Empty(stale2.Frame.Gaps);
        Assert.Equal(now - 200, fresh.Frame.MonotonicMs);
        Assert.True(stale1.Frame.MonotonicMs <= stale2.Frame.MonotonicMs && stale2.Frame.MonotonicMs <= fresh.Frame.MonotonicMs);
        Assert.Equal(stale1.Frame.MonotonicMs, Assert.Single(stale1.Frame.Damage).MonotonicMs);
        Assert.Equal(stale2.Frame.MonotonicMs, Assert.Single(stale2.Frame.Damage).MonotonicMs);
        var validate = typeof(MabiTool.Host.Game.GameObservationService).Assembly.GetType("MabiTool.Host.Game.CaptureCombatValidation")!
            .GetMethod("Validate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var report in new[] { stale1, stale2, fresh }) validate.Invoke(null, [report, source, "game"]);
        Assert.Equal(2, corrector.CorrectedCombatFrames);
        // 超龄段结束后再次超龄重新记缺口。
        var again = corrector.Combat(Report(4, now + 30 - 3000, at), now + 2000);
        Assert.Equal("combat.captureLag", Assert.Single(again.Frame.Gaps).Reason);
    }

    [Fact]
    public void QueueLagCorrectorRestampsStaleInventoryFramesMonotonically()
    {
        var corrector = new QueueLagCorrector();
        InventoryCaptureReport Report(ulong sequence, double mono) => new(1, new string('b', 32), new(1, CoexistenceInventoryFixture.Source, "game",
            new(1), new(sequence), DateTimeOffset.UtcNow, mono, InventoryFrameKind.Invalidated, null, [], [], [], Reason: "inventory.awaitingIdentity"));
        var first = corrector.Inventory(Report(1, 1000), 5000);
        var second = corrector.Inventory(Report(2, 1200), 4900);
        var fresh = corrector.Inventory(Report(3, 4800), 5000);
        Assert.Equal(4000, first.Frame.MonotonicMs);
        Assert.Equal(4000, second.Frame.MonotonicMs);
        Assert.Equal(4800, fresh.Frame.MonotonicMs);
        Assert.Equal(2, corrector.CorrectedInventoryFrames);
    }

    private static double ThreadCpuMs()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)) throw new System.ComponentModel.Win32Exception();
        return (kernel + user) / 10_000d;
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

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
