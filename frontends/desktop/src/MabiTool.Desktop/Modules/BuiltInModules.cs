using Avalonia.Media;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages.Diagnostics;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Overlay;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.Desktop.Pages.Settings;
using MabiTool.Desktop.Shell;

namespace MabiTool.Desktop.Modules;

public static class BuiltInModules
{
    /// <param name="layouts">浮窗版面的保存处；留空时只保存在内存里。</param>
    public static ModuleCatalog CreateCatalog(IDesktopBackend backend, OverlayLayoutStore? layouts = null)
    {
        var catalog = new ModuleCatalog();
        var overlay = new OverlayController(layouts ?? new OverlayLayoutStore(), catalog, backend);
        catalog.Overlay = overlay;
        var mini = new MiniWindowController(backend);
        catalog.Mini = mini;
        catalog.Register(new ModuleDescriptor("music-library", new("乐谱库", "樂譜庫"), new("谱", "譜"), Color.Parse("#C97A10"),
            ModuleOrigin.BuiltIn, () => new MusicPage { DataContext = new MusicPageModel(backend, mini.Show) },
            [
                new("performance.now-playing", new("当前演奏", "目前演奏"), ["scale", "skin"]),
                new("performance.nearby", new("附近演奏字幕", "附近演奏字幕"), ["holdSeconds", "skin"],
                    PresentationTopic: PresentationTopics.Nearby)
            ], pending: true));
        catalog.Register(new ModuleDescriptor("combat-observation", new("DPS检测", "DPS檢測"), new("战", "戰"),
            Color.Parse("#D9502F"), ModuleOrigin.BuiltIn,
            () => new CombatPage { DataContext = new CombatPageModel(backend) },
            [
                new("combat.dps", new("每秒伤害", "每秒傷害"), ["scale", "skin"]),
                new("combat.skills", new("技能占比", "技能佔比"), ["scale", "skin", "rows"]),
                new("combat.coverage", new("暴击、强击、连击覆盖率", "暴擊、強擊、連擊覆蓋率"), ["scale", "skin"]),
                new("combat.self-effects", new("自身效果列", "自身效果列"), ["scale", "skin", "iconPack", "maxItems"]),
                new("combat.target-effects", new("怪物效果列", "怪物效果列"), ["scale", "skin", "iconPack", "maxItems"]),
                new("combat.measurement", new("定时测量", "定時測量"), ["scale", "skin"])
            ]));
        catalog.Register(new ModuleDescriptor("rooms", "合奏", "奏", Color.Parse("#6F51E0"), ModuleOrigin.BuiltIn,
            () => new RoomsPage { DataContext = new RoomsPageModel(backend) },
            [new("rooms.readiness", new("信息浮窗", "資訊浮窗"), ["scale", "skin"])]));
        catalog.Register(new ModuleDescriptor("color", "染色", "染", Color.Parse("#D43D74"), ModuleOrigin.BuiltIn,
            () => new ColorPage { DataContext = new ColorPageModel(backend) },
            [new("color.current", new("当前颜色", "目前顏色"), ["scale", "skin"])]));
        catalog.Register(new ModuleDescriptor("inventory", "背包", "包", Color.Parse("#0E8C7D"), ModuleOrigin.BuiltIn,
            () => new InventoryPage { DataContext = new InventoryPageModel(backend) }));
        catalog.Register(new ModuleDescriptor("presentation", new("浮窗版面", "浮窗版面"), "窗", Color.Parse("#2A6FD6"),
            ModuleOrigin.BuiltIn, () => OverlayPage.Create(new OverlayPageModel(catalog, overlay), backend)));
        catalog.Register(new ModuleDescriptor("shell.modules", new("模块与扩展", "模組與擴充"), "模", Color.Parse("#5F6B85"),
            ModuleOrigin.Shell, () => new ModulesPage { DataContext = new ModulesPageModel(backend, catalog) }));
        catalog.Register(new ModuleDescriptor("shell.settings", new("设置", "設定"), new("设", "設"), Color.Parse("#5F6B85"),
            ModuleOrigin.Shell, () => new SettingsPage { DataContext = new SettingsPageModel(backend) }));
        catalog.Register(new ModuleDescriptor("shell.diagnostics", new("诊断", "診斷"), new("诊", "診"), Color.Parse("#5F6B85"),
            ModuleOrigin.Shell, () => new DiagnosticsPage { DataContext = new DiagnosticsPageModel(backend) }));
        return catalog;
    }
}
