using MabiTool.Contracts;
using MabiTool.Modules.Life.Domain;

namespace MabiTool.Life.Tests;

/// <summary>以假时间逐帧驱动状态机；动作默认按已发出回报，测试可改为拒绝。</summary>
internal sealed class Driver
{
    private ulong frame;
    public FishingMachine Machine { get; }
    public double Now { get; private set; }
    public List<FishingAction> Actions { get; } = [];
    public FishingTerminal? Terminal { get; private set; }
    /// <summary>返回 true 的动作按“分发前被拒绝”回报。</summary>
    public Func<FishingAction, bool>? RejectWhen { get; set; }

    public Driver(int? duration = null, FishingOptions? options = null) => Machine = new(new(duration), options);

    public static FishingObservation Observation(ulong id, bool scene = false, bool dismount = false, bool full = false,
        bool hook = false, double? gauge = null, string? dialog = null) => new(new(id), id * 100d, scene, dismount, full,
        hook ? new FishingHookObservation(new(630, 371), .99) : null,
        gauge is { } value ? new FishingGaugeObservation(value, (int)Math.Round(value * 96), 96, hook ? "hook-track" : "located") : null,
        dialog is null ? null : new FishingDialogObservation(dialog, new(752, 864), new(530, 864)), 3, 2);

    /// <summary>推进 <paramref name="seconds"/> 秒后送入一帧。</summary>
    public FishingStep Frame(double seconds, bool scene = false, bool dismount = false, bool full = false, bool hook = false,
        double? gauge = null, string? dialog = null)
    {
        Now += seconds;
        var step = Machine.Step(Observation(++frame, scene, dismount, full, hook, gauge, dialog), Now);
        if (step.Action is { } action)
        {
            Actions.Add(action);
            if (RejectWhen?.Invoke(action) == true) Machine.Rejected(action); else Machine.Dispatched(action, Now);
        }
        Terminal ??= step.Terminal;
        return step;
    }

    /// <summary>连续送入场景帧，间隔 <paramref name="step"/> 秒，直到累计 <paramref name="seconds"/> 秒。</summary>
    public void Scene(double seconds, double step = .1)
    {
        for (var elapsed = 0d; elapsed < seconds - 1e-9; elapsed += step) Frame(step, scene: true);
    }

    /// <summary>进行到首次抛竿已发出（含模式准备的 0.5 秒探测）。</summary>
    public FishingAction FirstCast()
    {
        for (var i = 0; i < 40 && !Actions.Any(a => a.Kind == FishingActionKind.Cast); i++) Frame(.1, scene: true);
        return Actions.Single(a => a.Kind == FishingActionKind.Cast);
    }

    /// <summary>送入轮次帧，给定读数序列，每帧间隔 <paramref name="step"/> 秒。</summary>
    public void Round(IEnumerable<double> gauges, double step = .2, bool hook = true)
    {
        foreach (var value in gauges) Frame(step, hook: hook, gauge: value);
    }

    /// <summary>信号消失后场景 HUD 暂时隐藏 <paramref name="hidden"/> 秒，再恢复场景帧 <paramref name="visible"/> 秒。</summary>
    public void Vanish(double hidden, double visible, double step = .5)
    {
        for (var elapsed = 0d; elapsed < hidden - 1e-9; elapsed += step) Frame(step);
        for (var elapsed = 0d; elapsed < visible - 1e-9; elapsed += step) Frame(step, scene: true);
    }
}
