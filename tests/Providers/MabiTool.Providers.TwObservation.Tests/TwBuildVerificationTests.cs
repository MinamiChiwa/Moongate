using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>未登记构建的运行时核对：与登记基线一致的能力启用，变化只停用依赖它的能力。</summary>
public sealed class TwBuildVerificationTests
{
    private const string Handler = "MM.Client.Framework.Network.GC.IGCPacketHandler";
    private const string CharacterPublic = "Shared.Protocols.Replications.GroupReplicatedDatas.CharacterPublicReplicatedData";
    private static readonly string Build = TwClientFingerprint.AdaptiveBuild(new string('a', 64), new string('b', 64));

    [Fact]
    public void UnchangedFactsEnableEveryCapabilityAndAppendedPrefixFieldsStayUsable()
    {
        var verdict = TwBuildVerification.Verify(Build, Facts(baseline =>
        {
            // 新版在角色出现消息末尾追加字段：按字段前缀读取的身份照常可用；新增的协议号进入分帧集合。
            ((JsonArray)baseline["types"]![CharacterPublic]!["fields"]!).Add(new JsonObject { ["name"] = "Added", ["type"] = "int32" });
            baseline["protocols"]!["CHANNEL_SomethingNew_NTF"] = 29999;
        }));
        Assert.False(verdict.Registered);
        Assert.Equal((true, true, true), (verdict.Identity.Enabled, verdict.Music.Enabled, verdict.Combat.Enabled));
        Assert.Contains(29999, verdict.KnownProtocols); Assert.True(verdict.CaptureUsable);
    }

    [Fact]
    public void EachChangeDisablesOnlyTheCapabilitiesThatDependOnIt()
    {
        var renumbered = TwBuildVerification.Verify(Build, Facts(baseline => baseline["protocols"]!["CHANNEL_ShowDamageFloater_NTF"] = 21197));
        Assert.Equal((true, true), (renumbered.Identity.Enabled, renumbered.Music.Enabled));
        Assert.Equal(new TwCapabilityCheck(false, TwBuildVerification.ProtocolChanged), renumbered.Combat);

        var widened = TwBuildVerification.Verify(Build, Facts(baseline =>
            ((JsonArray)baseline["types"]!["Shared.GameLogic.Hits.DamageFloaterData"]!["fields"]!).Add(new JsonObject { ["name"] = "Extra", ["type"] = "int32" })));
        Assert.True(widened.Music.Enabled);
        Assert.Equal(new TwCapabilityCheck(false, TwBuildVerification.LayoutChanged), widened.Combat);

        var concert = TwBuildVerification.Verify(Build, Facts(baseline => baseline["replicated"]!["IConcertsReplicated.Add"] = new JsonArray("int64")));
        Assert.True(concert.Combat.Enabled);
        Assert.Equal(new TwCapabilityCheck(false, TwBuildVerification.LayoutChanged), concert.Music);

        var identity = TwBuildVerification.Verify(Build, Facts(baseline => baseline["handlers"]!["CHANNEL_AppearingCharacter_NTF"] = new JsonArray("int64")));
        Assert.Equal(new TwCapabilityCheck(false, TwBuildVerification.LayoutChanged), identity.Identity);
        Assert.Equal(new TwCapabilityCheck(false, TwBuildVerification.IdentityUnavailable), identity.Music);
        Assert.Equal(new TwCapabilityCheck(false, TwBuildVerification.IdentityUnavailable), identity.Combat);
        Assert.True(identity.CaptureUsable);
    }

    /// <summary>以登记基线与前缀 schema 资源构造客户端事实，修改后交给核对。</summary>
    private static ClientBuildFacts Facts(Action<JsonObject> change)
    {
        var assembly = typeof(TwBuildVerification).Assembly;
        JsonObject Resource(string name) => JsonNode.Parse(assembly.GetManifestResourceStream(name)!)!.AsObject();
        var baseline = Resource("MabiTool.Tw20261110BuildFacts");
        var types = baseline["types"]!.AsObject();
        foreach (var name in new[] { "MabiTool.Tw20261110CharacterSchema", "MabiTool.Tw20261110ChannelMusicSchema",
            "MabiTool.Tw20261110CharacterMusicSchema", "MabiTool.Tw20261110AutomatonSchema" })
            foreach (var (type, value) in Resource(name))
            {
                // 角色音乐 schema 是角色前缀的延伸，取字段较多的一份。
                if (types[type] is { } existing && existing["fields"]!.AsArray().Count >= value!["fields"]!.AsArray().Count) continue;
                types[type] = value!.DeepClone();
            }
        change(baseline);
        var protocols = baseline["protocols"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<int>(), StringComparer.Ordinal);
        var interfaces = new Dictionary<string, List<InventoryProtocolMethod>>(StringComparer.Ordinal) { [Handler] = [] };
        foreach (var (name, parameters) in baseline["handlers"]!.AsObject())
            interfaces[Handler].Add(new(name, Handler, parameters!.AsArray().Select(value => value!.GetValue<string>()).ToArray()));
        foreach (var (name, parameters) in baseline["replicated"]!.AsObject())
        {
            var type = "Shared.Protocols.Replications.IReplicatedGroups." + name[..name.IndexOf('.')];
            if (!interfaces.TryGetValue(type, out var methods)) interfaces[type] = methods = [];
            methods.Add(new(name[(name.IndexOf('.') + 1)..], type, parameters!.AsArray().Select(value => value!.GetValue<string>()).ToArray()));
        }
        var schema = JsonSerializer.SerializeToElement(new JsonObject { ["types"] = types.DeepClone() });
        return new(new string('a', 64), new string('b', 64), protocols, schema,
            interfaces.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InventoryProtocolMethod>)pair.Value, StringComparer.Ordinal));
    }
}
