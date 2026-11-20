using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.Providers.TwObservation.Combat;

/// <summary>一项能力的核对结果；停用时 <see cref="Reason"/> 为固定原因码。</summary>
public sealed record TwCapabilityCheck(bool Enabled, string? Reason)
{
    public static TwCapabilityCheck Ok { get; } = new(true, null);
}

/// <summary>
/// 客户端构建的核对结果。已登记构建各项直接启用；未登记构建按运行时从客户端读出的协议枚举、收包签名与字段布局
/// 逐项核对：<see cref="Identity"/>（本机身份与地图频道）、<see cref="Music"/>（合奏与演奏）、<see cref="Combat"/>（战斗统计与效果，
/// 依赖身份）、<see cref="Health"/>（实体血量、死亡、离场与本机主目标，依赖战斗）。<see cref="KnownProtocols"/> 为分帧可识别的协议号集合，为空时不能抓包。
/// </summary>
public sealed record TwBuildVerdict(string Build, bool Registered, string? RegisteredName, TwCapabilityCheck Identity,
    TwCapabilityCheck Music, TwCapabilityCheck Combat, IReadOnlySet<int> KnownProtocols, TwCapabilityCheck? HealthCheck = null)
{
    /// <summary>实体血量能力的核对结果；构造时未给出时与战斗相同。</summary>
    public TwCapabilityCheck Health => HealthCheck ?? Combat;
    public bool CaptureUsable => KnownProtocols.Count > 0;
    public static TwBuildVerdict ForRegistered(TwRegisteredBuild build) => new(TwClientFingerprint.GameBuild, true, build.Name,
        TwCapabilityCheck.Ok, TwCapabilityCheck.Ok, TwCapabilityCheck.Ok, Tw20261110CombatProtocol.StreamProfile.KnownProtocols);
    /// <summary>读不出客户端事实时的结果：各项停用且不能抓包。</summary>
    public static TwBuildVerdict Failed(string build, string reason)
    {
        var failed = new TwCapabilityCheck(false, reason);
        return new(build, false, null, failed, failed, failed, FrozenSet<int>.Empty, failed);
    }
}

/// <summary>
/// 未登记构建的运行时核对。登记基线（<c>tw-20261110-build-facts.json</c>）记录各能力依赖的协议号、收包方法签名、
/// 复制方法签名与定长消息的字段；当前客户端的同名事实与基线完全一致时该能力启用，协议号不同记
/// <c>gameBuild.protocolChanged</c>，签名或字段不同记 <c>gameBuild.layoutChanged</c>。按字段前缀读取的消息
/// （角色、自动体出现与频道进入）只要求基线字段是当前字段的前缀。核对结果按 GameAssembly 与 metadata 哈希缓存。
/// </summary>
public static class TwBuildVerification
{
    /// <summary>核对规则版本；规则变化后旧缓存失效。</summary>
    public const int RulesVersion = 2;
    public const string ProtocolChanged = "gameBuild.protocolChanged", LayoutChanged = "gameBuild.layoutChanged",
        VerifyFailed = "gameBuild.verifyFailed", IdentityUnavailable = "gameBuild.identityUnavailable";
    private const string Handler = "MM.Client.Framework.Network.GC.IGCPacketHandler";
    private const string Replicated = "Shared.Protocols.Replications.IReplicatedGroups.";

    private sealed record Requirement(string[] Protocols, string[] Handlers, string[] ReplicatedMethods, string[] Types, string[] PrefixSchemas);

    private static readonly Requirement IdentityRequirement = new(
        ["CHANNEL_MyCharacterPersonalData_NTF", "CHANNEL_AppearingCharacter_NTF", "CHANNEL_DisappearingChannelObject_NTF", "CHANNEL_EnteringChannel_NTF"],
        ["CHANNEL_MyCharacterPersonalData_NTF", "CHANNEL_AppearingCharacter_NTF", "CHANNEL_DisappearingChannelObject_NTF", "CHANNEL_EnteringChannel_NTF"],
        [], [], ["MabiTool.Tw20261110CharacterSchema", "MabiTool.Tw20261110ChannelMusicSchema"]);
    private static readonly Requirement MusicRequirement = new(
        ["CHANNEL_NotifyConcertJoin_NTF", "REPLICATION_Concerts_Add_REPL", "REPLICATION_Concerts_Remove_REPL",
            "REPLICATION_InstrumentPlayer_PlayInstrument_REPL", "REPLICATION_InstrumentPlayer_Stop_REPL"],
        ["CHANNEL_NotifyConcertJoin_NTF"],
        ["IConcertsReplicated.Add", "IConcertsReplicated.Remove", "IInstrumentPlayerReplicated.PlayInstrument", "IInstrumentPlayerReplicated.Stop"],
        ["Shared.Protocols.ProtocolDatas.ReplicatedDatas.ConcertReplicatedData"],
        ["MabiTool.Tw20261110CharacterMusicSchema"]);
    private static readonly Requirement CombatRequirement = new(
        ["CHANNEL_MyCharacterPersonalData_NTF", "CHANNEL_AppearingCharacter_NTF", "CHANNEL_AppearingAutomaton_NTF", "CHANNEL_HitPresentation_NTF",
            "CHANNEL_ShowDamageFloater_NTF", "REPLICATION_ActorBuff_Add_REPL", "REPLICATION_ActorBuff_Expire_REPL", "REPLICATION_ActorBuff_Refresh_REPL",
            "REPLICATION_ActorStatus_SetBreakPoint_REPL", "REPLICATION_ActorParameter_UpdateBreakPointMax_REPL", "CHANNEL_NotifyHealCasted_NTF",
            "CHANNEL_NotifyHealReceived_NTF", "REPLICATION_ActorStatus_SetHealth_REPL", "REPLICATION_ActorStates_SetCombatState_REPL"],
        ["CHANNEL_AppearingAutomaton_NTF", "CHANNEL_HitPresentation_NTF", "CHANNEL_ShowDamageFloater_NTF", "CHANNEL_NotifyHealCasted_NTF",
            "CHANNEL_NotifyHealReceived_NTF"],
        ["IActorBuffReplicated.Add", "IActorBuffReplicated.Refresh", "IActorBuffReplicated.Expire", "IActorStatusReplicated.SetBreakPoint",
            "IActorStatusReplicated.SetHealth", "IActorStatesReplicated.SetCombatState", "IActorParameterReplicated.UpdateBreakPointMax"],
        ["Shared.GameLogic.Hits.DamageFloaterData", "Shared.GameLogic.Hits.HitPresentationData",
            "Shared.Protocols.ProtocolDatas.ReplicatedDatas.BuffReplicatedData", "Shared.GameLogic.ChannelObjects.Actors.Buffs.BuffInstanceId",
            "Shared.ChannelObjects.ChannelObjectId", "Shared.Protocols.ProtocolDatas.ReplicatedDatas.ReplicatedTime"],
        ["MabiTool.Tw20261110AutomatonSchema", "MabiTool.Tw20261110CharacterSchema"]);
    private static readonly Requirement HealthRequirement = new(
        ["REPLICATION_ActorStatus_SetHealth2_REPL", "REPLICATION_ActorStatus_SetHealth3_REPL", "REPLICATION_ActorStatus_SetHealth4_REPL",
            "REPLICATION_ActorStatus_SetHealth5_REPL", "REPLICATION_ActorStatus_SetHealth6_REPL", "REPLICATION_ActorParameter_UpdateHealthMax_REPL",
            "SYSTEM_SendBroadcastPacketAES_NTF", "REPLICATION_ActorStates_SetDead_REPL", "CHANNEL_DisappearingChannelObject_NTF",
            "REPLICATION_MainTarget_SetTarget_REPL"],
        ["SYSTEM_SendBroadcastPacketAES_NTF", "CHANNEL_DisappearingChannelObject_NTF"],
        ["IActorStatusReplicated.SetHealth2", "IActorStatusReplicated.SetHealth3", "IActorStatusReplicated.SetHealth4",
            "IActorStatusReplicated.SetHealth5", "IActorStatusReplicated.SetHealth6", "IActorParameterReplicated.UpdateHealthMax",
            "IActorStatesReplicated.SetDead", "IMainTargetReplicated.SetTarget"],
        [], ["MabiTool.Tw20261110AutomatonSchema"]);
    private static readonly Requirement[] Requirements = [IdentityRequirement, MusicRequirement, CombatRequirement, HealthRequirement];

    private static readonly Lazy<JsonDocument> Baseline = new(() => JsonDocument.Parse(
        typeof(TwBuildVerification).Assembly.GetManifestResourceStream("MabiTool.Tw20261110BuildFacts")!));
    private static readonly Lazy<IReadOnlyDictionary<string, JsonDocument>> PrefixSchemas = new(() =>
        Requirements.SelectMany(value => value.PrefixSchemas).Distinct(StringComparer.Ordinal).ToDictionary(name => name,
            name => JsonDocument.Parse(typeof(TwBuildVerification).Assembly.GetManifestResourceStream(name)!), StringComparer.Ordinal));

    /// <summary>读取客户端事实时需要的字段闭包根类型。</summary>
    public static IReadOnlyCollection<string> SchemaRoots { get; } = Requirements.SelectMany(value => value.Types)
        .Concat(["Shared.Protocols.Replications.GroupReplicatedDatas.CharacterPublicReplicatedData",
            "Shared.Protocols.Replications.GroupReplicatedDatas.AutomatonPublicReplicatedData",
            "Shared.Protocols.Replications.GroupReplicatedDatas.ChannelGlobalReplicatedData"])
        .Distinct(StringComparer.Ordinal).ToArray();
    /// <summary>读取客户端事实时需要的接口（收包处理器与各复制组）。</summary>
    public static IReadOnlyCollection<string> Interfaces { get; } = Requirements.SelectMany(value => value.ReplicatedMethods)
        .Select(value => Replicated + value[..value.IndexOf('.')]).Prepend(Handler).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>按登记基线逐项核对客户端事实；身份核对不过时战斗一并停用。</summary>
    public static TwBuildVerdict Verify(string build, ClientBuildFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var known = facts.Protocols.Values.Where(value => value > 0).ToFrozenSet();
        if (known.Count == 0 || known.Count != facts.Protocols.Values.Count(value => value > 0)) return TwBuildVerdict.Failed(build, VerifyFailed);
        var identity = Check(IdentityRequirement, facts);
        var music = identity.Enabled ? Check(MusicRequirement, facts) : new(false, IdentityUnavailable);
        var combat = identity.Enabled ? Check(CombatRequirement, facts) : new(false, IdentityUnavailable);
        var health = combat.Enabled ? Check(HealthRequirement, facts) : combat;
        return new(build, false, null, identity, music, combat, known, health);
    }

    private static TwCapabilityCheck Check(Requirement requirement, ClientBuildFacts facts)
    {
        var baseline = Baseline.Value.RootElement;
        var protocols = baseline.GetProperty("protocols");
        foreach (var name in requirement.Protocols)
            if (!facts.Protocols.TryGetValue(name, out var code) || code != protocols.GetProperty(name).GetInt32()) return new(false, ProtocolChanged);
        var handlers = baseline.GetProperty("handlers");
        foreach (var name in requirement.Handlers)
            if (!SameParameters(Method(facts, Handler, name), handlers.GetProperty(name))) return new(false, LayoutChanged);
        var replicated = baseline.GetProperty("replicated");
        foreach (var name in requirement.ReplicatedMethods)
        {
            var separator = name.IndexOf('.');
            if (!SameParameters(Method(facts, Replicated + name[..separator], name[(separator + 1)..]), replicated.GetProperty(name)))
                return new(false, LayoutChanged);
        }
        var types = baseline.GetProperty("types");
        var current = facts.Schema.GetProperty("types");
        foreach (var name in Closure(types, requirement.Types))
            if (!current.TryGetProperty(name, out var actual) || !SameType(types.GetProperty(name), actual, prefix: false)) return new(false, LayoutChanged);
        foreach (var resource in requirement.PrefixSchemas)
            foreach (var entry in PrefixSchemas.Value[resource].RootElement.EnumerateObject())
                if (!current.TryGetProperty(entry.Name, out var actual) || !SameType(entry.Value, actual, prefix: true)) return new(false, LayoutChanged);
        return TwCapabilityCheck.Ok;
    }

    private static IReadOnlyList<string>? Method(ClientBuildFacts facts, string type, string name) =>
        facts.Interfaces.TryGetValue(type, out var methods) && methods.Where(method => method.Name == name).Take(2).ToArray() is [var only]
            ? only.Parameters : null;
    private static bool SameParameters(IReadOnlyList<string>? actual, JsonElement expected) =>
        actual is not null && actual.SequenceEqual(expected.EnumerateArray().Select(value => value.GetString()!), StringComparer.Ordinal);

    /// <summary>基线类型及其父类型、字段类型中同样登记在基线里的类型。</summary>
    private static IEnumerable<string> Closure(JsonElement types, IEnumerable<string> roots)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out var name))
        {
            if (!types.TryGetProperty(name, out var type) || !result.Add(name)) continue;
            if (type.GetProperty("parent").GetString() is { } parent) pending.Push(parent);
            foreach (var field in type.GetProperty("fields").EnumerateArray()) pending.Push(field.GetProperty("type").GetString()!);
        }
        return result;
    }

    private static bool SameType(JsonElement expected, JsonElement actual, bool prefix)
    {
        if (expected.GetProperty("parent").GetString() != actual.GetProperty("parent").GetString()) return false;
        var want = expected.GetProperty("fields").EnumerateArray().ToArray();
        var have = actual.GetProperty("fields").EnumerateArray().ToArray();
        if (prefix ? want.Length > have.Length : want.Length != have.Length) return false;
        for (var index = 0; index < want.Length; index++)
            if (want[index].GetProperty("name").GetString() != have[index].GetProperty("name").GetString() ||
                want[index].GetProperty("type").GetString() != have[index].GetProperty("type").GetString()) return false;
        return true;
    }

    /// <summary>从客户端事实取出登记基线所含的条目，用于登记新的基线。</summary>
    public static JsonObject Snapshot(ClientBuildFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var protocols = new JsonObject(); var handlers = new JsonObject(); var replicated = new JsonObject(); var types = new JsonObject();
        foreach (var name in Requirements.SelectMany(value => value.Protocols).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            protocols[name] = facts.Protocols[name];
        foreach (var name in Requirements.SelectMany(value => value.Handlers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            handlers[name] = new JsonArray(Method(facts, Handler, name)!.Select(value => (JsonNode?)value).ToArray());
        foreach (var name in Requirements.SelectMany(value => value.ReplicatedMethods).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var separator = name.IndexOf('.');
            replicated[name] = new JsonArray(Method(facts, Replicated + name[..separator], name[(separator + 1)..])!.Select(value => (JsonNode?)value).ToArray());
        }
        var current = facts.Schema.GetProperty("types");
        foreach (var name in Closure(current, Requirements.SelectMany(value => value.Types)).Order(StringComparer.Ordinal))
            types[name] = JsonNode.Parse(current.GetProperty(name).GetRawText());
        return new JsonObject { ["protocols"] = protocols, ["handlers"] = handlers, ["replicated"] = replicated, ["types"] = types };
    }

    /// <summary>
    /// 核对客户端：已登记构建直接启用；未登记构建先查 <paramref name="cacheDirectory"/> 中同一哈希的结果，没有时读取客户端事实核对并写入缓存。
    /// 客户端文件读不全或被写入时返回空。
    /// </summary>
    public static async Task<TwBuildVerdict?> EvaluateAsync(string imagePath, string? cacheDirectory, CancellationToken ct) =>
        await TwClientFingerprint.HashAsync(imagePath, ct).ConfigureAwait(false) is { } hashes
            ? await EvaluateAsync(imagePath, hashes, cacheDirectory, ct).ConfigureAwait(false) : null;

    /// <summary>按已算出的三文件哈希核对；读取客户端事实时再核对哈希，文件已变化时返回空。</summary>
    public static async Task<TwBuildVerdict?> EvaluateAsync(string imagePath, TwClientHashes hashes, string? cacheDirectory, CancellationToken ct)
    {
        if (TwClientFingerprint.Registered(hashes.AssemblySha256, hashes.MetadataSha256, hashes.UnitySha256) is { } registered)
            return TwBuildVerdict.ForRegistered(registered);
        var build = TwClientFingerprint.AdaptiveBuild(hashes.AssemblySha256, hashes.MetadataSha256);
        if (cacheDirectory is not null && TwBuildVerdictCache.Read(cacheDirectory, hashes) is { } cached) return cached;
        TwBuildVerdict verdict;
        try
        {
            var facts = await Task.Run(() => ClientLayoutReader.ReadBuildFacts(Path.GetDirectoryName(Path.GetFullPath(imagePath))!,
                SchemaRoots, Interfaces, ct), ct).ConfigureAwait(false);
            // 读取期间文件被替换时以读到的哈希为准，不把结果记到另一构建名下。
            if (facts.AssemblyHash != hashes.AssemblySha256 || facts.MetadataHash != hashes.MetadataSha256) return null;
            verdict = Verify(build, facts);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or KeyNotFoundException)
        { verdict = TwBuildVerdict.Failed(build, VerifyFailed); }
        if (cacheDirectory is not null) TwBuildVerdictCache.Write(cacheDirectory, hashes, verdict);
        return verdict;
    }
}

/// <summary>
/// 未登记构建的核对结果缓存：每个构建一个 JSON 文件，按 GameAssembly 与 metadata 哈希前缀命名，内容带完整哈希与规则版本；
/// 不符、损坏或规则版本不同的文件视为没有缓存。读到即刷新文件时间，写入后按最近使用只保留 <see cref="MaximumEntries"/> 个，
/// 其余删除（链接与重解析点不跟随、不删除）。
/// </summary>
public static class TwBuildVerdictCache
{
    /// <summary>
    /// 保留的构建数：每个文件不足 1 KiB；台服客户端约每周至多更新一次，同一数据根里的宿主与 Worker 实际只用当前与上一个构建，
    /// 8 个覆盖约两个月的更新与回退，合计不超过 8 KiB。
    /// </summary>
    public const int MaximumEntries = 8;
    private sealed record Entry(int RulesVersion, string AssemblySha256, string MetadataSha256, string Build,
        Check Identity, Check Music, Check Combat, int[] KnownProtocols, Check? Health = null);
    private sealed record Check(bool Enabled, string? Reason);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static string FilePath(string directory, TwClientHashes hashes) =>
        Path.Combine(directory, "tw-" + hashes.AssemblySha256[..16] + "-" + hashes.MetadataSha256[..16] + ".json");

    public static TwBuildVerdict? Read(string directory, TwClientHashes hashes)
    {
        try
        {
            var path = FilePath(directory, hashes);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path), Options);
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (entry is null || entry.RulesVersion != TwBuildVerification.RulesVersion || entry.AssemblySha256 != hashes.AssemblySha256 ||
                entry.MetadataSha256 != hashes.MetadataSha256 || entry.Build != TwClientFingerprint.AdaptiveBuild(hashes.AssemblySha256, hashes.MetadataSha256) ||
                entry.Identity is null || entry.Music is null || entry.Combat is null || entry.KnownProtocols is null || entry.Health is null) return null;
            return new(entry.Build, false, null, new(entry.Identity.Enabled, entry.Identity.Reason), new(entry.Music.Enabled, entry.Music.Reason),
                new(entry.Combat.Enabled, entry.Combat.Reason), entry.KnownProtocols.Where(value => value > 0).ToFrozenSet(),
                new(entry.Health.Enabled, entry.Health.Reason));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static void Write(string directory, TwClientHashes hashes, TwBuildVerdict verdict)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = FilePath(directory, hashes); var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Entry(TwBuildVerification.RulesVersion, hashes.AssemblySha256,
                hashes.MetadataSha256, verdict.Build, new(verdict.Identity.Enabled, verdict.Identity.Reason), new(verdict.Music.Enabled, verdict.Music.Reason),
                new(verdict.Combat.Enabled, verdict.Combat.Reason), verdict.KnownProtocols.Order().ToArray(),
                new(verdict.Health.Enabled, verdict.Health.Reason)), Options));
            File.Move(temporary, path, overwrite: true);
            Prune(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>按最近使用（文件时间）保留最近 <see cref="MaximumEntries"/> 个 tw-*.json，删除其余；跳过链接与重解析点。</summary>
    private static void Prune(string directory)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("tw-*.json", SearchOption.TopDirectoryOnly)
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LinkTarget is null)
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).Skip(MaximumEntries).ToArray();
        foreach (var file in files)
            try { file.Delete(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
