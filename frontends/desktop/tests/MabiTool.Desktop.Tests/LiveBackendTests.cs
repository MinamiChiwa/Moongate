using System.Diagnostics;
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

/// <summary>对后端模拟宿主的联调：需要已构建的 MabiTool.Host，未构建时跳过。</summary>
[Trait("Category", "Slow")]
public sealed class LiveBackendTests
{
    [AvaloniaFact]
    public async Task ConnectsToMockHostObservesCombatAndRequestsEmergencyStop()
    {
        var hostDll = FindHostDll();
        Assert.SkipWhen(hostDll is null, "未找到已构建的 MabiTool.Host.dll");
        var (host, root) = await StartHostAsync(hostDll!);
        using var hostProcess = host;
        try
        {
            var changes = 0;
            await using var backend = new LiveBackend(Path.Combine(root, "runtime", "instance.json"),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
            backend.Changed += (_, _) => Interlocked.Increment(ref changes);

            // 宿主启动前：未连接，紧急停止不可用。
            Assert.False(backend.GetActivity().CanEmergencyStop);
            Assert.Equal(new CombatActions(false, false, false, false, false, false), backend.GetCombat().Actions);

            backend.Start();
            await Until(() => backend.GetCombat().Actions.CanStart, "连接模拟宿主并取得游戏绑定");
            Assert.True(backend.GetGameBinding().Confirmed);
            Assert.Contains("游戏窗口", backend.GetGameBinding().CharacterName);
            Assert.True(backend.GetActivity().CanEmergencyStop);

            // 能力表随页面状态读取：未实现的能力给出说明，可用或能力表没有的编号没有说明。
            await Until(() => backend.GetCapabilityNotice(CapabilityIds.PerformancePlay) is not null, "读取后端能力表");
            Assert.Equal("台服暂不支持在游戏内选曲演奏", backend.GetCapabilityNotice(CapabilityIds.PerformancePlay));
            Assert.Equal("后端尚未实现，暂不可用", backend.GetCapabilityNotice(CapabilityIds.RoomsCoordinate));
            Assert.StartsWith("暂不可用：", backend.GetCapabilityNotice(CapabilityIds.ColorSearch));
            Assert.Null(backend.GetCapabilityNotice("system.inspect"));
            Assert.Null(backend.GetCapabilityNotice("no.such.capability"));

            // 游戏内演奏不可用：开始演奏与下一首随能力停用，说明与能力表一致。
            using (var music = new Pages.Music.MusicPageModel(backend))
            {
                Assert.True(music.HasCapabilityNotice);
                Assert.Equal("台服暂不支持在游戏内选曲演奏", music.CapabilityNotice);
                Assert.False(music.CanStart);
                Assert.False(music.CanNext);
            }

            // 展示接口的 nearby、environment 主题在模拟宿主上缺失且原因为 provider.unavailable：
            // 依赖它们的浮窗元件在游戏浮窗里隐藏，编辑页保留并标注；其他主题没有说明。
            await Until(() => backend.GetPresentationNotice(PresentationTopics.Nearby) is not null, "读取展示接口主题状态");
            foreach (var topic in PresentationTopics.Watched)
            {
                var missing = backend.GetPresentationNotice(topic);
                Assert.NotNull(missing);
                Assert.True(missing.PlatformUnsupported);
                Assert.Equal("provider.unavailable", missing.ReasonCode);
                Assert.Equal("台服暂不支持", missing.Text);
            }
            Assert.Null(backend.GetPresentationNotice("room"));
            Assert.Null(backend.GetPresentationNotice("no.such.topic"));
            using (var widget = new Overlay.OverlayWidgetModel("performance.nearby", backend, PresentationTopics.Nearby))
            {
                Assert.False(widget.FrameVisible);
                widget.Editing = true;
                Assert.True(widget.FrameVisible);
                Assert.Equal("台服暂不支持", widget.NoticeText);
            }

            await backend.SendCombatIntentAsync(CombatIntent.StartObservation, null, TestContext.Current.CancellationToken);
            await Until(() => backend.GetCombat().Skills.Count > 0, "开始观测后出现技能统计");
            var combat = backend.GetCombat();
            Assert.DoesNotContain(combat.Kpis, tile => tile.Primary && tile.Value == "—");
            Assert.Equal(4, combat.Coverage.Count);

            // 真实契约数据下页面照常排版，并随后端状态刷新。
            var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
            var page = window.GetVisualDescendants().OfType<CombatPage>().Single();
            Assert.Equal(backend.GetCombat().Skills.Count, ((CombatPageModel)page.DataContext!).Rows.Count);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
            // 攻击特性六列、攻击特性块与元素构成随真实契约数据出现或隐藏：
            // 宿主给出这些计数时三处都显示，所有分组都没有时都不出现。
            var traitsKnown = backend.GetCombat().ShowTraitColumns;
            Assert.Equal(backend.GetCombat().ShowTraitColumns,
                page.GetVisualDescendants().OfType<MabiTool.Desktop.Controls.Tables.DataTable>().Single(table => table.Name == "DamageTable")
                    .VisibleColumns.Any(column => column.Id == "elements"));
            Assert.Equal(backend.GetCombat().Traits is { Count: > 0 },
                page.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "TraitTiles").IsEffectivelyVisible);
            Assert.Equal(backend.GetCombat().Elements is not null,
                page.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "ElementsGroup").IsEffectivelyVisible);
            if (!traitsKnown)
                Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text is "无防备" or "首击" or "普攻" or "多重打击" or "元素");
            LayoutProbe.SaveScreenshot(window, "combat-live-mock-1280x860");

            // 其余页面读取同一个后端：模块清单、系统信息和乐谱库来自真实接口，各页排版照常。
            await Until(() => backend.GetModules().Modules.Count > 0, "读取模块清单");
            Assert.Contains(backend.GetSettings().Backend, row => row.Value == "已连接");
            Assert.Contains(backend.GetSettings().Shortcuts, row => row.Id == "overlay-toggle" && row.Chord == "F8" && row.CanEdit);
            Assert.Contains(backend.GetSettings().Shortcuts, row => row.Id == "overlay-click-through-toggle" && row.Chord == "F7" && row.CanEdit);
            foreach (var module in new[] { "music-library", "rooms", "color", "inventory", "shell.modules", "shell.settings" })
            {
                LayoutProbe.Select(window, module);
                Assert.Empty(LayoutProbe.FindTextOverflow(window));
                LayoutProbe.SaveScreenshot(window, $"{module}-live-mock-1280x860");
            }

            // 迷你窗读同一个后端：清单、乐谱与活动区来自真实接口，紧急停止可用。
            var mini = new Shell.MiniWindow { DataContext = new Shell.MiniWindowModel(backend), Width = 440, Height = 920 };
            mini.Show();
            LayoutProbe.Settle();
            Assert.True(((Shell.MiniWindowModel)mini.DataContext!).Activity.CanEmergencyStop);
            Assert.True(mini.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "EmergencyStopButton").IsEffectivelyEnabled);
            Assert.Empty(LayoutProbe.FindTextOverflow(mini));
            LayoutProbe.SaveScreenshot(mini, "mini-live-mock-440x920");
            mini.Close();

            // 背包随全局常驻抓包自动同步，没有开始按钮；模拟宿主没有运行中的抓包，状态写明未读取及抓包原因。
            Assert.StartsWith("未读取 · ", backend.GetInventory().Status);
            window.Close();

            // 手动拆段：后端受理后给出说明；新分段要等下一笔伤害，状态行先显示等待，随后显示拆段后开始的分段。
            Assert.True(backend.GetCombat().Actions.CanSplit);
            await backend.SendCombatIntentAsync(CombatIntent.SplitEncounter, null, TestContext.Current.CancellationToken);
            Assert.Equal("已拆段，下一次伤害开始新分段", backend.GetActivity().Detail);
            await Until(() => backend.GetCombat().Status is var status
                && (status.Contains("已拆段，等待下一次伤害") || status.Contains("手动拆段后开始")), "拆段后状态行显示等待或新分段");

            await backend.RequestEmergencyStopAsync(TestContext.Current.CancellationToken);
            Assert.Contains("已请求紧急停止", backend.GetActivity().Detail);

            if (backend.GetCombat().Actions.CanStop)
            {
                await backend.SendCombatIntentAsync(CombatIntent.StopObservation, null, TestContext.Current.CancellationToken);
                await Until(() => !backend.GetCombat().Actions.CanStop, "停止观测");
            }
            await Until(() => backend.GetCombat().History.Count > 0, "战斗记录出现本场");
            Assert.True(changes > 0);
        }
        finally { await StopHostAsync(host, root); }
    }

    [AvaloniaFact]
    public async Task ActionsReachTheMockHost()
    {
        var hostDll = FindHostDll();
        Assert.SkipWhen(hostDll is null, "未找到已构建的 MabiTool.Host.dll");
        var (host, root) = await StartHostAsync(hostDll!);
        using var hostProcess = host;
        var token = TestContext.Current.CancellationToken;
        try
        {
            string? copied = null;
            await using var backend = new LiveBackend(Path.Combine(root, "runtime", "instance.json"),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action))
            {
                CopyText = text => { copied = text; return Task.CompletedTask; }
            };
            backend.Start();
            await Until(() => backend.GetModules().Modules.Count > 0, "连接模拟宿主并读取模块");
            string Notice() => backend.GetActivity().Detail;

            // 模块启停：状态由后端改变后再读回。
            var before = backend.GetModules().Modules.Single(module => module.Id == "inventory").Enabled;
            await backend.SendIntentAsync("modules.toggle", "inventory", token);
            await Until(() => backend.GetModules().Modules.Single(module => module.Id == "inventory").Enabled != before
                              || Notice().Contains("请求未完成"), "停用背包模块");
            Assert.DoesNotContain("请求未完成", Notice());
            await backend.SendIntentAsync("modules.toggle", "inventory", token);
            await Until(() => backend.GetModules().Modules.Single(module => module.Id == "inventory").Enabled == before, "恢复背包模块");

            // 演奏队列：建立会话、加入、开始、下一首、移除、停止，都由后端的队列状态机决定结果。
            await Until(() => backend.GetMusic().Actions.CanEnqueue, "取得游戏绑定");
            await backend.SendIntentAsync("music.enqueue", "score-a", token);
            await Until(() => backend.GetMusic().Queue.Count == 1 || Notice().Contains("请求未完成"), "队列出现第一首");
            Assert.DoesNotContain("请求未完成", Notice());
            await backend.SendIntentAsync("music.enqueue", "score-b", token);
            await Until(() => backend.GetMusic().Queue.Count == 2, "队列出现第二首");
            Assert.True(backend.GetMusic().Actions.CanStart);
            await backend.SendIntentAsync("music.start", null, token);
            await Until(() => backend.GetMusic().Actions.CanStop, "开始后可以停止");
            Assert.True(backend.GetMusic().Queue[0].Current);
            await backend.SendIntentAsync("music.next", null, token);
            await Until(() => backend.GetMusic().Queue.Count == 2 && backend.GetMusic().Queue[1].Current
                              || Notice().Contains("请求未完成"), "下一首成为当前");
            Assert.DoesNotContain("请求未完成", Notice());
            await backend.SendIntentAsync("music.stop", null, token);
            await Until(() => !backend.GetMusic().Actions.CanStop, "停止演奏");

            // 合奏：启动本机房间服务，用手动地址建房，复制邀请，房主改自己的组号。
            Assert.False(backend.GetRooms().InRoom);
            await backend.SendIntentAsync("rooms.start-server", "loopback", token);
            await Until(() => backend.GetRooms().Server.LocalEndpoint is { Length: > 0 } || Notice().Contains("请求未完成"), "本机房间服务给出地址");
            Assert.DoesNotContain("请求未完成", Notice());
            await backend.SendIntentAsync("rooms.create", $"manual\n琉特\n{backend.GetRooms().Server.LocalEndpoint}\nensemble", token);
            await Until(() => backend.GetRooms().InRoom || Notice().Contains("请求未完成"), "建房");
            Assert.True(backend.GetRooms().InRoom, Notice());
            Assert.Equal("ensemble", backend.GetRooms().Room!.Mode);
            Assert.Equal("琉特", backend.GetRooms().Self!.Nickname);
            Assert.True(backend.GetRooms().IsHost);
            await backend.SendIntentAsync("rooms.copy-invite", null, token);
            Assert.StartsWith("MabiSync/1|ws", copied);
            Assert.EndsWith("|" + backend.GetRooms().Room!.Code, copied);
            Assert.Contains("邀请已复制", Notice());
            // 成员编辑：房主把自己移到第 2 组，名单由后端重读。
            var selfId = backend.GetRooms().SelfId!;
            await backend.SendIntentAsync("rooms.roster", $"{selfId}\ngroup\n2", token);
            await Until(() => backend.GetRooms().Self?.Group == 2 || Notice().Contains("请求未完成"), "移到第 2 组");
            Assert.Equal(2, backend.GetRooms().Self!.Group);
            Assert.DoesNotContain("请求未完成", Notice());

            // 染色草稿：区域卡的改动写入草稿并读回；当前草稿存为命名方案，再载入与删除。
            var color = backend.GetColor();
            Assert.NotNull(color.Draft);
            var edited = new ColorDraft(color.Draft!.Strategy, color.Draft.Regions.Select((region, index) =>
                region.ToDraft() with { TargetHex = index == 0 ? "#123456" : region.TargetHex }).ToList());
            var savedDraft = await backend.SaveColorDraftAsync(edited, token);
            Assert.True(savedDraft.Ok, savedDraft.Message);
            await Until(() => backend.GetColor().Draft?.Regions[0].TargetHex == "#123456", "草稿读回新的目标色");
            var saved = await backend.SaveColorProfileAsync("聯調方案", edited, token);
            Assert.True(saved.Ok, saved.Message);
            await Until(() => backend.GetColor().Profiles.Any(profile => profile.Name == "聯調方案"), "方案列表出现新方案");
            var profileId = backend.GetColor().Profiles.Single(profile => profile.Name == "聯調方案").Id;
            Assert.True((await backend.LoadColorProfileAsync(profileId, token)).Ok);
            Assert.True((await backend.DeleteColorProfileAsync(profileId, token)).Ok);
            await Until(() => backend.GetColor().Profiles.All(profile => profile.Name != "聯調方案"), "方案已删除");

            // 模拟宿主没有配置桌面截图源：错误码原样交回界面。
            var capture = await backend.CaptureScreenAsync(token);
            Assert.Null(capture.Capture);
            Assert.Contains("capability.unavailable", capture.Message);

            // 快捷键：设置保存成功；模拟宿主没有系统快捷键来源，注册状态如实显示为不可用。
            var shortcut = await backend.ChangeShortcutAsync("emergency-stop", new("F8", true, false, false, false), token);
            Assert.False(shortcut.Ok);
            Assert.Contains("shortcut.sourceUnavailable", shortcut.Message);
            await Until(() => backend.GetSettings().Shortcuts.Any(row => row.Id == "emergency-stop" && row.Chord.Contains("F8")), "快捷键读回新组合");

            await backend.SendIntentAsync("rooms.leave", null, token);
            await Until(() => !backend.GetRooms().InRoom, "关闭房间");

            // 邀请格式不对时不发送请求，反馈横幅说明原因。
            await backend.SendIntentAsync("rooms.join", "community\n琉特\nABC", token);
            Assert.Contains("邀请不完整", Notice());
            Assert.Equal(RoomTone.Error, backend.GetRooms().Feedback!.Tone);

            // 染色：模拟宿主没有染色引擎时，后端给出的错误码要显示出来，界面保持未开始。
            await backend.SendIntentAsync("color.start", null, token);
            await Until(() => backend.GetColor().Actions.CanStop || Notice().Contains("请求未完成"), "染色开始或被后端拒绝");
        }
        finally { await StopHostAsync(host, root); }
    }

    [AvaloniaFact]
    public async Task GameWindowsAndUpdatePolicyReachTheMockHost()
    {
        var hostDll = FindHostDll();
        Assert.SkipWhen(hostDll is null, "未找到已构建的 MabiTool.Host.dll");
        var (host, root) = await StartHostAsync(hostDll!);
        using var hostProcess = host;
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using var backend = new LiveBackend(Path.Combine(root, "runtime", "instance.json"),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
            Assert.False(backend.GetGameBinding().Connected);
            backend.Start();
            await Until(() => backend.GetGameBinding().Confirmed, "连接模拟宿主并取得游戏绑定");
            Assert.True(backend.GetGameBinding().Connected);

            // 模拟宿主没有系统窗口来源：候选列表为空，绑定一个不存在的候选被后端拒绝，错误码显示在活动胶囊。
            var list = await backend.ListGameWindowsAsync(token);
            Assert.True(list.Ok, list.Message);
            Assert.Empty(list.Windows);
            var select = await backend.SelectGameWindowAsync("missing-candidate", token);
            Assert.False(select.Ok);
            Assert.Contains("resource.notFound", select.Message);
            Assert.Contains("resource.notFound", backend.GetActivity().Detail);
            Assert.True(backend.GetGameBinding().Confirmed);

            // 弹出层在真实后端下显示没有找到游戏窗口。
            var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
            try
            {
                OpenPicker(window);
                await Until(() => { LayoutProbe.Settle(); return !Picker(window).IsLoading; }, "读取候选窗口");
                LayoutProbe.Settle();
                Assert.True(Named<TextBlock>(window, "GamePickerEmpty").IsEffectivelyVisible);
                Assert.Empty(LayoutProbe.FindTextOverflow(window));
                Picker(window).Close();
            }
            finally { window.Close(); }

            // 更新策略：读取后端默认值，改检查间隔，下载并发数原值带回。
            await Until(() => backend.GetSettings().Policy is not null, "读取更新策略");
            var policy = backend.GetSettings().Policy!;
            var saved = await backend.SaveUpdatePolicyAsync(policy with { CheckIntervalHours = 24 }, token);
            Assert.True(saved.Ok, saved.Message);
            await Until(() => backend.GetSettings().Policy?.CheckIntervalHours == 24, "读回新的检查间隔");
            Assert.Equal(policy.DownloadConcurrency, backend.GetSettings().Policy!.DownloadConcurrency);

            // 超出后端范围的值被拒绝，策略保持后端当前值。
            var rejected = await backend.SaveUpdatePolicyAsync(policy with { CheckIntervalHours = 500 }, token);
            Assert.False(rejected.Ok);
            Assert.Contains("request.invalid", rejected.Message);
            Assert.Equal(24, backend.GetSettings().Policy!.CheckIntervalHours);
        }
        finally { await StopHostAsync(host, root); }
    }

    [AvaloniaFact]
    public async Task RoomPreferencesReadinessAndColorHistoryReachTheMockHost()
    {
        var hostDll = FindHostDll();
        Assert.SkipWhen(hostDll is null, "未找到已构建的 MabiTool.Host.dll");
        var (host, root) = await StartHostAsync(hostDll!);
        using var hostProcess = host;
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using var backend = new LiveBackend(Path.Combine(root, "runtime", "instance.json"),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
            backend.Start();
            await Until(() => backend.GetGameBinding().Confirmed, "连接模拟宿主并取得游戏绑定");
            string Notice() => backend.GetActivity().Detail;

            // 本机设置：读取、改动后保存，未显示的字段与修订按读取时的值带回。
            var loaded = await RetryStorage(() => backend.LoadRoomPreferencesAsync(token), reply => reply.Message);
            Assert.True(loaded.Value is not null, loaded.Message);
            var original = loaded.Value!;
            var edited = RoomLocalSettings.WithStart(original with { TimingOffsetMs = -40, CountdownMs = 7000 }, "emote",
                new(LocalBindingKind.Key, 0x20));
            var saved = await RetryStorage(() => backend.SaveRoomPreferencesAsync(edited, loaded.ETag, token), reply => reply.Message);
            Assert.True(saved.Ok, saved.Message);
            var reread = (await backend.LoadRoomPreferencesAsync(token)).Value!;
            Assert.Equal(original.Revision.Value + 1, reread.Revision.Value);
            Assert.Equal((-40, 7000), (reread.TimingOffsetMs, reread.CountdownMs));
            Assert.Equal((original.Endpoint, original.PublicProvider, original.Nickname, original.OverlayOpacity, original.LocalStopActionId),
                (reread.Endpoint, reread.PublicProvider, reread.Nickname, reread.OverlayOpacity, reread.LocalStopActionId));
            Assert.Equal((ushort?)0x20, reread.Bindings["emote"].Start?.Key);
            // 保存后合奏页的数据随例行刷新读回新修订。
            await Until(() => backend.GetRooms().Preferences?.Revision.Value == reread.Revision.Value, "合奏页读回本机设置");

            // 旧修订与越界值被后端拒绝，错误码显示在活动胶囊。
            var stale = await backend.SaveRoomPreferencesAsync(edited, loaded.ETag, token);
            Assert.False(stale.Ok);
            Assert.Contains("revision.stale", stale.Message);
            var current = await backend.LoadRoomPreferencesAsync(token);
            var invalid = await backend.SaveRoomPreferencesAsync(current.Value! with { TimingOffsetMs = 2001 }, current.ETag, token);
            Assert.False(invalid.Ok);
            Assert.Contains("request.invalid", invalid.Message);
            Assert.Contains("request.invalid", Notice());

            // 染色历史：模拟宿主没有完成过寻色，第一页为空，没有分页标记。
            var history = await RetryStorage(() => backend.QueryColorHistoryAsync(null, token), reply => reply.Message);
            Assert.True(history.Ok, history.Message);
            Assert.Null(history.NextCursor);

            // 合奏：建房（服务端默认模式）后本机成员就绪，后端快照里成员变为已就绪；取消就绪后回到未就绪。
            await backend.SendIntentAsync("rooms.start-server", "loopback", token);
            await Until(() => backend.GetRooms().Server.LocalEndpoint is { Length: > 0 } || Notice().Contains("请求未完成"), "本机房间服务给出地址");
            await backend.SendIntentAsync("rooms.create", $"manual\n琉特\n{backend.GetRooms().Server.LocalEndpoint}", token);
            await Until(() => backend.GetRooms().InRoom || Notice().Contains("请求未完成"), "建房");
            Assert.True(backend.GetRooms().InRoom, Notice());
            await Until(() => backend.GetRooms().RoomConnected, "房间已连接");

            await SendRetryingStorage(backend, "rooms.ready", "on", Notice);
            await Until(() => backend.GetRooms().Self!.Ready || Notice().Contains("请求未完成"), "本机成员就绪");
            Assert.True(backend.GetRooms().Self!.Ready, Notice());

            await backend.SendIntentAsync("rooms.ready", "off", token);
            await Until(() => !backend.GetRooms().Self!.Ready || Notice().Contains("请求未完成"), "取消就绪");
            Assert.False(backend.GetRooms().Self!.Ready);

            // 取消角色匹配只发送一次（编号与区服为空）；后端已知会把它当作未观测到的角色拒绝，结果原样显示在活动胶囊。
            await backend.SendIntentAsync("rooms.character", $"{backend.GetRooms().SelfId}\n\n", token);
            Assert.True(Notice().Contains("已取消角色匹配") || Notice().StartsWith("请求未完成：", StringComparison.Ordinal), Notice());

            await backend.SendIntentAsync("rooms.leave", null, token);
            await Until(() => !backend.GetRooms().InRoom, "关闭房间");
            Assert.Null(backend.GetRooms().Self);
        }
        finally { await StopHostAsync(host, root); }
    }

    // 模拟宿主刚启动时存储可能尚未就绪，写操作返回 storage.unavailable；等一会儿再发。
    private static async Task<T> RetryStorage<T>(Func<Task<T>> send, Func<T, string> message)
    {
        for (var attempt = 0; ; attempt++)
        {
            var reply = await send();
            if (!message(reply).Contains("storage.") || attempt >= 30) return reply;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
    }

    private static async Task SendRetryingStorage(LiveBackend backend, string intent, string argument, Func<string> notice)
    {
        for (var attempt = 0; ; attempt++)
        {
            await backend.SendIntentAsync(intent, argument, TestContext.Current.CancellationToken);
            if (!notice().Contains("storage.") || attempt >= 30) return;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
    }

    // 机器繁忙时模拟宿主的数据初始化偶尔超出后端自己的事务预算而退出；这与被测的前端无关，重启宿主即可。
    private static async Task<(Process Host, string Root)> StartHostAsync(string hostDll)
    {
        for (var attempt = 1; ; attempt++)
        {
            var root = Path.Combine(Path.GetTempPath(), "moongate-desktop-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var host = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { hostDll, "--mock", "--portable-root", root, "--initialize-data" },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                // 随机端口：不与本机正在运行的宿主（默认 19998）冲突。
                Environment = { ["MABITOOL_API_PORT"] = "0" }
            })!;
            host.OutputDataReceived += (_, _) => { };
            host.ErrorDataReceived += (_, _) => { };
            host.BeginOutputReadLine();
            host.BeginErrorReadLine();
            var discovery = Path.Combine(root, "runtime", "instance.json");
            var waited = Stopwatch.StartNew();
            while (!File.Exists(discovery) && !host.HasExited && waited.Elapsed < TimeSpan.FromSeconds(40))
                await Task.Delay(200, TestContext.Current.CancellationToken);
            if (File.Exists(discovery) && !host.HasExited) return (host, root);
            await StopHostAsync(host, root);
            if (attempt == 4) Assert.Fail("模拟宿主连续 4 次没有启动成功");
        }
    }

    /// <summary>
    /// 停止模拟宿主（连同它启动的子进程）并删除本次创建的测试数据根。进程刚退出时文件句柄可能还没释放，删除失败时短暂重试；
    /// 只删除传入的这一个目录。测试失败或超时也经由 finally 调用。
    /// </summary>
    private static async Task StopHostAsync(Process host, string root)
    {
        try
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await host.WaitForExitAsync(exit.Token);
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
        finally { host.Dispose(); }
        for (var attempt = 0; Directory.Exists(root); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 50)
            {
                await Task.Delay(200, CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        }
    }

    /// <summary>记录每个响应的状态码与路径，用来观察前端的轮询。</summary>
    private sealed class RecordingHandler(System.Collections.Concurrent.ConcurrentQueue<(int Status, string Path)> log)
        : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            log.Enqueue(((int)response.StatusCode, request.RequestUri!.AbsolutePath));
            return response;
        }
    }

    // 观测中点“开始计时”：后端事件密集时状态通知按刷新间隔合并，界面线程始终能及时处理输入，倒计时照常推进。
    [AvaloniaFact]
    public async Task StartingATimedMeasurementKeepsTheWindowResponsive()
    {
        var hostDll = FindHostDll();
        Assert.SkipWhen(hostDll is null, "未找到已构建的 MabiTool.Host.dll");
        var (host, root) = await StartHostAsync(hostDll!);
        using var hostProcess = host;
        var token = TestContext.Current.CancellationToken;
        try
        {
            var changes = 0;
            await using var backend = new LiveBackend(Path.Combine(root, "runtime", "instance.json"),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
            backend.Changed += (_, _) => Interlocked.Increment(ref changes);
            backend.Start();
            await Until(() => backend.GetCombat().Actions.CanStart, "连接模拟宿主并取得游戏绑定");
            await backend.SendCombatIntentAsync(CombatIntent.StartObservation, null, token);
            await Until(() => backend.GetCombat().Measurement?.CanStart == true, "观测收到数据后可以开始定时测量");

            var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
            var model = (CombatPageModel)window.GetVisualDescendants().OfType<CombatPage>().Single().DataContext!;
            Assert.True(model.CanStartMeasure);
            changes = 0;
            var watch = Stopwatch.StartNew();
            model.StartMeasureCommand.Execute(null);
            Assert.False(model.CanStartMeasure);
            var worst = TimeSpan.Zero;
            var clocks = new HashSet<string>();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                var input = Stopwatch.StartNew();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Input);
                if (input.Elapsed > worst) worst = input.Elapsed;
                if (model.MeasureRunning) clocks.Add(model.RunningText);
                await Task.Delay(100, token);
            }
            var rate = changes / watch.Elapsed.TotalSeconds;
            Assert.True(model.MeasureRunning, backend.GetActivity().Detail);
            // 测量从首次伤害开始计时：模拟宿主的回放伤害到达后转入计时，剩余时间按本机时钟递减。
            Assert.True(clocks.Count >= 3, $"计时剩余时间只出现了 {string.Join(",", clocks)}");
            Assert.True(worst < TimeSpan.FromSeconds(1), $"界面线程处理输入最长等待 {worst.TotalMilliseconds:0} ms");
            // 每轮刷新至少隔 MinimumRefreshInterval（每秒最多 4 轮），另有页面状态的五秒读取等少量通知；修复前约每秒 9–10 次。
            Assert.True(rate <= 1 / LiveBackend.MinimumRefreshInterval.TotalSeconds + 1, $"状态通知每秒 {rate:0.0} 次");
            Assert.True(backend.QueuedNotifications <= 1);
            model.CancelMeasureCommand.Execute(null);
            await Until(() => model.MeasureIdle, "取消定时测量");
            window.Close();
        }
        finally { await StopHostAsync(host, root); }
    }

    // 不存在的资源只请求一次：选中已不存在的测量记录时页面写明“这条记录已不存在”，例行刷新不再反复查询它。
    [AvaloniaFact]
    public async Task MissingRecordsAreNotPolledEveryRefresh()
    {
        var hostDll = FindHostDll();
        Assert.SkipWhen(hostDll is null, "未找到已构建的 MabiTool.Host.dll");
        var (host, root) = await StartHostAsync(hostDll!);
        using var hostProcess = host;
        var token = TestContext.Current.CancellationToken;
        try
        {
            var log = new System.Collections.Concurrent.ConcurrentQueue<(int Status, string Path)>();
            await using var backend = new LiveBackend(Path.Combine(root, "runtime", "instance.json"),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action)) { Handler = () => new RecordingHandler(log) };
            backend.Start();
            await Until(() => backend.GetCombat().Actions.CanStart, "连接模拟宿主并取得游戏绑定");
            // 观测开始前整场范围没有来源：不查询，也不报错。
            await backend.SendIntentAsync("combat.scope", "observation", token);
            await Task.Delay(1500, token);
            await backend.SendCombatIntentAsync(CombatIntent.StartObservation, null, token);
            await Until(() => backend.GetCombat().Skills.Count > 0, "开始观测后出现技能统计");
            await backend.SendIntentAsync("combat.scope", "measurement:enc-00000000000000000000000000000000", token);
            await Until(() => backend.GetCombat().Scope?.Problem == "这条记录已不存在", "页面写明记录已不存在");
            await Task.Delay(4000, token);
            var missing = log.Where(item => item.Status == 404).GroupBy(item => item.Path).ToDictionary(group => group.Key, group => group.Count());
            Assert.True(missing.Values.All(count => count == 1), string.Join("; ", missing.Select(item => $"{item.Value} × {item.Key}")));
            Assert.Equal("这条记录已不存在", backend.GetCombat().Scope?.Problem);
        }
        finally { await StopHostAsync(host, root); }
    }

    [AvaloniaFact]
    public async Task StoppingTheMockHostRemovesItsDataRoot()
    {
        var hostDll = FindHostDll();
        Assert.SkipWhen(hostDll is null, "未找到已构建的 MabiTool.Host.dll");
        var (host, root) = await StartHostAsync(hostDll!);
        Assert.True(File.Exists(Path.Combine(root, "runtime", "instance.json")));
        await StopHostAsync(host, root);
        Assert.False(Directory.Exists(root));
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail($"等待超时：{what}");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    private static string? FindHostDll()
    {
        var artifactsPath = Environment.GetEnvironmentVariable("MABITOOL_TEST_ARTIFACTS_PATH");
        var configured = Environment.GetEnvironmentVariable("MABITOOL_TEST_CONFIGURATION");
        var configuration = (configured ?? "Release").ToLowerInvariant();
        var required = artifactsPath is not null || configured is not null;
        string? HostDll(string buildRoot)
        {
            var path = Path.Combine(buildRoot, "bin", "MabiTool.Host", configuration, "MabiTool.Host.dll");
            if (File.Exists(path)) return path;
            return required ? throw new FileNotFoundException("指定的测试宿主构建不存在", path) : null;
        }
        if (artifactsPath is not null && Path.IsPathRooted(artifactsPath))
            return HostDll(Path.GetFullPath(artifactsPath));
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) continue;
            return HostDll(Path.GetFullPath(artifactsPath ?? Path.Combine("artifacts", "build"), directory.FullName));
        }
        if (required) throw new DirectoryNotFoundException("无法确定指定测试构建的项目根目录");
        return null;
    }
}

/// <summary>写请求遇到存储繁忙时的退避重试与幂等键。</summary>
public sealed class StorageRetryTests
{
    private static MabiToolApiException Failure(int status, string code) => new(status,
        new ApiProblem("about:blank", code, status, code, code, "trace", new Dictionary<string, System.Text.Json.JsonElement>(), true));

    private static async Task<(int Rounds, List<string> Keys, Exception? Error)> Run(int failures, int status, string code)
    {
        var previous = LiveBackend.StorageBackoff;
        LiveBackend.StorageBackoff = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];
        var rounds = 0;
        var keys = new List<string>();
        try
        {
            await LiveBackend.WithStorageRetryAsync(() =>
            {
                rounds++;
                keys.Add(LiveBackend.IdempotencyKey() + "/" + LiveBackend.IdempotencyKey());
                return rounds <= failures ? Task.FromException<string>(Failure(status, code)) : Task.FromResult("ok");
            }, TestContext.Current.CancellationToken);
            return (rounds, keys, null);
        }
        catch (MabiToolApiException error) { return (rounds, keys, error); }
        finally { LiveBackend.StorageBackoff = previous; }
    }

    [Theory]
    [InlineData("storage.unavailable")]
    [InlineData("storage.writeQueueFull")]
    [InlineData("storage.readCapacityExceeded")]
    [InlineData("storage.transactionBudgetExceeded")]
    public async Task BusyStorageIsRetriedWithTheSameIdempotencyKeys(string code)
    {
        var (rounds, keys, error) = await Run(2, 503, code);
        Assert.Null(error);
        Assert.Equal(3, rounds);
        // 每一轮里两个写请求的键各不相同，重试时逐个沿用上一轮的键。
        Assert.Single(keys.Distinct());
        Assert.NotEqual(keys[0].Split('/')[0], keys[0].Split('/')[1]);
    }

    [Fact]
    public async Task RetryStopsAfterTheBackoffScheduleAndReportsTheCode()
    {
        var (rounds, _, error) = await Run(10, 503, "storage.writeQueueFull");
        Assert.Equal(4, rounds);
        Assert.Equal("storage.writeQueueFull", Assert.IsType<MabiToolApiException>(error).Problem?.Code);
    }

    [Theory]
    [InlineData(503, "provider.unavailable")]
    [InlineData(409, "resource.conflict")]
    [InlineData(412, "revision.stale")]
    public async Task OtherFailuresAreNotRetried(int status, string code)
    {
        var (rounds, _, error) = await Run(10, status, code);
        Assert.Equal(1, rounds);
        Assert.NotNull(error);
    }

    [Fact]
    public void KeysOutsideARetryScopeAreAlwaysNew() =>
        Assert.NotEqual(LiveBackend.IdempotencyKey(), LiveBackend.IdempotencyKey());
}
