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
            // 模拟宿主没有游戏连接器环境：原因为 platform.unsupported；指向“游戏连接器”胶囊的说明只跟在 gameCli.* 原因后面。
            Assert.Equal("暂不可用：系统不受支持", backend.GetCapabilityNotice(CapabilityIds.PerformancePlay));
            Assert.Equal("后端未实现，暂不可用", backend.GetCapabilityNotice(CapabilityIds.RoomsCoordinate));
            Assert.StartsWith("暂不可用：", backend.GetCapabilityNotice(CapabilityIds.ColorSearch));
            Assert.Null(backend.GetCapabilityNotice("system.inspect"));
            Assert.Null(backend.GetCapabilityNotice("no.such.capability"));

            // 游戏内演奏不可用：开始演奏与下一首随能力停用，说明与能力表一致。
            using (var music = new Pages.Music.MusicPageModel(backend))
            {
                Assert.True(music.HasCapabilityNotice);
                Assert.StartsWith("暂不可用：", music.CapabilityNotice);
                Assert.False(music.CanStart);
                Assert.False(music.CanNext);
            }

            // 展示接口的 nearby、environment 主题随游戏连接器：模拟宿主没有连接器环境，主题缺失并带连接器的原因（platform.unsupported），
            // 不再固定为 provider.unavailable；浮窗元件照常显示外框并写“暂不可用：原因”，其他主题没有说明。
            await Until(() => backend.GetPresentationNotice(PresentationTopics.Nearby) is not null, "读取展示接口主题状态");
            foreach (var topic in PresentationTopics.Watched)
            {
                var missing = backend.GetPresentationNotice(topic);
                Assert.NotNull(missing);
                Assert.False(missing.PlatformUnsupported);
                Assert.Equal("platform.unsupported", missing.ReasonCode);
                Assert.StartsWith("暂不可用：", missing.Text);
            }
            Assert.Null(backend.GetPresentationNotice("room"));
            Assert.Null(backend.GetPresentationNotice("no.such.topic"));
            using (var widget = new Overlay.OverlayWidgetModel("performance.nearby", backend, PresentationTopics.Nearby))
            {
                Assert.True(widget.FrameVisible);
                Assert.Equal(backend.GetPresentationNotice(PresentationTopics.Nearby)!.Text, widget.NoticeText);
            }

            await backend.SendCombatIntentAsync(CombatIntent.StartObservation, null, TestContext.Current.CancellationToken);
            // DPS 的分母是第一击到最后一击的时长：模拟宿主每 250 ms 送一帧、每帧一次命中，只有第一帧时 DPS 为空（insufficientDuration），
            // 第二帧到达后才可读。桌面随宿主通知即时读取，等待条件以 DPS 可用为准。
            await Until(() => backend.GetCombat() is { } snapshot && snapshot.Skills.Count > 0
                && snapshot.Kpis.Any(tile => tile.Primary && tile.Value != "—"), "开始观测后出现技能统计与可读的 DPS");
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
                page.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "RateGrid").GetVisualDescendants()
                    .OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == "无防备"));
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

            // 乐谱库在没有游戏连接器的宿主上照常打开：状态行写明游戏乐谱来源不可用的原因（连接器的原因码），本机整理不受影响。
            LayoutProbe.Select(window, "music-library");
            await Until(() => backend.GetMusic().Status.Contains("游戏乐谱来源暂不可用：系统不受支持"), "乐谱库状态行写明来源不可用的原因");
            Assert.DoesNotContain("尚未从游戏同步", backend.GetMusic().Status);

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
            // 窗口关闭后测试直接读取战斗数据：战斗数据只为可见的使用者读取，这里以 DPS检测页可见的身份声明使用者（与页面模型同一入口）。
            backend.SetCombatPageVisible(true);

            // 手动拆段：后端受理后给出说明；新分段要等下一笔伤害，状态行先显示等待，随后显示拆段后开始的分段。
            Assert.True(backend.GetCombat().Actions.CanSplit);
            await backend.SendCombatIntentAsync(CombatIntent.SplitEncounter, null, TestContext.Current.CancellationToken);
            Assert.Equal("已拆段，下一次伤害开始新分段", backend.GetActivity().Detail);
            await Until(() => backend.GetCombat().Status is var status
                && (status.Contains("已拆段，等待下一次伤害") || status.Contains("手动拆段后开始")), "拆段后状态行显示等待或新分段");

            await backend.RequestEmergencyStopAsync(TestContext.Current.CancellationToken);
            // 紧急停止的后续写明来源与结果（合奏规格“紧急停止之后”：“已紧急停止（按钮）：……”）。
            Assert.StartsWith("已紧急停止（按钮）：", backend.GetActivity().Detail);

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
            // 建房分两步（先建房，再按界面选择设模式）：只等进入房间会读到设模式之前的默认模式，要等模式也设好。
            await Until(() => backend.GetRooms().Room?.Mode == "ensemble" || Notice().Contains("请求未完成"), "建房并设为合奏模式");
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

            // 模拟宿主没有配置桌面截图源：错误码经共用映射换成中文短句交回界面。
            var capture = await backend.CaptureScreenAsync(new PixelRect(0, 0, 1920, 1080), token);
            Assert.Null(capture.Capture);
            Assert.Equal("请求未完成：功能不可用", capture.Message);

            // 快捷键：设置保存成功；模拟宿主没有系统快捷键来源，注册状态如实显示为不可用。
            var shortcut = await backend.ChangeShortcutAsync("emergency-stop", new("F8", true, false, false, false), token);
            Assert.False(shortcut.Ok);
            Assert.Equal("已保存 Ctrl + F8，快捷键服务不可用", shortcut.Message);
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
            Assert.Equal("请求未完成：找不到所选内容", select.Message);
            Assert.Equal("请求未完成：找不到所选内容", backend.GetActivity().Detail);
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
            Assert.Equal("请求未完成：请求内容无效", rejected.Message);
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

            // 旧修订与越界值被后端拒绝，错误码换成中文短句显示在活动胶囊。
            var stale = await backend.SaveRoomPreferencesAsync(edited, loaded.ETag, token);
            Assert.False(stale.Ok);
            Assert.Equal("请求未完成：数据已变化，请刷新", stale.Message);
            var current = await backend.LoadRoomPreferencesAsync(token);
            var invalid = await backend.SaveRoomPreferencesAsync(current.Value! with { TimingOffsetMs = 2001 }, current.ETag, token);
            Assert.False(invalid.Ok);
            Assert.Equal("请求未完成：请求内容无效", invalid.Message);
            Assert.Equal("请求未完成：请求内容无效", Notice());

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

    // 存储尚未就绪的回应：错误码经共用映射显示为“存储不可用”。
    private static bool StorageBusy(string message) => message.Contains("存储不可用") || message.Contains("storage.");

    // 模拟宿主刚启动时存储可能尚未就绪，写操作返回 storage.unavailable；等一会儿再发。
    private static async Task<T> RetryStorage<T>(Func<Task<T>> send, Func<T, string> message)
    {
        for (var attempt = 0; ; attempt++)
        {
            var reply = await send();
            if (!StorageBusy(message(reply)) || attempt >= 30) return reply;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
    }

    private static async Task SendRetryingStorage(LiveBackend backend, string intent, string argument, Func<string> notice)
    {
        for (var attempt = 0; ; attempt++)
        {
            await backend.SendIntentAsync(intent, argument, TestContext.Current.CancellationToken);
            if (!StorageBusy(notice()) || attempt >= 30) return;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
    }

    // 机器繁忙时模拟宿主的数据初始化偶尔超出后端自己的事务预算而退出；这与被测的前端无关，重启宿主即可。
    /// <param name="existingRoot">在已有的数据根上重新启动（模拟宿主重启）；为空时新建一个。</param>
    private static async Task<(Process Host, string Root)> StartHostAsync(string hostDll, string? existingRoot = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var root = existingRoot ?? Path.Combine(Path.GetTempPath(), "moongate-desktop-test-" + Guid.NewGuid().ToString("N"));
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
            // 每轮战斗读取至少隔一帧（FrameInterval，每秒至多 60 轮），另有页面状态的五秒读取等少量通知。
            Assert.True(rate <= 1 / LiveBackend.FrameInterval.TotalSeconds + 1, $"状态通知每秒 {rate:0.0} 次");
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

    // 背包快照只在背包事件、用户操作或重新连接后重读：五秒例行刷新照常读取其他页面状态，不再反复拉取整份背包。
    [AvaloniaFact]
    public async Task RoutinePageRefreshDoesNotRereadTheInventorySnapshot()
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
            await Until(() => log.Any(item => item.Path == "/api/v1/inventory/snapshot"), "首次读取背包快照");
            await Task.Delay(11500, token);
            Assert.Equal(200, log.First(item => item.Path == "/api/v1/inventory/snapshot").Status);
            Assert.True(log.Count(item => item.Path == "/api/v1/modules") >= 3, "五秒例行刷新照常读取模块状态");
            Assert.Equal(1, log.Count(item => item.Path == "/api/v1/inventory/snapshot"));
            await backend.SendIntentAsync("inventory.account", null, token);
            await Until(() => log.Count(item => item.Path == "/api/v1/inventory/snapshot") == 2, "用户操作后重读背包快照");
        }
        finally { await StopHostAsync(host, root); }
    }

    // 宿主重启后前端重新握手并全部重读：事件驱动与按修订沿用的缓存在新连接上作废，不沿用旧连接的结果，也不等例行间隔。
    [AvaloniaFact]
    public async Task ReconnectsAfterTheHostRestartsAndReadsEverythingAgain()
    {
        var hostDll = FindHostDll();
        Assert.SkipWhen(hostDll is null, "未找到已构建的 MabiTool.Host.dll");
        var (host, root) = await StartHostAsync(hostDll!);
        var token = TestContext.Current.CancellationToken;
        try
        {
            var log = new System.Collections.Concurrent.ConcurrentQueue<(int Status, string Path)>();
            await using var backend = new LiveBackend(Path.Combine(root, "runtime", "instance.json"),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action)) { Handler = () => new RecordingHandler(log) };
            backend.Start();
            await Until(() => backend.GetCombat().Actions.CanStart, "连接模拟宿主并取得游戏绑定");
            await backend.SendCombatIntentAsync(CombatIntent.StartObservation, null, token);
            await Until(() => backend.GetCombat().Skills.Count > 0, "开始观测后出现技能统计");
            await Until(() => backend.GetModules().Modules.Count > 0, "读取模块清单");

            // 宿主进程结束（保留数据根）：前端标为未连接并保留最后结果。
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(token);
            host.Dispose();
            await Until(() => !backend.GetGameBinding().Connected, "宿主停止后标为未连接");
            Assert.True(backend.GetCombat().Skills.Count > 0, "断线后保留最后的战斗结果");

            // 在同一数据根上重新启动：重新握手后快照、战斗记录、页面状态全部重读。
            File.Delete(Path.Combine(root, "runtime", "instance.json"));
            (host, _) = await StartHostAsync(hostDll!, root);
            var before = log.Count;
            await Until(() => backend.GetGameBinding().Connected && backend.GetCombat().Actions.CanStart, "重新连接模拟宿主");
            await Until(() => log.Skip(before).Any(item => item.Path == "/api/v1/combat/snapshot")
                && log.Skip(before).Any(item => item.Path == "/api/v1/combat/encounters")
                && log.Skip(before).Any(item => item.Path == "/api/v1/modules"), "重连后重读战斗与页面状态");
            Assert.True(backend.GetModules().Modules.Count > 0);
        }
        finally { await StopHostAsync(host, root); }
    }

    // MobiFolio 数据文件：三张乐谱（两张同名）、一个文件夹里的一个清单（两个条目）。
    private static MusicImportFile Json(string name, string json) => new(name, System.Text.Encoding.UTF8.GetBytes(json));

    /// <summary>乐谱来源合并、清单读取与编辑、导入预览与提交都经真实后端（模拟宿主）完成。</summary>
    [AvaloniaFact]
    public async Task MusicImportListsAndEditsReachTheMockHost()
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
            await Until(() => backend.GetModules().Modules.Count > 0, "连接模拟宿主并读取模块");
            backend.SetSurfaceVisible("main", true);
            backend.SetSurfaceVisible("music", true);
            await Until(() => backend.GetMusic().Lists is { CanEdit: true }, "读到曲库");
            Assert.Empty(backend.GetMusic().Lists!.Playlists);

            // 导入：预览不写入曲库；确认后才提交，来源合并进乐谱表，清单与文件夹按层级读到。
            var files = new[]
            {
                Json("scores.json", """{"items":[{"Name":"夕陽"},{"Name":"夕陽"},{"Name":"旅程"}]}"""),
                Json("playlists.json", """{"folders":[{"id":"f1","name":"收藏","parent":null}],"playlists":[{"id":"l1","name":"晚間","folder":"f1","memo":"備註","items":[{"key":"夕陽#1","title":"夕陽","inst":""},{"key":"旅程","title":"旅程","inst":""}]}]}""")
            };
            var preview = await RetryStorage(() => backend.PreviewMusicImportAsync(files, "KR", token), reply => reply.Message);
            Assert.True(preview.Ok, preview.Message);
            Assert.Equal((3, 1, 1, 2), (preview.Preview!.Scores, preview.Preview.Folders, preview.Preview.Playlists, preview.Preview.PlaylistItems));
            Assert.Empty(backend.GetMusic().Lists!.Playlists);
            var bad = await backend.PreviewMusicImportAsync([Json("scores.json", "不是 JSON")], "KR", token);
            Assert.False(bad.Ok);
            var committed = await backend.CommitMusicImportAsync(preview.Preview.Id, token);
            Assert.True(committed.Ok, committed.Message);
            Assert.Equal("已导入 3 张乐谱、1 个清单", committed.Message);
            await Until(() => backend.GetMusic().Scores.Count == 3 && backend.GetMusic().Lists!.Playlists.Count == 1, "导入的乐谱与清单出现");
            var music = backend.GetMusic();
            Assert.All(music.Scores, score => Assert.Equal("导入", score.Source));
            Assert.Equal(["夕陽", "夕陽", "旅程"], music.Scores.Select(score => score.Title).Order(StringComparer.Ordinal));
            Assert.Equal("3 张", music.Playlists[0].Note);
            var imported = music.Lists!.Playlists.Single();
            Assert.Equal(("晚間", "收藏", 2), (imported.Name, imported.FolderPath, imported.Items.Count));
            Assert.Equal("2 首 · 收藏", music.Playlists.Single(chip => chip.Id == imported.Id).Note);
            Assert.Equal(["收藏"], music.Lists.Folders.Select(folder => folder.Name));
            // 同一份数据不能重复导入。
            var again = await backend.PreviewMusicImportAsync(files, "KR", token);
            Assert.False(again.Ok);

            // 选中清单后乐谱表只显示清单里的乐谱（两个条目指向两张乐谱）。
            await backend.SendIntentAsync("music.playlist", imported.Id, token);
            await Until(() => backend.GetMusic().Lists!.Selected?.Id == imported.Id && backend.GetMusic().Scores.Count == 2, "选中清单后筛选乐谱");

            // 编辑：新建、加入乐谱（可重复）、排序、逐条乐器、默认乐器、重命名、备注、移动、删除；每次都由后端判定。
            async Task<BackendReply> Edit(MusicListEdit edit) => await RetryStorage(() => backend.EditMusicLibraryAsync(edit, token), reply => reply.Message);
            Assert.True((await Edit(new(MusicListEditKind.CreatePlaylist, Text: "新清單"))).Ok);
            await Until(() => backend.GetMusic().Lists!.Playlists.Any(item => item.Name == "新清單"), "新清單出現");
            var created = backend.GetMusic().Lists!.Playlists.Single(item => item.Name == "新清單");
            Assert.Equal(string.Empty, created.FolderPath);
            var scoreId = backend.GetMusic().Lists!.Selected!.Items[0].ScoreId;
            Assert.True((await Edit(new(MusicListEditKind.AddItem, PlaylistId: created.Id, ScoreId: scoreId))).Ok);
            Assert.True((await Edit(new(MusicListEditKind.AddItem, PlaylistId: created.Id, ScoreId: scoreId))).Ok);
            Assert.False((await Edit(new(MusicListEditKind.AddItem, PlaylistId: created.Id, ScoreId: "不存在的乐谱"))).Ok);
            await Until(() => backend.GetMusic().Lists!.Playlists.Single(item => item.Id == created.Id).Items.Count == 2, "同一乐谱重复加入得到两个条目");
            var items = backend.GetMusic().Lists!.Playlists.Single(item => item.Id == created.Id).Items;
            Assert.NotEqual(items[0].Id, items[1].Id);
            Assert.True((await Edit(new(MusicListEditKind.MoveItem, PlaylistId: created.Id, ItemId: items[1].Id, Direction: -1))).Ok);
            Assert.True((await Edit(new(MusicListEditKind.SetItemInstrument, PlaylistId: created.Id, ItemId: items[0].Id, Text: "piano"))).Ok);
            Assert.True((await Edit(new(MusicListEditKind.SetDefaultInstrument, PlaylistId: created.Id, Text: "harp"))).Ok);
            Assert.True((await Edit(new(MusicListEditKind.RenamePlaylist, PlaylistId: created.Id, Text: "改名清單"))).Ok);
            Assert.True((await Edit(new(MusicListEditKind.SetNotes, PlaylistId: created.Id, Text: "第一行\n第二行"))).Ok);
            Assert.True((await Edit(new(MusicListEditKind.MovePlaylist, PlaylistId: created.Id, FolderId: music.Lists.Folders[0].Id))).Ok);
            await Until(() => backend.GetMusic().Lists!.Playlists.Any(item => item.Name == "改名清單" && item.FolderPath == "收藏" && item.Notes == "第一行\n第二行"), "重命名、备注与移动生效");
            var edited = backend.GetMusic().Lists!.Playlists.Single(item => item.Id == created.Id);
            Assert.Equal([items[1].Id, items[0].Id], edited.Items.Select(item => item.Id));
            Assert.Equal(("piano", "沿用清单"), (edited.Items[1].InstrumentId, edited.Items[0].Instrument));
            Assert.Equal("harp", edited.DefaultInstrumentId);
            Assert.True((await Edit(new(MusicListEditKind.RemoveItem, PlaylistId: created.Id, ItemId: items[0].Id))).Ok);
            Assert.True((await Edit(new(MusicListEditKind.CreateFolder, Text: "新夾"))).Ok);
            await Until(() => backend.GetMusic().Lists!.Folders.Any(folder => folder.Name == "新夾") &&
                              backend.GetMusic().Lists!.Playlists.Single(item => item.Id == created.Id).Items.Count == 1, "移除条目与新建文件夹生效");
            var folder = backend.GetMusic().Lists!.Folders.Single(item => item.Name == "新夾");
            Assert.True((await Edit(new(MusicListEditKind.RenameFolder, FolderId: folder.Id, Text: "改名夾"))).Ok);
            Assert.True((await Edit(new(MusicListEditKind.DeleteFolder, FolderId: music.Lists.Folders[0].Id))).Ok);
            // 删除文件夹：其中的清单移到上一层（根目录）。
            await Until(() => backend.GetMusic().Lists!.Folders.Select(item => item.Name).SequenceEqual(["改名夾"])
                              && backend.GetMusic().Lists!.Playlists.All(item => item.FolderPath.Length == 0), "删除文件夹后清单移到根目录");
            Assert.True((await Edit(new(MusicListEditKind.DeletePlaylist, PlaylistId: created.Id))).Ok);
            await Until(() => backend.GetMusic().Lists!.Playlists.All(item => item.Id != created.Id), "清单已删除");
            // 引用的清单已不存在：不发送，回执说明。
            var gone = await backend.EditMusicLibraryAsync(new(MusicListEditKind.RenamePlaylist, PlaylistId: created.Id, Text: "晚"), token);
            Assert.False(gone.Ok);
            Assert.Contains("已经不存在", gone.Message);
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

    [AvaloniaFact]
    public async Task MockHostCurrentTargetHealthReachesTheDpsData()
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
            await Until(() => backend.GetCombat().Actions.CanStart, "连接模拟宿主并取得游戏绑定");
            backend.SetCombatPageVisible(true);
            // 战斗能力表里的怪物血量随页面状态读取：模拟宿主可用，没有停用说明。
            await Until(() => backend.GetCapabilityLevel(CapabilityIds.MonsterHealth) == CapabilityLevel.Available, "读取战斗能力表");
            Assert.Null(backend.GetCapabilityNotice(CapabilityIds.MonsterHealth));
            await backend.SendCombatIntentAsync(CombatIntent.StartObservation, null, token);
            // 模拟宿主的回放：本机主目标是首领标志为真、按伤害掉血的怪物，最大值 1,000,000。
            await Until(() => backend.GetCombat().Health is { IsTarget: true, Empty: false }, "读到当前攻击目标与血量");
            var health = backend.GetCombat().Health!;
            Assert.True(health.Boss);
            Assert.Equal("同名怪物", health.Name);
            Assert.EndsWith(" / 1,000,000", health.Value);
            Assert.Matches(@"^\d{1,3}\.\d%$", health.Percent);
            Assert.InRange(health.Fill, 0, 1);
        }
        finally { await StopHostAsync(host, root); }
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
