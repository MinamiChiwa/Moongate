using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class CombatDetailContractTests
{
    [Fact]
    public void SharedDetailFixturePreservesDecimalsFullWidthIdsAndNullableEvidence()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "contracts/fixtures/combat-detail.v1.json")))!.AsObject();
        var group = fixture["group"]!.Deserialize<CombatDamageGroup>(ContractJson.Options)!;
        var frame = fixture["frame"]!.Deserialize<CombatFrame>(ContractJson.Options)!;
        Assert.Equal(ulong.MaxValue, group.MinimumDamage!.Value.Value);
        Assert.Equal(group.MinimumDamage, group.MaximumDamage);
        Assert.Null(group.DoubleHitCount);
        Assert.Equal(1UL, group.ClassificationCounts!.Direct.Value);
        Assert.Equal(0UL, group.ClassificationCounts.Unknown.Value);
        Assert.Equal(1UL, group.CriticalEligibleCount!.Value.Value);
        Assert.Equal(1UL, group.MainHitEligibleCount!.Value.Value);
        Assert.Equal(frame.Entities[0].RawName, frame.Entities[1].RawName);
        Assert.NotEqual(frame.Entities[0].Key, frame.Entities[1].Key);
        Assert.Equal(125.5, frame.Entities[0].BreakGauge!.Current);
        Assert.Equal(ulong.MaxValue, frame.Entities[0].BreakGauge!.Maximum!.Value.Value);
        Assert.True(frame.Entities[1].BreakGauge!.Stale);
        Assert.Null(frame.Entities[1].BreakGauge!.Maximum);
        Assert.True(frame.Damage[0].PowerfulHit);
        Assert.False(frame.Damage[0].RapidHit);
        Assert.Null(frame.Damage[0].DoubleHit);
        Assert.Equal(CombatDamageClassification.Direct, frame.Damage[0].Classification!.Value.KnownValue);
        Assert.Equal(1UL, group.BreakedHitCount!.Value.Value); Assert.Null(group.MultiHitCount);
        Assert.Equal(1UL, group.ElementCounts!.Fire.Value); Assert.Equal(0UL, group.ElementCounts.Soul.Value);
        Assert.True(frame.Damage[0].BreakedHit); Assert.False(frame.Damage[0].DefaultAttack); Assert.Null(frame.Damage[0].MultiHit);
        Assert.Equal(CombatDamageElement.Fire, Assert.Single(frame.Damage[0].Elements!).KnownValue);
        var roundtrip = new JsonObject
        {
            ["group"] = JsonSerializer.SerializeToNode(group, ContractJson.Options),
            ["frame"] = JsonSerializer.SerializeToNode(frame, ContractJson.Options)
        };
        Assert.True(JsonNode.DeepEquals(fixture, roundtrip));
        var output = Path.Combine(root, "artifacts/generated/contracts/fixtures");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "csharp-combat-detail.v1.json"), roundtrip.ToJsonString());

        var legacyGroup = fixture["group"]!.DeepClone().AsObject();
        foreach (var name in new[] { "powerfulHitCount", "rapidHitCount", "doubleHitCount", "minimumDamage", "maximumDamage",
            "classificationCounts", "criticalEligibleCount", "mainHitEligibleCount", "breakedHitCount", "breakOccurredCount",
            "firstAttackCount", "defaultAttackCount", "multiHitCount", "elementCounts" }) legacyGroup.Remove(name);
        var legacy = legacyGroup.Deserialize<CombatDamageGroup>(ContractJson.Options)!;
        Assert.Null(legacy.PowerfulHitCount); Assert.Null(legacy.MinimumDamage);
        Assert.Null(legacy.BreakedHitCount); Assert.Null(legacy.ElementCounts);
        Assert.Null(legacy.ClassificationCounts); Assert.Null(legacy.CriticalEligibleCount); Assert.Null(legacy.MainHitEligibleCount);
        var legacyFrame = fixture["frame"]!.DeepClone().AsObject();
        foreach (var entity in legacyFrame["entities"]!.AsArray()) entity!.AsObject().Remove("breakGauge");
        foreach (var hit in legacyFrame["damage"]!.AsArray())
            foreach (var name in new[] { "powerfulHit", "rapidHit", "doubleHit", "classification", "breakedHit", "breakOccurred",
                "firstAttack", "defaultAttack", "elements" }) hit!.AsObject().Remove(name);
        var old = legacyFrame.Deserialize<CombatFrame>(ContractJson.Options)!;
        Assert.All(old.Entities, entity => Assert.Null(entity.BreakGauge));
        Assert.Null(old.Damage[0].PowerfulHit);
        Assert.Null(old.Damage[0].Classification);
        Assert.Null(old.Damage[0].BreakedHit); Assert.Null(old.Damage[0].Elements);
        Assert.DoesNotContain("elements", JsonSerializer.SerializeToNode(old.Damage[0], ContractJson.Options)!.AsObject().Select(value => value.Key));
        var future = fixture["group"]!.DeepClone().AsObject();
        future["futureMetric"] = 1;
        var extended = future.Deserialize<CombatDamageGroup>(ContractJson.Options)!;
        Assert.Equal(group.MaximumDamage, extended.MaximumDamage);
        var futureFrame = fixture["frame"]!.DeepClone().AsObject();
        futureFrame["entities"]![0]!["breakGauge"]!["quality"] = "future-quality";
        futureFrame["damage"]![0]!["classification"] = "future-classification";
        futureFrame["damage"]![0]!["elements"] = new JsonArray("fire", "future-element");
        var futureObservation = futureFrame.Deserialize<CombatFrame>(ContractJson.Options)!;
        Assert.Equal("future-quality", futureObservation.Entities[0].BreakGauge!.Quality.ToString());
        Assert.Equal("future-classification", futureObservation.Damage[0].Classification!.Value.RawValue);
        Assert.Equal(new[] { "fire", "future-element" },futureObservation.Damage[0].Elements!.Select(value => value.RawValue));
    }
}
