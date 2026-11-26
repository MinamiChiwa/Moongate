using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Shell;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>设置页“下载更新”的确认窗口：先确认再下载，只显示当前界面语言的更新说明，读不到时仍可下载。</summary>
public sealed class SettingsUpdateConfirmTests
{
    private static readonly UpdateNotesView Notes = new("1.2.0",
    [
        new("zh-Hans", "## 新增\n\n- 简体正文 **加粗** 与 [说明](https://example.com/hans)\n"),
        new("zh-Hant", "## 新增\n\n- 繁體正文 **加粗** 與 [說明](https://example.com/hant)\n")
    ]);

    private static SampleBackend Offer(UpdateNotesView? notes = null, string kind = "incremental", ulong bytes = 12 * 1048576UL) =>
        new() { AvailableRelease = "1.2.0", UpdateDownloadKind = kind, UpdateDownloadBytes = bytes, UpdateNotes = notes ?? Notes };

    private static Panel Layer(Window window) => Named<Panel>(window, "UpdateConfirmLayer");

    private static int Downloads(SampleBackend backend) => backend.UiIntents.Count(intent => intent.Item1 == "update.download");

    // 文字块的全部文字，链接按钮里的文字也算。
    private static string Plain(TextBlock block) => block.Inlines is { Count: > 0 } inlines
        ? string.Concat(inlines.Select(inline => inline switch
        {
            Run run => run.Text,
            InlineUIContainer { Child: Button { Content: TextBlock label } } => label.Text,
            _ => string.Empty
        }))
        : block.Text ?? string.Empty;

    private static string NotesText(Window window) => string.Join("\n",
        Named<MarkdownView>(window, "UpdateConfirmNotesView").GetVisualDescendants().OfType<TextBlock>().Select(Plain));

    [AvaloniaFact]
    public void DownloadAsksFirstAndOnlyConfirmStartsIt()
    {
        var backend = Offer();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            Assert.False(Layer(window).IsVisible);
            Click(window, Named<Button>(window, "DownloadUpdateButton"));
            Assert.True(Layer(window).IsVisible);
            Assert.Equal("新版本 1.2.0", Named<TextBlock>(window, "UpdateConfirmVersion").Text);
            Assert.Equal("下载方式：增量更新，12.0 MB", Named<TextBlock>(window, "UpdateConfirmMethod").Text);
            // 只显示简体的那一份，没有繁体正文。
            Assert.Contains("简体正文 加粗 与 说明", NotesText(window));
            Assert.DoesNotContain("繁體", NotesText(window));
            // 弹出确认时还没有发出下载请求。
            Assert.Equal(0, Downloads(backend));

            // 取消：关闭，不下载。
            Click(window, Named<Button>(window, "UpdateConfirmCancel"));
            Assert.False(Layer(window).IsVisible);
            Assert.Equal(0, Downloads(backend));

            // 再次点下载并确认：关闭窗口，发出一次下载请求。
            Click(window, Named<Button>(window, "DownloadUpdateButton"));
            Assert.True(Layer(window).IsVisible);
            Click(window, Named<Button>(window, "UpdateConfirmDownload"));
            Assert.False(Layer(window).IsVisible);
            Assert.Equal(1, Downloads(backend));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EscapeAndClickingOutsideCancelWithoutDownloading()
    {
        var backend = Offer();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            Click(window, Named<Button>(window, "DownloadUpdateButton"));
            Assert.True(Layer(window).IsVisible);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            LayoutProbe.Settle();
            Assert.False(Layer(window).IsVisible);

            Click(window, Named<Button>(window, "DownloadUpdateButton"));
            Assert.True(Layer(window).IsVisible);
            // 窗口左上角在弹出层之外（面板居中）：点那里等同取消。
            var outside = new Point(60, 400);
            var panel = Named<Border>(window, "UpdateConfirmPanel");
            Assert.False(new Rect(panel.TranslatePoint(default, window)!.Value, panel.Bounds.Size).Contains(outside));
            window.MouseDown(outside, MouseButton.Left);
            window.MouseUp(outside, MouseButton.Left);
            LayoutProbe.Settle();
            Assert.False(Layer(window).IsVisible);
            Assert.Equal(0, Downloads(backend));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NotesShowOnlyTheInterfaceLanguageAndMissingNotesStillAllowDownload()
    {
        var pending = new TaskCompletionSource<UpdateNotesView?>();
        var backend = Offer();
        backend.PendingUpdateNotes = pending;
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings", UiLanguage.TraditionalChinese);
        try
        {
            Click(window, Named<Button>(window, "DownloadUpdateButton"));
            // 读取中：写明读取中，“下载”仍可点。
            Assert.Equal("正在讀取更新說明…", Named<TextBlock>(window, "UpdateConfirmNotesStatus").Text);
            Assert.True(Named<Button>(window, "UpdateConfirmDownload").IsEnabled);
            pending.SetResult(Notes);
            LayoutProbe.Settle();
            Assert.False(Named<TextBlock>(window, "UpdateConfirmNotesStatus").IsVisible);
            Assert.Contains("繁體正文 加粗 與 說明", NotesText(window));
            Assert.DoesNotContain("简体", NotesText(window));

            // 切换到简体：只换成简体的一份。
            UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
            LayoutProbe.Settle();
            Assert.Contains("简体正文", NotesText(window));
            Assert.DoesNotContain("繁體", NotesText(window));
            Click(window, Named<Button>(window, "UpdateConfirmCancel"));

            // 当前语言没有读到更新说明：不拿另一种语言顶替，写明读不到，仍可下载。
            backend.PendingUpdateNotes = null;
            backend.UpdateNotes = new("1.2.0", [new("zh-Hant", "繁體正文")]);
            Click(window, Named<Button>(window, "DownloadUpdateButton"));
            Assert.True(Named<TextBlock>(window, "UpdateConfirmNotesStatus").IsVisible);
            Assert.Equal("未能读取更新说明，仍可下载", Named<TextBlock>(window, "UpdateConfirmNotesStatus").Text);
            Assert.False(Named<ScrollViewer>(window, "UpdateConfirmScroller").IsVisible);
            Click(window, Named<Button>(window, "UpdateConfirmDownload"));
            Assert.Equal(1, Downloads(backend));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NothingDownloadableDoesNotPopUpAndAWithdrawnOfferClosesTheWindow()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            // 没有新版本：请求被忽略，不弹窗，也没有下载请求。
            ShellRequests.RequestUpdateDownload();
            LayoutProbe.Settle();
            Assert.False(Layer(window).IsVisible);
            Assert.Equal(0, backend.UpdateNotesLoads);

            // 窗口打开后后端不再报告这个更新（已开始下载、被取消等）：窗口关闭，不再下载。
            backend.AvailableRelease = "1.2.0";
            backend.UpdateNotes = Notes;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            ShellRequests.RequestUpdateDownload();
            LayoutProbe.Settle();
            Assert.True(Layer(window).IsVisible);
            backend.AvailableRelease = null;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.False(Layer(window).IsVisible);
            Assert.Equal(0, Downloads(backend));

            // 确认前出现更新的版本：窗口换成新版本，需要对新版本重新确认。
            backend.AvailableRelease = "1.2.0";
            backend.RaiseChanged();
            LayoutProbe.Settle();
            ShellRequests.RequestUpdateDownload();
            LayoutProbe.Settle();
            backend.AvailableRelease = "1.3.0";
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.True(Layer(window).IsVisible);
            Assert.Equal("新版本 1.3.0", Named<TextBlock>(window, "UpdateConfirmVersion").Text);
            Assert.Equal(0, Downloads(backend));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void MarkdownKeepsHeadingsListsBoldAndOnlyHttpLinks()
    {
        var blocks = MarkdownDocument.Parse("""
            ## 战斗统计

            - 第一项 **加粗** 与 `代码`
              - 嵌套项
            - 链接 [首页](https://example.com/a) 与 [邮件](mailto:a@b.c) 与 [脚本](javascript:alert(1))
            1. 有序项

            > 引用一
            > 引用二

            ---

            普通段落
            第二行
            """);
        Assert.Equal(
        [
            (MarkdownBlockKind.Heading, 2, null), (MarkdownBlockKind.ListItem, 0, "•"), (MarkdownBlockKind.ListItem, 1, "•"),
            (MarkdownBlockKind.ListItem, 0, "•"), (MarkdownBlockKind.ListItem, 0, "1."), (MarkdownBlockKind.Quote, 0, null),
            (MarkdownBlockKind.Rule, 0, null), (MarkdownBlockKind.Paragraph, 0, null)
        ], blocks.Select(block => (block.Kind, block.Level, block.Marker)));
        Assert.Equal([("第一项 ", false, false), ("加粗", true, false), (" 与 ", false, false), ("代码", false, true)],
            blocks[1].Inlines.Select(inline => (inline.Text, inline.Bold, inline.Code)));
        // 只有 http／https 链接带地址；其他协议只留文字。
        Assert.Equal([("首页", "https://example.com/a"), ("邮件", null), ("脚本", null)],
            blocks[3].Inlines.Where(inline => inline.Text is "首页" or "邮件" or "脚本").Select(inline => (inline.Text, inline.Url)));
        Assert.Equal("引用一\n引用二", string.Concat(blocks[5].Inlines.Select(inline => inline.Text)));
        Assert.Equal("普通段落\n第二行", string.Concat(blocks[7].Inlines.Select(inline => inline.Text)));
    }

    [AvaloniaFact]
    public void LinksOpenInTheSystemBrowserOnlyForHttpAddresses()
    {
        var backend = Offer();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            var model = ((ShellViewModel)window.DataContext!).UpdateConfirm;
            var opened = new List<string>();
            model.OpenUrl = opened.Add;
            Click(window, Named<Button>(window, "DownloadUpdateButton"));
            var link = Named<MarkdownView>(window, "UpdateConfirmNotesView").GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("link"));
            Click(window, link);
            Assert.Equal(["https://example.com/hans"], opened);
            model.OpenLinkCommand.Execute("file:///C:/Windows/System32/calc.exe");
            model.OpenLinkCommand.Execute("javascript:alert(1)");
            Assert.Single(opened);
        }
        finally { window.Close(); }
    }
}
