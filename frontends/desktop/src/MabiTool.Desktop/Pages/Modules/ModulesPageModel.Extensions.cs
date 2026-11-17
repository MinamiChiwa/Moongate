using System.Windows.Input;
using MabiTool.Contracts;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Pages.Modules;

/// <summary>
/// 一个已安装扩展的一行。<see cref="UninstallLabel"/> 在按过一次“卸载”后变为“确认卸载”；
/// <see cref="CanUsePack"/> 为真表示扩展启用、完好且带素材包，后端给了素材包目录。
/// </summary>
public sealed record ExtensionRow(string Id, string Name, string Meta, string State, string Contents, string Description,
    string ToggleLabel, bool CanToggle, string UninstallLabel, bool CanUsePack, bool IsDamaged)
{
    public bool HasDescription => Description.Length > 0;
    /// <summary>带代码扩展的运行状态一句话；纯数据扩展为空。</summary>
    public string Runtime { get; init; } = string.Empty;
    public bool HasRuntime => Runtime.Length > 0;
    public IReadOnlyList<PermissionRow> Permissions { get; init; } = [];
    public bool HasPermissions => Permissions.Count > 0;
    public bool CanRestart { get; init; }
    public string RestartLabel => UiText.Pick("重启", "重新啟動");
    public string LogsLabel { get; init; } = UiText.Pick("日志", "日誌");
    public string Logs { get; init; } = string.Empty;
    public bool ShowsLogs => Logs.Length > 0;
}

/// <summary>
/// 扩展的一项权限。高风险权限（操作游戏、截图、网络、读本机文件）标明“高风险”，安装后默认关闭，由用户逐项打开；
/// 每一项都可以随时撤销，撤销立即生效。
/// </summary>
public sealed record PermissionRow(string ExtensionId, string Id, string Name, string Description, bool High, bool Granted, bool CanToggle)
{
    public string Risk => High ? UiText.Pick("高风险", "高風險") : UiText.Pick("普通", "普通");
    public string ToggleLabel => Granted ? UiText.Pick("撤销", "撤銷") : UiText.Pick("授予", "授予");
    public string Status => Granted ? UiText.Pick("已授予", "已授予") : UiText.Pick("未授予", "未授予");
}

/// <summary>投放位置里的一个待安装包。</summary>
public sealed record PendingRow(string Name, string Size);

/// <summary>
/// “扩展”卡片：列表来自 <see cref="ModulesPageData.Extensions"/>（与模块状态同一轮读取），
/// 安装预览、确认、启停与卸载经 <see cref="Backend.IDesktopBackend"/> 交给后端，结果写在卡片里。
/// </summary>
public sealed partial class ModulesPageModel
{
    private ExtensionInstallPreview? preview;
    private string extensionMessage = string.Empty;
    private string? confirmingUninstall;
    private bool busy;

    /// <summary>选择扩展包文件；由页面提供系统的文件选择框，取消时返回空。</summary>
    public Func<Task<string?>>? PickPackage { get; set; }

    /// <summary>用系统文件管理器打开一个目录；由页面提供。</summary>
    public Func<string, Task>? OpenFolder { get; set; }

    public ICommand InstallExtensionCommand { get; }
    public ICommand InspectPendingCommand { get; }
    public ICommand ConfirmInstallCommand { get; }
    public ICommand CancelPreviewCommand { get; }
    public ICommand ToggleExtensionCommand { get; }
    public ICommand UninstallExtensionCommand { get; }
    public ICommand UseAssetPackCommand { get; }
    public ICommand OpenExtensionsFolderCommand { get; }

    public bool ExtensionsAvailable => Data.Extensions is not null;
    public bool ExtensionsUnavailable => Data.Extensions is null;

    public IReadOnlyList<ExtensionRow> ExtensionRows => Data.Extensions?.Installed.Select(Row).ToList() ?? [];
    public bool HasExtensions => ExtensionRows.Count > 0;
    public bool NoExtensions => ExtensionsAvailable && ExtensionRows.Count == 0;

    public IReadOnlyList<PendingRow> PendingRows =>
        Data.Extensions?.Pending.Select(item => new PendingRow(item.Name, Size(item.SizeBytes.Value))).ToList() ?? [];
    public bool HasPending => PendingRows.Count > 0;

    public bool CanOpenExtensionsFolder => Data.Extensions?.Directory is not null;

    /// <summary>安装预览的各项；没有预览时为空。</summary>
    public IReadOnlyList<KeyValue> PreviewRows => preview is not { Package: var package } ? [] :
    [
        new(UiText.Pick("名称", "名稱"), ModuleText.Pick(package.DisplayNames) ?? package.Id),
        new(UiText.Pick("标识", "標識"), package.Id),
        new(UiText.Pick("版本", "版本"), preview.InstalledVersion is { } installed && preview.Action == "upgrade"
            ? UiText.Pick($"{installed} → {package.Version}", $"{installed} → {package.Version}") : package.Version),
        new(UiText.Pick("发布者", "發布者"), package.Publisher),
        new(UiText.Pick("文件数", "檔案數"), package.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new(UiText.Pick("大小", "大小"), Size(package.SizeBytes.Value)),
        new(UiText.Pick("签名", "簽章"), SignatureText(package.Signature)),
        new(UiText.Pick("提供", "提供"), ContentsText(package.Contributions)),
        .. RuntimeRows(package),
        new(UiText.Pick("动作", "動作"), preview.Action switch
        {
            "install" => UiText.Pick("安装，装好后为停用", "安裝，裝好後為停用"),
            "upgrade" => UiText.Pick("升级，保持原启用状态", "升級，保持原啟用狀態"),
            "reinstall" => UiText.Pick("重新安装，内容与已安装的相同", "重新安裝，內容與已安裝的相同"),
            var other => other
        })
    ];

    public bool HasPreview => preview is not null;
    public bool CanConfirm => preview is not null && !busy;
    public string ExtensionMessage => extensionMessage;
    public bool HasExtensionMessage => extensionMessage.Length > 0;

    protected override void OnDataChanged()
    {
        Raise(nameof(DevelopmentMode)); Raise(nameof(DevelopmentLabel)); Raise(nameof(DevelopmentNote));
        // 正在用作素材包的扩展被停用、卸载或损坏后，回到内建外观。
        if (Data.Extensions is { Directory: { } root } catalog && IconRegistry.PackDirectory is { } pack && Within(pack, root)
            && !catalog.Installed.Any(item => item.AssetPackDirectory is { } directory && SamePath(directory, pack)))
        {
            IconRegistry.Remove();
            skipped = [];
            packMessage = UiText.Pick("素材包所属的扩展已停用或卸载，回到内建外观", "素材包所屬的擴充已停用或解除安裝，回到內建外觀");
            RaisePack();
        }
    }

    private ExtensionRow Row(InstalledExtension item)
    {
        var package = item.Package;
        var name = ModuleText.Pick(package?.DisplayNames) ?? item.Id;
        var meta = UiText.Pick($"版本 {item.Version ?? "未知"} · 发布者 {package?.Publisher ?? "未知"}",
            $"版本 {item.Version ?? "未知"} · 發布者 {package?.Publisher ?? "未知"}");
        var damaged = item.State != "ready";
        var state = damaged ? UiText.Pick($"损坏：{item.Problem ?? "未知"}", $"損壞：{item.Problem ?? "未知"}")
            : item.Enabled ? UiText.Pick("已启用", "已啟用") : UiText.Pick("已停用", "已停用");
        var contents = package is null ? UiText.Pick("提供：未知", "提供：未知") : ContentsText(package.Contributions);
        if (item.Development) meta += UiText.Pick(" · 开发模式", " · 開發模式");
        return new(item.Id, name, meta, state, contents, ModuleText.Pick(package?.Descriptions) ?? string.Empty,
            item.Enabled ? UiText.Pick("停用", "停用") : UiText.Pick("启用", "啟用"), !busy && (item.Enabled || !damaged),
            confirmingUninstall == item.Id ? UiText.Pick("确认卸载", "確認解除安裝") : UiText.Pick("卸载", "解除安裝"),
            item.AssetPackDirectory is not null && !busy, damaged)
        {
            Runtime = item.Runtime is { } runtime ? RuntimeText(runtime) : string.Empty,
            Permissions = (item.Permissions ?? []).OrderBy(x => x.Risk == "high").Select(x => new PermissionRow(item.Id, x.Id,
                ModuleText.Pick(x.Names) ?? x.Id, ModuleText.Pick(x.Descriptions) ?? string.Empty, x.Risk == "high", x.Granted, !busy && !damaged)).ToArray(),
            CanRestart = item.Runtime is not null && item.Enabled && !damaged && !busy,
            LogsLabel = logsFor == item.Id ? UiText.Pick("收起日志", "收起日誌") : UiText.Pick("日志", "日誌"),
            Logs = logsFor == item.Id ? logText : string.Empty
        };
    }

    private string? logsFor;
    private string logText = string.Empty;

    public ICommand ToggleGrantCommand { get; }
    public ICommand RestartExtensionCommand { get; }
    public ICommand ShowLogsCommand { get; }

    private static string RuntimeText(ExtensionRuntimeState runtime)
    {
        var text = runtime.State switch
        {
            "running" => UiText.Pick($"运行中（进程 {runtime.ProcessId}）", $"執行中（處理程序 {runtime.ProcessId}）"),
            "starting" => UiText.Pick("正在启动", "正在啟動"),
            "stopping" => UiText.Pick("正在停止", "正在停止"),
            "restarting" => UiText.Pick($"异常退出，将自动重启（第 {runtime.Restarts} 次）", $"異常結束，將自動重新啟動（第 {runtime.Restarts} 次）"),
            "failed" => UiText.Pick("已停止，不再自动重启", "已停止，不再自動重新啟動"),
            "stopped" => UiText.Pick("未运行", "未執行"),
            var other => other
        };
        return runtime.Reason is { Length: > 0 } reason && runtime.State != "running"
            ? text + UiText.Pick("：", "：") + reason + (runtime.ReasonCode is { } code ? $"（{code}）" : string.Empty)
            : text;
    }

    private static IEnumerable<KeyValue> RuntimeRows(ExtensionPackageSummary package)
    {
        if (package.Runtime is { } runtime)
            yield return new(UiText.Pick("运行方式", "執行方式"), runtime.Kind switch
            {
                "executable" => UiText.Pick("随包程序 ", "隨包程式 ") + runtime.Entry,
                "script" => UiText.Pick($"随包解释器 {runtime.Interpreter} 运行 {runtime.Entry}", $"隨包直譯器 {runtime.Interpreter} 執行 {runtime.Entry}"),
                "dotnet" => UiText.Pick("工具所用的 .NET 运行时运行 ", "工具所用的 .NET 執行階段執行 ") + runtime.Entry,
                var other => other + " " + runtime.Entry
            });
        var permissions = package.Permissions ?? [];
        string List(IEnumerable<ExtensionPermissionInfo> items) => string.Join("、", items.Select(x =>
            (ModuleText.Pick(x.Names) ?? x.Id) + (x.Added ? UiText.Pick("（新增）", "（新增）") : string.Empty)));
        if (permissions.Any(x => x.Risk != "high"))
            yield return new(UiText.Pick("普通权限", "普通權限"), List(permissions.Where(x => x.Risk != "high")) + UiText.Pick("，确认安装即授予", "，確認安裝即授予"));
        if (permissions.Any(x => x.Risk == "high"))
            yield return new(UiText.Pick("高风险权限", "高風險權限"), List(permissions.Where(x => x.Risk == "high")) +
                UiText.Pick("。默认关闭，装好后在扩展卡片上逐项打开", "。預設關閉，裝好後在擴充卡片上逐項開啟"));
        if (package.NetworkHosts is { Count: > 0 } hosts)
            yield return new(UiText.Pick("访问的主机", "存取的主機"), string.Join("、", hosts));
        if (package.Runtime is not null)
            yield return new(UiText.Pick("说明", "說明"), UiText.Pick("扩展程序以当前用户身份在独立进程里运行，工具只能约束它经工具开放的接口所做的事",
                "擴充程式以目前使用者身分在獨立處理程序裡執行，工具只能約束它經工具開放的介面所做的事"));
    }

    public ICommand ToggleDevelopmentCommand { get; }
    public ICommand LoadDevelopmentCommand { get; }
    public bool DevelopmentMode => Data.Extensions?.DevelopmentMode == true;
    public string LoadDevelopmentLabel => UiText.Pick("加载开发目录", "載入開發目錄");
    public string DevelopmentLabel => DevelopmentMode ? UiText.Pick("关闭开发模式", "關閉開發模式") : UiText.Pick("开启开发模式", "開啟開發模式");
    public string DevelopmentNote => DevelopmentMode
        ? UiText.Pick("开发模式已开启：可以直接加载开发目录，改完后按扩展的“重启”即生效", "開發模式已開啟：可以直接載入開發目錄，改完後按擴充的「重新啟動」即生效")
        : UiText.Pick("开发模式供扩展作者使用：开启后可以直接加载本机目录里的扩展，不需要打包", "開發模式供擴充作者使用：開啟後可以直接載入本機目錄裡的擴充，不需要打包");

    private async Task ToggleDevelopmentAsync()
    {
        if (busy) return;
        SetBusy(true, string.Empty);
        var reply = await Backend.SetExtensionDevelopmentModeAsync(!DevelopmentMode, CancellationToken.None);
        SetBusy(false, reply.Message);
        Raise(nameof(DevelopmentMode)); Raise(nameof(DevelopmentLabel)); Raise(nameof(DevelopmentNote));
    }

    private async Task LoadDevelopmentAsync()
    {
        if (busy || !DevelopmentMode || PickFolder is null || await PickFolder() is not { Length: > 0 } directory) return;
        SetBusy(true, UiText.Pick("正在加载开发目录…", "正在載入開發目錄…"));
        var reply = await Backend.LoadDevelopmentExtensionAsync(directory, CancellationToken.None);
        SetBusy(false, reply.Message);
    }

    private async Task ToggleGrantAsync(PermissionRow? row)
    {
        if (row is null || busy) return;
        SetBusy(true, string.Empty);
        var reply = await Backend.SetExtensionPermissionAsync(row.ExtensionId, row.Id, !row.Granted, CancellationToken.None);
        SetBusy(false, reply.Message);
    }

    private async Task RestartAsync(string? id)
    {
        if (id is null || busy) return;
        SetBusy(true, UiText.Pick("正在重启扩展…", "正在重新啟動擴充…"));
        var reply = await Backend.RestartExtensionAsync(id, CancellationToken.None);
        SetBusy(false, reply.Message);
    }

    // 日志按一次展开、再按一次收起；展开时读取最后 200 行，不随页面刷新自动重读。
    private async Task ShowLogsAsync(string? id)
    {
        if (id is null) return;
        if (logsFor == id) { logsFor = null; logText = string.Empty; Raise(nameof(ExtensionRows)); return; }
        var page = await Backend.GetExtensionLogsAsync(id, CancellationToken.None);
        logsFor = id;
        logText = page is null ? UiText.Pick("日志未读取", "日誌未讀取")
            : page.Lines.Count == 0 ? UiText.Pick("还没有日志", "還沒有日誌")
            : string.Join("\n", page.Lines.Select(x => $"{x.At.ToLocalTime():HH:mm:ss} [{x.Level}/{x.Source}] {x.Message}"));
        Raise(nameof(ExtensionRows));
    }

    private async Task PickAndPreviewAsync()
    {
        if (PickPackage is null || await PickPackage() is not { Length: > 0 } path) return;
        await PreviewAsync(path, null);
    }

    private async Task PreviewAsync(string? path, string? inboxName)
    {
        if (busy || path is null && inboxName is null) return;
        SetBusy(true, UiText.Pick("正在检查扩展包…", "正在檢查擴充包…"));
        var reply = await Backend.PreviewExtensionAsync(path, inboxName, CancellationToken.None);
        preview = reply.Preview;
        SetBusy(false, reply.Preview is null ? reply.Message : UiText.Pick("检查通过，确认后安装", "檢查通過，確認後安裝"));
    }

    private async Task ConfirmInstallAsync()
    {
        if (preview is not { } current || busy) return;
        SetBusy(true, UiText.Pick("正在安装…", "正在安裝…"));
        var reply = await Backend.InstallExtensionAsync(current.Id, CancellationToken.None);
        preview = null;
        SetBusy(false, reply.Message);
    }

    private async Task ToggleExtensionAsync(string? id)
    {
        if (id is null || busy || Data.Extensions?.Installed.FirstOrDefault(item => item.Id == id) is not { } item) return;
        confirmingUninstall = null;
        SetBusy(true, string.Empty);
        var reply = await Backend.SetExtensionEnabledAsync(id, !item.Enabled, CancellationToken.None);
        SetBusy(false, reply.Message);
    }

    // 卸载要按两次：第一次把按钮变为“确认卸载”，第二次才发送；期间做其他扩展操作会取消确认。
    private async Task UninstallAsync(string? id)
    {
        if (id is null || busy) return;
        if (confirmingUninstall != id)
        {
            confirmingUninstall = id;
            Raise(nameof(ExtensionRows));
            return;
        }
        confirmingUninstall = null;
        SetBusy(true, string.Empty);
        var reply = await Backend.UninstallExtensionAsync(id, CancellationToken.None);
        SetBusy(false, reply.Message);
    }

    private void UseAssetPack(string? id)
    {
        if (Data.Extensions?.Installed.FirstOrDefault(item => item.Id == id)?.AssetPackDirectory is { } directory) LoadPack(directory);
    }

    private async Task OpenExtensionsFolderAsync()
    {
        if (OpenFolder is not null && Data.Extensions?.Directory is { } directory) await OpenFolder(directory);
    }

    private void ClearPreview(string message)
    {
        preview = null;
        confirmingUninstall = null;
        SetBusy(false, message);
    }

    private void SetBusy(bool value, string message)
    {
        busy = value;
        extensionMessage = message;
        foreach (var name in new[] { nameof(PreviewRows), nameof(HasPreview), nameof(CanConfirm), nameof(ExtensionMessage),
                     nameof(HasExtensionMessage), nameof(ExtensionRows) })
            Raise(name);
    }

    private static string SignatureText(string signature) => signature switch
    {
        "unsigned" => UiText.Pick("未签名", "未簽章"),
        "unverified" => UiText.Pick("已附签名，未验证", "已附簽章，未驗證"),
        _ => UiText.Pick("未知", "未知")
    };

    private static string ContentsText(IReadOnlyList<string> contributions) => contributions.Count == 0
        ? UiText.Pick("提供：没有可用的内容", "提供：沒有可用的內容")
        : UiText.Pick("提供：", "提供：") + string.Join("、", contributions.Select(item => item switch
        {
            "assetPack" => UiText.Pick("素材包", "素材包"),
            "code" => UiText.Pick("程序", "程式"),
            "settings" => UiText.Pick("设置项", "設定項"),
            "commands" => UiText.Pick("快捷键命令", "快捷鍵命令"),
            var other => other
        }));

    private static string Size(ulong bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB"
    };

    private static bool Within(string path, string parent) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
