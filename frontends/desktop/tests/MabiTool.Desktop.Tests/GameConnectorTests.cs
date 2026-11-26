using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Settings;
using MabiTool.Desktop.Shell;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>游戏连接器：状态胶囊文字、首次启用的风险确认、设置页卡片与胶囊一致、LiveBackend 对连接器接口的读取。</summary>
public sealed class GameConnectorTests
{
    private static SampleBackend SampleAt(ConnectorState state, bool riskAcknowledged = true, bool? running = null) =>
        new() { ConnectorView = SampleBackend.SampleConnector(state, riskAcknowledged, running) };

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese)]
    [InlineData(UiLanguage.TraditionalChinese)]
    public void ChipShowsEachStatusAndUnknownWithoutABackend(UiLanguage language)
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, null, language);
        try
        {
            var chip = Named<TextBlock>(window, "ConnectorChipText");
            var traditional = language == UiLanguage.TraditionalChinese;
            var expected = new (ConnectorState State, string Text)[]
            {
                (ConnectorState.Disabled, traditional ? "未啟用" : "未启用"), (ConnectorState.Connected, traditional ? "已連接" : "已连接"),
                (ConnectorState.Expired, "已失效"), (ConnectorState.GameNotRunning, traditional ? "未連接遊戲" : "未连接游戏"),
                (ConnectorState.Unavailable, "不可用"), (ConnectorState.Checking, traditional ? "檢測中" : "检测中")
            };
            foreach (var (state, text) in expected)
            {
                backend.ConnectorView = SampleBackend.SampleConnector(state, riskAcknowledged: true);
                backend.RaiseChanged();
                LayoutProbe.Settle();
                Assert.Equal(text, chip.Text);
                Assert.Equal(text, ConnectorText.Status(backend.GetGameConnector()));
            }

            // 桌面没有连上后端：胶囊显示未知，卡片的写操作停用并说明先连接后端。
            backend.Offline = true;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal("未知", chip.Text);
            Click(window, Named<Button>(window, "ConnectorChip"));
            Assert.False(Named<Button>(window, "ConnectorOnButton").IsEffectivelyEnabled);
            Assert.False(Named<Button>(window, "ConnectorRestoreButton").IsEffectivelyEnabled);
            Assert.Equal(traditional ? "連接後端後才能設定" : "连接后端后才能设置", Named<TextBlock>(window, "ConnectorHintText").Text);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally
        {
            window.Close();
            ResetAppearance();
        }
    }

    [AvaloniaFact]
    public void RiskDialogAppearsOnlyBeforeTheBackendRemembersTheAcknowledgement()
    {
        var backend = new SampleBackend();
        using var model = new GameConnectorModel(backend, ownsRiskLayer: true);
        Assert.False(model.View.RiskAcknowledged);

        // 首次：先弹窗，确认前不发任何请求；取消仍不发，开关保持关闭。
        model.EnableCommand.Execute(null);
        Assert.True(model.RiskOpen);
        Assert.Empty(backend.ConnectorCommands);
        model.CancelRiskCommand.Execute(null);
        Assert.False(model.RiskOpen);
        Assert.Empty(backend.ConnectorCommands);
        Assert.False(model.Enabled);

        // 确认后才发送带确认的启用请求；启用成功后提示从启动器重新启动台服。
        model.EnableCommand.Execute(null);
        model.ConfirmRiskCommand.Execute(null);
        Assert.False(model.RiskOpen);
        Assert.Equal(new[] { ("enable", true, (string?)null) }, backend.ConnectorCommands);
        Assert.True(model.Enabled);
        Assert.Equal(ConnectorState.GameNotRunning, model.View.State);
        Assert.Equal("已启用，请从 Nexon Launcher 重新启动台服并进入角色", model.Message);
        Assert.True(model.MessageSucceeded);

        // 后端已记住确认：关闭后再启用直接发送，不再弹窗。
        model.DisableCommand.Execute(null);
        Assert.False(model.Enabled);
        model.EnableCommand.Execute(null);
        Assert.False(model.RiskOpen);
        Assert.Equal(new[] { ("enable", true, (string?)null), ("disable", false, (string?)null), ("enable", true, (string?)null) },
            backend.ConnectorCommands);
    }

    [AvaloniaFact]
    public void RiskLayerIsCenteredKeepsOpenOnOutsideClicksAndEscapeCancels()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            Click(window, Named<Button>(window, "ConnectorChip"));
            Assert.True(shell.Connector.IsOpen);
            Assert.Equal("游戏连接器：游戏连接器未启用，启用后需从 Nexon Launcher 重启台服", shell.Connector.ChipTip);
            Click(window, Named<Button>(window, "ConnectorOnButton"));

            // 窗口级居中窗口：焦点落在确认按钮，点外面不关闭。
            var layer = Named<Panel>(window, "ConnectorRiskLayer");
            var panel = Named<Border>(window, "ConnectorRiskPanel");
            Assert.True(layer.IsEffectivelyVisible);
            var center = Center(panel, window);
            Assert.InRange(center.X, window.Bounds.Width / 2 - 2, window.Bounds.Width / 2 + 2);
            Assert.Same(Named<Button>(window, "ConnectorRiskConfirm"), window.FocusManager?.GetFocusedElement());
            var items = new[] { "ConnectorRiskItem1", "ConnectorRiskItem2", "ConnectorRiskItem3", "ConnectorRiskItem4" }
                .Select(name => Named<TextBlock>(window, name).Text).ToList();
            Assert.Contains(items, text => text!.Contains("修改台服客户端的设置"));
            Assert.Contains(items, text => text!.Contains("没有公开提供"));
            Assert.Contains(items, text => text!.Contains("账号风险"));
            Assert.Contains(items, text => text!.Contains("恢复原设置"));
            Assert.Contains("乐谱库游戏内演奏", Named<TextBlock>(window, "ConnectorRiskModulesNow").Text);
            // 角色信息、附近玩家与环境、交易所查价已接入；聊天与动作不做，不再出现在清单里。
            Assert.Contains("交易所查价", Named<TextBlock>(window, "ConnectorRiskModulesNow").Text);
            Assert.Contains("采集加工制作", Named<TextBlock>(window, "ConnectorRiskModulesLater").Text);
            Assert.DoesNotContain("聊天", Named<TextBlock>(window, "ConnectorRiskModulesNow").Text + Named<TextBlock>(window, "ConnectorRiskModulesLater").Text);
            PressAt(window, new Avalonia.Point(900, 700));
            Assert.True(shell.Connector.RiskOpen);

            // Esc 等于取消：不发送请求，开关保持关闭，焦点回到打开前的位置。
            PressKey(window, Key.Escape, PhysicalKey.Escape);
            Assert.False(shell.Connector.RiskOpen);
            Assert.Empty(backend.ConnectorCommands);
            Assert.False(shell.Connector.Enabled);
            Assert.True(shell.Connector.IsOpen);

            // 再次打开并确认：发送一次带确认的启用请求，胶囊随之换成新状态，卡片显示下一步。
            Click(window, Named<Button>(window, "ConnectorOnButton"));
            Click(window, Named<Button>(window, "ConnectorRiskConfirm"));
            Assert.Equal(new[] { ("enable", true, (string?)null) }, backend.ConnectorCommands);
            Assert.False(layer.IsEffectivelyVisible);
            Assert.Equal("未连接游戏", Named<TextBlock>(window, "ConnectorChipText").Text);
            Assert.Equal("已启用，请从 Nexon Launcher 重新启动台服并进入角色", Named<TextBlock>(window, "ConnectorMessageText").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SettingsCardAsksTheShellForTheSameDialogAndStaysConsistentWithTheChip()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            var page = Page<SettingsPage>(window);
            var card = Named<GameConnectorCardView>(page, "ConnectorCard");
            Assert.Equal("游戏连接器未启用", Named<TextBlock>(card, "ConnectorStatusText").Text);

            // 卡片里首次启用：弹出主窗口里的同一个风险确认窗口，确认前不发请求。
            Click(window, Named<Button>(card, "ConnectorOnButton"));
            Assert.True(shell.Connector.RiskOpen);
            Assert.True(Named<Panel>(window, "ConnectorRiskLayer").IsEffectivelyVisible);
            Assert.Empty(backend.ConnectorCommands);
            Click(window, Named<Button>(window, "ConnectorRiskConfirm"));
            Assert.Equal(new[] { ("enable", true, (string?)null) }, backend.ConnectorCommands);

            // 胶囊与卡片读同一份状态：两处文字一致。
            Assert.Equal("未连接游戏", Named<TextBlock>(window, "ConnectorChipText").Text);
            Assert.Equal(shell.Connector.StatusLine, Named<TextBlock>(card, "ConnectorStatusText").Text);
            Assert.Equal("已启用，游戏未运行", Named<TextBlock>(card, "ConnectorStatusText").Text);
            Assert.Equal("已启用，请从 Nexon Launcher 重新启动台服并进入角色", Named<TextBlock>(card, "ConnectorMessageText").Text);

            // 状态在别处变化（例如从另一个窗口关闭）：两处随下一次刷新同时更新。
            backend.ConnectorView = SampleBackend.SampleConnector(ConnectorState.Expired, riskAcknowledged: true, gameRunning: true);
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal("已失效", Named<TextBlock>(window, "ConnectorChipText").Text);
            Assert.Equal("连接器已失效", Named<TextBlock>(card, "ConnectorStatusText").Text);
            Assert.Equal(shell.Connector.Hint, Named<TextBlock>(card, "ConnectorHintText").Text);
            Assert.Equal("正常退出游戏后重新启用", shell.Connector.Hint);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RunningGameAndMissingCliDisableTheToggleAndExplainWhy()
    {
        // 游戏运行中：开关与恢复停用，提示先正常退出游戏。
        var running = SampleAt(ConnectorState.Connected);
        using (var model = new GameConnectorModel(running, ownsRiskLayer: false))
        {
            Assert.True(model.GameRunning);
            Assert.False(model.CanToggle);
            Assert.False(model.CanRestore);
            Assert.Equal("游戏运行中，请先正常退出游戏", model.ToggleBlocked);
            Assert.Equal("游戏运行中，请先正常退出游戏", model.RestoreBlocked);
            Assert.Equal("游戏运行中，修改前请先正常退出游戏", model.Hint);
            model.DisableCommand.Execute(null);
            model.RestoreCommand.Execute(null);
            Assert.Empty(running.ConnectorCommands);
        }

        // 命令行文件缺失：开关停用，说明发行包里没有该文件且本机也没有。
        var missing = SampleAt(ConnectorState.Unavailable);
        using (var model = new GameConnectorModel(missing, ownsRiskLayer: false))
        {
            Assert.False(model.CanToggle);
            Assert.Equal("找不到命令行工具文件", model.StatusLine);
            Assert.Equal("发行包与本机都没有该文件", model.Hint);
            Assert.Equal("找不到命令行工具文件", model.ToggleBlocked);
            Assert.Equal("未找到", model.CliLine);
            model.EnableCommand.Execute(null);
            Assert.False(model.RiskOpen);
            Assert.Empty(missing.ConnectorCommands);
        }

        // 游戏已退出：关闭与恢复可用，恢复发出一次。
        var stopped = SampleAt(ConnectorState.GameNotRunning);
        using (var model = new GameConnectorModel(stopped, ownsRiskLayer: false))
        {
            Assert.True(model.CanToggle);
            Assert.True(model.CanRestore);
            model.RestoreCommand.Execute(null);
            Assert.Equal(new[] { ("restore", false, (string?)null) }, stopped.ConnectorCommands);
            Assert.Equal("已恢复原设置", model.Message);
        }
    }

    [AvaloniaFact]
    public void RefusedCommandShowsTheReasonInChineseAndFollowsTheBackendState()
    {
        // 卡片读到的状态是游戏未运行；点按之前游戏刚好启动，后端拒绝写入：卡片写明中文原因，并采用后端当前的状态。
        var backend = SampleAt(ConnectorState.GameNotRunning);
        using var model = new GameConnectorModel(backend, ownsRiskLayer: false);
        Assert.True(model.CanToggle);
        backend.ConnectorView = SampleBackend.SampleConnector(ConnectorState.GameNotRunning, true, gameRunning: true);
        model.DisableCommand.Execute(null);
        Assert.Equal("请求未完成：游戏运行中，请先正常退出游戏", model.Message);
        Assert.True(model.MessageFailed);
        Assert.True(model.Enabled);
        Assert.False(model.CanToggle);
        Assert.True(model.GameRunning);
    }

    [AvaloniaFact]
    public void DirectoryCanBeChangedFromTheCard()
    {
        var backend = SampleAt(ConnectorState.Disabled, running: false);
        using var model = new GameConnectorModel(backend, ownsRiskLayer: false);
        Assert.Equal(@"C:\Nexon\MabinogiMobile（默认位置）", model.DirectoryLine);
        model.ChangeDirectoryCommand.Execute(null);
        Assert.True(model.EditingDirectory);
        Assert.Equal(@"C:\Nexon\MabinogiMobile", model.DirectoryInput);
        model.DirectoryInput = @"D:\Games\MabinogiMobile_TW";
        model.ApplyDirectoryCommand.Execute(null);
        Assert.False(model.EditingDirectory);
        Assert.Equal(new[] { ("set-directory", false, (string?)@"D:\Games\MabinogiMobile_TW") }, backend.ConnectorCommands);
        Assert.Equal(@"D:\Games\MabinogiMobile_TW（手动指定）", model.DirectoryLine);
    }

    // ---- LiveBackend 对接口的读取 ----

    private static GameConnectorSnapshot Snapshot(GameConnectorStatus status, ulong revision, bool risk = true, bool running = true,
        string raw = "") => new(CombatStubHost.Epoch, new(revision), status, raw, status != GameConnectorStatus.Disabled, risk,
        new(GameConnectorCliSource.Package, GameConnectorCliFileState.Verified, "0.1.0+b587207e", true, false),
        new(running, @"C:\Nexon\MabinogiMobile", GameConnectorDirectorySource.Bound),
        new(false, "gameCli.alreadyEnabled", true, null, true, null), new(true, false, 42), DateTimeOffset.UnixEpoch, null, null);

    private static (LiveBackend Backend, CombatStubHost Stub) Connect(Action<CombatStubHost>? before = null)
    {
        var stub = new CombatStubHost();
        before?.Invoke(stub);
        var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "instance.json"),
            action => Dispatcher.UIThread.Post(action))
        {
            Handler = () => stub,
            Bootstrap = _ => Task.FromResult(stub.Bootstrap)
        };
        backend.Start();
        return (backend, stub);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds), "等待超时：" + what);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task LiveBackendReadsTheConnectorAndQueriesScoreSupportOnlyWhileConnected()
    {
        var (backend, stub) = Connect(host =>
        {
            host.Connector = Snapshot(GameConnectorStatus.Connected, 1);
            host.ScoreSupport["own"] = GameScoreSupportState.UserComposed;
            host.ScoreSupport["game"] = GameScoreSupportState.Supported;
        });
        await using var _ = backend;
        await Until(() => backend.GetGameConnector().IsLoaded, "读到连接器快照");
        var view = backend.GetGameConnector();
        Assert.Equal(ConnectorState.Connected, view.State);
        Assert.True(view.RiskAcknowledged);
        Assert.True(view.GameIsRunning);
        Assert.Equal(GameConnectorCliSource.Package, view.CliSource);
        Assert.Equal(@"C:\Nexon\MabinogiMobile", view.GameDirectory);
        Assert.Equal("已连接", ConnectorText.Status(view));

        // 连接器已连接：登记的乐谱批量查询一次，结果进入只读缓存，没登记的与没答案的是未知。
        backend.RequestScoreSupport(["own", "game", "unlisted"]);
        await Until(() => backend.GameScoreSupportOf("own").State == ScoreSupportState.UserComposed, "查到可演奏性");
        Assert.Equal(ScoreSupportState.Supported, backend.GameScoreSupportOf("game").State);
        Assert.Equal(ScoreSupportState.Unknown, backend.GameScoreSupportOf("unlisted").State);
        Assert.Equal(ScoreSupportState.Unknown, backend.GameScoreSupportOf("never-asked").State);
        Assert.Equal(new[] { "own", "game", "unlisted" }, stub.ScoreSupportQueries.Single());

        // 状态与乐谱来源都没变：已查到的不重复查询（只有“未知”的结果按五秒间隔复查，这里等待不到一个间隔）。
        await Task.Delay(800, TestContext.Current.CancellationToken);
        backend.RequestScoreSupport(["own", "game"]);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Single(stub.ScoreSupportQueries);

        // 连接器回到未启用：可演奏性全部回到未知，不再查询；写操作的回应直接成为最新状态。
        stub.Connector = Snapshot(GameConnectorStatus.Disabled, 2, running: false);
        var reply = await backend.CommandGameConnectorAsync("refresh", false, null, TestContext.Current.CancellationToken);
        Assert.True(reply.Ok);
        Assert.Equal(new GameConnectorCommand("refresh"), stub.ConnectorCommands.Single());
        Assert.Equal(ConnectorState.Disabled, backend.GetGameConnector().State);
        Assert.Equal(ScoreSupportState.Unknown, backend.GameScoreSupportOf("own").State);
        backend.RequestScoreSupport(["own", "fresh"]);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Single(stub.ScoreSupportQueries);
    }

    [AvaloniaFact]
    public async Task LiveBackendTreatsAnOldHostWithoutTheEndpointAsUnsupportedAndStopsProbing()
    {
        var (backend, stub) = Connect();
        await using var _ = backend;
        await Until(() => backend.GetGameConnector().Read == ConnectorRead.Unsupported, "探测到旧宿主没有连接器接口");
        Assert.Equal("未知", ConnectorText.Status(backend.GetGameConnector()));
        Assert.Equal("后端没有提供游戏连接器", ConnectorText.Line(backend.GetGameConnector()));
        backend.RequestScoreSupport(["a"]);
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.Equal(1, stub.Count("GET /api/v1/game/connector"));
        Assert.Equal(0, stub.Count("POST /api/v1/game/connector/score-support/query"));
        Assert.Equal(ScoreSupportState.Unknown, backend.GameScoreSupportOf("a").State);
    }

    [AvaloniaFact]
    public void UnrecognizedStatusAndOptionValuesFromANewerBackendReadAsUnknown()
    {
        var snapshot = Snapshot(GameConnectorStatus.Connected, 1) with
        {
            Status = new OpenEnum<GameConnectorStatus>("something-new"),
            Cli = new(new OpenEnum<GameConnectorCliSource>("cloud"), new OpenEnum<GameConnectorCliFileState>("odd"), null, false, false)
        };
        var view = GameConnectorView.From(snapshot);
        Assert.Equal(ConnectorState.Unknown, view.State);
        Assert.Equal("未知", ConnectorText.Status(view));
        Assert.Equal("来源未知 · 核对结果未知", ConnectorText.Cli(view));
    }
}
