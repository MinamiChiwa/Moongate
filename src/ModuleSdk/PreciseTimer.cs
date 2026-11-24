using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.ModuleSdk;

/// <summary>
/// 只作用于本进程的高精度等待：Windows 10 1803 起的高精度可等待计时器（<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>），
/// 按截止时间唤醒的误差约 0.5–1 ms。不调用 <c>timeBeginPeriod</c>，不改变整机计时器精度；系统不支持时（旧版 Windows、其他系统）
/// <see cref="HighResolution"/> 为假，等待退回系统计时器（Windows 默认约 15.6 ms 粒度）。
/// 计时器为同步计时器：到期置位一次，被一个等待满足后自动复位。一个实例同一时刻只供一个等待方使用。
/// </summary>
public sealed class PreciseTimer : IDisposable
{
    private const uint HighResolutionFlag = 0x2, TimerAllAccess = 0x1F0003;
    private readonly SafeWaitHandle? handle;
    private readonly TimerWaitHandle? wait;
    private int disposed;

    public PreciseTimer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var raw = CreateWaitableTimerExW(0, null, HighResolutionFlag, TimerAllAccess);
        if (raw == 0) return;
        handle = new SafeWaitHandle(raw, ownsHandle: true);
        wait = new TimerWaitHandle(handle);
    }

    /// <summary>是否取得了高精度计时器。</summary>
    public bool HighResolution => handle is not null;

    /// <summary>可与其他句柄一起交给 <c>WaitForMultipleObjects</c> 的原生句柄；没有高精度计时器时为 0。</summary>
    public nint Handle => handle is { IsInvalid: false } value ? value.DangerousGetHandle() : 0;

    /// <summary>设定相对到期时间并复位为未置位（小于等于零时立即到期）；没有高精度计时器时返回假。</summary>
    public bool Arm(TimeSpan due)
    {
        if (handle is null || Volatile.Read(ref disposed) != 0) return false;
        // 相对时间以负数表示，单位 100 ns；至少 1 个单位，避免 0 被解释为绝对时间。
        var relative = -Math.Max(1, due.Ticks);
        return SetWaitableTimer(handle, ref relative, 0, 0, 0, false);
    }

    /// <summary>停止尚未到期的计时（已置位的状态不变，下次 <see cref="Arm"/> 时复位）。</summary>
    public void Cancel()
    {
        if (handle is not null && Volatile.Read(ref disposed) == 0) CancelWaitableTimer(handle);
    }

    /// <summary>
    /// 异步等待到 <paramref name="delay"/> 之后；没有高精度计时器时等同 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。
    /// 取消时以 <see cref="OperationCanceledException"/> 结束，计时随之停止。
    /// </summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delay <= TimeSpan.Zero) return Task.CompletedTask;
        if (wait is null || !Arm(delay)) return Task.Delay(delay, cancellationToken);
        return WaitAsync(cancellationToken);
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(wait!, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), completion))
                await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
            if (!completion.Task.IsCompletedSuccessfully) Cancel();
        }
    }

    /// <summary>同步等待到期或 <paramref name="cancel"/> 置位（捕获线程使用）；返回真表示到期。</summary>
    public bool Wait(TimeSpan delay, WaitHandle cancel)
    {
        if (delay <= TimeSpan.Zero) return true;
        if (wait is null || !Arm(delay)) return !cancel.WaitOne(delay);
        return WaitHandle.WaitAny([wait, cancel]) == 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (handle is not null) CancelWaitableTimer(handle);
        wait?.Dispose();
        handle?.Dispose();
    }

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle timer) { SafeWaitHandle = timer; }
        protected override void Dispose(bool explicitDisposing) { SafeWaitHandle = null!; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, nint completion, nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
