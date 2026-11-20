using MabiTool.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Diagnostics;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>诊断页：键值换算、探针显示数据、记录筛选与续读、采集表单与权限、导出前的确认、实时记录。</summary>
public sealed class DiagnosticsTests
{
    private static DiagnosticsPageModel Model(SampleBackend backend, Func<DateTimeOffset>? clock = null)
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        return new DiagnosticsPageModel(backend, clock);
    }

    private static DiagnosticRecordView Pushed(int sequence, string session = "0123456789abcdef0123456789abcdef") => new(session,
        new DateTimeOffset(2026, 11, 13, 9, 10, 0, TimeSpan.Zero).AddSeconds(sequence), "info", "core", null,
        "diagnostics.capture.started", null, [], (1000 + sequence).ToString(), null);

    [AvaloniaTheory]
    [InlineData("uptimeSeconds", "45", "45 秒")]
    [InlineData("uptimeSeconds", "3725", "62 分 5 秒")]
    [InlineData("uptimeSeconds", "120", "2 分")]
    [InlineData("frames.assembleMs", "37", "37 毫秒")]
    [InlineData("frames.assembleMs", "1500", "1.5 秒")]
    [InlineData("memoryBytes", "512", "512 字节")]
    [InlineData("memoryBytes", "1536", "1.5 KB")]
    [InlineData("memoryBytes", "186646528", "178 MB")]
    [InlineData("packets.received", "48213", "48213")]
    [InlineData("revision", "12", "12")]
    [InlineData("someFutureMs", "not-a-number", "not-a-number")]
    public void CountersAreConvertedBySuffixAndOtherKeysStayRaw(string key, string raw, string expected)
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        Assert.Equal(expected, DiagnosticsText.Value(key, raw));
    }

    [AvaloniaFact]
    public void UnknownAndUnavailableProbesAreShownWithoutAffectingTheOthers()
    {
        var model = Model(new SampleBackend());
        var blocks = model.Runtime.Blocks.ToDictionary(block => block.Id);

        var unavailable = blocks["music"];
        Assert.True(unavailable.Unavailable);
        Assert.Equal("该探针读取出错（system.unexpectedError）", unavailable.ReasonLine);
        Assert.True(unavailable.NoRows);
        Assert.All(blocks.Where(pair => pair.Key != "music").Select(pair => pair.Value), block => Assert.False(block.Unavailable));

        // 后端以后增加的探针与键照常显示：键原样、按后缀换算、其余原样，且按键排序。
        var unknown = blocks["future.probe-with-a-long-identifier"];
        Assert.Equal(
            ["anotherBytes=1.5 MB", "someNewCounterMs=90.06 秒", "someVeryLongKeyNameThatKeepsGoingAndGoing.withNested.parts=a-short-code-value-that-is-also-long-enough-to-wrap", "unknownNumber=42"],
            unknown.Rows.Select(row => $"{row.Label}={row.Value}"));

        // 映射不到文案的原因码带短句或原样显示；被截断与丢弃的条目写在探针上，被省略的探针写在顶部。
        Assert.Equal("未设置按键（input.actionMappingUnavailable）", blocks["rooms"].ReasonLine);
        Assert.Equal("2 项因数量上限被截断，1 项不符合白名单被丢弃", blocks["rooms"].Notice);
        Assert.Equal("1 个探针因数量上限被省略，2 个探针因标识无效或重复被省略", model.Runtime.Notice);
        Assert.Contains("epoch-7f3a", model.Runtime.Header);
        Assert.Equal("该探针读取出错", DiagnosticsText.Reason("system.unexpectedError").Split('（')[0]);
        Assert.Equal("未知原因（some.unmapped.code）", DiagnosticsText.Reason("some.unmapped.code"));
    }

    [AvaloniaFact]
    public async Task CopyAllPutsTheWholeSnapshotAsPlainTextOnTheClipboard()
    {
        var model = Model(new SampleBackend());
        string? copied = null;
        model.Runtime.ClipboardWriter = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        await model.Runtime.CopyAllAsync();

        Assert.True(model.Runtime.Copied);
        Assert.NotNull(copied);
        Assert.Contains("backendEpoch: epoch-7f3a", copied);
        Assert.Contains("[music] module=music-library state=unavailable reason=system.unexpectedError", copied);
        Assert.Contains("  someNewCounterMs: 90061", copied);
        Assert.Contains("  state: bound", copied);
    }

    [AvaloniaFact]
    public async Task RuntimeReadFailureKeepsTheLastSnapshotAndSaysSo()
    {
        var backend = new SampleBackend();
        var model = Model(backend);
        var count = model.Runtime.Blocks.Count;
        backend.RuntimeFailure = "http.503";
        await model.Runtime.RefreshAsync();

        Assert.Equal(count, model.Runtime.Blocks.Count);
        // 错误码经共用映射写成短句，原码不上界面。
        Assert.Equal("请求未完成：后端暂时不可用，以下是上一次读取的结果", model.Runtime.Error);
    }

    [AvaloniaFact]
    public async Task RecordsAreFilteredAndPagedByTheBackendCursor()
    {
        var backend = new SampleBackend { SampleRecordTotal = 230 };
        var model = Model(backend);
        var records = model.Records;

        Assert.Empty(backend.RecordQueries[0].Modules);
        Assert.Empty(backend.RecordQueries[0].Levels);
        Assert.Null(backend.RecordQueries[0].Cursor);
        Assert.Equal(100, backend.RecordQueries[0].Limit);
        Assert.Equal(DiagnosticRecordFilter.Descending, backend.RecordQueries[0].Order);
        Assert.Equal(100, records.Rows.Count);
        Assert.True(records.HasMore);
        // 从新到旧：最新的一条（序号 230）在最上面。
        Assert.Equal("230", records.Rows[0].Record.Sequence);
        Assert.True(records.Rows[0].Record.OccurredAt > records.Rows[^1].Record.OccurredAt);

        // “更多”带同一顺序与 d- 开头的游标，继续往旧的读。
        await records.MoreAsync();
        Assert.Equal(("d-100", DiagnosticRecordFilter.Descending), (backend.RecordQueries[^1].Cursor, backend.RecordQueries[^1].Order));
        Assert.Equal(200, records.Rows.Count);
        await records.MoreAsync();
        Assert.Equal(230, records.Rows.Count);
        Assert.Equal("1", records.Rows[^1].Record.Sequence);
        Assert.False(records.HasMore);

        // 改筛选回到第一页，列表内为“或”。
        records.ModuleChoices.Single(chip => chip.Id == "rooms").ToggleCommand.Execute(null);
        records.ModuleChoices.Single(chip => chip.Id == "core").ToggleCommand.Execute(null);
        records.LevelChoices.Single(chip => chip.Id == "error").ToggleCommand.Execute(null);
        var last = backend.RecordQueries[^1];
        Assert.Equal(["core", "rooms"], last.Modules.Order());
        Assert.Equal(["error"], last.Levels);
        Assert.Null(last.Cursor);
        Assert.All(records.Rows, row => Assert.Equal("error", row.Level));
        Assert.All(records.Rows, row => Assert.Equal("rooms", row.Module));
    }

    [AvaloniaFact]
    public async Task CursorFromTheOtherOrderIsRejected()
    {
        var backend = new SampleBackend { SampleRecordTotal = 230 };
        var reply = await backend.QueryDiagnosticRecordsAsync(new(null, [], [], "100", 100, Order: DiagnosticRecordFilter.Descending),
            TestContext.Current.CancellationToken);
        Assert.Equal((false, "request.invalid"), (reply.Ok, reply.Message));
    }

    [Fact]
    public void LegacyCollapsedListsMigrateIntoCardStatesAndSavedCardStatesWin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moongate-preferences-{Guid.NewGuid():N}.json");
        try
        {
            // 旧版：诊断页只记收起的分区；合奏页记完整的收起列表（这里用户展开了“执行记录与诊断”、收起了“本机动作”）。
            File.WriteAllText(path, """{ "DiagnosticsCollapsed": ["runtime"], "RoomsCollapsed": ["local"] }""");
            var cards = new DesktopPreferenceStore(path).Load().Cards!;
            Assert.Equal(new CardState(false), cards["diagnostics/runtime"]);
            Assert.False(cards.ContainsKey("diagnostics/export"));
            Assert.Equal(new CardState(false), cards["rooms/local"]);
            Assert.Equal(new CardState(true), cards["rooms/diagnostics"]);
            Assert.Equal(new CardState(true), cards["rooms/status"]);

            // 新版保存后只写 Cards；同一张卡片两处都有时以 Cards 为准。
            new DesktopPreferenceStore(path).Save(DesktopPreferences.Default with { Cards = cards });
            var saved = File.ReadAllText(path);
            Assert.DoesNotContain("\"runtime\"", saved);
            File.WriteAllText(path, """{ "Cards": { "diagnostics/runtime": { "Expanded": true } }, "DiagnosticsCollapsed": ["runtime"] }""");
            Assert.Equal(new CardState(true), new DesktopPreferenceStore(path).Load().Cards!["diagnostics/runtime"]);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void SectionsFollowTheExportCaptureRuntimeRecordsOrderAndCollapseIntoPreferences()
    {
        CardStateStore.Restore(null);
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.diagnostics");
        var path = Path.Combine(Path.GetTempPath(), $"moongate-preferences-{Guid.NewGuid():N}.json");
        try
        {
            var panels = new[] { "ExportPanel", "CapturePanel", "RuntimePanel", "RecordsPanel" }
                .Select(name => Named<Card>(window, name).TranslatePoint(default, window)!.Value.Y).ToList();
            Assert.Equal(panels.Order(), panels);

            // 点标题行收起运行状态：内容与操作区隐藏，标题仍在，状态记入卡片状态并随偏好文件保存。
            var toggle = Named<Card>(window, "RuntimePanel").Toggle;
            Assert.True(Named<ItemsControl>(window, "ProbeList").IsEffectivelyVisible);
            Click(window, toggle);
            Assert.False(toggle.IsChecked);
            Assert.False(Named<ItemsControl>(window, "ProbeList").IsEffectivelyVisible);
            Assert.False(Named<Button>(window, "CopyAllButton").IsEffectivelyVisible);
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Equal(["diagnostics/runtime"], CardStateStore.All.Keys);
            Assert.False(CardStateStore.IsExpanded("diagnostics", "runtime"));
            var expand = (Avalonia.Automation.Provider.IExpandCollapseProvider)
                Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(toggle);
            Assert.Equal(Avalonia.Automation.ExpandCollapseState.Collapsed, expand.ExpandCollapseState);

            new DesktopPreferenceStore(path).Save(DesktopPreferences.Current);
            Assert.Equal(new CardState(false), Assert.Single(new DesktopPreferenceStore(path).Load().Cards!).Value);

            // 无障碍接口展开同样写回卡片状态。
            expand.Expand();
            LayoutProbe.Settle();
            Assert.True(CardStateStore.IsExpanded("diagnostics", "runtime"));
            Assert.True(Named<ItemsControl>(window, "ProbeList").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            CardStateStore.Restore(null);
            File.Delete(path);
        }
    }

    [AvaloniaTheory]
    [InlineData("0")]
    [InlineData("601")]
    [InlineData("abc")]
    [InlineData("")]
    public void CaptureFormRejectsOutOfRangeDurations(string duration)
    {
        var backend = new SampleBackend();
        var capture = Model(backend).Capture;
        Assert.True(capture.CanStart);
        capture.Duration = duration;
        Assert.True(capture.DurationInvalid);
        Assert.False(capture.CanStart);
        capture.StartCommand.Execute(null);
        Assert.Empty(backend.CaptureStarts);
    }

    [AvaloniaFact]
    public async Task CaptureNeedsAModuleAndSendsTheFormAsEntered()
    {
        var backend = new SampleBackend();
        var capture = Model(backend).Capture;
        Assert.Equal(7, capture.ModuleChoices.Count(chip => chip.Selected));
        foreach (var chip in capture.ModuleChoices) chip.ToggleCommand.Execute(null);
        Assert.True(capture.NoModuleSelected);
        Assert.False(capture.CanStart);

        capture.ModuleChoices.Single(chip => chip.Id == "rooms").ToggleCommand.Execute(null);
        capture.SetLevelCommand.Execute("trace");
        capture.ToggleDetailCommand.Execute(null);
        capture.Duration = "120";
        await capture.StartAsync();

        var request = Assert.Single(backend.CaptureStarts);
        Assert.Equal(["rooms"], request.Modules);
        Assert.Equal("trace", request.Level);
        Assert.Equal(120, request.DurationSeconds);
        Assert.True(request.IncludeDetail);
        Assert.True(capture.Active);
        Assert.False(capture.ShowForm);
    }

    [AvaloniaFact]
    public async Task StartingWhileAnotherCaptureRunsShowsTheActiveCaptureMessage()
    {
        var backend = new SampleBackend();
        var capture = Model(backend).Capture;
        backend.SampleCapture = backend.SampleCapture! with { Id = "capture-other", State = "active", EndReason = null, EndedAt = null };
        await capture.StartAsync();

        Assert.Equal("已有采集在进行", capture.Message);
        Assert.True(capture.Active);
        Assert.Equal("capture-other", capture.Current!.Id);
    }

    [AvaloniaFact]
    public async Task StoppingShowsTheEndReasonAndKeepsTheCounts()
    {
        var backend = new SampleBackend();
        var capture = Model(backend).Capture;
        await capture.StartAsync();
        await capture.StopAsync();

        Assert.False(capture.Active);
        Assert.Equal("已结束，原因：手动结束", capture.StateText);
        Assert.Equal("到期", DiagnosticsText.EndReason("expired"));
        Assert.Equal("宿主退出", DiagnosticsText.EndReason("hostStopping"));
        Assert.Equal("宿主重启", DiagnosticsText.EndReason("restarted"));
    }

    [AvaloniaFact]
    public void CaptureBlockIsHiddenWithoutTheDebugPermissionOrWhenTheBackendRefuses()
    {
        var denied = new SampleBackend { DiagnosticDebugAllowed = false, DiagnosticExportAllowed = false };
        var window = LayoutProbe.Open(denied, 1280, 860, "shell.diagnostics");
        Assert.False(Named<Card>(window, "CapturePanel").IsEffectivelyVisible);
        Assert.False(Named<Card>(window, "ExportPanel").IsEffectivelyVisible);
        Assert.True(Named<Card>(window, "RuntimePanel").IsEffectivelyVisible);
        Assert.Empty(denied.CaptureStarts);
        window.Close();

        // 握手权限显示有权限但实际被 403 拒绝：读取当前采集后隐藏。
        var refused = new SampleBackend { CaptureForbidden = true };
        var model = Model(refused);
        Assert.False(model.ShowCapture);
        Assert.True(model.ShowExport);
    }

    [AvaloniaFact]
    public async Task ExportOfTheLatestCaptureWithoutDetailSavesDirectly()
    {
        var backend = new SampleBackend();
        var export = Model(backend).Export;
        string? saved = null;
        export.FileSaver = (name, content) =>
        {
            saved = name;
            return Task.FromResult<string?>(name);
        };
        await export.ExportAsync();

        Assert.False(export.ConfirmOpen);
        Assert.Equal((DiagnosticExportScope.LatestCapture, backend.SampleCapture!.SessionId), backend.DiagnosticExports.Single());
        Assert.Equal("moongate-diagnostics-sample.json", saved);
        Assert.StartsWith("已保存", export.Message);
    }

    [AvaloniaFact]
    public async Task ExportWithTextDetailAsksFirstAndWritesNothingUntilConfirmed()
    {
        var backend = new SampleBackend();
        backend.SampleCapture = backend.SampleCapture! with { IncludeDetail = true };
        var export = Model(backend).Export;
        var saves = 0;
        export.FileSaver = (name, content) =>
        {
            saves++;
            return Task.FromResult<string?>(name);
        };

        await export.ExportAsync();
        Assert.True(export.ConfirmOpen);
        Assert.Empty(backend.DiagnosticExports);
        Assert.Contains("文字详情", export.ConfirmText);

        export.CancelConfirmCommand.Execute(null);
        Assert.False(export.ConfirmOpen);
        Assert.Empty(backend.DiagnosticExports);
        Assert.Equal(0, saves);

        await export.ExportAsync();
        export.ConfirmCommand.Execute(null);
        Assert.Single(backend.DiagnosticExports);
        Assert.Equal(1, saves);
    }

    [AvaloniaFact]
    public async Task ExportOfAllSessionsAsksBeforeSavingWhenTheDocumentContainsDetail()
    {
        var backend = new SampleBackend { AllSessionsContainDetail = true };
        var export = Model(backend).Export;
        var saves = 0;
        export.FileSaver = (name, content) =>
        {
            saves++;
            return Task.FromResult<string?>(name);
        };
        export.SelectAllCommand.Execute(null);

        await export.ExportAsync();
        Assert.Equal(DiagnosticExportScope.AllSessions, backend.DiagnosticExports.Single().Scope);
        Assert.True(export.ConfirmOpen);
        Assert.Equal(0, saves);

        export.CancelConfirmCommand.Execute(null);
        Assert.Equal(0, saves);
        await export.ExportAsync();
        export.ConfirmCommand.Execute(null);
        Assert.Equal(1, saves);
    }

    [AvaloniaFact]
    public async Task LiveRecordsKeepTheLatestFiveHundredAndIgnoreDuplicatesAndOtherCaptures()
    {
        var backend = new SampleBackend();
        var capture = Model(backend).Capture;
        await capture.StartAsync();
        var id = capture.Current!.Id;

        backend.PushDiagnosticRecord(id, Pushed(1));
        backend.PushDiagnosticRecord(id, Pushed(1));
        backend.PushDiagnosticRecord("capture-unrelated", Pushed(2));
        Assert.Single(capture.LiveRows);

        for (var sequence = 2; sequence <= 520; sequence++) backend.PushDiagnosticRecord(id, Pushed(sequence));
        Assert.Equal(DiagnosticsCaptureSection.MaxLive, capture.LiveRows.Count);
        Assert.Equal("1021", capture.LiveRows[0].Record.Sequence);
        Assert.Equal("1520", capture.LiveRows[^1].Record.Sequence);
    }

    [AvaloniaFact]
    public void ReconnectingFillsTheLiveListFromTheSessionQuery()
    {
        var backend = new SampleBackend();
        backend.SampleCapture = backend.SampleCapture! with { Id = "capture-running", State = "active", EndReason = null, EndedAt = null };
        var capture = Model(backend).Capture;
        Assert.Equal(14, capture.LiveRows.Count);
        backend.PushDiagnosticRecord("capture-running", Pushed(1));
        Assert.Equal(15, capture.LiveRows.Count);
        backend.RecordQueries.Clear();

        backend.RestoreDiagnosticStream();

        var query = Assert.Single(backend.RecordQueries);
        Assert.Equal(capture.Current!.SessionId, query.SessionId);
        Assert.Equal(500, query.Limit);
        Assert.NotNull(query.From);
        // 补取到的记录与已有记录按序号去重，按发生时间排序。
        Assert.Equal(15, capture.LiveRows.Count);
        Assert.Equal("1001", capture.LiveRows[0].Record.Sequence);
    }

    [AvaloniaFact]
    public void DiagnosticsEntryJoinsTheShellGroupAndOpensThePage()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860);
        var item = Nav(window, "shell.diagnostics");
        Click(window, item);
        var page = Page<DiagnosticsPage>(window);
        Assert.Contains(VisibleTexts(page), text => text == "future.probe-with-a-long-identifier");
        // 最近记录是虚拟化的表格：滚到表格处才生成可见的行。
        Named<MabiTool.Desktop.Controls.Tables.DataTable>(page, "RecordList").BringIntoView();
        LayoutProbe.Render(window);
        Assert.Contains(VisibleTexts(page), text => text == "observation.worker.faulted");
        window.Close();
    }

    [AvaloniaFact]
    public void ExportAndCaptureCardsAreShownDisabledWhileTheBackendIsNotConnected()
    {
        var backend = new SampleBackend { Offline = true };
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.diagnostics");
        try
        {
            foreach (var name in new[] { "ExportPanel", "CapturePanel" })
            {
                var card = Named<Card>(window, name);
                Assert.True(card.IsEffectivelyVisible);
                Assert.False(card.IsEffectivelyEnabled);
            }
            Assert.Empty(backend.CaptureStarts);
        }
        finally { window.Close(); }
    }
}
