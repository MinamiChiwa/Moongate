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
    private (CombatNameTables Tables, IReadOnlyDictionary<uint, string> Roles)? awakening;
    private (CombatNameTables Tables, CombatClientBuffCatalog Catalog)? buffs;
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
        if (kind == CombatNames.AiCompanion) return new(kind, new(0), null, CombatNames.Generic, CombatNames.GenericReason);
        var key = kind switch { CombatNames.Monster => "monster", CombatNames.Skill => "skill", CombatNames.Effect => "effect", _ => null };
        if (key is null && !CombatNames.ActorKinds.Contains(kind)) return new(kind, new(sheetId), null, CombatNames.Unknown, "combat.nameKindUnknown");
        key ??= kind;
        if (current.Unsupported.TryGetValue(key, out var unsupported)) return new(kind, new(sheetId), null, CombatNames.Unavailable, unsupported);
        var table = key switch
        {
            "monster" => current.Monsters, "skill" => current.Skills, "effect" => current.Effects,
            _ => current.Actors?.GetValueOrDefault(key)
        };
        if (table is null) return new(kind, new(sheetId), null, CombatNames.Unavailable, "combat.nameTableMissing");
        if (table.TryGetValue(sheetId, out var text)) return new(kind, new(sheetId), text, CombatNames.Resolved);
        // 怪物表名称为空：经 Raid／BattleZone 关联到首领组名时用组名，否则给出“客户端名称为空”。
        if (key == "monster" && current.EmptyMonsters?.Contains(sheetId) == true)
            return current.BossGroupNames?.TryGetValue(sheetId, out var group) == true
                ? new(kind, new(sheetId), group, CombatNames.Resolved)
                : new(kind, new(sheetId), null, CombatNames.Unknown, "combat.nameEmpty");
        return new(kind, new(sheetId), null, CombatNames.Unknown, "combat.nameNotFound");
    }

    public string? EffectPolarity(uint sheetId) =>
        Current(out _) is { EffectPolarities: { } polarities } && polarities.TryGetValue(sheetId, out var polarity) ? polarity : null;

    /// <summary>
    /// 客户端 Buff 表里全部可显示的效果（按编号排序），去掉名称为空与开发占位的行（<see cref="ClientCombatNameCatalog.IsPlaceholderName"/>）。
    /// 名称表未就绪时给出 loading、binding-required 或 unavailable 与原因；Buff 表结构不符时为 unavailable。同一份表只整理一次。
    /// </summary>
    public CombatClientBuffCatalog ClientBuffs()
    {
        var current = Current(out var reason);
        if (current is null)
            return new(reason switch
            {
                "combat.nameCatalogLoading" => CombatClientBuffCatalogState.Loading,
                "game.bindingRequired" => CombatClientBuffCatalogState.BindingRequired,
                _ => CombatClientBuffCatalogState.Unavailable
            }, reason, null, []);
        if (current.Unsupported.TryGetValue("effect", out var unsupported)) return new(CombatClientBuffCatalogState.Unavailable, unsupported, current.Id, []);
        lock (gate)
        {
            if (buffs is { } cached && ReferenceEquals(cached.Tables, current)) return cached.Catalog;
            var list = current.Effects.Where(pair => !ClientCombatNameCatalog.IsPlaceholderName(pair.Value)).OrderBy(pair => pair.Key)
                .Select(pair => new CombatClientBuff(new(pair.Key), pair.Value, current.EffectPolarities?.GetValueOrDefault(pair.Key),
                    current.EffectDescriptions?.GetValueOrDefault(pair.Key)))
                .ToArray();
            var catalog = new CombatClientBuffCatalog(CombatClientBuffCatalogState.Ready, null, current.Id, list);
            buffs = (current, catalog);
            return catalog;
        }
    }

    /// <summary>目录的强 ETag：状态、原因与客户端指纹决定内容。</summary>
    public static string ClientBuffsETag(CombatClientBuffCatalog catalog) =>
        "\"combat-client-buffs-" + catalog.State.RawValue + "-" + (catalog.Reason ?? "") + "-" + (catalog.ClientId ?? "") + "\"";

    /// <summary>觉醒效果标记：效果表可用时按开发名换算的 Id 核对表（见 <see cref="CombatAwakening.Resolve"/>），不可用时按内置清单。</summary>
    public string? EffectAwakening(uint sheetId) => Awakening().GetValueOrDefault(sheetId);

    private IReadOnlyDictionary<uint, string> Awakening()
    {
        var current = Current(out _);
        if (current is null || current.Unsupported.ContainsKey("effect")) return CombatAwakening.Builtin;
        lock (gate)
        {
            if (awakening is { } cached && ReferenceEquals(cached.Tables, current)) return cached.Roles;
            var roles = CombatAwakening.Resolve(current.Effects.ContainsKey);
            awakening = (current, roles);
            return roles;
        }
    }

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
            // 只用有限查询权限读映像路径（游戏以管理员身份运行时 MainModule 会被拒绝），并解析联接与符号链接后再定位客户端目录。
            image = MabiTool.Platform.Windows.WindowsProcessAccess.ImagePath(binding.ProcessId) ?? throw new InvalidOperationException("无法读取进程映像路径");
        }
        image = MabiTool.Providers.TwObservation.Combat.TwClientFingerprint.ResolveFinalPath(image) ?? image;
        var directory = ClientItemCatalogLocation.Resolve(image);
        LocalDiscoverySecurity.RejectLinks(directory);
        return ClientCombatNameCatalog.Extract(directory, ct);
    }
}
