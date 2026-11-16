using Avalonia.Controls;

namespace MabiTool.Desktop.Pages;

/// <summary>尚未制作的模块页：只显示模块名称和它将承载的内容范围。</summary>
public sealed record PendingPageModel(string Title, string Scope);

public sealed partial class PendingPage : UserControl
{
    public PendingPage() => InitializeComponent();
}
