using System.Numerics;
using Avalonia.Headless.XUnit;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Combat;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>治疗块到页面数据的映射：施放、每秒治疗、受到治疗与过量说明、治疗目标，以及后端未提供时的显示。</summary>
public sealed class CombatHealingMapperTests
{
    private static readonly ObservationSource Source = new("test", "1", "TW", "synthetic");
    private static readonly EntityKey Local = new("game", new(1), new(1), new(1));
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public CombatHealingMapperTests()
    {
        if (UiText.Language != UiLanguage.SimplifiedChinese && Avalonia.Application.Current is { } application)
            UiText.Apply(application, UiLanguage.SimplifiedChinese);
    }

    private static DamageAmountString Amount(long value) => new(new BigInteger(value));

    private static CombatHealingSummary Healing(DamageAmountString? effective, DamageAmountString? overheal, string? reason,
        double? hps = 26.7, double? rolling = 40.2, IReadOnlyList<CombatHealingTarget>? targets = null) =>
        new("observed-healing-v1", Amount(4037), new(5), Amount(4037), Amount(0), Amount(45817), new(45), Amount(4037), Amount(41780),
            effective, overheal, new(5), reason, hps, rolling, [], targets);

    private static CombatPageData Map(CombatHealingSummary? healing)
    {
        var summary = new CombatSummary("enc", Local, "琉特", Amount(1000), 151_000, 6.6, 7.1, 60000, "observed-damage-v1", false,
            "active", ObservationQuality.Confirmed, [], Healing: healing is null ? null : healing with { Targets = null });
        var totals = new CombatDamageTotals(Amount(1000), Amount(0), Amount(0), Amount(0));
        var encounter = new CombatEncounter("enc", "owner", "obs", "game", new(1), new(3), summary, Now, null, 0, null,
            true, totals, totals, totals, totals, [], [], [], new(0, 0, 0, 0, new(0), new(0), new(0), new(0)), false,
            Healing: healing);
        var status = new CombatObservationStatus("obs", "owner", "game", "observing", Source, Local, new("game"), new(3), Now,
            Now, 0, ObservationQuality.Confirmed, [], new(0));
        return CombatMapper.Map(new(true, true, new(status, encounter, [], null, new(3)), [], null, null, []));
    }

    [AvaloniaFact]
    public void HealingTilesShowCastPerSecondAndKnownOverheal()
    {
        var data = Map(Healing(Amount(44000), Amount(1817), null,
            targets: [new("ce-self", "琉特", Amount(4037), new(5), true)]));
        Assert.True(data.Healing!.Available);
        // HPS 在前，其后是总治疗量与受到治疗；标题行写明口径与统计时长。
        var (perSecond, cast, received) = (data.Healing.Tiles[0], data.Healing.Tiles[1], data.Healing.Tiles[2]);
        Assert.Equal(("总治疗量", "4,037"), (cast.Label, cast.Value));
        Assert.Equal("5 次，对自己 4,037、对他人 0", cast.Note);
        Assert.Equal(("HPS", "27", "最近 60 秒 40"), (perSecond.Label, perSecond.Value, perSecond.Note));
        Assert.Equal("本人施放的治疗（对自己与队友）；HPS = 施放治疗量 ÷ 统计时长 2:31", data.Healing.Header);
        Assert.Equal("", data.Healing.Note);
        Assert.Equal("45,817", received.Value);
        Assert.Equal("45 次，自疗 4,037、其他来源 41,780（协议不提供来源），过量 1,817", received.Note);
        var row = Assert.Single(data.HealingTargets!);
        Assert.Equal(new HealingTargetRow("本人", "4,037", "100%", "5"), row);
    }

    [AvaloniaFact]
    public void UnavailableOverhealAndRatesAreWrittenOutInsteadOfZero()
    {
        var data = Map(Healing(null, null, "hp-unpaired", hps: null, rolling: null));
        Assert.Equal("—", data.Healing!.Tiles[0].Value);
        Assert.Equal("最近 60 秒 未知", data.Healing.Tiles[0].Note);
        Assert.EndsWith("过量不可得：未与 HP 变化配对", data.Healing.Tiles[2].Note);
        Assert.DoesNotContain("过量 0", data.Healing.Tiles[2].Note);
        Assert.EndsWith("过量不可得：HP 变化不一致", Map(Healing(null, null, "hp-mismatch")).Healing!.Tiles[2].Note);
    }

    [AvaloniaFact]
    public void MissingHealingBlockIsReportedAsNotProvided()
    {
        var data = Map(null);
        Assert.False(data.Healing!.Available);
        Assert.Empty(data.Healing.Tiles);
        Assert.Empty(data.HealingTargets!);
    }

    [AvaloniaFact]
    public void OtherTargetsGroupAndSharesFollowBackendGroups()
    {
        var data = Map(Healing(null, null, "hp-unpaired", targets:
        [
            new("ce-a", "芙蕾雅", Amount(3000), new(3), false),
            new("other-targets", "other-targets", Amount(1037), new(2), false)
        ]));
        Assert.Equal(["芙蕾雅", "其他目标"], data.HealingTargets!.Select(row => row.Name));
        Assert.Equal(["74%", "26%"], data.HealingTargets!.Select(row => row.Share));
    }
}
