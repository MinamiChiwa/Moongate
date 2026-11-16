using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;

/// <summary>模拟宿主的共享捕获：以 tw-20261110 v1 战斗配置（不含治疗与战斗状态）解析自行构造的合成帧。</summary>
internal sealed class CombatFixtureSource(string root, string mode) : IObservationCaptureSource
{
    private static readonly ObservationCombatProfile Profile = Tw20261110CombatProtocol.CombatProfileV1 with { Simulation = true };
    private int runs;
    public IReadOnlyList<ObservationCombatProfile> CombatProfiles => mode == "combat-unsupported" ? [] : [Profile];
    public IReadOnlyList<CaptureChannelOption> Channels() => [new(new string('1', 32), "共享合成捕获", ["127.0.0.1"])];
    public Task CaptureAsync(ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct) =>
        CaptureSharedAsync(request, new(CombatProfiles, publish, _ => { }), ct);
    public async Task CaptureSharedAsync(ObservationCaptureRequest request, ObservationCaptureContext context, CancellationToken ct)
    {
        var run = Interlocked.Increment(ref runs);
        File.WriteAllText(Path.Combine(root, "capture-start-" + run), request.GameSessionId);
        File.WriteAllText(Path.Combine(root, "handles"), "1");
        using var handle = new FileStream(Path.Combine(root, "capture-handle"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        long generations = 0;
        using var stream = new SharedGameStream(request.GameSessionId, context, () => checked((ulong)Interlocked.Increment(ref generations)),
            layout: CombatProtocolLayout.Tw20261110);
        using var record = context.OnChanging((_, value) =>
        {
            File.WriteAllText(Path.Combine(root, "interests"), JsonSerializer.Serialize(value, ContractJson.Options));
            if (mode == "combat-interest-hold" && value.Combat is not null)
            {
                File.WriteAllText(Path.Combine(root, "interest-requested"), "pending");
                var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
                while (!File.Exists(Path.Combine(root, "release-interest")) && Stopwatch.GetTimestamp() < deadline)
                    Thread.Sleep(10);
                if (!File.Exists(Path.Combine(root, "release-interest"))) throw new TimeoutException("兴趣确认闸门超时");
            }
        });
        var captureGeneration = Guid.NewGuid().ToString("N");
        ulong musicSequence = 0, manualSequence = 1;
        uint tcpSequence = 0;
        string? interest = null;
        CombatCaptureReport? manual = null, retired = null;
        var step = 0;
        var faultSent = false;
        Process? child = null;
        try
        {
            if (mode == "combat-tree")
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--child"); child = Process.Start(start)!;
                File.WriteAllText(Path.Combine(root, "child-pid"), child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var now = Mono(); var at = DateTimeOffset.UtcNow;
                context.Execute(() =>
                {
                    var current = context.Current.Combat;
                    if (current?.Id != interest)
                    {
                        retired = manual; manual = null; manualSequence = 1; step = 0; interest = current?.Id;
                        // combat-capture-lag：身份包在捕获循环停顿 2.5 s 后才被读取，Npcap 时点早于处理时点。
                        if (current is not null && mode is not ("combat-hold" or "combat-interest-hold" or "combat-identity-later") && !ManualMode)
                            Feed(Join(Identity(), Buff()), now, at, mode == "combat-capture-lag" ? now - 2500 : now);
                    }
                    // combat-identity-later：战斗兴趣建立时没有身份帧，直到测试放置 identity-now（模拟用户之后切图）才发送。
                    if (mode == "combat-identity-later" && current is not null && File.Exists(Path.Combine(root, "identity-now")))
                    { File.Delete(Path.Combine(root, "identity-now")); Feed(Join(Identity(), Buff()), now, at, now); }
                    if (mode == "combat-capture-lag" && current is not null && step == 3)
                    {
                        // 切图换连接：旧连接移除后，新连接的身份包同样在停顿后读取。
                        stream.Restart("connection removed", now, at, GameStreamResetCause.ConnectionRemoved);
                        Feed(Join(Identity(), Buff()), now, at, now - 2500);
                    }
                    if (retired is not null && mode == "combat-late")
                    {
                        var old = retired.Frame;
                        context.Publish(retired with { Frame = old with { MonotonicMs = now, ObservedAt = at,
                            Damage = old.Damage.Select(value => value with { Amount = new(999999), MonotonicMs = now, ObservedAt = at }).ToArray() } });
                        File.WriteAllText(Path.Combine(root, "late-attempt"), "rejected-interest"); retired = null;
                    }
                    if (current is not null && mode is not ("combat-hold" or "combat-interest-hold" or "combat-identity-later"))
                    {
                        if (ManualMode)
                        {
                            if (manual is null) { manual = Manual(request.GameSessionId, current.Id, 10, 1, now, at); context.Publish(manual); }
                            else if (step == 3)
                            {
                                if (mode == "combat-conflict")
                                {
                                    context.Publish(manual);
                                    var conflict = manual with { Frame = manual.Frame with
                                        { Damage = manual.Frame.Damage.Select(value => value with { Amount = new(999999) }).ToArray() } };
                                    context.Publish(conflict); context.Publish(conflict);
                                }
                                if (mode == "combat-old-generation")
                                {
                                    context.Publish(Manual(request.GameSessionId, current.Id, 9, 1, now, at, 999999));
                                    context.Publish(Manual(request.GameSessionId, current.Id, 10, ++manualSequence, now, at));
                                }
                                if (mode == "combat-overflow")
                                {
                                    for (var index = 0; index < 300; index++)
                                    {
                                        var batch = Manual(request.GameSessionId, current.Id, 10, ++manualSequence, Mono(), DateTimeOffset.UtcNow);
                                        context.Publish(batch with { Frame = batch.Frame with { Damage = Enumerable.Range(0, 64)
                                            .Select(hit => batch.Frame.Damage[0] with { EventId = $"burst-{index}-{hit}", SubEventIndex = hit }).ToArray() } });
                                    }
                                    File.WriteAllText(Path.Combine(root, "burst-finished"), "19200");
                                }
                            }
                            else if (step > 5)
                            {
                                var heartbeat = Manual(request.GameSessionId, current.Id, 10, ++manualSequence, now, at);
                                context.Publish(heartbeat with { Frame = heartbeat.Frame with { Damage = [] } });
                            }
                        }
                        else
                        {
                            Feed(Join(Skill(), Damage(), Buff(), MonsterBuff()), now, at);
                            stream.Tick(now, at);
                            manual = Manual(request.GameSessionId, current.Id, stream.LocalEntity?.Key.ObservationGeneration.Value ?? 1, 1, now, at);
                        }
                        step++;
                        if (!faultSent && File.Exists(Path.Combine(root, "inject-fault")))
                        {
                            var bad = Manual(request.GameSessionId, current.Id, 10, ++manualSequence, now, at);
                            context.Publish(mode switch
                            {
                                "combat-null-element" => bad with { Frame = bad.Frame with { Damage = [null!] } },
                                "combat-bad-scope" => bad with { Frame = bad.Frame with { Damage = [bad.Frame.Damage[0] with
                                    { TargetEntity = bad.Frame.Damage[0].TargetEntity with { GameSessionId = "other-game" } }] } },
                                "combat-bad-time" => bad with { Frame = bad.Frame with { Damage = [bad.Frame.Damage[0] with { MonotonicMs = -1 }] } },
                                _ => bad
                            });
                            faultSent = true;
                        }
                    }
                    var identity = new ObservedGameIdentity(new(9007199254740993), "3", "共享玩家", new(11));
                    var music = new GameObservationValue(captureGeneration, identity, null, null, new(++musicSequence), "共享合成观察");
                    context.Publish(new GameCaptureReport(music, new(musicSequence), true, Mono(), DateTimeOffset.UtcNow,
                        null, new("observation-fixture", "1.0.0", "TW", "synthetic-v1")));
                });
                if (mode == "combat-exit" && step > 10) Environment.Exit(12);
                await Task.Delay(50, ct);
            }
        }
        finally
        {
            child?.Dispose(); File.WriteAllText(Path.Combine(root, "handles"), "0");
            File.WriteAllText(Path.Combine(root, "capture-stopped-" + run), "closed");
        }
        void Feed(byte[] bytes, double now, DateTimeOffset at, double? received = null)
        {
            for (var offset = 0; offset < bytes.Length; offset += 7)
                stream.Feed(unchecked(tcpSequence + (uint)offset), bytes.AsMemory(offset, Math.Min(7, bytes.Length - offset)), now, received ?? now, at);
            tcpSequence = unchecked(tcpSequence + (uint)bytes.Length);
        }
    }
    private bool ManualMode => mode is "combat-conflict" or "combat-old-generation" or "combat-overflow" or
        "combat-null-element" or "combat-bad-scope" or "combat-bad-time";
    private static double Mono() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
    private static CombatCaptureReport Manual(string session, string interest, ulong generation, ulong sequence,
        double now, DateTimeOffset at, ulong amount = 100)
    {
        var key = new EntityKey(session, new(generation), new(1111), null);
        var target = key with { RawInstanceId = new(2222) };
        var local = new CombatEntityObservation(key, "共享玩家", "共享玩家", CombatEntityRole.LocalPlayer, null, false, true, ObservationQuality.Confirmed);
        var monster = local with { Key = target, RawName = "合成怪物", DisplayName = "合成怪物", Role = CombatEntityRole.Monster };
        var damage = new CombatDamageEvent("fixture-" + sequence, Profile.Source, key, target, new(77), new(amount),
            CombatDamageKind.Unclassified, at, now, new(sequence), 0, false, true, ObservationQuality.Confirmed, key, true, false);
        return new(1, interest, local, new(1, Profile.Source, session, new(generation), new(sequence),
            at, now, [local, monster], [damage], [], [], [], true));
    }
    private static byte[] Join(params byte[][] frames) => frames.SelectMany(value => value).ToArray();
    /// <summary>
    /// 个人数据 20590 与角色出现 20054 取当前构建身份 fixture，角色出现的实体 ID 改为 1111；怪物出现 20051 为实体 2222、
    /// MonsterSheetId 0x44332211 的自动体前缀。
    /// </summary>
    private static byte[] Identity()
    {
        using var fixture = JsonDocument.Parse(typeof(CombatFixtureSource).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var personal = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var player = Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!);
        BinaryPrimitives.WriteUInt64LittleEndian(player, 1111);
        return Join(Frame(Tw20261110CombatProtocol.PersonalData, personal, true), Frame(Tw20261110CombatProtocol.AppearingCharacter, player, true),
            Frame(Tw20261110CombatProtocol.AppearingAutomaton, Monster(2222, 0x44332211), true));
    }
    /// <summary>
    /// AutomatonPublicReplicatedData 前缀（1,307 字节）：按 tw-20261110 schema 顺序写出、其余字段取默认值时的非零位置为
    /// 实体 Id（偏移 0）、一个 Nullable 标志（439）、AutomatonIdentity.Type=3（1254）及两处 MonsterSheetId（854、1258）。
    /// </summary>
    private static byte[] Monster(ulong entity, uint sheet)
    {
        var body = new byte[1307];
        BinaryPrimitives.WriteUInt64LittleEndian(body, entity); body[439] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(854), sheet);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1254), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1258), sheet);
        return body;
    }
    private static byte[] Skill()
    {
        var body = new byte[35]; BinaryPrimitives.WriteUInt64LittleEndian(body, 1111); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), 2222);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), 77); return Frame(Tw20261110CombatProtocol.HitPresentation, body);
    }
    private static byte[] Damage()
    {
        var body = new byte[53]; BinaryPrimitives.WriteUInt64LittleEndian(body, 1111); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), 2222);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), 100); return Frame(Tw20261110CombatProtocol.DamageFloater, body);
    }
    private static byte[] Buff(ulong owner = 1111)
    {
        var body = new byte[36]; BinaryPrimitives.WriteUInt64LittleEndian(body, owner); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), owner);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), 7); BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(20), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), 1); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(28), 1111);
        return Frame(Tw20261110CombatProtocol.BuffAdd, body);
    }
    private static byte[] MonsterBuff() => Buff(2222);
    private static byte[] Frame(int opcode, byte[] body, bool compressed = false)
    {
        if (compressed)
        {
            using var target = new MemoryStream();
            using (var encoder = new BrotliStream(target, CompressionLevel.Optimal, true)) encoder.Write(body);
            body = target.ToArray();
        }
        var result = new byte[body.Length + 9]; BinaryPrimitives.WriteInt32LittleEndian(result, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), body.Length); result[8] = compressed ? (byte)1 : (byte)0; body.CopyTo(result, 9); return result;
    }
}
