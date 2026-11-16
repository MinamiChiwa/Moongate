using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>
/// 扩展包清单的摘要与检查结果。<see cref="Signature"/> 为 <c>unsigned</c>（未附签名）或 <c>unverified</c>（已附签名、本期未验证）；
/// <see cref="Contributions"/> 是扩展提供的内容，当前只有 <c>assetPack</c>（素材包）。
/// </summary>
public sealed record ExtensionPackageSummary(string Id, string Version, string Publisher,
    IReadOnlyDictionary<string, string> DisplayNames, IReadOnlyDictionary<string, string> Descriptions,
    int FileCount, UInt64String SizeBytes, string TreeSha256, string Signature, IReadOnlyList<string> Contributions);

/// <summary>
/// 一个已安装扩展。<see cref="State"/> 为 <c>ready</c> 或 <c>damaged</c>；损坏时 <see cref="Package"/> 可能为空，
/// <see cref="ProblemCode"/> 与 <see cref="Problem"/> 写明原因。<see cref="Directory"/> 与 <see cref="AssetPackDirectory"/>
/// 只返回给原生桌面客户端，且只在扩展启用并完好时提供。
/// </summary>
public sealed record InstalledExtension(string Id, string? Version, bool Enabled, string State, ExtensionPackageSummary? Package,
    string? ProblemCode, string? Problem, DateTimeOffset? InstalledAt, string? Directory, string? AssetPackDirectory);

/// <summary>投放位置里等待用户检查并确认安装的扩展包。</summary>
public sealed record PendingExtensionPackage(string Name, UInt64String SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>扩展列表；两个目录只返回给原生桌面客户端。</summary>
public sealed record ExtensionCatalog(IReadOnlyList<InstalledExtension> Installed, IReadOnlyList<PendingExtensionPackage> Pending,
    string? Directory, string? InboxDirectory);

/// <summary>安装预览的来源：已完成的上传，或投放位置里的文件名，二选一。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionPreviewRequest(string? UploadId = null, string? InboxName = null);

/// <summary>
/// 安装预览，到期前可确认安装。<see cref="Action"/> 为 <c>install</c>、<c>upgrade</c> 或 <c>reinstall</c>；
/// <see cref="InstalledVersion"/> 是当前已安装的版本。
/// </summary>
public sealed record ExtensionInstallPreview(string Id, string Source, string? InboxName, ExtensionPackageSummary Package,
    string Action, string? InstalledVersion, DateTimeOffset ExpiresAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionEnableRequest(bool Enabled);

public sealed record ExtensionUninstallReceipt(string Id, string? Version);
