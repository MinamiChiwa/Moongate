using MabiTool.Client;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

public sealed partial class LiveBackend
{
    /// <summary>一次测量操作固定动作、目标与幂等键；快捷键首次读取阶段，重试沿用同一个测量 ID。</summary>
    internal sealed class MeasurementCall(string intent, CombatMeasurementState? shown, string? observationId)
    {
        private readonly string key = IdempotencyKey();
        private CombatMeasurementState? measurement = shown;
        private bool resolved = intent != "combat.measure.toggle";
        internal bool Cancelling { get; private set; }
        internal CombatMeasurementSettings Settings => measurement?.Settings ?? new();

        internal async Task<CombatMeasurementState> ExecuteAsync(MabiToolClient api, CancellationToken token)
        {
            if (!resolved) { measurement = await api.GetCombatMeasurementAsync(token); resolved = true; }
            Cancelling = intent == "combat.measure.cancel" || intent == "combat.measure.toggle" && measurement?.Phase is "waiting" or "countdown" or "running";
            if (Cancelling)
            {
                var id = measurement?.MeasurementId
                    ?? throw new InvalidOperationException(Pick("定时测量已结束，请刷新后再试", "定時測量已結束，請重新整理後再試"));
                return await api.CancelCombatMeasurementAsync(new(id), key, token);
            }
            var observation = observationId ?? throw new InvalidOperationException(Pick("需要先开始 DPS 观测", "需要先開始 DPS 觀測"));
            // 预备倒计时已停用：测量从本机第一次有效伤害起计时。
            return await api.StartCombatMeasurementAsync(new(observation, Settings.DurationMs, 0), key, token);
        }
    }

    /// <summary>保存使用打开设定层时的配置修订；存储重试保留同一强 ETag 和幂等键。</summary>
    internal static Func<MabiToolClient, CancellationToken, Task<CombatMeasurementState>> MeasurementSettingsWrite(CombatMeasurementSettings settings, ulong settingsRevision)
    {
        var tag = $"\"combat-measurement-settings-{new UInt64String(settingsRevision)}\"";
        var key = IdempotencyKey();
        return (api, token) => api.SaveCombatMeasurementSettingsAsync(settings, key, tag, token);
    }

    internal static Func<MabiToolClient, CancellationToken, Task<CombatAutoObservationState>> AutoObservationWrite(bool enabled)
    {
        string? tag = null;
        var key = IdempotencyKey();
        return async (api, token) =>
        {
            if (tag is null)
            {
                await api.GetCombatAutoObservationAsync(token, metadata => tag = metadata.ETag);
                if (tag is null) throw new InvalidOperationException(Pick("后端没有返回自动观测设定修订标记", "後端沒有返回自動觀測設定修訂標記"));
            }
            return await api.SetCombatAutoObservationAsync(new(enabled), key, tag, token);
        };
    }
}
