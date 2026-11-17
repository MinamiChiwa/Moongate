using System.Runtime.InteropServices;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 激活绑定的游戏窗口：没有绑定时不发请求；请求前允许后端进程前置窗口，以回执里的 foreground 判断是否已切到游戏。
public sealed partial class LiveBackend
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    public async Task<GameWindowActivation> ActivateGameWindowAsync(CancellationToken cancellationToken)
    {
        var (current, _, _, _) = ReadPages();
        if (current.Binding?.Binding is null) return GameWindowActivation.Unbound;
        if (!current.Connected || client is not { } api)
            return new(true, false, false, false, null, Pick("未连接后端", "未連接後端"));
        AllowHostForeground();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ActivationTimeout);
        try
        {
            return Activation(await api.ActivateGameWindowAsync(timeout.Token));
        }
        catch (MabiToolApiException error)
        {
            var code = error.Problem?.Code ?? $"http.{error.Status}";
            var holder = error.Problem?.Details is { } details && details.TryGetValue("occupiedBy", out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                : null;
            return Rejected(code, holder);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(true, false, false, false, null, Pick("后端没有及时回应", "後端沒有及時回應"));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            return new(true, false, false, false, null, error.Message);
        }
    }

    /// <summary>回执：没有发出系统请求（模拟宿主或没有窗口来源）、发出但窗口没到前台、或已在前台。</summary>
    internal static GameWindowActivation Activation(GameWindowActivationReceipt receipt) => new(true, receipt.Attempted, receipt.Restored,
        receipt.Foreground, null,
        receipt.Foreground ? string.Empty
        : !receipt.Attempted ? receipt.Simulation
            ? Pick("模拟宿主没有真实的游戏窗口", "模擬宿主沒有真實的遊戲視窗")
            : Pick("后端没有可激活的游戏窗口", "後端沒有可啟用的遊戲視窗")
        : receipt.Restored ? Pick("系统没有把游戏窗口切到前台", "系統沒有把遊戲視窗切到前景")
        : Pick("游戏窗口仍是最小化", "遊戲視窗仍是最小化"));

    /// <summary>后端拒绝：绑定已变化、输入被其他操作占用、没有权限，其余显示错误码。</summary>
    internal static GameWindowActivation Rejected(string code, string? holder) => new(true, false, false, false, code, code switch
    {
        "input.bindingChanged" => Pick("游戏窗口绑定已变化，请重新选择游戏窗口", "遊戲視窗綁定已變化，請重新選擇遊戲視窗"),
        "resource.busy" => holder is { Length: > 0 }
            ? Pick($"游戏输入正被占用（{holder}）", $"遊戲輸入正被佔用（{holder}）")
            : Pick("游戏输入正被其他操作占用", "遊戲輸入正被其他操作佔用"),
        "permission.denied" => Pick("没有控制游戏窗口的权限", "沒有控制遊戲視窗的權限"),
        _ => Pick("请求未完成：", "請求未完成：") + Localization.CodeText.Reason(code)
    });

    // Windows 只允许前台进程把前台让给别的进程：Moongate 仍在前台时，允许后端进程（本机发现文件里的进程号）前置窗口；读不到进程号时允许任意进程。
    private void AllowHostForeground()
    {
        if (!OperatingSystem.IsWindows()) return;
        var process = AnyProcess;
        try
        {
            var bytes = File.ReadAllBytes(discoveryPath);
            if (JsonSerializer.Deserialize<LocalDiscoveryInfo>(bytes, ContractJson.Options) is { ProcessId: > 0 } discovery)
                process = (uint)discovery.ProcessId;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        _ = AllowSetForegroundWindow(process);
    }

    private const uint AnyProcess = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
