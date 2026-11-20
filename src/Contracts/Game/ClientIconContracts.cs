namespace MabiTool.Contracts;

/// <summary>
/// 客户端属性图标目录的状态：ready 已读取；loading 正在后台读取；binding-required 没有游戏绑定且本次运行尚未读到；
/// unavailable 读取失败（换游戏进程或满一分钟后重试）。
/// </summary>
public enum ClientIconCatalogState { Ready, Loading, BindingRequired, Unavailable }

/// <summary>
/// 图标资源路径的来源：parameter-info 为客户端 ParameterInfo 表的 IconPath 列；fixed-path 为已核对的固定资源路径
/// （元素类，或该属性在表中没有可读的行）。
/// </summary>
public enum ClientIconSource { ParameterInfo, FixedPath }

/// <summary>
/// 战斗属性图标目录。图标是游戏客户端自带的美术资源，宿主在运行时从当前绑定游戏的本机客户端读取并缓存到数据根，
/// 不随工具分发。<see cref="Icons"/> 固定列出 <see cref="ClientIconKeys.All"/> 的每个键：目录未就绪时各项不可用，
/// 原因与目录 <see cref="Reason"/> 相同。ClientId 为客户端资源索引的指纹（客户端更新后变化）；Reasons 为不影响图标可用的附加说明，
/// 例如 clientIcon.parameterTableUnavailable（ParameterInfo 表不可读，路径改用固定路径、着色为空）。
/// </summary>
public sealed record ClientIconCatalog(OpenEnum<ClientIconCatalogState> State, string? Reason, string? ClientId,
    IReadOnlyList<ClientIcon> Icons, IReadOnlyList<string> Reasons);

/// <summary>
/// 单个属性图标。Available 为真时可从 <c>GET /api/v1/client-icons/{key}/content</c> 读取 PNG，Version 为该 PNG 的 SHA-256
/// （与内容接口的 ETag、X-Content-Sha256 相同），前端按键与 Version 缓存。图标是白色字形，Tint 为客户端 ParameterInfo.MainColor
/// 的着色（#RRGGBB）；表中该行没有颜色、元素类或表不可读时为空，由前端自选颜色。不可用时 Reason 为原因码，前端保留文字显示。
/// NameKey 为该属性在客户端 ParameterInfo 表中的键（ParameterType，例如追击为 DoubleHit），DisplayName 为该行的显示名称原文
/// （例如“追擊”），供前端放进图标的悬浮提示；只登记与战斗标签同义的行（critical、doubleHit、breakOccurred），其他键与表不可读时为空，
/// 前端改用自己的文字。图标不可用时名称照常返回。
/// </summary>
public sealed record ClientIcon(string Key, bool Available, string? Reason, string? Tint, string? Version,
    int? Width, int? Height, OpenEnum<ClientIconSource>? Source, string? SourcePath, string? NameKey = null, string? DisplayName = null);

/// <summary>图标键，与战斗伤害标签字段同名：无防备为 breakedHit，破防为 breakOccurred，追击（追擊）为 doubleHit。</summary>
public static class ClientIconKeys
{
    public const string Critical = "critical", PowerfulHit = "powerfulHit", RapidHit = "rapidHit", DoubleHit = "doubleHit",
        BreakedHit = "breakedHit", BreakOccurred = "breakOccurred", MultiHit = "multiHit";
    public static IReadOnlyList<string> Elements { get; } = Array.AsReadOnly(
        ["element.bleed", "element.dark", "element.fire", "element.holy", "element.ice", "element.lightning", "element.poison", "element.soul"]);
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly<string>(
        [Critical, PowerfulHit, RapidHit, DoubleHit, BreakedHit, BreakOccurred, MultiHit, .. Elements]);
}

/// <summary>客户端图标的原因码；同时登记为 ApiProblem 错误码，内容接口以对应码返回。</summary>
public static class ClientIconReasons
{
    public const string BindingRequired = "clientIcon.bindingRequired", Loading = "clientIcon.loading",
        CatalogUnavailable = "clientIcon.catalogUnavailable", ResourceMissing = "clientIcon.resourceMissing",
        ResourceInvalid = "clientIcon.resourceInvalid", FormatUnsupported = "clientIcon.formatUnsupported",
        ParameterTableUnavailable = "clientIcon.parameterTableUnavailable";

    public static IReadOnlyList<ErrorDefinition> Definitions { get; } = Array.AsReadOnly<ErrorDefinition>([
        new(BindingRequired, 409, false), new(Loading, 503, true), new(CatalogUnavailable, 503, true),
        new(ResourceMissing, 422, false), new(ResourceInvalid, 422, false), new(FormatUnsupported, 422, false),
        new(ParameterTableUnavailable, 422, false)
    ]);
}
