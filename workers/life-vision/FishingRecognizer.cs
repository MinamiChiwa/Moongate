// 移植自 MabiAgent 82bc0ef 的 dotnet/MabiAgent.Windows/ScreenFishingPort.cs（Decorate、RecognizeFishingWait、ObserveFrame 的识别部分）
// （MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：原端口把识别与钓鱼状态（轮次、收竿动画、续抛窗口、导航）放在一起；Moongate 把识别放进独立 Worker，
// 这里只输出每帧的类型化特征：弹窗、下坐骑图标、场景、上钩图标、进度条读数与满包提示。带时钟的轮次推导留在宿主领域层。
// 原端口的“只有轮次已建立或正在等待抛竿才读进度条”门槛，在 Worker 内等价为“无记忆框、无上钩图标时只做定位（Find），不做固定框读数”。

using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;

namespace MabiTool.LifeVision.Worker;

/// <summary>
/// 逐帧识别钓鱼画面。持有模板匹配的工作缓冲区与少量小状态（进度条位置记忆、满包提示的检查与保持时刻），
/// 时间由调用方以 Worker 自己的单调毫秒传入；同一实例不可并发使用，每次观察运行新建一个。
/// </summary>
internal sealed class FishingRecognizer
{
    private readonly TemplateMatcher matcher;
    private PixelBox? rememberedGauge;
    private double lastSignalMilliseconds = double.NegativeInfinity;
    private double lastBackpackCheckMilliseconds = double.NegativeInfinity;
    private double backpackFullUntilMilliseconds = double.NegativeInfinity;

    public FishingRecognizer(TemplateCatalog catalog) => matcher = new TemplateMatcher(catalog);

    internal TemplateMatcher Matcher => matcher;

    /// <summary>
    /// 识别一帧。返回的观察不含帧序号与耗时（由应用层填写），<see cref="FishingObservation.CapturedAtMilliseconds"/> 为传入的 <paramref name="nowMilliseconds"/>。
    /// 帧不是 1280×960 时以 <c>life.fishing.layoutUnsupported</c> 失败。
    /// </summary>
    public FishingObservation Recognize(RgbImage image, double nowMilliseconds)
    {
        if (image.Width != FishingProfile.ClientWidth || image.Height != FishingProfile.ClientHeight)
            throw new WorkerException("life.fishing.layoutUnsupported");

        var dialog = DetectDialog(image);
        var backpackFull = UpdateBackpackFull(image, nowMilliseconds);
        // 弹窗帧只看弹窗：与原端口一致，弹窗期间不评估场景、坐骑、上钩图标与进度条。
        if (dialog is not null)
            return Compose(nowMilliseconds, sceneVisible: false, dismountVisible: false, backpackFull, null, null, dialog);

        var dismountVisible = matcher.Match(image, FishingProfile.Dismount) is not null;
        var sceneVisible = matcher.Match(image, FishingProfile.SceneBag, FishingProfile.SceneThreshold, true, FishingProfile.SceneThreshold) is not null &&
            matcher.Match(image, FishingProfile.SceneMap, FishingProfile.SceneThreshold, true, FishingProfile.SceneThreshold) is not null;
        // 骑乘时直接返回：不评估上钩图标与进度条。
        if (dismountVisible)
            return Compose(nowMilliseconds, sceneVisible, true, backpackFull, null, null, null);

        var hit = matcher.Match(image, FishingProfile.Hook);
        FishingHookObservation? hook = hit is { } found ? new(new VisionPoint(found.Box.Center.X, found.Box.Center.Y), Math.Clamp(found.Score, 0, 1)) : null;
        var gauge = ReadGauge(image, hit?.Box, nowMilliseconds);
        return Compose(nowMilliseconds, sceneVisible, false, backpackFull, hook, gauge, null);
    }

    private static FishingObservation Compose(double nowMilliseconds, bool sceneVisible, bool dismountVisible, bool backpackFull,
        FishingHookObservation? hook, FishingGaugeObservation? gauge, FishingDialogObservation? dialog) =>
        new(new UInt64String(0), nowMilliseconds, sceneVisible, dismountVisible, backpackFull, hook, gauge, dialog, 0, 0);

    private FishingDialogObservation? DetectDialog(RgbImage image)
    {
        var (kind, spec, hit) = FindDialog(image);
        if (hit is null) return null;
        return new FishingDialogObservation(kind, ClickPoint(FishingProfile.ConfirmButton, spec, hit.Value.Box), ClickPoint(FishingProfile.CancelButton, spec, hit.Value.Box));
    }

    private (string Kind, TemplateSpec Spec, TemplateHit? Hit) FindDialog(RgbImage image)
    {
        if (matcher.Match(image, FishingProfile.FocusedDialog) is { } focused) return ("focused", FishingProfile.FocusedDialog, focused);
        if (matcher.Match(image, FishingProfile.FocusedDialogCurrent) is { } current) return ("focused", FishingProfile.FocusedDialogCurrent, current);
        var leisure = matcher.Match(image, FishingProfile.LeisureDialog);
        return ("leisure", FishingProfile.LeisureDialog, leisure);
    }

    /// <summary>
    /// 按钮点击点：期望按钮框的中心（整数除法向下取整），加上标题命中框相对该模板期望框的位移，再夹紧到客户区。
    /// </summary>
    private static VisionPoint ClickPoint(PixelBox button, TemplateSpec spec, PixelBox hit)
    {
        var x = (button.Left + button.Right) / 2 + hit.Left - spec.Box.Left;
        var y = (button.Top + button.Bottom) / 2 + hit.Top - spec.Box.Top;
        return new(Math.Clamp(x, 0, FishingProfile.ClientWidth - 1), Math.Clamp(y, 0, FishingProfile.ClientHeight - 1));
    }

    /// <summary>每 300 毫秒检查一次满包提示；检出后保持 1.5 秒，期间每帧输出 true。</summary>
    private bool UpdateBackpackFull(RgbImage image, double nowMilliseconds)
    {
        if (nowMilliseconds - lastBackpackCheckMilliseconds >= FishingProfile.BackpackCheckMilliseconds)
        {
            lastBackpackCheckMilliseconds = nowMilliseconds;
            if (matcher.Match(image, FishingProfile.BackpackFull) is not null)
                backpackFullUntilMilliseconds = nowMilliseconds + FishingProfile.BackpackHoldMilliseconds;
        }
        return nowMilliseconds < backpackFullUntilMilliseconds;
    }

    /// <summary>
    /// 进度条读数：有上钩图标时先读图标下方的固定偏移框（hook-track），读不到再在图标搜索区外扩后定位（located）；
    /// 没有图标时读记忆框（remembered），记忆框读不到或没有记忆框时只做定位，因此普通场景的全空颜色不会读成零。
    /// 任何非空读数都刷新记忆框与信号时刻；既无图标又无读数的状态超过 3 秒，记忆框清空。
    /// </summary>
    private FishingGaugeObservation? ReadGauge(RgbImage image, PixelBox? hook, double nowMilliseconds)
    {
        if (rememberedGauge is not null && nowMilliseconds - lastSignalMilliseconds > FishingProfile.GaugeMemoryMilliseconds)
            rememberedGauge = null;
        FishingGauge.Reading? reading = null;
        PixelBox? box = null;
        var source = "";
        if (hook is { } icon)
        {
            var center = icon.Center;
            box = new PixelBox(center.X + FishingProfile.TrackLeft, center.Y + FishingProfile.TrackTop, center.X + FishingProfile.TrackRight, center.Y + FishingProfile.TrackBottom);
            reading = FishingGauge.Read(image, box.Value);
            source = "hook-track";
        }
        else if (rememberedGauge is { } remembered)
        {
            box = remembered;
            reading = FishingGauge.Read(image, remembered);
            source = "remembered";
        }
        if (reading is null)
        {
            if (FishingGauge.Find(image, LocateArea(), FishingProfile.GaugeWidth) is { } found)
            {
                box = found.Box;
                reading = new FishingGauge.Reading(found.FillPixels, found.TrackPixels);
                source = "located";
            }
        }
        FishingGaugeObservation? gauge = reading is { } value && box is not null
            ? new(Math.Clamp(value.Value, 0, 1), value.FillPixels, value.TrackPixels, source) : null;
        if (gauge is not null) rememberedGauge = box;
        if (hook is not null || gauge is not null) lastSignalMilliseconds = nowMilliseconds;
        return gauge;
    }

    /// <summary>上钩图标搜索区向四周外扩 max(8, 期望宽度/2) 像素并夹紧到图像，作为无图标时定位进度条的范围。</summary>
    private static PixelBox LocateArea()
    {
        var search = FishingProfile.Hook.Search;
        var padding = Math.Max(8, FishingProfile.GaugeWidth / 2);
        return new PixelBox(Math.Max(0, search.Left - padding), Math.Max(0, search.Top - padding),
            Math.Min(FishingProfile.ClientWidth, search.Right + padding), Math.Min(FishingProfile.ClientHeight, search.Bottom + padding));
    }
}
