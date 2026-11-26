// 移植自 MabiAgent 82bc0ef 的配置 config/screen_catalog.1280x960.tw.json（ui.templates、ui.idle_fishing）、
// config/screen_maintenance.1280x960.tw.json（ui.templates.maint_scene_map）与 config/screen_overlays.1280x960.tw.json（templates.backpack_full_toast）
// （MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：Moongate 的识别 Worker 只支持台服 1280×960 客户区，规则固定为常量表，不读 JSON、不做按客户区尺寸的缩放。

namespace MabiTool.LifeVision.Worker;

internal enum TransformMode
{
    /// <summary>min(R,G,B) 二值化（可选相对对比度）后 5×5 高斯模糊：亮色文字与图标。</summary>
    LightText,
    /// <summary>红色字形二值化后 5×5 高斯模糊。</summary>
    RedGlyph,
    /// <summary>OpenCV 的 RGB 转灰度，不做模糊。</summary>
    Grayscale
}

/// <summary>
/// 一张识别模板的固定参数。<see cref="Box"/> 是模板在 1280×960 客户区的期望位置，<see cref="Search"/> 是搜索区域；
/// <see cref="Threshold"/> 与 <see cref="GlyphThreshold"/> 对应配置里的 threshold 与 glyph_threshold。
/// </summary>
internal sealed record TemplateSpec(string Id, string File, TransformMode Mode, int LightThreshold, bool RelativeContrast,
    double Threshold, double? GlyphThreshold, PixelBox Box, PixelBox Search, string Role)
{
    /// <summary>字形类模式（二值化后模糊）使用较低的字形阈值。</summary>
    public bool IsGlyph => Mode is TransformMode.LightText or TransformMode.RedGlyph;
}

internal static class FishingProfile
{
    public const int ClientWidth = 1280;
    public const int ClientHeight = 960;

    /// <summary>上钩图标。配置键 ui.templates.fishing_hook（anchors.hook）。</summary>
    public static readonly TemplateSpec Hook = new("fishing_hook", "fishing_hook.png", TransformMode.LightText, 195, false, .93, .90,
        new(613, 354, 648, 389), new(550, 280, 750, 470), "上钩图标");

    /// <summary>“改为专注”弹窗标题。配置键 ui.templates.fishing_mode_confirm。</summary>
    public static readonly TemplateSpec FocusedDialog = new("focused_dialog_title", "focused_dialog_title.png", TransformMode.LightText, 200, false, .97, .97,
        new(582, 693, 697, 725), new(576, 687, 703, 731), "“改为专注”弹窗标题");

    /// <summary>“改为专注”弹窗标题的用户实采变体。配置键 ui.templates.fishing_mode_confirm_current（anchor_variants.fishing_mode_confirm）。</summary>
    public static readonly TemplateSpec FocusedDialogCurrent = new("focused_dialog_title_current", "focused_dialog_title_current.png", TransformMode.LightText, 200, false, .97, .97,
        new(582, 689, 695, 718), new(400, 400, 900, 930), "“改为专注”弹窗标题（用户实采变体）");

    /// <summary>“改为悠闲”弹窗标题。配置键 ui.templates.fishing_leisure_dialog_title。</summary>
    public static readonly TemplateSpec LeisureDialog = new("fishing_leisure_dialog_title", "fishing_leisure_dialog_title.png", TransformMode.LightText, 200, false, .97, .97,
        new(582, 693, 697, 725), new(576, 687, 703, 731), "“改为悠闲”弹窗标题");

    /// <summary>场景背包图标。配置键 ui.templates.scene_bag_core_current（anchor_variants.scene_bag）。</summary>
    public static readonly TemplateSpec SceneBag = new("scene_bag_core_current", "scene_bag_core_current.png", TransformMode.Grayscale, 0, false, .97, null,
        new(1062, 877, 1099, 911), new(1057, 872, 1104, 916), "场景背包图标");

    /// <summary>场景地图键。配置键 screen_maintenance ui.templates.maint_scene_map（图片 scene_map_key_core.png）。</summary>
    public static readonly TemplateSpec SceneMap = new("scene_map_key_core", "scene_map_key_core.png", TransformMode.LightText, 180, false, .94, null,
        new(1244, 129, 1261, 146), new(1240, 125, 1265, 150), "场景地图键");

    /// <summary>下坐骑图标。配置键 screen_maintenance ui.templates.maint_dismount。</summary>
    public static readonly TemplateSpec Dismount = new("maint_dismount", "maint_dismount.png", TransformMode.RedGlyph, 200, false, .94, .97,
        new(1102, 783, 1137, 816), new(1098, 779, 1141, 820), "下坐骑图标");

    /// <summary>满包提示。配置键 screen_overlays templates.backpack_full_toast（relative_contrast）。</summary>
    public static readonly TemplateSpec BackpackFull = new("backpack_full_toast", "backpack_full_toast.png", TransformMode.LightText, 200, true, .97, .97,
        new(540, 50, 790, 82), new(480, 20, 850, 110), "满包提示");

    public static readonly IReadOnlyList<TemplateSpec> Templates =
        [Hook, FocusedDialog, FocusedDialogCurrent, LeisureDialog, SceneBag, SceneMap, Dismount, BackpackFull];

    /// <summary>弹窗确认按钮的期望框。配置键 ui.idle_fishing.controls.confirm_mode。</summary>
    public static readonly PixelBox ConfirmButton = new(729, 852, 775, 877);

    /// <summary>弹窗取消按钮的期望框。配置键 ui.templates.fishing_change_cancel.box。</summary>
    public static readonly PixelBox CancelButton = new(484, 852, 577, 877);

    /// <summary>进度条轨道相对上钩图标中心的偏移。配置键 ui.idle_fishing.gauge.hook_track_offset。</summary>
    public const int TrackLeft = -48, TrackTop = 25, TrackRight = 48, TrackBottom = 55;

    /// <summary>浮动进度条的期望宽度。配置键 ui.idle_fishing.gauge.region（576,430,680,468 的宽度）。</summary>
    public const int GaugeWidth = 104;

    /// <summary>模板匹配的默认下限，场景图标使用放宽后的阈值。</summary>
    public const double DefaultMinimum = .97;
    public const double SceneThreshold = .85;

    /// <summary>既无上钩图标又无有效读数持续超过该时长，清空进度条位置记忆（毫秒）。</summary>
    public const double GaugeMemoryMilliseconds = 3000;

    /// <summary>满包提示的检查间隔与检出后保持时长（毫秒）。</summary>
    public const double BackpackCheckMilliseconds = 300;
    public const double BackpackHoldMilliseconds = 1500;
}
