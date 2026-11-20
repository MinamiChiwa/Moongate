using System.Net;
using System.Text.Json;
using Avalonia.Threading;
using MabiTool.Client;
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

/// <summary>全局快捷键：前端动作的补登记、冲突，以及 shortcut.triggered 的投递、去重、过期与断线丢弃。</summary>
public sealed class SettingsShortcutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly ShortcutChordSnapshot F8 = new("F8", false, false, false, false);

    [Fact]
    public async Task RegistrationAddsOnlyMissingSupportedActionsAndPreservesCustomBindings()
    {
        using var handler = new ShortcutHandler();
        using var api = Client(handler);
        await LiveBackend.RegisterFrontendShortcutsAsync(api, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Writes);
        Assert.Equal("F12", handler.Settings.Bindings.Single(x => x.ActionId == "emergency-stop").Chord.Key);
        Assert.Equal("F6", handler.Settings.Bindings.Single(x => x.ActionId == "overlay-toggle").Chord.Key);
        Assert.Equal("F7", handler.Settings.Bindings.Single(x => x.ActionId == "overlay-click-through-toggle").Chord.Key);
        Assert.True(handler.Settings.Bindings.Single(x => x.ActionId == "overlay-toggle").Chord.Control);
        await LiveBackend.RegisterFrontendShortcutsAsync(api, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Writes);
    }

    [Fact]
    public async Task UnsupportedActionsAreNotRegistered()
    {
        using var handler = new ShortcutHandler();
        handler.Settings = handler.Settings with { Actions = [new("emergency-stop", "core")] };
        using var api = Client(handler);
        await LiveBackend.RegisterFrontendShortcutsAsync(api, TestContext.Current.CancellationToken);
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task RevisionConflictIsReportedWithoutResubmittingOldBindings()
    {
        using var handler = new ShortcutHandler { Conflict = true };
        using var api = Client(handler);
        var error = await Assert.ThrowsAsync<MabiToolApiException>(() =>
            LiveBackend.RegisterFrontendShortcutsAsync(api, TestContext.Current.CancellationToken));
        Assert.Equal(412, error.Status);
        Assert.Equal(1, handler.Writes);
        Assert.Equal(2, handler.Settings.Bindings.Count);
    }

    [Fact]
    public async Task MeasurementToggleIsDeliveredButNotGivenADefaultKey()
    {
        using var handler = new ShortcutHandler();
        handler.Settings = handler.Settings with { Actions = [.. handler.Settings.Actions, new("combat-measurement-toggle", "combat-observation")] };
        using var api = Client(handler);
        await LiveBackend.RegisterFrontendShortcutsAsync(api, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(handler.Settings.Bindings, binding => binding.ActionId == "combat-measurement-toggle");

        var queue = new Queue<Action>();
        var actions = new List<string>();
        var notifications = new DesktopShortcutNotifications(queue.Enqueue, actions.Add, new TestClock());
        var session = notifications.Connect("epoch", CancellationToken.None);
        notifications.Receive(session, Message(1, "combat-measurement-toggle"));
        queue.Dequeue()();
        Assert.Equal(["combat-measurement-toggle"], actions);
    }

    [AvaloniaFact]
    public void MeasurementToggleShortcutStartsOrCancelsThroughTheBackend()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        try
        {
            backend.TriggerShortcut("combat-measurement-toggle");
            Assert.Contains(("combat.measure.toggle", (string?)null), backend.UiIntents);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "WaitingText" && text.IsEffectivelyVisible);
            backend.TriggerShortcut("combat-measurement-toggle");
            Assert.Equal(2, backend.UiIntents.Count(intent => intent.Intent == "combat.measure.toggle"));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void NotificationsWaitForUiAndDeduplicateAcrossReconnectsInSameEpoch()
    {
        var queue = new Queue<Action>();
        var actions = new List<string>();
        var clock = new TestClock();
        var notifications = new DesktopShortcutNotifications(queue.Enqueue, actions.Add, clock);
        var session = notifications.Connect("epoch", CancellationToken.None);
        notifications.Receive(session, Message(1));
        notifications.Receive(session, Message(1));
        Assert.Empty(actions);
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(["overlay-toggle"], actions);
        notifications.Disconnect(session);
        session = notifications.Connect("epoch", CancellationToken.None);
        notifications.Receive(session, Message(1));
        notifications.Receive(session, Message(2, "overlay-click-through-toggle"));
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(["overlay-toggle", "overlay-click-through-toggle"], actions);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("epoch")]
    [InlineData("action")]
    [InlineData("schema")]
    [InlineData("malformed")]
    public void InvalidNotificationsNeverReachUi(string kind)
    {
        var queue = new Queue<Action>();
        var notifications = new DesktopShortcutNotifications(queue.Enqueue, _ => Assert.Fail("不应执行"), new TestClock());
        var session = notifications.Connect("epoch", CancellationToken.None);
        // 紧急停止的通知现在送到前端做后续（取消定时测量与界面反馈），不在动作表里的动作仍被丢弃。
        var message = Message(1, kind == "action" ? "unknown-action" : "overlay-toggle",
            kind == "epoch" ? "old" : "epoch", kind == "expired" ? Now.AddSeconds(-2) : kind == "future" ? Now.AddSeconds(1) : Now,
            kind == "schema" ? 2 : 1);
        if (kind == "malformed") message = message with { Data = JsonSerializer.SerializeToElement(new { data = 5 }) };
        notifications.Receive(session, message);
        Assert.Empty(queue);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("disconnect")]
    [InlineData("cancel")]
    [InlineData("reconnect")]
    public void QueuedActionsAreCheckedAgainAtExecution(string kind)
    {
        var queue = new Queue<Action>();
        var actions = new List<string>();
        var clock = new TestClock();
        using var cancellation = new CancellationTokenSource();
        var notifications = new DesktopShortcutNotifications(queue.Enqueue, actions.Add, clock);
        var session = notifications.Connect("epoch", cancellation.Token);
        notifications.Receive(session, Message(1));
        if (kind == "expired") clock.Now = Now.AddSeconds(2);
        if (kind == "disconnect") notifications.Disconnect(session);
        if (kind == "cancel") cancellation.Cancel();
        if (kind == "reconnect") notifications.Connect("new", CancellationToken.None);
        queue.Dequeue()();
        Assert.Empty(actions);
    }

    [Fact]
    public void UiBacklogIsBoundedAndNewEpochStartsItsOwnWatermark()
    {
        var queue = new Queue<Action>();
        var actions = new List<string>();
        var notifications = new DesktopShortcutNotifications(queue.Enqueue, actions.Add, new TestClock());
        var session = notifications.Connect("epoch", CancellationToken.None);
        for (ulong i = 1; i <= 100; i++) notifications.Receive(session, Message(i));
        Assert.Equal(32, queue.Count);
        while (queue.TryDequeue(out var run)) run();
        Assert.Equal(32, actions.Count);
        session = notifications.Connect("new", CancellationToken.None);
        notifications.Receive(session, Message(1, epoch: "new"));
        queue.Dequeue()();
        Assert.Equal(33, actions.Count);
    }

    [AvaloniaFact]
    public async Task SdkStreamDeliversShortcutOnUiThreadAndDisconnectsOnCancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var handler = new EventHandler();
        using var api = Client(handler);
        await using var backend = new LiveBackend("unused", action => Dispatcher.UIThread.Post(action));
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.ShortcutTriggered += (_, action) =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            received.TrySetResult(action);
        };
        var stream = backend.ListenAsync(api, "epoch", cancel.Token);
        try
        {
            Assert.Equal("overlay-toggle", await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
        }
    }

    [AvaloniaFact]
    public async Task EmergencyStopNotificationIsHandledByTheBackendAdapterAndShowsFeedback()
    {
        // F9 由后端注册并执行；后端事后发来通知，前端写明结果，不交给外壳（外壳只分发浮窗与定时测量的动作）。
        using var cancel = new CancellationTokenSource();
        using var handler = new EventHandler("emergency-stop");
        using var api = Client(handler);
        await using var backend = new LiveBackend("unused", action => Dispatcher.UIThread.Post(action));
        var forwarded = new List<string>();
        backend.ShortcutTriggered += (_, action) => forwarded.Add(action);
        var stream = backend.ListenAsync(api, "epoch", cancel.Token);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (backend.GetRooms().Feedback is null)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20, limit.Token);
            }
            Assert.StartsWith("已紧急停止（快捷键）：游戏输入与自动操作已中止", backend.GetRooms().Feedback!.Text);
            Assert.Empty(forwarded);
        }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
        }
    }

    [AvaloniaFact]
    public async Task HostHotkeysF8AndF7ReachTheOverlayThroughTheRealStreamCursor()
    {
        // 宿主的 SSE id 是续传游标“epoch:序号:订阅绑定”（EventStreamEndpoints 写 subscription.Cursor），信封 eventId 是“epoch:序号”。
        // 此前前端要求两者相等，真实宿主发来的 F8、F7 通知全部被丢弃；F9 由宿主直接执行，所以只有它有效。
        using var cancel = new CancellationTokenSource();
        using var handler = new EventHandler(["overlay-toggle", "overlay-click-through-toggle"], cursorSuffix: ":3f9c2a");
        using var api = Client(handler);
        await using var backend = new LiveBackend("unused", action => Dispatcher.UIThread.Post(action));
        var window = LayoutProbe.Open(backend, 1280, 860);
        var overlay = ((MabiTool.Desktop.Shell.ShellViewModel)window.DataContext!).Catalog.Overlay!;
        var lockedBefore = overlay.Store.AllLocked;
        var stream = backend.ListenAsync(api, "epoch", cancel.Token);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!overlay.IsShown || overlay.Store.AllLocked == lockedBefore)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20, limit.Token);
            }
            Assert.True(overlay.IsShown);
            Assert.NotEqual(lockedBefore, overlay.Store.AllLocked);
        }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
            overlay.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EmergencyStopCancelsARunningMeasurementThroughTheCombatEntry()
    {
        // 定时测量等待首击或计时中：经战斗页已有的取消入口取消；空闲时不发。
        Assert.Equal(["combat.measure.cancel"], EmergencyStopFollowUp.Plan(EmergencyStopFollowUp.Hotkey, "running", null).Intents);
        Assert.Equal(["combat.measure.cancel"], EmergencyStopFollowUp.Plan(EmergencyStopFollowUp.Button, "waiting", null).Intents);
        Assert.Empty(EmergencyStopFollowUp.Plan(EmergencyStopFollowUp.Hotkey, "idle", null).Intents);
        var receipt = new GameEmergencyStopReceipt("epoch", false, Now, 2, 3, "released", true, EffectStatus.Unknown,
            [new("color", "game.activityCancellationFailed")]);
        var plan = EmergencyStopFollowUp.Plan(EmergencyStopFollowUp.Button, null, receipt);
        Assert.True(plan.Failed);
        Assert.Contains("撤销 2 个输入占用", plan.Text);
        Assert.Contains("染色：没能取消游戏操作", plan.Text);
        Assert.False(EmergencyStopFollowUp.Plan(EmergencyStopFollowUp.Button, null, receipt with { Failures = [] }).Failed);
    }

    private static StreamMessage Message(ulong sequence, string action = "overlay-toggle", string epoch = "epoch",
        DateTimeOffset? triggeredAt = null, int schema = 1)
    {
        var at = triggeredAt ?? Now;
        var envelope = new EventEnvelope<ShortcutTriggered>($"{epoch}:{sequence}", "shortcut.triggered", schema,
            "presentation", action, new(sequence), at, null, new(action, F8, at, at.AddSeconds(2)));
        return new("shortcut.triggered", envelope.EventId, JsonSerializer.SerializeToElement(envelope, ContractJson.Options));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = SettingsShortcutTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static MabiToolClient Client(HttpMessageHandler handler) =>
        new(new Uri("http://127.0.0.1:18543"), _ => ValueTask.FromResult("test-token"), handler);

    private sealed class EventHandler(string[] actions, string cursorSuffix = "") : HttpMessageHandler
    {
        public EventHandler(string action = "overlay-toggle") : this([action]) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("/api/v1/events", request.RequestUri!.AbsolutePath);
            var body = string.Concat(actions.Select((action, index) =>
            {
                var message = Message((ulong)index + 1, action, triggeredAt: DateTimeOffset.UtcNow);
                return $"id: {message.Id}{cursorSuffix}\nevent: {message.Event}\ndata: {message.Data.GetRawText()}\n\n";
            }));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new OnlineStream(System.Text.Encoding.UTF8.GetBytes(body)))
            };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(response);
        }
    }

    // 首帧之后保持在线，直到订阅取消；EOF 表示断线，会使尚未执行的动作失效。
    private sealed class OnlineStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class ShortcutHandler : HttpMessageHandler
    {
        public int Writes { get; private set; }
        public bool Conflict { get; init; }
        public ShortcutSettingsSnapshot Settings { get; set; } = new(new(7),
            [new("emergency-stop", "core"), new("overlay-toggle", "presentation"), new("overlay-click-through-toggle", "presentation")],
            [new("emergency-stop", new("F12", true, false, true, false)), new("overlay-toggle", new("F6", true, false, false, false))], []);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("/api/v1/system/shortcut-settings", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                Writes++;
                Assert.Equal("\"7\"", request.Headers.IfMatch.Single().Tag);
                Assert.Single(request.Headers.GetValues("Idempotency-Key"));
                if (Conflict) return new(HttpStatusCode.PreconditionFailed);
                var update = JsonSerializer.Deserialize<ShortcutSettingsRequest>(await request.Content!.ReadAsStringAsync(token), ContractJson.Options)!;
                Settings = Settings with { Revision = new(8), Bindings = update.Bindings };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(Settings, ContractJson.Options), System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.ETag = new("\"7\"");
            return response;
        }
    }
}
