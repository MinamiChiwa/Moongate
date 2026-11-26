using MabiTool.Client;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 快捷键修改、候选窗口、更新策略与合奏本机设置。每次请求只发送一次并带新的幂等键；需要修订的请求先读取当前 ETag。
// 回执同时交回页面（显示在编辑处）和活动胶囊。
public sealed partial class LiveBackend
{
    public Task<BackendReply> ChangeShortcutAsync(string actionId, KeyChord chord, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            string? tag = null;
            var settings = await api.GetShortcutSettingsAsync(token, metadata => tag = metadata.ETag);
            var binding = new ShortcutBindingSetting(actionId, new(chord.Key, chord.Control, chord.Alt, chord.Shift, chord.Windows));
            var bindings = settings.Bindings.Where(item => item.ActionId != actionId).Append(binding).ToList();
            var result = await api.ChangeShortcutSettingsAsync(new(bindings), IdempotencyKey(), tag ?? string.Empty, token);
            lock (gate) pagesDue = true;
            // 设置已保存；能否注册到系统由后端的注册状态说明。
            var registration = result.Registrations.FirstOrDefault(item => item.ActionId == actionId);
            if (registration is { Registered: not null, FailureCode: null })
            {
                var actual = registration.Registered;
                var registered = new KeyChord(actual.Key, actual.Control, actual.Alt, actual.Shift, actual.Windows);
                return Pick($"已注册 {registered}", $"已註冊 {registered}");
            }
            throw new RegistrationException(registration is null
                ? Pick($"已保存 {chord}，注册状态未知", $"已儲存 {chord}，註冊狀態未知")
                : Pick($"已保存 {chord}，{ShortcutFailure(registration.FailureCode ?? "unknown")}",
                    $"已儲存 {chord}，{ShortcutFailure(registration.FailureCode ?? "unknown")}"));
        }, cancellationToken);

    // 候选窗口每次都重新读取；与当前绑定是同一窗口（进程号、进程启动时间、窗口标识都相同）的一项标为当前。
    public async Task<GameWindowListReply> ListGameWindowsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GameWindowChoice> windows = [];
        var reply = await ReplyAsync(async (api, token) =>
        {
            var list = await api.GetGameWindowsAsync(token);
            var session = (await api.GetGameBindingAsync(token)).Binding?.Session;
            windows = list.Items.Select(candidate => new GameWindowChoice(candidate.Id, candidate.Name, candidate.ProcessName,
                candidate.ProcessId, candidate.Region, session is not null && session.ProcessId == candidate.ProcessId
                    && session.ProcessStartTime == candidate.ProcessStartTime && session.WindowIdentity == candidate.WindowIdentity)).ToList();
            return string.Empty;
        }, cancellationToken, remember: false);
        return new(reply.Ok, reply.Message, windows);
    }

    public Task<BackendReply> SelectGameWindowAsync(string candidateId, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            string? tag = null;
            await api.GetGameBindingAsync(token, metadata => tag = metadata.ETag);
            var snapshot = await api.SelectGameBindingAsync(new(candidateId), IdempotencyKey(), tag ?? string.Empty, token);
            // 换了窗口，旧窗口观测到的昵称不再适用；下一次页面刷新重新读取。
            lock (gate)
            {
                state = state with { Binding = snapshot };
                pages = pages with { Nickname = null, Identity = null };
            }
            return Pick("已绑定游戏窗口", "已綁定遊戲視窗");
        }, cancellationToken);

    // 请求体是完整的策略；界面不显示的下载并发数按页面读到的原值带回。
    public Task<BackendReply> SaveUpdatePolicyAsync(UpdatePolicyView policy, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            string? tag = null;
            await api.GetUpdatePolicyAsync(token, metadata => tag = metadata.ETag);
            var saved = await api.SaveUpdatePolicyAsync(new(policy.AutomaticChecks, policy.CheckIntervalHours,
                policy.DownloadConcurrency, policy.MaximumBytesPerSecond), IdempotencyKey(), tag ?? string.Empty, token);
            lock (gate) pages = pages with { Policy = saved };
            return Pick("已保存更新策略", "已儲存更新策略");
        }, cancellationToken);

    // 只读取：不改页面状态、不触发重读；读取失败（未连接、没有可用更新、后端读不到）时返回空，确认窗口写明未能读取。
    public async Task<UpdateNotesView?> LoadUpdateNotesAsync(CancellationToken cancellationToken)
    {
        var (current, page, _, _) = ReadPages();
        if (client is not { } api || !current.Connected || page.Update?.Available?.PlanId is not { } plan) return null;
        try
        {
            var notes = await api.GetUpdateNotesAsync(plan, cancellationToken);
            return new(notes.ReleaseId, notes.Entries.Select(entry => new UpdateNotesText(entry.Language, entry.Markdown)).ToList());
        }
        catch (Exception error) when (error is MabiToolApiException or InvalidOperationException or HttpRequestException
                                          or IOException or OperationCanceledException or FormatException)
        {
            return null;
        }
    }

    // 读取时记下 ETag；保存时以它为 If-Match，请求体里的修订与界面不显示的字段都是读取时的原值。
    public async Task<RoomPreferencesReply> LoadRoomPreferencesAsync(CancellationToken cancellationToken)
    {
        RoomPreferences? value = null;
        string? tag = null;
        var reply = await ReplyAsync(async (api, token) =>
        {
            value = await api.GetRoomPreferencesAsync(token, metadata => tag = metadata.ETag);
            return string.Empty;
        }, cancellationToken, remember: false);
        return reply.Ok && value is not null ? new(value, tag ?? string.Empty, string.Empty) : new(null, string.Empty, reply.Message);
    }

    public Task<BackendReply> SaveRoomPreferencesAsync(RoomPreferences preferences, string etag, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            await api.EditRoomPreferencesAsync(preferences, IdempotencyKey(), etag, token);
            MarkRoomPreferencesDue();
            return Pick("已保存合奏本机设置", "已儲存合奏本機設定");
        }, cancellationToken);

    /// <summary>设置已保存但没能注册到系统：回执标为未成功，消息是后端的注册状态。</summary>
    private sealed class RegistrationException(string message) : Exception(message);

    private async Task<BackendReply> ReplyAsync(Func<MabiToolClient, CancellationToken, Task<string>> send,
        CancellationToken cancellationToken, bool remember = true)
    {
        BackendReply reply;
        try
        {
            var api = client ?? throw new InvalidOperationException(Pick("未连接后端", "未連接後端"));
            if (!Read().State.Connected) throw new InvalidOperationException(Pick("未连接后端", "未連接後端"));
            reply = new(true, await WithStorageRetryAsync(() => send(api, cancellationToken), cancellationToken));
        }
        catch (RegistrationException error) { reply = new(false, error.Message); }
        catch (Exception error) when (error is MabiToolApiException or InvalidOperationException or HttpRequestException
                                          or IOException or OperationCanceledException or FormatException)
        {
            reply = new(false, Problem(error), (error as MabiToolApiException)?.Problem?.Code);
        }
        if (remember) Remember(reply.Message);
        lock (gate) pagesDue = true;
        Signal();
        return reply;
    }

    private void Remember(string message)
    {
        lock (gate) notice = message;
        Notify();
    }

    private static string Problem(Exception error)
    {
        return Pick("请求未完成：", "請求未完成：") + Localization.CodeText.Error(error);
    }
}
