using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MabiTool.Desktop.Pages.Inventory;

/// <summary>背包页补充倒计时的共用节拍（每秒一次）：有挂在界面上的倒计时才运行，概况与资源区共用同一个节拍。</summary>
public static class InventoryRefillTicker
{
    private static DispatcherTimer? timer;
    private static EventHandler? tick;

    public static event EventHandler? Tick
    {
        add
        {
            tick += value;
            if (timer is null) { timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => tick?.Invoke(null, EventArgs.Empty); }
            if (!timer.IsEnabled) timer.Start();
        }
        remove
        {
            tick -= value;
            if (tick is null) timer?.Stop();
        }
    }
}

/// <summary>
/// 补充倒计时文字（<see cref="InventoryRefillText.Countdown"/>）。挂在界面上时随 <see cref="InventoryRefillTicker"/> 每秒只改自己的文字；
/// 宽度由使用处固定、右对齐，数字变化不挤动同一行的其他内容。
/// </summary>
public sealed class InventoryCountdownText : TextBlock
{
    public static readonly StyledProperty<InventoryRefill?> RefillProperty =
        AvaloniaProperty.Register<InventoryCountdownText, InventoryRefill?>(nameof(Refill));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public InventoryRefill? Refill { get => GetValue(RefillProperty); set => SetValue(RefillProperty, value); }

    /// <summary>测试用的时钟；默认取本机时间。</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RefillProperty) Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InventoryRefillTicker.Tick += OnTick;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        InventoryRefillTicker.Tick -= OnTick;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick(object? sender, EventArgs e) => Update();

    private void Update() => Text = InventoryRefillText.Countdown(Refill, Clock()).Text;
}
