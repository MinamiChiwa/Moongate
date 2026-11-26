using Avalonia;
using Avalonia.Controls;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Shell;

namespace MabiTool.Desktop.Pages.Settings;

/// <summary>
/// 游戏连接器的内容（状态说明、启用开关、命令行文件、游戏目录、恢复原设置）。设置页里用 <see cref="Attach"/> 建立自己的
/// <see cref="GameConnectorModel"/>，随后端变化刷新（离开界面时退订）；侧栏弹出层直接把外壳的模型设为数据上下文，不调用 <see cref="Attach"/>。
/// </summary>
public sealed partial class GameConnectorCardView : UserControl
{
    private IDesktopBackend? backend;
    private GameConnectorModel? owned;

    public GameConnectorCardView() => InitializeComponent();

    /// <summary>设置页卡片：按后端建立自己的模型，首次启用的风险确认交给主窗口里的同一个窗口。</summary>
    public void Attach(IDesktopBackend source)
    {
        backend = source;
        Build();
    }

    private void Build()
    {
        if (backend is null) return;
        owned?.Dispose();
        owned = new GameConnectorModel(backend, ownsRiskLayer: false);
        DataContext = owned;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (backend is not null && owned is null) Build();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        owned?.Dispose();
        owned = null;
    }
}
