using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.Host.Combat;

/// <summary>
/// 战斗属性图标的常驻目录。首次查询时按当前游戏绑定在后台读取（同一游戏进程只读一次）：先按客户端资源索引指纹查数据根缓存
/// <c>cache/game-access/client-icons</c>，没有时从客户端解码并写入缓存。读取期间与失败时目录不可用并带原因；失败后换游戏进程或满一分钟才重试。
/// 只保留最近一个客户端的结果（约 15 个 64×64 PNG，数十 KiB）；没有绑定且本次运行从未读到时为需要绑定游戏。
/// </summary>
public sealed class ClientIconService(GameControlContext controls, string? cacheDirectory, TimeProvider clock)
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private ClientIconSet? icons;
    private (int Pid, DateTimeOffset Started)? source, attempted;
    private Task? loading;
    private string? failure;
    private long failedAt;
    /// <summary>测试注入的读取函数；默认按进程映像路径定位客户端目录。</summary>
    internal Func<GameSession, CancellationToken, ClientIconSet>? Reader { get; set; }

    public ClientIconCatalog Catalog()
    {
        var current = Current(out var reason);
        if (current is null)
        {
            var state = reason == ClientIconReasons.BindingRequired ? ClientIconCatalogState.BindingRequired
                : reason == ClientIconReasons.Loading ? ClientIconCatalogState.Loading : ClientIconCatalogState.Unavailable;
            return new(state, reason, null, [.. ClientIconKeys.All.Select(key => new ClientIcon(key, false, reason, null, null, null, null, null, null))], []);
        }
        return new(ClientIconCatalogState.Ready, null, current.ClientId, [.. current.Icons.Select(icon => new ClientIcon(icon.Key, icon.Available,
            icon.Reason, icon.Tint, icon.Sha256, icon.Width, icon.Height, icon.Source, icon.SourcePath, icon.NameKey, icon.DisplayName))], current.Reasons);
    }

    /// <summary>读取单个图标；键未登记为 resource.notFound，目录未就绪或该键不可用时以对应原因码抛出。</summary>
    public ClientIconEntry Content(string key)
    {
        if (!ClientIconKeys.All.Contains(key, StringComparer.Ordinal)) throw new OperationException(ApiErrors.ResourceNotFound, "图标键不存在");
        var current = Current(out var reason) ?? throw new OperationException(reason, "客户端图标尚不可用");
        var icon = current.Icons.Single(item => item.Key == key);
        return icon.Available ? icon : throw new OperationException(icon.Reason ?? ClientIconReasons.ResourceInvalid, "该图标在当前客户端中不可用");
    }

    /// <summary>目录响应的强 ETag：按序列化后的目录内容计算。</summary>
    public static string ETag(ClientIconCatalog catalog) =>
        "\"" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(catalog, ContractJson.Options)))[..32] + "\"";

    private ClientIconSet? Current(out string reason)
    {
        lock (gate)
        {
            var binding = controls.Bindings.Recent?.Session;
            var process = binding is null ? ((int, DateTimeOffset)?)null : (binding.ProcessId, binding.ProcessStartTime);
            var stale = process is not null && source != process;
            if (binding is not null && stale && loading is null && (attempted != process || failure is null || clock.GetElapsedTime(failedAt) >= RetryInterval))
            {
                attempted = process; failure = null;
                loading = Task.Run(() => Load(binding));
            }
            // 读取新客户端期间继续使用上一份结果；从未读到时说明原因。
            if (icons is not null) { reason = ""; return icons; }
            reason = loading is not null ? ClientIconReasons.Loading
                : failure ?? (binding is null ? ClientIconReasons.BindingRequired : ClientIconReasons.Loading);
            return null;
        }
    }

    private void Load(GameSession binding)
    {
        ClientIconSet? result = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            result = (Reader ?? ReadClient)(binding, deadline.Token);
        }
        catch (Exception value) when (value is InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException or
            InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or OverflowException or ClientIconFormatException)
        { }
        lock (gate)
        {
            loading = null;
            if (result is not null) { icons = result; source = (binding.ProcessId, binding.ProcessStartTime); }
            else { failure = ClientIconReasons.CatalogUnavailable; failedAt = clock.GetTimestamp(); }
        }
    }

    private ClientIconSet ReadClient(GameSession binding, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || binding.Region != "TW") throw new PlatformNotSupportedException();
        string image;
        using (var process = Process.GetProcessById(binding.ProcessId))
        {
            if (process.HasExited || process.StartTime.ToUniversalTime() != binding.ProcessStartTime.UtcDateTime)
                throw new InvalidOperationException("绑定进程已变化");
            image = MabiTool.Platform.Windows.WindowsProcessAccess.ImagePath(binding.ProcessId) ?? throw new InvalidOperationException("无法读取进程映像路径");
        }
        image = MabiTool.Providers.TwObservation.Combat.TwClientFingerprint.ResolveFinalPath(image) ?? image;
        var directory = ClientItemCatalogLocation.Resolve(image);
        LocalDiscoverySecurity.RejectLinks(directory);
        var clientId = ClientAttributeIcons.ClientId(directory, cancellationToken);
        if (cacheDirectory is not null && ClientIconCache.Read(cacheDirectory, clientId) is { } cached)
        {
            ClientIconCache.Prune(cacheDirectory, clientId);
            return cached;
        }
        var extracted = ClientAttributeIcons.Extract(directory, cancellationToken);
        if (cacheDirectory is not null) ClientIconCache.Write(cacheDirectory, extracted);
        return extracted;
    }
}
