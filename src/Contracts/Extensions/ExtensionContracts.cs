using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>
/// 扩展声明的一项权限。<see cref="Risk"/> 为 <c>normal</c> 或 <c>high</c>；高风险权限安装后默认关闭，由用户逐项打开。
/// <see cref="Added"/> 在升级预览里标出新版本新增的权限。<see cref="Names"/> 与 <see cref="Descriptions"/> 以语言标记为键。
/// </summary>
public sealed record ExtensionPermissionInfo(string Id, string Risk, IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions, bool Added = false);

/// <summary>
/// 带代码扩展的运行方式。<see cref="Kind"/> 为 <c>executable</c>（随包程序）、<c>script</c>（随包脚本由随包解释器运行）
/// 或 <c>dotnet</c>（随包程序集由工具所用的 .NET 运行时运行）；路径都相对扩展安装目录。
/// </summary>
public sealed record ExtensionRuntimeInfo(string Kind, string Entry, string? Interpreter, int Protocol);

/// <summary>
/// 扩展包清单的摘要与检查结果。<see cref="Signature"/> 为 <c>unsigned</c>（未附签名）或 <c>unverified</c>（已附签名、本期未验证）；
/// <see cref="Contributions"/> 是扩展提供的内容：<c>assetPack</c>（素材包）、<c>code</c>（程序）、<c>settings</c>（设置项）、<c>commands</c>（快捷键命令）。
/// 带代码的扩展另给出 <see cref="Runtime"/>、<see cref="Permissions"/> 与可访问的网络主机 <see cref="NetworkHosts"/>。
/// </summary>
public sealed record ExtensionPackageSummary(string Id, string Version, string Publisher,
    IReadOnlyDictionary<string, string> DisplayNames, IReadOnlyDictionary<string, string> Descriptions,
    int FileCount, UInt64String SizeBytes, string TreeSha256, string Signature, IReadOnlyList<string> Contributions,
    ExtensionRuntimeInfo? Runtime = null, IReadOnlyList<ExtensionPermissionInfo>? Permissions = null,
    IReadOnlyList<string>? NetworkHosts = null);

/// <summary>已安装扩展的一项权限及当前是否授予。</summary>
public sealed record ExtensionPermissionState(string Id, string Risk, IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions, bool Granted);

/// <summary>
/// 带代码扩展的运行状态。<see cref="State"/> 为 <c>stopped</c>、<c>starting</c>、<c>running</c>、<c>stopping</c>、
/// <c>restarting</c>（崩溃后按退避等待重启）或 <c>failed</c>（停止且不再自动重启）；<see cref="ReasonCode"/> 与 <see cref="Reason"/>
/// 写明最近一次停止或失败的原因，<see cref="Restarts"/> 是当前窗口内的自动重启次数。
/// </summary>
public sealed record ExtensionRuntimeState(string State, string? ReasonCode, string? Reason, int? ProcessId,
    DateTimeOffset? StartedAt, int Restarts, DateTimeOffset? NextStartAt);

/// <summary>
/// 一个已安装扩展。<see cref="State"/> 为 <c>ready</c> 或 <c>damaged</c>；损坏时 <see cref="Package"/> 可能为空，
/// <see cref="ProblemCode"/> 与 <see cref="Problem"/> 写明原因。<see cref="Directory"/> 与 <see cref="AssetPackDirectory"/>
/// 只返回给原生桌面客户端，且只在扩展启用并完好时提供。带代码的扩展另有 <see cref="Permissions"/> 与 <see cref="Runtime"/>；
/// <see cref="Development"/> 为真表示以开发模式从用户指定的目录加载。
/// </summary>
public sealed record InstalledExtension(string Id, string? Version, bool Enabled, string State, ExtensionPackageSummary? Package,
    string? ProblemCode, string? Problem, DateTimeOffset? InstalledAt, string? Directory, string? AssetPackDirectory,
    IReadOnlyList<ExtensionPermissionState>? Permissions = null, ExtensionRuntimeState? Runtime = null, bool Development = false);

/// <summary>投放位置里等待用户检查并确认安装的扩展包。</summary>
public sealed record PendingExtensionPackage(string Name, UInt64String SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>扩展列表；两个目录只返回给原生桌面客户端。<see cref="DevelopmentMode"/> 为用户是否开启了开发模式加载。</summary>
public sealed record ExtensionCatalog(IReadOnlyList<InstalledExtension> Installed, IReadOnlyList<PendingExtensionPackage> Pending,
    string? Directory, string? InboxDirectory, bool DevelopmentMode = false);

/// <summary>安装预览的来源：已完成的上传，或投放位置里的文件名，二选一。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionPreviewRequest(string? UploadId = null, string? InboxName = null);

/// <summary>
/// 安装预览，到期前可确认安装。<see cref="Action"/> 为 <c>install</c>、<c>upgrade</c> 或 <c>reinstall</c>；
/// <see cref="InstalledVersion"/> 是当前已安装的版本。确认安装即同意清单里的普通权限；高风险权限仍默认关闭。
/// </summary>
public sealed record ExtensionInstallPreview(string Id, string Source, string? InboxName, ExtensionPackageSummary Package,
    string Action, string? InstalledVersion, DateTimeOffset ExpiresAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionEnableRequest(bool Enabled);

public sealed record ExtensionUninstallReceipt(string Id, string? Version);

/// <summary>按权限标识授予（true）或撤销（false）；只能涉及清单声明过的权限，撤销立即生效。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionGrantRequest(IReadOnlyDictionary<string, bool> Grants);

/// <summary>扩展日志的一行；<see cref="Source"/> 为 <c>host</c>（宿主记录）、<c>extension</c>（扩展经协议写入）或 <c>stderr</c>。</summary>
public sealed record ExtensionLogLine(DateTimeOffset At, string Level, string Source, string Message);

/// <summary>扩展日志的最后若干行；<see cref="Truncated"/> 为真表示更早的内容未返回。</summary>
public sealed record ExtensionLogPage(string Id, IReadOnlyList<ExtensionLogLine> Lines, bool Truncated);

/// <summary>开发模式开关：开启后宿主接受从用户指定目录加载的开发中扩展。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionDevelopmentModeRequest(bool Enabled);

/// <summary>以开发模式加载一个本机目录（根目录有 extension.json）；只在开发模式开启时接受。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionDevelopmentLoadRequest(string Directory);

/// <summary>
/// 扩展提供的一个界面：<see cref="Kind"/> 为 <c>page</c>（侧栏“扩展”分组里的页面）或 <c>overlay</c>（浮窗元件）。
/// <see cref="Root"/> 是声明式界面描述（版本 <see cref="SchemaVersion"/>），由前端用自己的控件渲染；
/// <see cref="Size"/> 是浮窗元件的建议尺寸档 <c>small</c>／<c>medium</c>／<c>large</c>。
/// </summary>
public sealed record ExtensionSurface(string ExtensionId, string Id, string Kind, IReadOnlyDictionary<string, string> Titles,
    int SchemaVersion, JsonElement Root, string? Size, UInt64String Revision);

/// <summary>扩展推送给界面绑定的状态值（键到 JSON 值）。</summary>
public sealed record ExtensionStateValues(string ExtensionId, IReadOnlyDictionary<string, JsonElement> Values, UInt64String Revision);

/// <summary>
/// 清单声明的一个设置项。<see cref="Type"/> 为 <c>toggle</c>、<c>number</c>、<c>text</c> 或 <c>choice</c>；
/// <see cref="Value"/> 是当前值（未保存过时为默认值）。
/// </summary>
public sealed record ExtensionSettingField(string Key, string Type, IReadOnlyDictionary<string, string> Labels,
    JsonElement Value, double? Minimum, double? Maximum, IReadOnlyList<ExtensionSettingOption>? Options);

public sealed record ExtensionSettingOption(string Value, IReadOnlyDictionary<string, string> Labels);

/// <summary>一个扩展的设置表单；只有授予 ui.settings 的扩展出现在设置页。</summary>
public sealed record ExtensionSettingsForm(string ExtensionId, IReadOnlyDictionary<string, string> Titles,
    IReadOnlyList<ExtensionSettingField> Fields, UInt64String Revision);

/// <summary>
/// 扩展的一个快捷键命令。<see cref="ActionId"/> 是在快捷键设置里登记的动作标识（<c>extension.&lt;标识&gt;.&lt;命令&gt;</c>）。
/// </summary>
public sealed record ExtensionCommand(string ExtensionId, string Id, string ActionId, IReadOnlyDictionary<string, string> Labels);

/// <summary>扩展在活动栏显示的一句状态；<see cref="Tone"/> 为 <c>neutral</c>、<c>active</c>、<c>warning</c> 或 <c>error</c>。</summary>
public sealed record ExtensionActivity(string ExtensionId, string Text, string Tone, UInt64String Revision);

/// <summary>运行中扩展提供的全部界面与状态；修订变化时前端重读。</summary>
public sealed record ExtensionSurfaceCatalog(IReadOnlyList<ExtensionSurface> Surfaces, IReadOnlyList<ExtensionStateValues> States,
    IReadOnlyList<ExtensionSettingsForm> Settings, IReadOnlyList<ExtensionCommand> Commands, IReadOnlyList<ExtensionActivity> Activities,
    UInt64String Revision, IReadOnlyList<ExtensionFileRequest>? FileRequests = null);

/// <summary>
/// 扩展请求读取一个由用户选择的文件。原生桌面客户端据此弹出文件选择框（标题写明扩展名称），把所选文件经上传接口交给宿主，
/// 或回复取消；<see cref="MaximumBytes"/> 是可交给扩展的文件大小上限。
/// </summary>
public sealed record ExtensionFileRequest(string Id, string ExtensionId, IReadOnlyDictionary<string, string> ExtensionNames, string Title,
    UInt64String MaximumBytes, DateTimeOffset ExpiresAt);

/// <summary>对文件请求的回复：已完成上传的 <see cref="UploadId"/>，或 <see cref="Cancelled"/> 为真表示用户取消。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionFileResponse(string? UploadId = null, bool Cancelled = false);

/// <summary>界面交互事件：<see cref="Node"/> 是描述里节点的 <c>id</c>，<see cref="Kind"/> 为 <c>click</c> 或 <c>change</c>。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionUiEvent(string Surface, string Node, string Kind, JsonElement? Value = null);

/// <summary>保存一个扩展的设置值；只能写清单声明过的键，类型与范围由宿主核对。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtensionSettingsUpdate(IReadOnlyDictionary<string, JsonElement> Values);
