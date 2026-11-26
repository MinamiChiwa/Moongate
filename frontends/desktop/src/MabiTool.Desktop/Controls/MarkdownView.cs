using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MabiTool.Desktop.Infrastructure;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 把 <see cref="MarkdownDocument"/> 解析出的块排成界面：标题按级别加粗放大，列表项带项目符号并按层级缩进，引用靠左一条线，
/// 分隔线一条细线，段落内的粗体、行内代码与链接按行内片段排版。链接是可 Tab 聚焦的按钮，按下时把地址交给
/// <see cref="LinkCommand"/>（由外壳用系统浏览器打开）；控件自身不打开任何地址。
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<MarkdownBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownBlock>?>(nameof(Blocks));

    public static readonly StyledProperty<ICommand?> LinkCommandProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(LinkCommand));

    private static readonly FontFamily Mono = FontFamily.Parse("Cascadia Mono, Consolas, Courier New, monospace");

    static MarkdownView()
    {
        BlocksProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
        LinkCommandProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public MarkdownView() => Spacing = 6;

    public IReadOnlyList<MarkdownBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>按下链接时执行，参数是 http／https 地址。</summary>
    public ICommand? LinkCommand
    {
        get => GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Blocks is not { } blocks) return;
        foreach (var block in blocks) Children.Add(Build(block, first: Children.Count == 0));
    }

    private Control Build(MarkdownBlock block, bool first)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
            {
                var size = block.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 };
                var heading = Text(block.Inlines, "body", size, bold: true);
                heading.Margin = new Thickness(0, first ? 0 : 8, 0, 0);
                return heading;
            }
            case MarkdownBlockKind.ListItem:
            {
                var grid = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(block.Level * 18, 0, 0, 0) };
                var marker = new TextBlock { Text = block.Marker, MinWidth = 18, Margin = new Thickness(0, 0, 6, 0), Classes = { "muted" } };
                AutomationProperties.SetAccessibilityView(marker, AccessibilityView.Raw);
                Grid.SetColumn(marker, 0);
                var body = Text(block.Inlines, "body");
                Grid.SetColumn(body, 1);
                grid.Children.Add(marker);
                grid.Children.Add(body);
                return grid;
            }
            case MarkdownBlockKind.Quote:
                return new Border { Classes = { "quote" }, Child = Text(block.Inlines, "body", muted: true) };
            case MarkdownBlockKind.Rule:
                return new Border { Classes = { "rule" }, Height = 1, Margin = new Thickness(0, 4) };
            default:
                return Text(block.Inlines, "body");
        }
    }

    private TextBlock Text(IReadOnlyList<MarkdownInline> inlines, string @class, double? size = null, bool bold = false, bool muted = false)
    {
        var block = new TextBlock { Classes = { @class } };
        if (muted) block.Classes.Add("muted");
        if (size is { } fontSize) block.FontSize = fontSize;
        if (bold) block.FontWeight = FontWeight.Bold;
        foreach (var inline in inlines)
        {
            if (inline.Url is { } url) block.Inlines!.Add(Link(inline, url, bold));
            else
            {
                var run = new Run(inline.Text);
                if (inline.Bold) run.FontWeight = FontWeight.Bold;
                if (inline.Code) run.FontFamily = Mono;
                block.Inlines!.Add(run);
            }
        }
        return block;
    }

    private InlineUIContainer Link(MarkdownInline inline, string url, bool boldBlock)
    {
        var label = new TextBlock { Text = inline.Text, Classes = { "link" } };
        if (inline.Bold || boldBlock) label.FontWeight = FontWeight.Bold;
        if (inline.Code) label.FontFamily = Mono;
        var button = new Button { Classes = { "link" }, Content = label, Command = LinkCommand, CommandParameter = url };
        AutomationProperties.SetName(button, inline.Text);
        ToolTip.SetTip(button, url);
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center };
    }
}
