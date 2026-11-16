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
        return new(item.Id, name, meta, state, contents, ModuleText.Pick(package?.Descriptions) ?? string.Empty,
            item.Enabled ? UiText.Pick("停用", "停用") : UiText.Pick("启用", "啟用"), !busy && (item.Enabled || !damaged),
            confirmingUninstall == item.Id ? UiText.Pick("确认卸载", "確認解除安裝") : UiText.Pick("卸载", "解除安裝"),
            item.AssetPackDirectory is not null && !busy, damaged);
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
