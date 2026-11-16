using System.Diagnostics;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.ModuleSdk;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.Host.Combat;

/// <summary>
/// 怪物、技能与效果名称表的常驻缓存。首次查询时按当前游戏绑定在后台读取客户端 Sheetdata（同一进程只读一次），
/// 读取期间与失败时名称为 unavailable 并带原因；失败后绑定换进程或满一分钟才重试。只保留最近一个客户端的三张表
/// （当前客户端约 2.1 万条、约 1.5 MiB），统计与历史不依赖名称是否可用。
/// </summary>
public sealed class CombatNameCatalog(GameControlContext controls, TimeProvider clock) : ICombatNameResolver
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private CombatNameTables? tables;
    private (int Pid, DateTimeOffset Started)? source, attempted;
    private Task? loading;
    private string? failure;
    private long failedAt;
    /// <summary>测试与离线工具注入的读取函数；默认按进程映像路径定位客户端目录。</summary>
    internal Func<GameSession, CancellationToken, CombatNameTables>? Reader { get; set; }

    public long ResidentBytes { get { lock (gate) return tables?.Bytes ?? 0; } }

    public CombatResolvedName Resolve(string kind, uint sheetId)
    {
        var current = Current(out var reason);
        if (current is null) return new(kind, new(sheetId), null, CombatNames.Unavailable, reason);
        var key = kind switch { CombatNames.Monster => "monster", CombatNames.Skill => "skill", CombatNames.Effect => "effect", _ => null };
        if (key is null) return new(kind, new(sheetId), null, CombatNames.Unknown, "combat.nameKindUnknown");
        if (current.Unsupported.TryGetValue(key, out var unsupported)) return new(kind, new(sheetId), null, CombatNames.Unavailable, unsupported);
        var table = key switch { "monster" => current.Monsters, "skill" => current.Skills, _ => current.Effects };
        return table.TryGetValue(sheetId, out var text)
            ? new(kind, new(sheetId), text, CombatNames.Resolved)
            : new(kind, new(sheetId), null, CombatNames.Unknown, "combat.nameNotFound");
    }

    public string? EffectPolarity(uint sheetId) =>
        Current(out _) is { EffectPolarities: { } polarities } && polarities.TryGetValue(sheetId, out var polarity) ? polarity : null;

    private CombatNameTables? Current(out string reason)
    {
        lock (gate)
        {
            var binding = controls.Bindings.Current?.Session;
            var process = binding is null ? ((int, DateTimeOffset)?)null : (binding.ProcessId, binding.ProcessStartTime);
            var stale = process is not null && source != process;
            if (binding is not null && stale && loading is null && (attempted != process || failure is null || clock.GetElapsedTime(failedAt) >= RetryInterval))
            {
                attempted = process; failure = null;
                loading = Task.Run(() => Load(binding));
            }
            // 读取新客户端期间继续使用上一份表；从未读到时说明原因。
            if (tables is not null) { reason = ""; return tables; }
            reason = loading is not null ? "combat.nameCatalogLoading" : failure ?? (binding is null ? "game.bindingRequired" : "combat.nameCatalogLoading");
            return null;
        }
    }

    private void Load(GameSession binding)
    {
        CombatNameTables? result = null; string? error = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            result = (Reader ?? ReadClient)(binding, deadline.Token);
        }
        catch (Exception value) when (value is InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException or
            InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        { error = "combat.nameCatalogUnavailable"; }
        lock (gate)
        {
            loading = null;
            if (result is not null) { tables = result; source = (binding.ProcessId, binding.ProcessStartTime); }
            else { failure = error; failedAt = clock.GetTimestamp(); }
        }
    }

    private static CombatNameTables ReadClient(GameSession binding, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || binding.Region != "TW") throw new PlatformNotSupportedException();
        string image;
        using (var process = Process.GetProcessById(binding.ProcessId))
        {
            if (process.HasExited || process.StartTime.ToUniversalTime() != binding.ProcessStartTime.UtcDateTime)
                throw new InvalidOperationException("绑定进程已变化");
            image = process.MainModule?.FileName ?? throw new InvalidOperationException("无法读取进程映像路径");
        }
        var directory = ClientItemCatalogLocation.Resolve(image);
        LocalDiscoverySecurity.RejectLinks(directory);
        return ClientCombatNameCatalog.Extract(directory, ct);
    }
}
