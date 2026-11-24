using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 客户端 Buff 表（收藏层的可选范围）：只在收藏层打开时按需读取，不进例行刷新循环。读到后按游戏绑定的进程缓存，
// 游戏换了进程（客户端可能更新、名称表随之变化）时下次打开重读；宿主仍在读取名称表（loading）时每秒重读，至多一分钟。
public sealed partial class LiveBackend
{
    /// <summary>宿主名称表读取中时重读目录的间隔与次数上限。</summary>
    internal static readonly TimeSpan ClientBuffRetry = TimeSpan.FromSeconds(1);
    internal const int ClientBuffRetries = 60;

    private ClientBuffList clientBuffs = ClientBuffList.NotRead;
    private string? clientBuffBinding;
    private bool clientBuffReading;

    public event EventHandler? ClientBuffsChanged;

    public ClientBuffList GetClientBuffs() { lock (gate) return clientBuffs; }

    public void RequestClientBuffs()
    {
        MabiToolClient? api;
        string binding;
        lock (gate)
        {
            binding = BuffBindingKey(state.Binding);
            if (clientBuffReading || clientBuffs.Ready && clientBuffBinding == binding) return;
            api = state.Connected ? client : null;
            if (api is null)
            {
                clientBuffs = new([], false, Pick("未连接后端", "未連接後端"));
                dispatch(() => ClientBuffsChanged?.Invoke(this, EventArgs.Empty));
                return;
            }
            clientBuffReading = true;
            clientBuffs = clientBuffs with { Loading = true, Problem = null };
        }
        dispatch(() => ClientBuffsChanged?.Invoke(this, EventArgs.Empty));
        _ = ReadClientBuffsAsync(api, binding);
    }

    private async Task ReadClientBuffsAsync(MabiToolClient api, string binding)
    {
        ClientBuffList result;
        try
        {
            var attempt = 0;
            CombatClientBuffCatalog catalog;
            while (true)
            {
                catalog = await api.GetCombatClientBuffsAsync(lifetime.Token);
                if (catalog.State.KnownValue != CombatClientBuffCatalogState.Loading || ++attempt >= ClientBuffRetries) break;
                await Task.Delay(ClientBuffRetry, lifetime.Token);
            }
            result = Map(catalog);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is MabiToolApiException or HttpRequestException or IOException or InvalidDataException
                                          or OperationCanceledException or System.Text.Json.JsonException or ObjectDisposedException)
        {
            result = new([], false, CodeText.Error(error));
        }
        lock (gate)
        {
            clientBuffReading = false;
            clientBuffs = result;
            clientBuffBinding = binding;
        }
        dispatch(() => ClientBuffsChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>后端目录换成收藏层用的列表；未就绪时写明原因（原因码换成中文，未登记的写“原因未知”）。</summary>
    internal static ClientBuffList Map(CombatClientBuffCatalog catalog)
    {
        if (catalog.State.KnownValue == CombatClientBuffCatalogState.Ready)
            return new(catalog.Buffs.Select(buff => new ClientBuffEntry(buff.Id.ToString(), buff.Name, CombatMapper.Polarity(buff.Polarity), buff.Description))
                .ToArray(), false, catalog.Buffs.Count == 0 ? Pick("客户端 Buff 表里没有可显示的效果", "用戶端 Buff 表裡沒有可顯示的效果") : null);
        if (catalog.State.KnownValue == CombatClientBuffCatalogState.Loading)
            return new([], false, Pick("客户端名称表仍在读取，稍后重新打开收藏层", "用戶端名稱表仍在讀取，稍後重新開啟收藏層"));
        var reason = CombatNames.ReasonText(catalog.Reason);
        return new([], false, reason is null || reason == catalog.Reason
            ? CodeText.Unknown("client-buffs", catalog.Reason, "无法读取客户端 Buff 表", "無法讀取用戶端 Buff 表") : reason);
    }

    // 名称表随游戏进程变化：绑定的进程 ID 与启动时刻作为缓存键。
    private static string BuffBindingKey(GameBindingSnapshot? binding) =>
        binding?.Binding?.Session is { } session ? $"{session.ProcessId}|{session.ProcessStartTime.UtcTicks}" : "none";
}
