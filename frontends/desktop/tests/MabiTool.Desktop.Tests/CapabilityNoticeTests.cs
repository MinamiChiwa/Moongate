using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Overlay;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.Desktop.Pages.Settings;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>能力与展示主题的不可用说明：映射文字、两种语言，以及合奏、染色、背包页标题区的说明与不占位。</summary>
public sealed class CapabilityNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public CapabilityNoticeTests()
    {
        if (UiText.Language != UiLanguage.SimplifiedChinese && Avalonia.Application.Current is { } application)
            UiText.Apply(application, UiLanguage.SimplifiedChinese);
    }

    private static CapabilitySnapshot Capability(string id, CapabilityStatus status, string reason) =>
        new(id, "performance", status, null, null, null, null, reason, Now, []);

    [AvaloniaFact]
    public void NotImplementedCapabilityExplainsItself()
    {
        Assert.Equal("台服暂不支持在游戏内选曲演奏",
            CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Unavailable, "module.notImplemented")));
        Assert.Equal("后端尚未实现，暂不可用",
            CapabilityNotice.Describe(Capability("rooms.coordinate", CapabilityStatus.Unavailable, "module.notImplemented")));
    }

    [AvaloniaFact]
    public void PlayCapabilityAppendsOtherReasonCodes()
    {
        Assert.Equal("台服暂不支持在游戏内选曲演奏（provider.unavailable）",
            CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Unavailable, "provider.unavailable")));
    }

    [AvaloniaFact]
    public void OtherReasonCodesAreShownVerbatim()
    {
        Assert.Equal("暂不可用：game.validationRequired",
            CapabilityNotice.Describe(Capability("color.search", CapabilityStatus.Unavailable, "game.validationRequired")));
    }

    [AvaloniaFact]
    public void AvailableUnknownOrMissingCapabilitiesHaveNoNotice()
    {
        Assert.Null(CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Available, "available")));
        Assert.Null(CapabilityNotice.Describe(Capability("inventory.full", CapabilityStatus.Unknown, "inventory.layoutNotPrepared")));
        Assert.Null(CapabilityNotice.Describe(null));
    }

    [AvaloniaFact]
    public void NoticeFollowsTheInterfaceLanguage()
    {
        try
        {
            UiText.Apply(Avalonia.Application.Current!, UiLanguage.TraditionalChinese);
            Assert.Equal("台服暫不支援在遊戲內選曲演奏",
                CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Unavailable, "module.notImplemented")));
            Assert.Equal("台服暫不支援在遊戲內選曲演奏（provider.unavailable）",
                CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Unavailable, "provider.unavailable")));
            Assert.Equal("後端尚未實作，暫不可用",
                CapabilityNotice.Describe(Capability("rooms.coordinate", CapabilityStatus.Unavailable, "module.notImplemented")));
            Assert.Equal("暫不可用：storage.unavailable",
                CapabilityNotice.Describe(Capability("inventory.full", CapabilityStatus.Unavailable, "storage.unavailable")));
        }
        finally { UiText.Apply(Avalonia.Application.Current!, UiLanguage.SimplifiedChinese); }
    }

    [AvaloniaFact]
    public void TopicNoticeMapsOnlyUnavailableStatuses()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var unsupported = TopicNoticeText.Describe(new("nearby", CapabilityStatus.Unavailable, "provider.unavailable"))!;
        Assert.True(unsupported.PlatformUnsupported);
        Assert.Equal("台服暂不支持", unsupported.Text);
        var other = TopicNoticeText.Describe(new("environment", CapabilityStatus.Unavailable, "provider.unconfigured"))!;
        Assert.False(other.PlatformUnsupported);
        Assert.Equal("暂不可用：provider.unconfigured", other.Text);
        foreach (var status in new[] { CapabilityStatus.Available, CapabilityStatus.Degraded, CapabilityStatus.Unknown })
            Assert.Null(TopicNoticeText.Describe(new("nearby", status, "provider.unavailable")));
        Assert.Null(TopicNoticeText.Describe(null));
    }

    private static string? NoticeFor(string id) => id is CapabilityIds.PerformancePlay or CapabilityIds.RoomsCoordinate
        or CapabilityIds.ColorSearch or CapabilityIds.InventoryFull
        ? CapabilityNotice.Describe(new(id, "module", CapabilityStatus.Unavailable, null, null, null, null, "module.notImplemented",
            DateTimeOffset.UnixEpoch, []))
        : null;

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese)]
    [InlineData(UiLanguage.TraditionalChinese)]
    public void UnavailableCapabilityShowsTheNoticeOnEachPage(UiLanguage language)
    {
        var window = LayoutProbe.Open(new SampleBackend { CapabilityNotices = NoticeFor }, 1280, 860, null, language);
        try
        {
            foreach (var module in new[] { "music-library", "rooms", "color", "inventory" })
            {
                LayoutProbe.Select(window, module);
                // 乐谱库的说明在“游戏内演奏”层里，先打开层。
                if (module == "music-library")
                {
                    ((MusicPageModel)Page<MusicPage>(window).DataContext!).OpenGameCommand.Execute(null);
                    LayoutProbe.Settle();
                }
                var notice = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "CapabilityNotice").ToList();
                Assert.Single(notice);
                Assert.True(notice[0].IsEffectivelyVisible, module);
                var expected = module == "music-library"
                    ? language == UiLanguage.TraditionalChinese ? "台服暫不支援在遊戲內選曲演奏" : "台服暂不支持在游戏内选曲演奏"
                    : language == UiLanguage.TraditionalChinese ? "後端尚未實作，暫不可用" : "后端尚未实现，暂不可用";
                Assert.Equal(expected, notice[0].Text);
            }
        }
        finally
        {
            window.Close();
            ResetAppearance();
        }
    }

    [AvaloniaTheory]
    [InlineData("rooms")]
    [InlineData("color")]
    [InlineData("inventory")]
    public void AvailableCapabilityLeavesNoNoticeOrGap(string module)
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, module);
        try
        {
            var notice = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "CapabilityNotice");
            Assert.False(notice.IsVisible);
            Assert.Equal(0, notice.Bounds.Height);
        }
        finally { window.Close(); }
    }
}
