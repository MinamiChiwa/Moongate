using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>Worker 帧编解码：重复键检查与逐对象集合检查的结果一致，已编码正文的写出与解析后写出的帧一致。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WorkerProtocolTests
{
    private const string Request = "0123456789abcdef0123456789abcdef", Run = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task DuplicateKeysAreRejectedExactlyWhereAPerObjectSetRejectsThem()
    {
        var root = GameObservationApiTests.Area.FindRoot();
        var corpus = Directory.GetFiles(Path.Combine(root, "contracts", "fixtures"), "*.json")
            .Select(path => File.ReadAllText(path)).Where(text => text.TrimStart().StartsWith('{')).ToList();
        Assert.NotEmpty(corpus);
        var wide = string.Join(",", Enumerable.Range(0, 40).Select(index => $"\"k{index}\":{index}"));
        corpus.AddRange([
            """{"a":1,"a":2}""", """{"a":{"b":1,"b":2}}""", """{"a":[{"x":1},{"x":2}],"b":{"x":3}}""", """{"a":{"k":1},"k":2}""",
            """{"a":1,"\u0061":2}""", """{"é":1,"\u00e9":2}""", """{"中":1,"\u4e2d":2}""", """{"\u4e2d":1,"文":2}""", """{"a\"b":1,"a\u0022b":2}""",
            "{" + wide + "}", "{" + wide + ",\"k3\":0}", "{\"outer\":{" + wide + ",\"k39\":1},\"k39\":2}", "{\"list\":[{" + wide + "},{" + wide + ",\"k0\":1}]}",
            """{"a":[[{"b":1},{"b":1}],{"c":{"d":{"e":1,"e":2}}}]}""", """{"x":{},"y":{},"x2":[]}"""]);
        foreach (var payload in corpus)
        {
            var expected = PerObjectSetRejects(payload);
            var read = await Read(Frame(Encoding.UTF8.GetBytes(payload)));
            Assert.True(expected == (read == "worker.frameInvalid"), payload);
            if (!expected) Assert.Null(read);
            using var document = JsonDocument.Parse(payload);
            var written = new MemoryStream();
            var write = await Record(() => WorkerProtocol.WriteAsync(written, Envelope(document.RootElement)));
            Assert.Equal(expected ? "worker.frameInvalid" : null, write);
            Assert.Equal(expected, written.Length == 0);
            var encoded = new MemoryStream();
            Assert.Equal(expected ? "worker.frameInvalid" : null,
                await Record(() => WorkerProtocol.WriteAsync(encoded, Envelope(default), Encoding.UTF8.GetBytes(payload))));
        }
        // 属性名不是合法 UTF-8：读取时按无效帧拒绝。
        byte[] invalidName = [.. "{\"a\":1,\""u8, 0xff, .. "\":2}"u8];
        Assert.Equal("worker.frameInvalid", await Read(Frame(invalidName)));
    }

    [Fact]
    public async Task EncodedPayloadIsWrittenAsTheSameFrameAsItsParsedElement()
    {
        // 契约序列化选项编出的正文：含中文、需转义的字符、数组与嵌套对象。
        object[] payloads =
        [
            new { report = new { name = "隊友甲", detail = "a\"b\\c<d>&", values = new[] { 1, 2, 3 }, nested = new { empty = new { }, list = Array.Empty<int>() } } },
            new { captureDiagnostics = new { schemaVersion = 1, devices = new[] { new { kind = "loopback", primary = true } } } },
            new { }
        ];
        foreach (var payload in payloads)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options);
            using var document = JsonDocument.Parse(bytes);
            var parsed = new MemoryStream(); var encoded = new MemoryStream();
            await WorkerProtocol.WriteAsync(parsed, Envelope(document.RootElement));
            await WorkerProtocol.WriteAsync(encoded, Envelope(default), bytes);
            Assert.Equal(parsed.ToArray(), encoded.ToArray());
            encoded.Position = 0;
            var frame = await WorkerProtocol.ReadAsync(encoded);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bytes), JsonNode.Parse(frame.Payload.GetRawText())));
            Assert.Equal((Request, Run, "event"), (frame.RequestId, frame.RunId, frame.Kind));
        }
        // 不是对象、JSON 无效或尾部多余内容：拒绝且不写出。
        foreach (var invalid in new[] { "[1,2]", "{\"a\":", "{\"a\":1} {}", "1" })
        {
            var target = new MemoryStream();
            Assert.Equal("worker.frameInvalid", await Record(() => WorkerProtocol.WriteAsync(target, Envelope(default), Encoding.UTF8.GetBytes(invalid))));
            Assert.Equal(0, target.Length);
        }
    }

    [Fact]
    public async Task EachCombatSequenceLeavesTheWorkerOnceThroughExactlyOnePath()
    {
        // 宿主按“代次 + 序号”比较同一序号重发的原始编码（冲突指纹）。Worker 写出路径：入队编码原样拼帧；排队超龄的批次按报告对象校正后编码；
        // 容量缺口按报告对象编码。100 ms 内合并的批次只带最后一份的序号，进入容量缺口的报告只以最后一份的序号写出一次。
        var peer = new RecordingPeer();
        var profile = SyntheticCombat.Profile;
        var run = new MabiTool.Observation.Worker.ObservationCaptureRun(peer, new CombatOnlySource(profile),
            new MabiTool.Observation.Worker.ObservationCaptureRequest("fixture-game", "TW", 4242, DateTimeOffset.UnixEpoch, null,
                MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol.CaptureProfile),
            new WorkerFrame(1, "1", Request, "request", Run, "7", "1", JsonSerializer.SerializeToElement(new { })));
        run.Initialize();
        const string interest = "cccccccccccccccccccccccccccccccc";
        run.Context.Configure(new(true, new(interest, profile.Id, true, false, false)));
        var now = System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency);
        CombatCaptureReport Report(ulong sequence, double mono)
        {
            var key = new EntityKey("fixture-game", new(1), new(1111), null);
            var local = new CombatEntityObservation(key, "玩家", "玩家", CombatEntityRole.LocalPlayer, null, false, true, ObservationQuality.Confirmed);
            var at = DateTimeOffset.UtcNow.AddMilliseconds(mono - now);
            var damage = new CombatDamageEvent("hit-" + sequence, profile.Source, key, key with { RawInstanceId = new(2222) },
                new(77), new(100), CombatDamageKind.Unclassified, at, mono, new(sequence), 0, false, true, ObservationQuality.Confirmed);
            return new(1, interest, local, new(1, profile.Source, "fixture-game", new(1), new(sequence), at, mono, [local], [damage], [], [], [], true));
        }
        async Task WriteAll() { for (var round = 0; round < 32; round++) await run.WriteOnceAsync(CancellationToken.None); }
        // 1–3：100 ms 内合并为一批且已排队超龄（按对象校正）；4、5：各自一批；6–66 填满 64 批的队列；67–75 进入容量缺口；
        // 缺口写出后 76 照常入队。
        for (ulong sequence = 1; sequence <= 3; sequence++) run.Context.Publish(Report(sequence, now - 1500 + sequence * 20));
        run.Context.Publish(Report(4, now)); run.Context.Publish(Report(5, now + 150));
        for (ulong sequence = 6; sequence <= 75; sequence++) run.Context.Publish(Report(sequence, now + 300 + sequence * 150));
        await WriteAll();
        run.Context.Publish(Report(76, now + 20_000));
        await WriteAll();

        var sent = peer.Sent.Select(item => item.Sequence).ToArray();
        Assert.Equal(sent.Length, sent.Distinct().Count());
        Assert.Equal([3UL, .. Enumerable.Range(4, 63).Select(value => (ulong)value), 75UL, 76UL], sent);
        Assert.Equal("object", peer.Sent.Single(item => item.Sequence == 3).Path);
        Assert.Equal("object", peer.Sent.Single(item => item.Sequence == 75).Path);
        Assert.All(peer.Sent.Where(item => item.Sequence is not (3 or 75)), item => Assert.Equal("encoded", item.Path));
    }

    [Fact]
    public async Task CombatWriterSendsSparseReportsAtOnceAndBatchesBurstsWithinTheHostRateLimit()
    {
        // 写出按负载自适应：零星报告入队即写出，不再固定等待合批；突发时令牌用完后按速率上限写出，期间到达的报告并入队尾的批次。
        // 宿主按“窗口满 1 秒重置、窗口内至多 256 条”读取，突发期间不超出，也没有容量缺口。
        var peer = new TimedPeer();
        var profile = SyntheticCombat.Profile;
        const string interest = "cccccccccccccccccccccccccccccccc";
        var published = new List<(ulong Sequence, double At)>();
        var source = new BurstSource(profile, async (context, ct) =>
        {
            ulong sequence = 0;
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(60, ct);
                var report = Report(++sequence); lock (published) published.Add((sequence, Now())); context.Publish(report);
            }
            // 1.5 秒内 3,000 份报告（每毫秒两份）：远超每秒 80 批的写出上限。
            var start = Now();
            while (sequence < 5 + 3000)
            {
                var due = (int)Math.Min(3000, (Now() - start) * 2);
                while (sequence < 5 + (ulong)due) { var report = Report(++sequence); lock (published) published.Add((sequence, Now())); context.Publish(report); }
                await Task.Delay(1, ct);
            }
        });
        var run = new MabiTool.Observation.Worker.ObservationCaptureRun(peer, source,
            new MabiTool.Observation.Worker.ObservationCaptureRequest("fixture-game", "TW", 4242, DateTimeOffset.UnixEpoch, null,
                MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol.CaptureProfile),
            new WorkerFrame(1, "1", Request, "request", Run, "7", "1", JsonSerializer.SerializeToElement(new { })));
        run.Initialize();
        run.Context.Configure(new(true, new(interest, profile.Id, true, false, false)));
        using var stop = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await run.RunAsync(stop, lifetime.Token);

        var sent = peer.Sent.ToArray();
        // 零星报告各自一条消息，入队后很快写出（改动前固定等待 15 ms 合批，系统计时器下实际约 16–31 ms）。
        var sparse = published.Take(5).Select(item => sent.Single(message => message.Sequence == item.Sequence).At - item.At).Order().ToArray();
        Assert.True(sparse[2] < 12, $"零星报告入队到写出的中位延迟 {sparse[2]:0.0} ms");
        // 突发：每条写出的消息以最后一份的序号代表合并的批次，3,005 份报告全部写出、没有容量缺口；消息数远少于报告数。
        Assert.Equal(3005UL, sent[^1].Sequence);
        Assert.DoesNotContain(sent, message => message.Gap);
        Assert.InRange(sent.Length, 20, 400);
        var window = double.NegativeInfinity; var count = 0;
        foreach (var message in sent)
        {
            if (message.At - window >= 1000) { window = message.At; count = 0; }
            Assert.True(++count <= 256, "写出超过宿主每秒 256 条的受理上限");
        }
        // 最后一份报告在突发结束后不久写出：积压有界，延迟不随时间累积。
        Assert.True(sent[^1].At - published[^1].At < 500, $"最后一份报告 {sent[^1].At - published[^1].At:0} ms 后才写出");

        static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency);
        CombatCaptureReport Report(ulong sequence)
        {
            var mono = Now(); var at = DateTimeOffset.UtcNow;
            var key = new EntityKey("fixture-game", new(1), new(1111), null);
            var local = new CombatEntityObservation(key, "玩家", "玩家", CombatEntityRole.LocalPlayer, null, false, true, ObservationQuality.Confirmed);
            var damage = new CombatDamageEvent("hit-" + sequence, profile.Source, key, key with { RawInstanceId = new(2222) },
                new(77), new(100), CombatDamageKind.Unclassified, at, mono, new(sequence), 0, false, true, ObservationQuality.Confirmed);
            return new(1, interest, local, new(1, profile.Source, "fixture-game", new(1), new(sequence), at, mono, [local], [damage], [], [], [], true));
        }
    }

    private sealed class BurstSource(ObservationCombatProfile profile, Func<MabiTool.Observation.Worker.ObservationCaptureContext, CancellationToken, Task> capture)
        : MabiTool.Observation.Worker.IObservationCaptureSource
    {
        public IReadOnlyList<CaptureChannelOption> Channels() => [];
        public Task CaptureAsync(MabiTool.Observation.Worker.ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct) =>
            throw new NotSupportedException();
        public IReadOnlyList<ObservationCombatProfile> CombatProfiles => [profile];
        public Task CaptureSharedAsync(MabiTool.Observation.Worker.ObservationCaptureRequest request, MabiTool.Observation.Worker.ObservationCaptureContext context,
            CancellationToken ct) => capture(context, ct);
    }

    /// <summary>记录每条战斗消息写出的时刻、所代表的最后一份序号与是否为容量缺口。</summary>
    private sealed class TimedPeer : MabiTool.Observation.Worker.IObservationPeer
    {
        public readonly List<(double At, ulong Sequence, bool Gap)> Sent = [];
        public Task SendAsync(string kind, object payload, WorkerFrame? scope, CancellationToken ct)
        {
            Record(JsonSerializer.SerializeToUtf8Bytes(payload, WorkerProtocol.Json)); return Task.CompletedTask;
        }
        public Task SendEncodedAsync(string kind, ReadOnlyMemory<byte> payload, WorkerFrame? scope, CancellationToken ct)
        {
            Record(payload.ToArray()); return Task.CompletedTask;
        }
        private void Record(byte[] payload)
        {
            var at = System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency);
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("combat", out var combat)) return;
            var frame = combat.GetProperty("frame");
            var gap = frame.GetProperty("gaps").EnumerateArray().Any(item => item.GetProperty("reason").GetString() == "combat.transportCapacity");
            lock (Sent) Sent.Add((at, ulong.Parse(frame.GetProperty("sequence").GetString()!, System.Globalization.CultureInfo.InvariantCulture), gap));
        }
    }

    private sealed class CombatOnlySource(ObservationCombatProfile profile) : MabiTool.Observation.Worker.IObservationCaptureSource
    {
        public IReadOnlyList<CaptureChannelOption> Channels() => [];
        public Task CaptureAsync(MabiTool.Observation.Worker.ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct) =>
            throw new NotSupportedException();
        public IReadOnlyList<ObservationCombatProfile> CombatProfiles => [profile];
    }

    /// <summary>记录每条战斗事件的写出路径（原样拼帧或对象编码）与序号。</summary>
    private sealed class RecordingPeer : MabiTool.Observation.Worker.IObservationPeer
    {
        public readonly List<(string Path, ulong Generation, ulong Sequence)> Sent = [];
        public Task SendAsync(string kind, object payload, WorkerFrame? scope, CancellationToken ct)
        {
            Record("object", JsonSerializer.SerializeToUtf8Bytes(payload, WorkerProtocol.Json)); return Task.CompletedTask;
        }
        public Task SendEncodedAsync(string kind, ReadOnlyMemory<byte> payload, WorkerFrame? scope, CancellationToken ct)
        {
            Record("encoded", payload.ToArray()); return Task.CompletedTask;
        }
        private void Record(string path, byte[] payload)
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("combat", out var combat)) return;
            var frame = combat.GetProperty("frame");
            Sent.Add((path, ulong.Parse(frame.GetProperty("generation").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                ulong.Parse(frame.GetProperty("sequence").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>改动前的检查：逐个对象用字符串集合判重。</summary>
    private static bool PerObjectSetRejects(string json)
    {
        static bool Duplicate(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!names.Add(property.Name) || Duplicate(property.Value)) return true;
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var value in element.EnumerateArray()) if (Duplicate(value)) return true;
            return false;
        }
        using var document = JsonDocument.Parse(json);
        return Duplicate(document.RootElement);
    }

    private static WorkerFrame Envelope(JsonElement payload) => new(1, "7", Request, "event", Run, "1", "0", payload);

    private static MemoryStream Frame(byte[] payload)
    {
        var envelope = Encoding.UTF8.GetBytes($$"""{"protocolVersion":1,"messageId":"7","requestId":"{{Request}}","kind":"event","runId":"{{Run}}","bindingRevision":"1","fencingToken":"0","payload":""");
        byte[] body = [.. envelope, .. payload, (byte)'}'];
        var stream = new MemoryStream();
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        stream.Write(header); stream.Write(body); stream.Position = 0;
        return stream;
    }

    private static Task<string?> Read(MemoryStream frame) => Record(() => WorkerProtocol.ReadAsync(frame));

    private static async Task<string?> Record(Func<Task> action)
    {
        try { await action(); return null; }
        catch (WorkerException error) { return error.Code; }
    }
}
