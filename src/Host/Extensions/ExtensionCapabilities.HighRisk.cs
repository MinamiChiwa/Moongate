using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 高风险能力：操作游戏（前台或后台输入）、截取游戏画面、访问清单声明的主机。全部默认关闭，由用户逐项授予。
/// 输入经 <see cref="ResourceLeaseCoordinator"/> 申请租约（同时占用 <c>activity-control:&lt;游戏会话&gt;</c>，与染色、合奏互斥），
/// 每个动作核对租约、绑定与窗口；紧急停止、撤销授权与会话结束都会交还租约并释放按下的输入。
/// </summary>
public sealed partial class ExtensionCapabilities
{
    public const int MaximumCaptureWidth = 640, MaximumFetchResponseBytes = 512 * 1024, MaximumFetchBodyBytes = 64 * 1024;
    private static readonly HttpClient Fetcher = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(10)
    }) { Timeout = TimeSpan.FromSeconds(15) };

    private GameControlContext Controls => Service<GameControlContext>();

    private async Task<object?> DispatchHighRiskAsync(ExtensionSession session, string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "input.acquire": return Acquire(session, p);
            case "input.release":
                ReleaseInput(session);
                return new JsonObject();
            case "input.send": return await Task.Run(() => Send(session, p, ct), ct);
            case "capture.game": return await CaptureAsync(session, p, ct);
            case "network.fetch": return await FetchAsync(session, p, services.GetService<ExtensionNetworkOptions>(), ct);
            case "files.pick": return await PickAsync(session, p, ct);
            default:
                throw new ExtensionCallException("extension.methodUnknown", "宿主不提供方法 " + method);
        }
    }

    private Task HighRiskGrantsChangedAsync(ExtensionSession session)
    {
        if (!session.Grants.Contains("input.selected-window")) ReleaseInput(session);
        return Task.CompletedTask;
    }

    private Task HighRiskReleaseAsync(ExtensionSession session)
    {
        ReleaseInput(session);
        return Task.CompletedTask;
    }

    /// <summary>申请输入租约：<c>mode</c> 为 <c>background</c>（后台触控，不前置游戏、不动鼠标）或 <c>foreground</c>（前台键鼠）。</summary>
    private JsonObject Acquire(ExtensionSession session, JsonElement p)
    {
        var mode = Text(p, "mode", 16, optional: true) ?? "background";
        if (mode is not ("background" or "foreground")) throw new ExtensionCallException("request.invalid", "mode 须为 background 或 foreground");
        var seconds = p.TryGetProperty("seconds", out var s) && s.TryGetInt32(out var value) ? value : 60;
        if (seconds is < 1 or > 300) throw new ExtensionCallException("request.invalid", "seconds 须在 1–300 之间");
        var controls = Controls;
        if (mode == "background" && controls.Background.Availability() is { } reason)
            throw new ExtensionCallException(reason, "当前游戏窗口或系统条件不支持后台输入：" + reason);
        var binding = controls.Bindings.Current;
        if (binding is null) throw new ExtensionCallException("input.bindingUnavailable", "没有绑定游戏窗口");
        if (mode == "foreground" && !binding.CanAcceptInput) throw new ExtensionCallException("input.bindingUnavailable", "游戏窗口当前不能接收输入");
        ReleaseInput(session);
        ResourceLeaseSnapshot lease;
        try
        {
            lease = controls.Leases.Acquire(new(Owner(session), "extension-" + session.Id + "-" + Guid.NewGuid().ToString("N")[..8],
                binding.Session.Id, binding.Session.BindingRevision,
                [mode == "background" ? GameBackgroundInputBroker.Resource : "foreground-input", "activity-control:" + binding.Session.Id], TimeSpan.FromSeconds(seconds)));
        }
        catch (ResourceLeaseException error) { throw new ExtensionCallException(error.Code, "输入被其他活动占用：" + error.ConflictingOwner); }
        lock (session.Resources.Gate) session.Resources.Input = new(lease.Id, mode);
        session.Log.Write("warning", "host", "扩展取得" + (mode == "background" ? "后台" : "前台") + "输入租约，" + seconds + " 秒");
        return new JsonObject { ["leaseId"] = lease.Id, ["mode"] = mode, ["expiresAt"] = lease.ExpiresAt.ToString("O") };
    }

    private JsonObject Send(ExtensionSession session, JsonElement p, CancellationToken ct)
    {
        ExtensionInputHold? hold;
        lock (session.Resources.Gate) hold = session.Resources.Input;
        if (hold is null) throw new ExtensionCallException("input.leaseInvalid", "请先以 input.acquire 取得输入租约");
        var controls = Controls;
        var lease = controls.Leases.Snapshot().FirstOrDefault(x => x.Id == hold.LeaseId)
            ?? throw new ExtensionCallException("input.leaseInvalid", "输入租约已失效（到期、紧急停止或撤销）");
        if (!p.TryGetProperty("actions", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new ExtensionCallException("request.invalid", "actions 必须是数组");
        var actions = list.EnumerateArray().Select(Action).ToArray();
        int Number(string name, int fallback) => p.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;
        var binding = controls.Bindings.Current ?? throw new ExtensionCallException("input.bindingUnavailable", "没有绑定游戏窗口");
        try
        {
            if (hold.Mode == "background")
            {
                var delivered = controls.Background.Execute(new(Owner(session), lease.Id, lease.FencingToken, lease.GameSessionId, lease.BindingRevision,
                    binding.Session.WindowIdentity, actions, Number("intervalMs", 120), Number("jitterMs", 0), Number("jitterPx", 0)), ct);
                return new JsonObject { ["delivered"] = delivered };
            }
            return new JsonObject { ["delivered"] = Foreground(controls, lease, binding, actions, Number("intervalMs", 120), ct) };
        }
        catch (GameInputException error) { throw new ExtensionCallException(error.Code, "输入未完成：" + error.Code); }
    }

    /// <summary>前台通道：点按、长按、拖动与按键经既有输入代理执行，游戏窗口必须在前台。</summary>
    private static int Foreground(GameControlContext controls, ResourceLeaseSnapshot lease, GameWindowBinding binding,
        IReadOnlyList<GameBackgroundAction> actions, int interval, CancellationToken ct)
    {
        GameInputAction Make(InputActionKind kind, InputControl? control = null, PhysicalScreenPoint? point = null) => new(lease.OwnerId, lease.Id,
            lease.FencingToken, lease.GameSessionId, lease.BindingRevision, binding.Session.WindowIdentity, kind, control, point);
        var delivered = 0;
        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            if (action.Kind == BackgroundActionKind.Key)
            {
                var key = InputControls.VirtualKey(action.VirtualKey);
                controls.Input.Execute(Make(InputActionKind.Press, key));
                if (action.HoldMilliseconds > 0) Thread.Sleep(action.HoldMilliseconds);
                controls.Input.Execute(Make(InputActionKind.Release, key));
            }
            else
            {
                var points = action.Points.Select(x => GameBackgroundInputBroker.ToScreen(x, binding.Bounds)).ToArray();
                controls.Input.Execute(Make(InputActionKind.MovePointer, point: points[0]));
                controls.Input.Execute(Make(InputActionKind.Press, InputControl.MouseLeft));
                try
                {
                    if (action.Kind == BackgroundActionKind.LongPress) Thread.Sleep(action.HoldMilliseconds);
                    else if (points.Length == 2)
                        for (var i = 1; i <= 12; i++)
                        {
                            Thread.Sleep(20);
                            controls.Input.Execute(Make(InputActionKind.MovePointer, point: new(points[0].X + (points[1].X - points[0].X) * i / 12,
                                points[0].Y + (points[1].Y - points[0].Y) * i / 12)));
                        }
                    else Thread.Sleep(40);
                }
                finally { controls.Input.Execute(Make(InputActionKind.Release, InputControl.MouseLeft)); }
            }
            delivered++;
            if (delivered < actions.Count && interval > 0) Thread.Sleep(Math.Min(interval, 2000));
        }
        return delivered;
    }

    private static GameBackgroundAction Action(JsonElement item)
    {
        static PhysicalScreenPoint Point(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out var point) && point.ValueKind == JsonValueKind.Object &&
            point.TryGetProperty("x", out var x) && x.TryGetInt32(out var px) && point.TryGetProperty("y", out var y) && y.TryGetInt32(out var py)
                ? new(px, py) : throw new ExtensionCallException("request.invalid", name + " 须为 {x, y}（客户区物理像素）");
        int Int(string name, int fallback) => item.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;
        var kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        return kind switch
        {
            "tap" => new(BackgroundActionKind.Tap, [Point(item, "at")]),
            "longPress" => new(BackgroundActionKind.LongPress, [Point(item, "at")], Int("holdMs", 800)),
            "drag" => new(BackgroundActionKind.Drag, [Point(item, "from"), Point(item, "to")], Steps: Int("steps", 6)),
            "swipe" => new(BackgroundActionKind.Swipe, [Point(item, "from"), Point(item, "to")]),
            "key" => new(BackgroundActionKind.Key, [], Int("holdMs", 40), VirtualKey: (ushort)Int("vk", 0)),
            _ => throw new ExtensionCallException("request.invalid", "动作 kind 须为 tap、longPress、drag、swipe 或 key")
        };
    }

    private void ReleaseInput(ExtensionSession session)
    {
        ExtensionInputHold? hold;
        lock (session.Resources.Gate) { hold = session.Resources.Input; session.Resources.Input = null; }
        if (hold is null || services.GetService<GameControlContext>() is not { } controls) return;
        if (hold.Mode == "background") controls.Background.ReleaseRun(hold.LeaseId);
        else controls.Input.ReleaseRun(hold.LeaseId);
        session.Log.Write("info", "host", "已交还输入租约");
    }

    /// <summary>
    /// 请用户选择一个文件：桌面前端弹出文件选择框（标题写明扩展名称），选定后把文件名、大小、SHA-256 与内容（Base64，最多 512 KiB）
    /// 一次性交给扩展；用户取消时返回 <c>cancelled</c>。扩展拿不到本机路径。
    /// </summary>
    private async Task<JsonObject> PickAsync(ExtensionSession session, JsonElement p, CancellationToken ct)
    {
        var requests = Service<ExtensionFileRequests>();
        var title = Text(p, "title", 64, optional: true) ?? string.Empty;
        var file = await requests.RequestAsync(session.Id, session.Extension.Manifest.DisplayNames, title, ct);
        if (file is null)
        {
            session.Log.Write("info", "host", "用户取消了文件选择");
            return new JsonObject { ["cancelled"] = true };
        }
        session.Log.Write("info", "host", $"用户选择了文件 {file.Name}（{file.Content.Length} 字节）");
        return new JsonObject
        {
            ["cancelled"] = false, ["name"] = file.Name, ["size"] = file.Content.Length, ["sha256"] = file.Sha256,
            ["content"] = Convert.ToBase64String(file.Content)
        };
    }

    private static string Owner(ExtensionSession session) => "extension:" + session.Id;

    private long lastCapture;

    /// <summary>截取当前绑定的游戏窗口，按 <c>maxWidth</c>（不超过 640）等比缩小后以 RGB24 Base64 返回；每秒最多两次。</summary>
    private async Task<JsonObject> CaptureAsync(ExtensionSession session, JsonElement p, CancellationToken ct)
    {
        var now = Clock.GetTimestamp();
        if (Clock.GetElapsedTime(Interlocked.Read(ref lastCapture), now) < TimeSpan.FromMilliseconds(500))
            throw new ExtensionCallException("resource.capacityExceeded", "截图每秒最多两次");
        Interlocked.Exchange(ref lastCapture, now);
        var binding = Controls.Bindings.Current ?? throw new ExtensionCallException("input.bindingUnavailable", "没有绑定游戏窗口");
        if (binding.IsMinimized) throw new ExtensionCallException("input.windowMinimized", "游戏窗口已最小化");
        var source = services.GetService<IGameCaptureSource>() ?? (OperatingSystem.IsWindows() ? new MabiTool.Platform.Windows.WindowsGameCaptureSource() : null)
            ?? throw new ExtensionCallException("capability.unavailable", "当前平台不支持截图");
        GameCapture capture;
        try { capture = await source.CaptureAsync(binding, ct); }
        catch (GameInputException error) { throw new ExtensionCallException(error.Code, "截图失败：" + error.Code); }
        var maximum = p.TryGetProperty("maxWidth", out var w) && w.TryGetInt32(out var requested) ? Math.Clamp(requested, 16, MaximumCaptureWidth) : MaximumCaptureWidth;
        var width = Math.Min(maximum, capture.Width);
        var height = Math.Max(1, (int)((long)capture.Height * width / capture.Width));
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Buffer.BlockCopy(capture.Rgb, (int)(((long)y * capture.Height / height * capture.Width + (long)x * capture.Width / width) * 3), pixels, (y * width + x) * 3, 3);
        session.Log.Write("info", "host", $"截取游戏画面 {capture.Width}×{capture.Height}，返回 {width}×{height}");
        return new JsonObject { ["width"] = width, ["height"] = height, ["format"] = "rgb24", ["data"] = Convert.ToBase64String(pixels) };
    }

    /// <summary>经宿主访问清单 <c>network.hosts</c> 列出的主机：只用 HTTPS，不跟随重定向、不带 Cookie，请求体 64 KiB、响应 512 KiB、超时 15 秒。</summary>
    private static async Task<JsonObject> FetchAsync(ExtensionSession session, JsonElement p, ExtensionNetworkOptions? options, CancellationToken ct)
    {
        if (!Uri.TryCreate(Text(p, "url", 2048), UriKind.Absolute, out var url) ||
            !(url.Scheme == Uri.UriSchemeHttps || options?.AllowHttp == true && url.Scheme == Uri.UriSchemeHttp) || !url.IsDefaultPort ||
            url.UserInfo.Length > 0 || !session.Extension.Manifest.Hosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new ExtensionCallException("extension.hostNotDeclared", "只能以 HTTPS 访问清单 network.hosts 列出的主机");
        var method = Text(p, "method", 8, optional: true) ?? "GET";
        if (method is not ("GET" or "POST")) throw new ExtensionCallException("request.invalid", "method 须为 GET 或 POST");
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (Text(p, "body", MaximumFetchBodyBytes, optional: true) is { } body)
            request.Content = new StringContent(body, Encoding.UTF8, Text(p, "contentType", 64, optional: true) ?? "application/json");
        request.Headers.UserAgent.ParseAdd("Moongate-Extension/1");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options?.Timeout ?? TimeSpan.FromSeconds(15));
        HttpResponseMessage response;
        var buffer = new MemoryStream();
        try
        {
            response = await (options?.Client ?? Fetcher).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaximumFetchResponseBytes) { response.Dispose(); throw new ExtensionCallException("extension.resultTooLarge", "响应超过 512 KiB"); }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ExtensionCallException("extension.networkTimeout", "网络请求超时"); }
        catch (HttpRequestException) { throw new ExtensionCallException("extension.networkFailed", "网络请求失败"); }
        using var _ = response;
        session.Log.Write("info", "host", $"{method} {url.IdnHost}{url.AbsolutePath} → {(int)response.StatusCode}");
        var text = response.Content.Headers.ContentType?.MediaType is { } media && (media.StartsWith("text/", StringComparison.Ordinal) || media.Contains("json", StringComparison.Ordinal));
        return new JsonObject
        {
            ["status"] = (int)response.StatusCode, ["contentType"] = response.Content.Headers.ContentType?.ToString(),
            [text ? "text" : "base64"] = text ? Encoding.UTF8.GetString(buffer.ToArray()) : Convert.ToBase64String(buffer.ToArray())
        };
    }
}

/// <summary>
/// 网络访问的替换选项：嵌入宿主与测试用自己的 HTTP 客户端（例如连到本机回环的临时服务）、允许 HTTP 与更短的超时。正式宿主不登记，使用 HTTPS 与 15 秒超时。
/// </summary>
public sealed record ExtensionNetworkOptions(HttpClient Client, bool AllowHttp, TimeSpan Timeout);

/// <summary>扩展持有的输入租约；会话结束、撤销授权或紧急停止时释放。</summary>
public sealed record ExtensionInputHold(string LeaseId, string Mode);
