using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Host.GameCli;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.Tests;

/// <summary>替身命令行：按指令名返回预设响应，支持按指令延迟（可取消），记录每次调用的指令与参数。</summary>
internal sealed class DataRunner : ITwCliRunner
{
    private readonly object sync = new();
    private readonly List<string[]> calls = [];
    public Dictionary<string, Func<string[], TwCliRun>> Handlers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TimeSpan> Delays { get; } = new(StringComparer.Ordinal);
    public string[] Commands { get { lock (sync) return [.. calls.Select(x => x[0])]; } }
    public int CountOf(string command) { lock (sync) return calls.Count(x => x[0] == command); }
    public string[][] Calls { get { lock (sync) return [.. calls]; } }

    public static TwCliRun Ok(string json) => new(0, json, "", false, TimeSpan.FromMilliseconds(2));

    /// <summary>某个指令的 body 文本（调用参数里 <c>base64:</c> 后的 UTF-8 解码）。</summary>
    public static string BodyOf(string[] call) => call.Length > 1 ? Encoding.UTF8.GetString(Convert.FromBase64String(call[1]["base64:".Length..])) : string.Empty;

    public async Task<TwCliRun> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        lock (sync) calls.Add([.. arguments]);
        TimeSpan delay;
        lock (sync) delay = Delays.GetValueOrDefault(arguments[0]);
        if (delay > TimeSpan.Zero)
        {
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { return new(-1, "", "", true, delay); }
        }
        Func<string[], TwCliRun>? handler;
        lock (sync) Handlers.TryGetValue(arguments[0], out handler);
        return handler is null ? Ok("{}") : handler([.. arguments]);
    }
}

/// <summary>连接器测试装置：临时目录里的假游戏目录、偏好与命令行文件，替身命令行与进程探测；用后删除。</summary>
internal sealed class ConnectedRig : IDisposable
{
    private static readonly byte[] CliBytes = Encoding.UTF8.GetBytes("test command line tool");
    private readonly string root;

    public sealed class FakeProbe : IGameProcessProbe
    {
        public GameProcessInfo Info { get; set; } = new(GameProcessState.NotRunning);
        public GameProcessInfo Probe(string gameDirectory) => Info;
    }

    public FakeProbe Process { get; } = new();
    public DataRunner Runner { get; } = new();
    public GameConnectorService Service { get; }
    public string[] Catalog { get; private set; } = [];

    public ConnectedRig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
        root = Path.Combine(directory!.FullName, "artifacts", "tmp", "game-data-tests", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game"); var prefs = Path.Combine(root, "prefs");
        Directory.CreateDirectory(game); Directory.CreateDirectory(prefs);
        File.WriteAllText(Path.Combine(game, TwCliFile.GameExecutable), "game");
        File.WriteAllText(Path.Combine(prefs, "BaseMMPlayerPrefs.txt"), "BOOL\nother_flag : True\n");
        var cli = Path.Combine(root, "package", TwCliFile.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllBytes(cli, CliBytes);
        var environment = new GameConnectorEnvironment(Path.Combine(root, "state"), prefs, [game], Process, _ => null, Runner,
            () => cli, () => [], null, Convert.ToHexStringLower(SHA256.HashData(CliBytes)));
        var time = TimeProvider.System;
        var controls = MabiTool.Host.Game.GameControlContext.Create(new ResourceLeaseCoordinator(time), time, true);
        Service = new(controls, new("Moongate", "0.1.0", "1.0", "epoch", DateTimeOffset.UtcNow, "test", "test"), new GameConnectorSettingsStore(null), environment, time);
        Runner.Handlers["status"] = _ => DataRunner.Ok("{\"pipe\":\"connected\"}");
        Runner.Handlers["capabilities"] = _ => DataRunner.Ok("{\"commands\":[" + string.Join(",", Catalog.Select(c =>
            "{\"Command\":\"" + c + "\",\"Description\":\"d\"" + CommandNote(c) + "}")) + "]}");
    }

    /// <summary>目录条目的备注（冷却说明），用来验证冷却取自目录。</summary>
    public Dictionary<string, string> Notes { get; } = new(StringComparer.Ordinal);

    private string CommandNote(string command) => Notes.TryGetValue(command, out var note) ? ",\"Note\":\"" + note + "\"" : string.Empty;

    /// <summary>启用、让游戏处于运行状态并取得目录，使连接器为已连接。</summary>
    public async Task ConnectAsync(params string[] commands)
    {
        Catalog = commands;
        await Service.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-" + Guid.NewGuid().ToString("N"));
        Process.Info = new(GameProcessState.Running, 100, 1);
        var state = await Service.RefreshAsync(true);
        if (state.Status.KnownValue != GameConnectorStatus.Connected) throw new InvalidOperationException("连接器未连接：" + state.Reason);
    }

    public async Task DisconnectAsync()
    {
        Runner.Handlers["status"] = _ => new(5, "", "{\"reason\":\"option_off\"}", false, TimeSpan.Zero);
        await Service.RefreshAsync(true);
    }

    public void Dispose() { Service.Dispose(); Directory.Delete(root, true); }
}
