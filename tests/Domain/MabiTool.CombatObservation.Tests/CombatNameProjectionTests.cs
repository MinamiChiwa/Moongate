using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

/// <summary>读取侧名称投影：怪物、技能与效果给出原文名称，查不到的保留 ID 并标为 unknown，不把 ID 当名称。</summary>
public sealed class CombatNameProjectionTests
{
    private sealed class Names : ICombatNameResolver
    {
        private readonly Dictionary<(string, uint), string> values = new()
        {
            [(CombatNames.Monster, 0x0C11E09E)] = "深層棕毛鼠", [(CombatNames.Skill, 1000)] = "召喚連結者", [(CombatNames.Effect, 100)] = "觸電"
        };
        public CombatResolvedName Resolve(string kind, uint sheetId) => values.TryGetValue((kind, sheetId), out var text)
            ? new(kind, new(sheetId), text, CombatNames.Resolved) : new(kind, new(sheetId), null, CombatNames.Unknown, "combat.nameNotFound");
    }

    [Fact]
    public void MonsterSkillAndEffectNamesAreResolvedAndUnknownIdsKeepOnlyTheirSheetId()
    {
        var session = Session();
        // 来源把 MonsterSheetId 0x0C11E09E 按小端写成 9EE0110C。
        var monster = Entity(Monster, CombatEntityRole.Monster, "9EE0110C");
        Assert.True(session.Apply(Frame(1, 0, [Damage("hit", 0), Damage("other-skill", 0) with { SkillId = new(777) },
                Damage("teammate-skill", 0) with { SkillCasterKey = Self with { RawInstanceId = new(44) }, Chain = true }],
            effects: [Effect(Monster, 0)], entities: [monster])));
        var checkpoint = session.Capture();
        var workspace = CombatNameProjection.Project(checkpoint.Workspace, new Names());

        var encounter = workspace.Encounter!;
        var target = Assert.Single(encounter.Targets).ResolvedName!;
        Assert.Equal((CombatNames.Monster, 0x0C11E09EUL, "深層棕毛鼠", CombatNames.Resolved), (target.Kind, target.SheetId.Value, target.Text, target.State));
        Assert.Equal("深層棕毛鼠", Assert.Single(workspace.Entities, x => x.Key == Monster).ResolvedName!.Text);
        Assert.Equal("召喚連結者", encounter.Skills.Single(x => x.Key == "1000").ResolvedName!.Text);
        Assert.Equal("召喚連結者", encounter.Skills.Single(x => x.Key == "1000:teammate" && x.Teammate == true).ResolvedName!.Text);
        var unknown = encounter.Skills.Single(x => x.Key == "777").ResolvedName!;
        Assert.Equal((CombatNames.Unknown, (string?)null, "combat.nameNotFound", 777UL), (unknown.State, unknown.Text, unknown.Reason, unknown.SheetId.Value));

        var timeline = checkpoint.Timeline.Select(x => CombatNameProjection.Project(x.Item, new Names())).ToArray();
        Assert.Equal("召喚連結者", timeline.First(x => x.Damage?.EventId == "hit").SkillName!.Text);
        Assert.Equal("觸電", timeline.First(x => x.Effect is not null).EffectName!.Text);
        var effects = CombatNameProjection.Project(checkpoint.Effects.Single(x => x.Snapshot.EntityKey == Monster), new Names());
        Assert.Equal("觸電", Assert.Single(effects.Views).ResolvedName!.Text);
    }

    [Fact]
    public void AwakeningMarksFollowBuffIdsAndFallBackToTheBuiltinList()
    {
        // 字符串池里找得到开发名的项：按 MD5 规则换算出的 Id 与内置 Id 相同。
        foreach (var definition in CombatAwakening.Definitions.Where(x => x.DevName is not null))
            Assert.Equal(definition.BuiltinId, CombatAwakening.SheetId(definition.DevName!));
        // 名称表不可用或开发名不在表里：退回内置 Id。
        Assert.Equal(CombatAwakening.Builtin, CombatAwakening.Resolve(null));
        Assert.Equal(CombatAwakening.Builtin, CombatAwakening.Resolve(_ => false));
        ICombatNameResolver names = new Names();
        Assert.Equal(CombatAwakening.Class, names.EffectAwakening(674865859));
        Assert.Equal(CombatAwakening.Badge, names.EffectAwakening(355098955));
        Assert.Equal(CombatAwakening.BadgeCooldown, names.EffectAwakening(315606915));
        // 同名“覺醒”的雷法师效果与普通效果不标记。
        Assert.Null(names.EffectAwakening(2018547152));
        Assert.Null(names.EffectAwakening(100));
    }

    [Fact]
    public void LegacyPartialHistoryGetsItsDpsBackOnRead()
    {
        var session = Session();
        Assert.True(session.Apply(Frame(1, 0, [Damage("a", 0)])));
        Assert.True(session.Apply(Frame(2, 1000, [Damage("b", 1000)], gaps: [new("combat.captureDrops", 500, 500, new(1))])));
        var current = session.Capture().Workspace.Encounter!;
        // 旧版本不完整分段保存的 DPS 为空；读取时按已保存的伤害与 DPS 时长补算，不完整标注不变。
        var legacy = current with { Summary = current.Summary with { EncounterDps = null } };
        var projected = CombatNameProjection.Project(legacy, new Names()).Summary;
        Assert.Equal(200, projected.EncounterDps);
        Assert.Equal(ObservationQuality.Partial, projected.Quality.KnownValue); Assert.Contains("combat.captureDrops", projected.Reasons);
    }
}
