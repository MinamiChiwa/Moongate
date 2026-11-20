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
        Assert.Equal("台服暂不支持游戏内选曲演奏",
            CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Unavailable, "module.notImplemented")));
        Assert.Equal("后端未实现，暂不可用",
            CapabilityNotice.Describe(Capability("rooms.coordinate", CapabilityStatus.Unavailable, "module.notImplemented")));
    }

    [AvaloniaFact]
    public void PlayCapabilityExplainsTheTaiwanServerForEveryReason()
    {
        Assert.Equal("台服暂不支持游戏内选曲演奏",
            CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Unavailable, "provider.unavailable")));
    }

    [AvaloniaFact]
    public void OtherReasonCodesAreShownAsText()
    {
        Assert.Equal("暂不可用：游戏版本未核验",
            CapabilityNotice.Describe(Capability("color.search", CapabilityStatus.Unavailable, "game.validationRequired")));
    }

    [AvaloniaFact]
    public void EveryKnownIdentifierHasTextInBothLanguagesAndUnknownOnesAreSummarized()
    {
        // 取值集合取自契约与引擎源码：错误码目录、各状态与阶段枚举（线上为短横线小写）、染色引擎发出的阶段与准备失败原因。
        static string Kebab<T>(T value) where T : struct, Enum => System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
        var reasons = ApiErrors.Definitions.Select(definition => definition.Code)
            .Concat(Enum.GetValues<PerformanceStopReason>().Select(Kebab)).ToList();
        var states = Enum.GetValues<OperationState>().Select(Kebab).Concat(Enum.GetValues<CapabilityStatus>().Select(Kebab))
            .Concat(Enum.GetValues<RoomServerState>().Select(Kebab)).Concat(Enum.GetValues<RoomTunnelState>().Select(Kebab))
            .Concat(Enum.GetValues<ObservationQuality>().Select(Kebab)).Concat(Enum.GetValues<ColorRegionState>().Select(Kebab))
            .Concat(Enum.GetValues<PerformanceRepeatMode>().Select(Kebab)).ToList();
        var phases = Enum.GetValues<PerformancePhase>().Select(Kebab).ToList();
        var engine = EngineValues();
        Assert.NotEmpty(engine.Stages);
        Assert.NotEmpty(engine.Unavailable);
        Missing(reasons.Where(code => !CodeText.TryReason(code, out _)));
        Missing(states.Where(code => !CodeText.KnownStates.Contains(code)));
        Missing(phases.Where(code => !CodeText.KnownPhases.Contains(code)));
        Missing(engine.Stages.Where(stage => !ColorStages.Known.Contains(stage) && ColorRunText.StageName(stage) == ColorStages.Name(stage)));
        try
        {
            foreach (var language in new[] { UiLanguage.SimplifiedChinese, UiLanguage.TraditionalChinese })
            {
                UiText.Apply(Application.Current!, language);
                foreach (var (codes, text) in new (IEnumerable<string>, Func<string, string>)[]
                         {
                             (CodeText.KnownOperations, code => CodeText.Operation(code)), (CodeText.KnownPhases, code => CodeText.Phase(code)),
                             (CodeText.KnownStates, CodeText.State), (CodeText.KnownReasons, CodeText.Reason), (CodeText.KnownModules, CodeText.Module),
                             (CodeText.KnownResources, CodeText.Resource), (CodeText.KnownCapabilities, CodeText.Capability),
                             (ColorStages.Known, ColorStages.Name)
                         })
                    foreach (var code in codes)
                        Assert.False(string.IsNullOrWhiteSpace(text(code)) || text(code).Contains(code, StringComparison.Ordinal), $"{language} {code} → {text(code)}");
            }
        }
        finally { UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese); }
        // 引擎的准备失败原因都有专门的说明，不落到“未知原因”。
        var before = CodeText.Unmapped.Count(item => item.StartsWith("color-", StringComparison.Ordinal));
        var session = new GameSession("s", "TW", 1, DateTimeOffset.UnixEpoch, new(500), new(1), null);
        var profile = new ColorProfile("p", 1, "方案", ColorStrategy.Precise, [], new(1));
        foreach (var reason in engine.Unavailable)
            ColorRunText.Describe(new ColorRun("run", "op", session, profile, "failed", 0, null, null, null, null, "unavailable")
                { Result = new ColorRunResult("run", 0, "stopped", "unavailable", null, null, null) { StopDetail = reason } });
        Assert.Equal(before, CodeText.Unmapped.Count(item => item.StartsWith("color-", StringComparison.Ordinal)));
        // 已登记的标识只显示中文；没有文案的标识只显示简短概括，原文记入诊断页的待补清单。
        Assert.Equal("染色寻色", CodeText.Operation("color.search"));
        Assert.Equal("正在启动", CodeText.Phase("starting"));
        Assert.Equal("已停止", CodeText.State("stopped"));
        Assert.Equal("后端内部出错", CodeText.Reason("http.502"));
        Assert.Equal("未知原因", CodeText.Reason("some.newCode"));
        Assert.Equal("未知阶段", ColorStages.Name("new_stage"));
        Assert.Contains("reason:some.newCode", CodeText.Unmapped);
        Assert.Contains("color-stage:new_stage", CodeText.Unmapped);
        // 英文异常消息不上界面；前端自己的中文说明照常显示。
        Assert.Equal("无法连接后端", CodeText.Error(new System.Net.Http.HttpRequestException("No connection could be made")));
        Assert.Equal("请求未完成", CodeText.Error(new InvalidOperationException("Sequence contains no elements")));
        Assert.Equal("未连接后端", CodeText.Error(new InvalidOperationException("未连接后端")));
        Assert.Null(CodeText.Readable("失败：System.IO.IOException"));
    }

    private static void Missing(IEnumerable<string> missing)
    {
        var list = missing.ToList();
        Assert.True(list.Count == 0, "没有文案：" + string.Join("、", list));
    }

    /// <summary>染色引擎源码里发出的阶段名与精准准备失败原因（service.unavailable_reason 的返回值）。</summary>
    private static (IReadOnlyList<string> Stages, IReadOnlyList<string> Unavailable) EngineValues()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
        var engine = Path.Combine(root!.FullName, "workers", "color-engine", "mabitool_color");
        var stages = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(engine, "*.py", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file),
                         @"(?:\bstage\s*=\s*|'stage'\s*:\s*)'([a-z_]+)'"))
                stages.Add(match.Groups[1].Value);
        var service = File.ReadAllText(Path.Combine(engine, "_vendor", "native_live", "service.py"));
        var body = service[service.IndexOf("def unavailable_reason", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\ndef ", 1, StringComparison.Ordinal)];
        var unavailable = System.Text.RegularExpressions.Regex.Matches(body, @"return '([a-z_]+)'").Select(match => match.Groups[1].Value).ToList();
        // 异常类自带的原因码（引擎更新、渲染尺寸、输入辅助、显示缩放等）同样作为细分原因到达界面。
        foreach (var file in Directory.EnumerateFiles(engine, "*.py", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file),
                         @"\bcapability_reason\s*=\s*'([A-Za-z_.]+)'"))
                unavailable.Add(match.Groups[1].Value);
        return (stages.ToList(), unavailable);
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
            Assert.Equal("台服暫不支援遊戲內選曲演奏",
                CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Unavailable, "module.notImplemented")));
            Assert.Equal("台服暫不支援遊戲內選曲演奏",
                CapabilityNotice.Describe(Capability("performance.play", CapabilityStatus.Unavailable, "provider.unavailable")));
            Assert.Equal("後端未實作，暫不可用",
                CapabilityNotice.Describe(Capability("rooms.coordinate", CapabilityStatus.Unavailable, "module.notImplemented")));
            Assert.Equal("暫不可用：儲存不可用",
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
        Assert.Equal("暂不可用：数据来源未配置", other.Text);
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
                    ? language == UiLanguage.TraditionalChinese ? "台服暫不支援遊戲內選曲演奏" : "台服暂不支持游戏内选曲演奏"
                    : language == UiLanguage.TraditionalChinese ? "後端未實作，暫不可用" : "后端未实现，暂不可用";
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
