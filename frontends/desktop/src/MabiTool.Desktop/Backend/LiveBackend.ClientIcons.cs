using Avalonia.Media.Imaging;
using MabiTool.Client;
using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

// 客户端属性图标：只在界面有位置使用图标时读取。目录在连接后与游戏绑定变化后各读一次，目录为 loading 时按短间隔重读到
// ready 或不可用为止，之后不再轮询；可用的键按版本下载 PNG 并解码，版本不变时沿用内存里的位图，不写磁盘。
public sealed partial class LiveBackend
{
    /// <summary>目录为 loading（宿主正在后台读取客户端）时重读目录的间隔。</summary>
    internal static readonly TimeSpan ClientIconCatalogRetry = TimeSpan.FromSeconds(1);

    private bool iconsInUse, iconCatalogDue = true;
    private long iconCatalogRetryAt;
    private string? iconBindingKey;
    private ClientIconCatalog? iconCatalog;
    private readonly Dictionary<string, ClientIconImage> iconImages = new(StringComparer.Ordinal);
    // 下载或解码失败的“键|版本”：同一版本不再重试，目录重读（重连、绑定变化）时清空。
    private readonly HashSet<string> iconFailures = new(StringComparer.Ordinal);

    public event EventHandler? ClientIconsChanged;

    public ClientIconImage? GetClientIcon(string key)
    {
        lock (gate)
            return iconCatalog?.Icons.FirstOrDefault(icon => icon.Key == key) is { Available: true, Version: { } version }
                && iconImages.TryGetValue(key, out var image) && image.Version == version ? image : null;
    }

    public void SetClientIconsInUse(bool inUse)
    {
        lock (gate)
        {
            if (iconsInUse == inUse) return;
            iconsInUse = inUse;
        }
        if (inUse) Signal();
    }


    // 调用方持有 gate。连接后与绑定变化后目录到期；读取中按间隔重读；目录里有尚未下载的可用版本时下载。
    private bool ClientIconsDue(long now) => iconsInUse
        && (iconCatalogDue || iconCatalogRetryAt != 0 && now >= iconCatalogRetryAt || PendingIcons().Count > 0);

    // 调用方持有 gate。
    private List<ClientIcon> PendingIcons() => iconCatalog?.Icons.Where(icon => icon is { Available: true, Version: { } version }
        && !(iconImages.TryGetValue(icon.Key, out var image) && image.Version == version)
        && !iconFailures.Contains(icon.Key + "|" + version)).ToList() ?? [];

    // 调用方持有 gate。下一次读取目录的时刻（没有到期的目录读取时为 0）。
    private long ClientIconCatalogAt(long now) => !iconsInUse ? 0 : iconCatalogDue ? now : iconCatalogRetryAt;

    /// <summary>游戏绑定换了进程（或从未绑定变为绑定）时目录重新到期。调用方持有 gate。</summary>
    private void NoteIconBinding(GameBindingSnapshot binding)
    {
        var session = binding.Binding?.Session;
        var key = session is null ? binding.State : $"{binding.State}|{session.ProcessId}|{session.ProcessStartTime.UtcTicks}";
        if (key == iconBindingKey) return;
        iconBindingKey = key;
        iconCatalogDue = true;
    }

    private async Task RefreshClientIconsAsync(MabiToolClient api, CancellationToken token)
    {
        bool readCatalog;
        lock (gate)
        {
            if (!ClientIconsDue(Now)) return;
            readCatalog = iconCatalogDue || iconCatalogRetryAt != 0 && Now >= iconCatalogRetryAt;
            if (readCatalog) { iconCatalogDue = false; iconCatalogRetryAt = 0; }
        }
        var changed = false;
        if (readCatalog)
        {
            ClientIconCatalog? catalog;
            // 宿主没有图标接口或读取被拒绝：各处保留文字，等下一次连接或绑定变化再读。
            try { catalog = await api.GetClientIconsAsync(token); }
            catch (MabiToolApiException) { catalog = null; }
            lock (gate)
            {
                changed = Signature(iconCatalog) != Signature(catalog);
                iconCatalog = catalog;
                iconFailures.Clear();
                if (catalog?.State.KnownValue == ClientIconCatalogState.Loading) iconCatalogRetryAt = Now + Span(ClientIconCatalogRetry);
            }
        }
        List<ClientIcon> pending;
        lock (gate) pending = iconsInUse ? PendingIcons() : [];
        foreach (var icon in pending)
        {
            var image = await DownloadIconAsync(api, icon, token);
            lock (gate)
            {
                if (image is null) iconFailures.Add(icon.Key + "|" + icon.Version);
                else iconImages[icon.Key] = image;
            }
            changed |= image is not null;
        }
        if (changed) dispatch(() => ClientIconsChanged?.Invoke(this, EventArgs.Empty));
    }


    private async Task<ClientIconImage?> DownloadIconAsync(MabiToolClient api, ClientIcon icon, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        try
        {
            await using var download = await api.DownloadClientIconAsync(icon.Key, token);
            await download.Content.CopyToAsync(buffer, token);
        }
        catch (Exception error) when (error is MabiToolApiException or InvalidDataException) { return null; }
        buffer.Position = 0;
        try { return new(icon.Key, icon.Version!, new Bitmap(buffer), icon.Tint, icon.DisplayName); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { return null; }
    }

    private static string? Signature(ClientIconCatalog? catalog) => catalog is null ? null
        : catalog.State.RawValue + "|" + string.Join(',', catalog.Icons.Select(icon => $"{icon.Key}:{icon.Available}:{icon.Version}:{icon.Tint}:{icon.DisplayName}"));
}
