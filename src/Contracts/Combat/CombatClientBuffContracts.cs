using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>
/// 客户端 Buff 目录的状态：ready 已读取；loading 名称表正在后台读取；binding-required 没有游戏绑定且本次运行尚未读到；
/// unavailable 名称表读取失败或 Buff 表结构与已核对的不符（换游戏进程或满一分钟后重试）。
/// </summary>
public enum CombatClientBuffCatalogState { Ready, Loading, BindingRequired, Unavailable }

/// <summary>
/// 当前客户端 Buff 表里全部可显示的效果，供前端在效果尚未观测到时按名称或编号选择（例如效果收藏）。
/// 只在 State 为 ready 时 Buffs 有内容；其他状态 Buffs 为空，Reason 为原因码（game.bindingRequired、combat.nameCatalogLoading、
/// combat.nameCatalogUnavailable、combat.nameLayoutUnsupported、combat.nameTableMissing）。ClientId 为客户端资源索引的指纹，
/// 客户端更新后变化，前端据此判断是否重读；名称表正在读取新客户端时继续返回上一份。名称为空或只是开发占位的行不列出，同名的多个编号都列出。
/// </summary>
public sealed record CombatClientBuffCatalog(OpenEnum<CombatClientBuffCatalogState> State, string? Reason, string? ClientId,
    IReadOnlyList<CombatClientBuff> Buffs);

/// <summary>
/// 客户端 Buff 表的一行。Id 为 Buff 表 ID（与效果实例的 effectId 相同）；Name 为 DisplayName 原文；Polarity 为 AffectType 给出的
/// buff、debuff、none，列结构不符时为空；Description 为 DescriptionText（去掉富文本标记，客户端运行时代入的数值写作“？”），没有时为空。
/// </summary>
public sealed record CombatClientBuff(UInt64String Id, string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Polarity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);
