using System.Diagnostics;
using MabiTool.Contracts;

namespace MabiTool.LifeVision.Worker;

/// <summary>
/// 截图源。每次 <see cref="CaptureAsync"/> 核对窗口与请求一致后返回 RGB24 帧；实现复用同一个像素缓冲区以限制内存，
/// 所以返回的 <see cref="RgbImage"/> 只在下一次调用之前有效，调用方不得保存或把它交给别的线程。
/// 失败以 <see cref="MabiTool.Infrastructure.Workers.WorkerException"/> 报告契约内的错误码（<c>capture.sourceFailed</c>、<c>input.bindingChanged</c>、<c>capture.frameInvalid</c>）。
/// </summary>
public interface IFrameSource : IDisposable
{
    ValueTask<RgbImage> CaptureAsync(LifeVisionWatchArguments binding, CancellationToken cancellationToken);
}

/// <summary>单调毫秒时钟与延时，观察循环与识别状态都用它，测试以假时钟驱动。</summary>
public interface IVisionClock
{
    /// <summary>Worker 内部的单调毫秒，原点任意；宿主只用它的差值。</summary>
    double NowMilliseconds { get; }

    Task DelayAsync(double milliseconds, CancellationToken cancellationToken);
}

public sealed class SystemVisionClock : IVisionClock
{
    public static SystemVisionClock Instance { get; } = new();

    private SystemVisionClock() { }

    public double NowMilliseconds => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

    public Task DelayAsync(double milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
}
