using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Settings;
using MabiTool.Desktop.Shell;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>设置页“抓包”：自动抓包开关、指定网卡、手动调试检测与确认、自动选择游戏窗口；DPS 检测页的未抓包提示。</summary>
public sealed class SettingsCaptureTests
{
    private static Button ChannelButton(Window window, string description) =>
        Named<ItemsControl>(window, "CaptureChannelList").GetVisualDescendants().OfType<Button>()
            .Single(button => button.DataContext is CaptureChannelRow row && row.Description == description);

    [AvaloniaFact]
    public void TogglesSaveTheWholeSettingsAndTheStatusExplainsWhyCaptureIsOff()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            Assert.StartsWith("抓包中 · 来源：物理网卡", Named<TextBlock>(window, "CaptureStatusText").Text);
            Click(window, Named<Button>(window, "CaptureOffButton"));
            LayoutProbe.Settle();
            Assert.False(Assert.Single(backend.SavedCaptureSettings).AutomaticCapture);
            Assert.StartsWith("未抓包：自动抓包已关闭", Named<TextBlock>(window, "CaptureStatusText").Text);
            Assert.Contains("active", Named<Button>(window, "CaptureOffButton").Classes);

            Click(window, Named<Button>(window, "AutoWindowOffButton"));
            LayoutProbe.Settle();
            var saved = backend.SavedCaptureSettings[^1];
            Assert.False(saved.AutomaticWindowBinding); Assert.False(saved.AutomaticCapture);
            Assert.Equal(2UL, saved.Revision.Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ListedChannelsShowGameConnectionsAndChoosingOneSavesItAsTheManualChannel()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            // 没有保存过网卡时，“指定网卡”先列出网卡供选择。
            Click(window, Named<Button>(window, "ChannelManualButton"));
            LayoutProbe.Settle();
            Assert.Empty(backend.SavedCaptureSettings);
            Assert.Contains("游戏连接 1 条", ((CaptureChannelRow)ChannelButton(window, "Intel(R) Ethernet Connection (17) I219-V").DataContext!).Label);
            Assert.Contains("回环", ((CaptureChannelRow)ChannelButton(window, "Npcap Loopback Adapter").DataContext!).Label);
            Click(window, ChannelButton(window, "WireGuard Tunnel"));
            LayoutProbe.Settle();
            var saved = Assert.Single(backend.SavedCaptureSettings);
            Assert.Equal(GameCaptureSettings.ManualChannel, saved.ChannelMode); Assert.Equal("WireGuard Tunnel", saved.ChannelDescription);
            Assert.Equal("指定网卡：WireGuard Tunnel", Named<TextBlock>(window, "SavedChannelText").Text);
            Assert.Contains("active", ChannelButton(window, "WireGuard Tunnel").Classes);

            Click(window, Named<Button>(window, "ChannelAutoButton"));
            LayoutProbe.Settle();
            Assert.Equal(GameCaptureSettings.AutomaticChannel, backend.SavedCaptureSettings[^1].ChannelMode);
            Assert.False(Named<TextBlock>(window, "SavedChannelText").IsVisible);

            // 列出网卡后再点一次收起列表。
            Assert.True(Named<ItemsControl>(window, "CaptureChannelList").IsVisible);
            Click(window, Named<Button>(window, "ListChannelsButton"));
            LayoutProbe.Settle();
            Assert.False(Named<ItemsControl>(window, "CaptureChannelList").IsVisible);
            Click(window, Named<Button>(window, "ListChannelsButton"));
            LayoutProbe.Settle();
            Assert.True(Named<ItemsControl>(window, "CaptureChannelList").IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DetectionShowsTrafficPerChannelAndConfirmingSavesTheDetectedChannel()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend { Detecting = true };
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            Assert.StartsWith("正在检测 6 / 20 秒", Named<TextBlock>(window, "DetectionText").Text);
            Assert.True(Named<Button>(window, "DetectionCancelButton").IsVisible);
            Assert.False(Named<Button>(window, "DetectionConfirmButton").IsVisible);
            Click(window, Named<Button>(window, "DetectionCancelButton"));
            LayoutProbe.Settle();
            Assert.False(backend.Detecting);

            Click(window, Named<Button>(window, "DetectionStartButton"));
            LayoutProbe.Settle();
            Assert.StartsWith("检测完成：WireGuard Tunnel 承载游戏数据", Named<TextBlock>(window, "DetectionText").Text);
            var rows = Named<ItemsControl>(window, "DetectionList").Items.OfType<DetectionRow>().ToList();
            Assert.Equal(3, rows.Count); Assert.True(rows[2].Winner); Assert.StartsWith("512 个包", rows[2].Traffic);
            Click(window, Named<Button>(window, "DetectionConfirmButton"));
            LayoutProbe.Settle();
            var saved = Assert.Single(backend.SavedCaptureSettings);
            Assert.Equal(GameCaptureSettings.ManualChannel, saved.ChannelMode);
            Assert.Equal("3c5e7a9b1d2f44c6a8e0b2d4f6a8c0e2", saved.ChannelId);
            // 保存后在手动调试区写明已切换的网卡，并显示刷新后的指定网卡。
            Assert.Equal("已改用网卡：WireGuard Tunnel。抓包将切换到该网卡，状态见上方。", Named<TextBlock>(window, "DetectionMessageText").Text);
            Assert.Equal("指定网卡：WireGuard Tunnel", Named<TextBlock>(window, "SavedChannelText").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarShowsCaptureMapAndChannelWhileTheCombatPageDoesNot()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var running = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            // 地图为游戏原文，频道号取客户端频道序号；DPS检测页不再重复显示抓包状态、地图与频道。
            Assert.Equal(("杜巴頓", "频道 5"), (Named<TextBlock>(running, "MapText").Text, Named<TextBlock>(running, "ChannelText").Text));
            var page = running.GetVisualDescendants().OfType<CombatPage>().Single();
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<Control>(),
                control => control.Name is "CaptureNotice" or "CaptureStatusText" or "MapTag" or "ChannelTag");
        }
        finally { running.Close(); }
        Assert.Equal(("巴里2礦區地下城2層3區", "副本频道"), (CaptureText.Place(new(497513297, "巴里2礦區地下城2層3區", 1, null, false, null, "location.channelNotPermanent")).Map,
            CaptureText.Place(new(497513297, "巴里2礦區地下城2層3區", 1, null, false, null, "location.channelNotPermanent")).Channel));

        var window = LayoutProbe.Open(new SampleBackend { CaptureState = "faulted", CaptureReason = "observation.channelAmbiguous" }, 1280, 860, "combat-observation");
        try
        {
            // 抓包未运行时地图与频道为“未知”，左上角写明未抓包的原因，按下前往设置页抓包区。
            Assert.StartsWith("未抓包：多张网卡有游戏连接", Named<TextBlock>(window, "CaptureLineText").Text);
            Assert.Equal(("未知", "未知"), (Named<TextBlock>(window, "MapText").Text, Named<TextBlock>(window, "ChannelText").Text));
            Click(window, Named<Button>(window, "CaptureChip"));
            LayoutProbe.Settle();
            Assert.Single(window.GetVisualDescendants().OfType<SettingsPage>());
            Assert.Empty(window.GetVisualDescendants().OfType<CombatPage>());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ResidentCaptureFailingForMissingNpcapShowsTheGuideOnceAndOtherReasonsDoNot()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend { CaptureState = "faulted", CaptureReason = "observation.npcapMissing", NpcapState = "missing" };
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        try
        {
            // 自动开始的常驻抓包因 Npcap 缺失失败：弹出安装指引，给出官方下载页。
            Assert.True(Named<Panel>(window, "NpcapGuideLayer").IsVisible);
            Assert.Equal("https://npcap.com/#download", Named<TextBlock>(window, "NpcapGuideUrl").Text);
            Click(window, Named<Button>(window, "NpcapGuideLater"));
            LayoutProbe.Settle();
            Assert.False(Named<Panel>(window, "NpcapGuideLayer").IsVisible);

            // 退避重试仍失败：同一次运行不再弹出；抓包状态写明原因，按下打开指引而不是前往设置页。
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.False(Named<Panel>(window, "NpcapGuideLayer").IsVisible);
            Assert.StartsWith("未抓包：未安装 Npcap", Named<TextBlock>(window, "CaptureLineText").Text);
            Click(window, Named<Button>(window, "CaptureChip"));
            LayoutProbe.Settle();
            Assert.True(Named<Panel>(window, "NpcapGuideLayer").IsVisible);
            Assert.Empty(window.GetVisualDescendants().OfType<SettingsPage>());
        }
        finally { window.Close(); }

        // Npcap 无法加载、其他原因或 Npcap 状态未读取时不弹出。
        foreach (var other in new[]
                 {
                     new SampleBackend { CaptureState = "faulted", CaptureReason = "observation.npcapUnavailable" },
                     new SampleBackend { CaptureState = "faulted", CaptureReason = "observation.channelNotFound", NpcapState = null }
                 })
        {
            var shell = new ShellViewModel(BuiltInModules.CreateCatalog(other), other);
            try { Assert.False(shell.NpcapGuide.IsOpen); }
            finally { shell.Dispose(); }
        }
    }

    [AvaloniaFact]
    public void ManualCaptureStartsShowTheNpcapGuideEveryTimeWithoutSendingTheRequest()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var window = LayoutProbe.Open(new SampleBackend { CaptureState = "stopped", NpcapState = "missing" }, 1280, 860, "shell.settings");
        try
        {
            Assert.False(Named<Panel>(window, "NpcapGuideLayer").IsVisible);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Click(window, Named<Button>(window, "ListChannelsButton"));
                LayoutProbe.Settle();
                Assert.True(Named<Panel>(window, "NpcapGuideLayer").IsVisible);
                Assert.False(Named<ItemsControl>(window, "CaptureChannelList").IsVisible);
                Assert.StartsWith("未安装 Npcap", Named<TextBlock>(window, "CaptureMessageText").Text);
                Click(window, Named<Button>(window, "NpcapGuideLater"));
                LayoutProbe.Settle();
                Assert.False(Named<Panel>(window, "NpcapGuideLayer").IsVisible);
            }

            // 重新开启自动抓包也属于手动开始。
            Click(window, Named<Button>(window, "CaptureOffButton"));
            LayoutProbe.Settle();
            Click(window, Named<Button>(window, "CaptureOnButton"));
            LayoutProbe.Settle();
            Assert.True(Named<Panel>(window, "NpcapGuideLayer").IsVisible);
        }
        finally { window.Close(); }
    }
}
