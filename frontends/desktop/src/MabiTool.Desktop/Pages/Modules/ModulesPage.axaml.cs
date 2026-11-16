using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Modules;

public sealed partial class ModulesPage : UserControl
{
    public ModulesPage() => InitializeComponent();

    // 素材包目录与扩展包文件用系统的选择框选取；扩展文件夹用系统文件管理器打开。
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not ModulesPageModel model) return;
        model.PickFolder = PickFolderAsync;
        model.PickPackage = PickPackageAsync;
        model.OpenFolder = OpenFolderAsync;
    }

    private async Task<string?> PickPackageAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Pick("选择扩展包", "選擇擴充包"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiText.Pick("扩展包", "擴充包")) { Patterns = ["*.zip"] }]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task OpenFolderAsync(string path)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher && Directory.Exists(path))
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiText.Pick("选择素材包目录", "選擇素材包目錄"), AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
