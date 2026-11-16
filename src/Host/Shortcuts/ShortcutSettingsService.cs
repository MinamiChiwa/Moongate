using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Shortcuts;

public sealed class ShortcutSettingsService(ShortcutSettingsStore? store = null, IGlobalShortcutSource? source = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
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
                return await store.UpdateAsync(principal, request, Project(configured).Registrations, key, condition, CancellationToken.None);
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
        var registrations = configured.Bindings.Select(value =>
        {
            var actual = available.FirstOrDefault(result => result.ActionId == value.ActionId && Chord(result.Configured) == value.Chord);
            return actual is null
                ? new GlobalShortcutSnapshot(value.ActionId, value.Chord, null, "unavailable", "shortcut.sourceUnavailable", false)
                : new GlobalShortcutSnapshot(actual.ActionId, Chord(actual.Configured), actual.Registered is { } key ? Chord(key) : null,
                    actual.Status == ShortcutRegistrationStatus.Registered ? "registered" : "unavailable", actual.FailureCode, actual.UsedFallback);
        }).ToArray();
        return configured with { Actions = ShortcutSettingsStore.Default.Actions, Registrations = registrations };
    }
    private static ShortcutChordSnapshot Chord(ShortcutChord value) => new(value.Key.ToString(), value.Control, value.Alt, value.Shift, value.Windows);
    private static ShortcutRequest[] Requests(ShortcutSettingsSnapshot value) => value.Bindings.Select(binding =>
    {
        ShortcutKeys.TryParse(binding.Chord.Key, out var key);
        return new ShortcutRequest(binding.ActionId, new(key, binding.Chord.Control, binding.Chord.Alt, binding.Chord.Shift, binding.Chord.Windows), []);
    }).ToArray();
}
