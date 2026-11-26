namespace MabiTool.Contracts;

// life-vision Worker v1 的线格式。权威源是 contracts/worker/life-vision.v1.schema.json，本文件是同一结构的 C# 模型，
// 由 tests/Providers/MabiTool.LifeVision.Tests 的契约测试核对两者属性一致。

/// <summary>Worker 方法 <c>life.vision.fishing.watch</c> 的参数：识别一个游戏窗口，持续输出钓鱼观察直到取消。</summary>
/// <param name="Client">绑定客户区的物理像素矩形；Worker 每帧核对窗口客户区与它一致。</param>
/// <param name="PeriodMilliseconds">观察周期（50–500）；Worker 把截图与识别耗时从周期里扣除。</param>
/// <param name="Profile">识别配置标识，当前只有 <see cref="LifeVisionProfiles.TwFishing1280x960"/>。</param>
public sealed record LifeVisionWatchArguments(UInt64String WindowIdentity, int ProcessId, DateTimeOffset ProcessStartTime,
    PhysicalWindowRect Client, int Dpi, int PeriodMilliseconds, string Profile);

public static class LifeVisionProfiles
{
    public const string TwFishing1280x960 = "tw-fishing-1280x960-v1";
    public const int ClientWidth = 1280;
    public const int ClientHeight = 960;
    public const string WatchMethod = "life.vision.fishing.watch";
    public const string WorkerId = "life-vision";
}

/// <summary>客户区内的整数点（物理像素，原点在客户区左上角）。</summary>
public sealed record VisionPoint(int X, int Y);

/// <summary>上钩图标：中心点与相关系数。</summary>
public sealed record FishingHookObservation(VisionPoint Center, double Score);

/// <summary>
/// 进度条读数（0–1）。<see cref="Source"/> 为 <c>hook-track</c>（按上钩图标定位的条形）、<c>remembered</c>（沿用最近有效位置）或
/// <c>located</c>（在图标周围的搜索区里按双色条形定位）。
/// </summary>
public sealed record FishingGaugeObservation(double Value, int FillPixels, int TrackPixels, string Source);

/// <summary>模式弹窗：<c>focused</c> 为“改为专注”的确认窗，<c>leisure</c> 为“改为悠闲”的确认窗；两个按钮的点击点随标题位置换算。</summary>
public sealed record FishingDialogObservation(string Kind, VisionPoint Confirm, VisionPoint Cancel);

/// <summary>
/// 一帧的类型化识别结果；整帧图像不离开 Worker 进程。<see cref="CapturedAtMilliseconds"/> 是 Worker 自己的单调毫秒，
/// Host 只用它做帧间间隔，不与宿主时钟互相解释。<see cref="BackpackFull"/> 在检出满包提示后保持 1.5 秒。
/// </summary>
public sealed record FishingObservation(UInt64String FrameId, double CapturedAtMilliseconds, bool SceneVisible, bool DismountVisible,
    bool BackpackFull, FishingHookObservation? Hook, FishingGaugeObservation? Gauge, FishingDialogObservation? Dialog,
    double CaptureMilliseconds, double RecognizeMilliseconds);

/// <summary>Worker 事件帧的正文：只有一个属性 <c>observation</c>。</summary>
public sealed record LifeVisionEvent(FishingObservation Observation);
