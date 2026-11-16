using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers.Binary;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class CombatEvidenceInspectorTests
{
    [Fact]
    public async Task RegisteredCurrentOpcodesSelectCurrentValuesWithoutEnablingCapabilities()
    {
        using var area = new Area(); area.WriteProtocols();
        area.WriteFrames(CombatProtocolTests.Self(), CombatProtocolTests.Player(), CombatProtocolTests.Buff(owner: CombatProtocolTests.LocalEntityId));
        var result = await area.Run(true, "--export-sanitized"); Assert.True(result.Code == 0, result.Error);
        using var analysis = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(area.Root, "analysis.current-protocols.sanitized.json")));
        Assert.Equal("Tw20261110", analysis.RootElement.GetProperty("parsingLayout").GetString());
        Assert.False(analysis.RootElement.GetProperty("productCapabilitiesEnabled").GetBoolean());
        using var projection = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(area.Root, "projection.sanitized.json")));
        Assert.Equal("live-current-field-projection", projection.RootElement.GetProperty("sourceEvidenceKind").GetString());
        var effect = Assert.Single(projection.RootElement.GetProperty("cases").EnumerateArray(), item => item.GetProperty("kind").GetString() == "effect");
        Assert.Equal("finite", effect.GetProperty("durationKind").GetString());
        Assert.Equal(5000d, effect.GetProperty("durationMs").GetDouble());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task SplitAndRetransmittedCaptureProducesOneEffectLifecycle(bool retransmit, bool protocolTable, bool keepalive)
    {
        using var area = new Area();
        if (protocolTable) area.WriteProtocols();
        var rows = new List<object>(); uint sequence = 0;
        foreach (var bytes in new[] { CombatProtocolTests.Self(), CombatProtocolTests.Player(), CombatProtocolTests.Buff(),
            CombatProtocolTests.Buff(Tw20261110CombatProtocol.BuffRefresh, stacks: 3), CombatProtocolTests.Buff(Tw20261110CombatProtocol.BuffExpire) })
        {
            for (var offset = 0; offset < bytes.Length; offset += 7)
            {
                Add(bytes.AsSpan(offset, Math.Min(7, bytes.Length - offset)).ToArray(), sequence + (uint)offset);
                if (retransmit) Add(bytes.AsSpan(offset, Math.Min(7, bytes.Length - offset)).ToArray(), sequence + (uint)offset);
                if (keepalive) Add([0xff], sequence + (uint)offset + (uint)Math.Min(7, bytes.Length - offset) - 1, flags: 16);
            }
            sequence += (uint)bytes.Length;
        }
        void Add(byte[] bytes, uint at, int flags = 0) => rows.Add(new { index = rows.Count + 1, monotonicMs = (double)rows.Count,
            connectionId = new string('a', 32), tcpSequence = at, tcpFlags = flags, payloadBase64 = Convert.ToBase64String(bytes) });
        area.Write(rows);
        var result = await area.Run(protocolTable); Assert.True(result.Code == 0, result.Error);
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(area.Root, protocolTable ? "analysis.current-protocols.json" : "analysis.json")));
        Assert.False(report.RootElement.GetProperty("currentFieldsVerified").GetBoolean());
        Assert.False(report.RootElement.GetProperty("productCapabilitiesEnabled").GetBoolean());
        var layout = report.RootElement.GetProperty("combatObservation");
        Assert.True(layout.GetProperty("localIdentityResolved").GetBoolean());
        Assert.Equal(3, layout.GetProperty("effectEvents").GetInt64());
        Assert.Equal(0, layout.GetProperty("damageEvents").GetInt64());
        Assert.Empty(report.RootElement.GetProperty("gaps").EnumerateObject());
    }

    [Fact]
    public async Task CorruptCaptureIsRejectedBeforeProducingEvidence()
    {
        using var area = new Area(); area.Write([]);
        await File.AppendAllTextAsync(Path.Combine(area.Root, "packets.jsonl"), "{}\n");
        var result = await area.Run(); Assert.NotEqual(0, result.Code);
        Assert.False(File.Exists(Path.Combine(area.Root, "analysis.json")));
    }

    [Fact]
    public async Task ExplicitSanitizedExportUsesStableEntityAndEffectAliasesAcrossLifecycle()
    {
        using var area = new Area(); area.WriteProtocols();
        const uint local = 0xDDCCBBAA, monster = 0xEEEFFFAA;
        area.WriteFrames(CombatProtocolTests.Self(), CombatProtocolTests.Player(local),
            CombatProtocolTests.Message(Tw20261110CombatProtocol.AppearingAutomaton, TwAutomatonAppearanceTests.Prefix(entity: monster, sheet: 0x44332211), 1),
            CombatProtocolTests.Skill(caster: local, target: monster), CombatProtocolTests.Damage(attacker: local, target: monster),
            CombatProtocolTests.Buff(owner: local, instance: ulong.MaxValue, caster: local),
            CombatProtocolTests.Buff(Tw20261110CombatProtocol.BuffRefresh, owner: local, instance: ulong.MaxValue, stacks: 3, caster: local),
            CombatProtocolTests.Buff(Tw20261110CombatProtocol.BuffExpire, owner: local, instance: ulong.MaxValue),
            CombatProtocolTests.Buff(owner: monster, instance: ulong.MaxValue, caster: local),
            CombatProtocolTests.Buff(Tw20261110CombatProtocol.BuffRefresh, owner: monster, instance: ulong.MaxValue, durationBits: 0x7fc00000, caster: local),
            CombatProtocolTests.Buff(Tw20261110CombatProtocol.BuffExpire, owner: monster, instance: ulong.MaxValue));
        var result = await area.Run(protocolTable: true, thirdArgument: "--export-sanitized");
        Assert.True(result.Code == 0, result.Error);
        var text = await File.ReadAllTextAsync(Path.Combine(area.Root, "projection.sanitized.json"));
        using var projection = JsonDocument.Parse(text);
        Assert.Equal("live-current-field-projection", projection.RootElement.GetProperty("sourceEvidenceKind").GetString());
        Assert.False(projection.RootElement.GetProperty("currentFieldsVerified").GetBoolean());
        var cases = projection.RootElement.GetProperty("cases").EnumerateArray().ToArray(); Assert.InRange(cases.Length, 1, 24);
        var identity = Assert.Single(cases, x => x.GetProperty("kind").GetString() == "local-identity");
        Assert.Equal("entity-1", identity.GetProperty("entity").GetString());
        Assert.Equal("session-1", identity.GetProperty("session").GetString());
        var damage = Assert.Single(cases, x => x.GetProperty("kind").GetString() == "damage");
        Assert.Equal("local", damage.GetProperty("attribution").GetString());
        Assert.Equal("entity-1", damage.GetProperty("sourceEntity").GetString());
        var effects = cases.Where(x => x.GetProperty("kind").GetString() == "effect").ToArray();
        var self = effects.Where(x => x.GetProperty("scope").GetString() == "self").ToArray();
        var targets = effects.Where(x => x.GetProperty("scope").GetString() == "monster").ToArray();
        Assert.Equal(3, self.Length); Assert.Equal(3, targets.Length);
        Assert.All(self, x => Assert.Equal("entity-1", x.GetProperty("entity").GetString()));
        Assert.All(self, x => Assert.False(x.TryGetProperty("monsterTypeCode", out _)));
        Assert.All(targets, x => Assert.Equal("11223344", x.GetProperty("monsterTypeCode").GetString()));
        Assert.Single(self.Select(x => x.GetProperty("effectInstance").GetString()).Distinct());
        Assert.Single(targets.Select(x => x.GetProperty("effectInstance").GetString()).Distinct());
        Assert.NotEqual(self[0].GetProperty("effectInstance").GetString(), targets[0].GetProperty("effectInstance").GetString());
        Assert.Equal("unknown", targets.Single(x => x.GetProperty("change").GetString() == "refresh").GetProperty("durationKind").GetString());
        foreach (var forbidden in new[] { "payload", "account", "rawInstanceId", "instanceId", "connectionId", "observedAt", "nickname", "rawName",
            new string('a', 32), local.ToString(), monster.ToString(), ulong.MaxValue.ToString(), "2026-11-10", "192.0.2" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        Assert.All(cases, x => Assert.True(x.GetProperty("monotonicMs").GetDouble() >= 0));
        var again = await area.Run(protocolTable: true, thirdArgument: "--export-sanitized");
        Assert.NotEqual(0, again.Code); Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(area.Root, "projection.sanitized.json")));
    }

    [Fact]
    public async Task UnknownEntitiesAreNotExportedAsMonsterEffects()
    {
        using var area = new Area(); area.WriteProtocols();
        area.WriteFrames(CombatProtocolTests.Self(), CombatProtocolTests.Player(),
            CombatProtocolTests.Buff(owner: 3333), CombatProtocolTests.Buff(Tw20261110CombatProtocol.BuffExpire, owner: 3333));
        var result = await area.Run(protocolTable: true, thirdArgument: "--export-sanitized"); Assert.True(result.Code == 0, result.Error);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(area.Root, "projection.sanitized.json")));
        Assert.DoesNotContain(document.RootElement.GetProperty("cases").EnumerateArray(), x => x.GetProperty("kind").GetString() == "effect");
    }

    [Fact]
    public async Task FailureReportRetainsCurrentOpcodeForInvalidBodyAndExpansion()
    {
        using var area = new Area(); area.WriteProtocols();
        var corruptExpansion = new byte[10]; BinaryPrimitives.WriteInt32LittleEndian(corruptExpansion, Tw20261110CombatProtocol.AppearingAutomaton);
        BinaryPrimitives.WriteInt32LittleEndian(corruptExpansion.AsSpan(4), 1); corruptExpansion[8] = 1; corruptExpansion[9] = 0xff;
        area.WriteFrames(CombatProtocolTests.Self(), CombatProtocolTests.Player(),
            CombatProtocolTests.Message(Tw20261110CombatProtocol.DamageFloater, new byte[52]), corruptExpansion);
        var result = await area.Run(protocolTable: true); Assert.True(result.Code == 0, result.Error);
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(area.Root, "analysis.current-protocols.json")));
        var failures = report.RootElement.GetProperty("protocolFailures").EnumerateArray().ToArray();
        var invalid = Assert.Single(failures, x => x.GetProperty("reason").GetString() == "combat.protocolInvalid");
        Assert.Equal(Tw20261110CombatProtocol.DamageFloater, invalid.GetProperty("code").GetInt32());
        var expansion = Assert.Single(failures, x => x.GetProperty("reason").GetString() == "frame.expandFailed");
        Assert.Equal(Tw20261110CombatProtocol.AppearingAutomaton, expansion.GetProperty("code").GetInt32());
        Assert.Equal(1, expansion.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task InvalidThirdArgumentIsRejectedBeforeWritingAnyProjection()
    {
        using var area = new Area(); area.Write([]); area.WriteProtocols();
        var result = await area.Run(protocolTable: true, thirdArgument: "--export-raw");
        Assert.NotEqual(0, result.Code);
        Assert.False(File.Exists(Path.Combine(area.Root, "projection.sanitized.json")));
        Assert.False(File.Exists(Path.Combine(area.Root, "analysis.current-protocols.sanitized.json")));
    }

    private sealed class Area : IDisposable
    {
        private readonly string repository;
        public string Root { get; }
        public Area()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            repository = directory!.FullName;
            Root = Path.Combine(repository, "artifacts", "tmp", "evidence-inspector-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public void Write(IReadOnlyList<object> rows)
        {
            var raw = Path.Combine(Root, "packets.jsonl");
            File.WriteAllText(raw, string.Concat(rows.Select(row => JsonSerializer.Serialize(row) + "\n")));
            File.WriteAllText(Path.Combine(Root, "summary.json"), JsonSerializer.Serialize(new
            {
                rawSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(raw))),
                startedAt = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero)
            }));
        }
        /// <summary>写出 tw-20261110 战斗协议的名称与编号表（客户端 GCProtocol 枚举名）。</summary>
        public void WriteProtocols() => File.WriteAllText(Path.Combine(Root, "protocols.json"), JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["CHANNEL_MyCharacterPersonalData_NTF"] = Tw20261110CombatProtocol.PersonalData,
            ["CHANNEL_AppearingCharacter_NTF"] = Tw20261110CombatProtocol.AppearingCharacter,
            ["CHANNEL_AppearingAutomaton_NTF"] = Tw20261110CombatProtocol.AppearingAutomaton,
            ["CHANNEL_HitPresentation_NTF"] = Tw20261110CombatProtocol.HitPresentation,
            ["CHANNEL_ShowDamageFloater_NTF"] = Tw20261110CombatProtocol.DamageFloater,
            ["REPLICATION_ActorBuff_Add_REPL"] = Tw20261110CombatProtocol.BuffAdd,
            ["REPLICATION_ActorBuff_Expire_REPL"] = Tw20261110CombatProtocol.BuffExpire,
            ["REPLICATION_ActorBuff_Refresh_REPL"] = Tw20261110CombatProtocol.BuffRefresh,
            ["REPLICATION_ActorStatus_SetBreakPoint_REPL"] = Tw20261110CombatProtocol.SetBreakPoint,
            ["REPLICATION_ActorParameter_UpdateBreakPointMax_REPL"] = Tw20261110CombatProtocol.UpdateBreakPointMax
        }));
        public void WriteFrames(params byte[][] frames)
        {
            List<object> rows = []; uint sequence = 0;
            foreach (var bytes in frames)
            {
                rows.Add(new { index = rows.Count + 1, monotonicMs = rows.Count * 3d, connectionId = new string('a', 32),
                    tcpSequence = sequence, tcpFlags = 0, payloadBase64 = Convert.ToBase64String(bytes) });
                sequence += (uint)bytes.Length;
            }
            Write(rows);
        }
        public async Task<(int Code, string Error)> Run(bool protocolTable = false, string? thirdArgument = null)
        {
            var flavor = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..",
                "MabiTool.CombatEvidenceInspector", flavor, "MabiTool.CombatEvidenceInspector.dll")));
            start.ArgumentList.Add(Root);
            if (protocolTable) start.ArgumentList.Add(Path.Combine(Root, "protocols.json"));
            if (thirdArgument is not null) start.ArgumentList.Add(thirdArgument);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            await output; return (process.ExitCode, await error);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
