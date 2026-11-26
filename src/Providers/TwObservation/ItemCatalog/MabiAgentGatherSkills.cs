using System.Text.Json;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 用户 MabiAgent 写好的采集归类（采集物名 → 生活技能），随程序内嵌，归属分类时先于客户端表使用。
/// 数据移植自 MabiAgent 的采集路线（<c>config/screen_catalog.1280x960.tw.json</c> 的 <c>gathering_routes</c> 与 <c>material_sources</c>、
/// <c>config/gathering_routes.tw.json</c>），技能名换成台服客户端 <c>LivingSkill</c> 的显示名；来源与授权登记在 <c>source-baselines.json</c>。
/// </summary>
public static class MabiAgentGatherSkills
{
    private static readonly Lazy<IReadOnlyDictionary<string, LifeSkillRef>> Loaded = new(Load);

    /// <summary>采集物名（台服繁体）→ 生活技能；同一物品只属于一个技能。</summary>
    public static IReadOnlyDictionary<string, LifeSkillRef> Items => Loaded.Value;

    private static IReadOnlyDictionary<string, LifeSkillRef> Load()
    {
        using var stream = typeof(MabiAgentGatherSkills).Assembly.GetManifestResourceStream("MabiTool.MabiAgentGatherSkills")
            ?? throw new InvalidOperationException("缺少内嵌的 MabiAgent 采集归类");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var skills = root.GetProperty("skills").EnumerateArray()
            .ToDictionary(skill => skill.GetProperty("name").GetString()!, skill => new LifeSkillRef(skill.GetProperty("name").GetString()!, skill.GetProperty("fishing").GetBoolean()),
                StringComparer.Ordinal);
        var items = new Dictionary<string, LifeSkillRef>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var skill = skills[item.GetProperty("skill").GetString()!];
            if (!items.TryAdd(name, skill)) throw new InvalidDataException("MabiAgent 采集归类里同一物品出现两次：" + name);
        }
        return items;
    }
}
