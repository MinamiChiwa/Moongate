using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Shortcuts;

public sealed class ShortcutSettingsService(ShortcutSettingsStore? store = null, IGlobalShortcutSource? source = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// 扩展清单声明的命令（含不可用的）；由扩展运行时提供。不可用的命令绑定保留但不注册，
    /// 与内建动作的绑定组合相同时也不注册（内建优先），注册结果写明原因。
    /// </summary>
    public Func<IReadOnlyList<ShortcutActionSnapshot>>? ExtensionActions { get; set; }

    /// <summary>扩展可用性变化后按保存的配置重新注册；与修改配置互斥。</summary>
    public Task RefreshAsync(CancellationToken token = default) => RestoreAsync(token);
    public async Task<ShortcutSettingsSnapshot> GetAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { return Project(store?.Read() ?? ShortcutSettingsStore.Default); }
        finally { gate.Release(); }
    }
    public async Task RestoreAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (source?.CanReconfigure == true)
                await source.ReconfigureAsync(Requests(store?.Read() ?? ShortcutSettingsStore.Default), token);
        }
        finally { gate.Release(); }
    }
    public async Task<ShortcutSettingsSnapshot> UpdateAsync(string principal, ShortcutSettingsRequest request,
        string? key, string? condition, CancellationToken token = default)
    {
        request = ShortcutSettingsStore.Normalize(request); ShortcutSettingsStore.ValidateHeaders(key, condition);
        if (store is null) throw new OperationException("storage.unavailable", "快捷键配置需要持久化宿主");
        await gate.WaitAsync(token);
        try
        {
            if (store.Receipt(principal, request, key, condition) is { } receipt) return receipt;
            var previous = store.Read();
            if (condition != ShortcutSettingsStore.ETag(previous.Revision)) throw new OperationException("revision.stale", "快捷键配置已变化");
            var configured = previous with { Bindings = request.Bindings };
            token.ThrowIfCancellationRequested();
            try
            {
                if (source?.CanReconfigure == true) await source.ReconfigureAsync(Requests(configured), token);
                // 注册已经变更后先完成持久提交，确保取消不会留下无法恢复的组合。
                var saved = await store.UpdateAsync(principal, request, Project(configured).Registrations, key, condition, CancellationToken.None);
                return saved with { Actions = [.. ShortcutSettingsStore.Default.Actions, .. Extensions()] };
            }
            catch
            {
                if (source?.CanReconfigure == true) await source.ReconfigureAsync(Requests(previous), CancellationToken.None);
                throw;
            }
        }
        finally { gate.Release(); }
    }
    private ShortcutSettingsSnapshot Project(ShortcutSettingsSnapshot configured)
    {
        var available = source?.GetRegistrations() ?? [];
        var extensions = Extensions();
        var registrations = configured.Bindings.Select(value =>
        {
            if (ShortcutSettingsStore.IsExtensionAction(value.ActionId) && Skipped(configured, value, extensions) is { } reason)
                return new GlobalShortcutSnapshot(value.ActionId, value.Chord, null, "unavailable", reason, false);
            var actual = available.FirstOrDefault(result => result.ActionId == value.ActionId && Chord(result.Configured) == value.Chord);
            return actual is null
                ? new GlobalShortcutSnapshot(value.ActionId, value.Chord, null, "unavailable", "shortcut.sourceUnavailable", false)
                : new GlobalShortcutSnapshot(actual.ActionId, Chord(actual.Configured), actual.Registered is { } key ? Chord(key) : null,
                    actual.Status == ShortcutRegistrationStatus.Registered ? "registered" : "unavailable", actual.FailureCode, actual.UsedFallback);
        }).ToArray();
        return configured with { Actions = [.. ShortcutSettingsStore.Default.Actions, .. extensions], Registrations = registrations };
    }

    private IReadOnlyList<ShortcutActionSnapshot> Extensions() => ExtensionActions?.Invoke() ?? [];

    /// <summary>扩展命令的绑定不注册的原因：命令已不存在、扩展不可用，或与内建动作的组合相同。</summary>
    private static string? Skipped(ShortcutSettingsSnapshot configured, ShortcutBindingSetting binding, IReadOnlyList<ShortcutActionSnapshot> extensions)
    {
        var action = extensions.FirstOrDefault(x => x.ActionId == binding.ActionId);
        if (action is null) return "extension.commandMissing";
        if (action.UnavailableReason is { } reason) return reason;
        return configured.Bindings.Any(other => !ShortcutSettingsStore.IsExtensionAction(other.ActionId) && other.Chord == binding.Chord) ||
            ShortcutSettingsStore.IsReservedChord(binding.Chord) ? "shortcut.conflictWithConfiguredAction" : null;
    }

    private static ShortcutChordSnapshot Chord(ShortcutChord value) => new(value.Key.ToString(), value.Control, value.Alt, value.Shift, value.Windows);
    private ShortcutRequest[] Requests(ShortcutSettingsSnapshot value)
    {
        var extensions = Extensions();
        return value.Bindings.Where(binding => !ShortcutSettingsStore.IsExtensionAction(binding.ActionId) || Skipped(value, binding, extensions) is null)
            .Select(binding =>
            {
                ShortcutKeys.TryParse(binding.Chord.Key, out var key);
                return new ShortcutRequest(binding.ActionId, new(key, binding.Chord.Control, binding.Chord.Alt, binding.Chord.Shift, binding.Chord.Windows), []);
            }).ToArray();
    }
}
