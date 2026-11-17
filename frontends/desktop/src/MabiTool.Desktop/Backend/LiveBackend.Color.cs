using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Dyeing;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 染色页：草稿、命名方案、运行状态与进度、候选、目标色集合、屏幕取色与寻色记录。草稿与方案的编辑都带配色库的 ETag 与新的幂等键，只发送一次。
public sealed partial class LiveBackend
{
    private const int CaptureChunkBytes = 262_144;

    // 本机第一次看到某次寻色的时间：运行栏“已用 N 秒”从这里算起。
    private (string RunId, DateTimeOffset SeenAt)? colorRunSeen;

    public ColorPageData GetColor()
    {
        var (current, page, _, _) = ReadPages();
        if (page.Colors is not { } library)
        {
            var reason = current.Connected ? Pick("后端没有提供染色草稿", "後端沒有提供染色草稿") : Pick("未连接后端", "未連接後端");
            return new(null, new Dictionary<string, ColorReading>(), reason, ColorTone.Idle, null, [], [],
                new(false, false, false, false, false), reason);
        }
        ColorRun? run;
        DateTimeOffset seen;
        lock (gate)
        {
            run = colorRun;
            if (run is not null && colorRunSeen?.RunId != run.Id) colorRunSeen = (run.Id, DateTimeOffset.Now);
            seen = colorRunSeen?.SeenAt ?? DateTimeOffset.Now;
        }
        // 截图寻色出一局结果后仍在等待重试，运行是否进行中只看宿主写入的终态阶段，不看结果是否已有。
        var active = run is not null && ColorRunText.IsActive(run);
        var draft = library.Draft;
        var (readings, accepted) = Readings(draft, run, active);
        // 本局区域可用性只在寻色进行中采用：结束后可能已换了服装，旧的“本局不可用”不再限制区域开关。
        var available = active ? run!.AvailableByRegion : null;
        var bound = current.Binding?.Binding is not null;
        var blocker = active ? null : StartBlocker(page.Capabilities?.FirstOrDefault(item => item.Id == CapabilityIds.ColorSearch),
            current.Binding?.Binding);
        var status = run is null
            ? new ColorRunStatus(bound ? Pick("就绪", "就緒") : Pick("请先选择游戏窗口", "請先選擇遊戲視窗"),
                bound ? Pick("打开游戏染色界面后点「开始」", "打開遊戲染色介面後點「開始」") : string.Empty,
                ColorTone.Idle)
            : ColorRunText.Describe(run);
        var connected = current.Connected;
        return new(
            new(draft.Strategy.RawValue, draft.Regions.Select(region => new ColorRegionView(region.Id, region.TargetHex,
                region.AlternativeHex, region.MatchMode.RawValue, region.Tolerance, region.Priority, region.Enabled,
                available?.GetValueOrDefault(region.Id, true) ?? true)).ToList(),
                draft.Revision.ToString()),
            readings, status.Title, status.Tone, accepted,
            active && run!.Candidates is { } batch ? Candidates(run, batch) : [],
            library.Profiles.Select(Row).ToList(),
            new(connected && !active, connected && !active && blocker is null, active, active && run!.Interaction is { RetryEnabled: true },
                connected && !active),
            Progress: active && run!.Progress is { } progress ? Progress(run.Id, progress, seen) : null,
            StartBlocker: blocker ?? string.Empty, Detail: status.Detail) { OverlayGuide = status.Guide, DetailTip = status.Tip };
    }

    /// <summary>
    /// 区域卡、运行栏与浮窗共用的一份实测：进行中取引擎对当前画面的最新读数；结束后取结束时复核的当前颜色，
    /// 没有复核时取上一次实测并标为当前未确认（“当前”为未读取）。最佳实测不作为当前或实测颜色显示。
    /// 整体达标只取结束时复核的当前颜色。
    /// </summary>
    internal static (Dictionary<string, ColorReading> Readings, bool? Accepted) Readings(ColorProfile draft, ColorRun? run, bool active)
    {
        if (active)
        {
            var live = run!.Progress?.Reading;
            var judgements = live?.Regions.Select(item => (item.RegionId, item.State.RawValue)).ToList() ?? [];
            return (draft.Regions.ToDictionary(region => region.Id, region =>
            {
                var hex = live?.HexByRegion.GetValueOrDefault(region.Id);
                return live is null || !live.HexByRegion.ContainsKey(region.Id)
                    ? new ColorReading(null, null, null)
                    : new ColorReading(hex, hex, live.DeltaByRegion.GetValueOrDefault(region.Id),
                        hex is null && region.Enabled ? new ColorMark(ColorMark.Unread) : ColorMark.Of(judgements, region.Id, region.Enabled));
            }), null);
        }
        var current = run?.CurrentVerified ?? run?.Result?.CurrentVerified;
        var measured = current ?? run?.LastMeasured ?? run?.Result?.LastMeasured;
        var marks = Judgements(measured);
        return (draft.Regions.ToDictionary(region => region.Id, region => measured is null
            ? new ColorReading(null, null, null)
            : new ColorReading(current?.HexByRegion.GetValueOrDefault(region.Id), measured.HexByRegion.GetValueOrDefault(region.Id),
                measured.DeltaByRegion.GetValueOrDefault(region.Id), ColorMark.Of(marks, region.Id, region.Enabled), current is null)),
            current?.Accepted);
    }

    /// <summary>
    /// 寻色交互租约的续约循环：开始寻色后每秒续约一次，与页面刷新分开，任何一轮读取变慢或断线重连都不会让续约间隔超过后端的 5 秒期限。
    /// 运行进入终态、换成另一次寻色、后端回复运行已结束或已不存在时退出。
    /// </summary>
    private void StartColorPresence()
    {
        string? runId;
        lock (gate) runId = colorRunId;
        if (runId is not null) _ = Task.Run(() => RenewColorPresenceAsync(runId, lifetime.Token));
    }

    private async Task RenewColorPresenceAsync(string runId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (gate)
                if (colorRunId != runId || colorRun is { } run && run.Id == runId && !ColorRunText.IsActive(run)) return;
            if (client is { } api)
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await api.RenewColorPresenceAsync(runId, timeout.Token);
                }
                catch (MabiToolApiException error) when (error.Status == 404 || error.Problem?.Code == "color.runInactive") { return; }
                catch (Exception error) when (error is MabiToolApiException or HttpRequestException or IOException or OperationCanceledException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// “开始寻色”的前置条件，按检查顺序取第一条不满足的说明：后端寻色能力（染色组件、持久存储、平台）、
    /// 已绑定游戏窗口、窗口未最小化（开始时要把游戏切到前台，最小化窗口恢复后绑定会变化）。全部满足时为 null。
    /// 能力表尚未读到时不拦截，开始请求由后端按同样条件拒绝。
    /// </summary>
    internal static string? StartBlocker(CapabilitySnapshot? capability, GameWindowBinding? binding)
    {
        if (capability?.Status.KnownValue == CapabilityStatus.Unavailable)
            return capability.ReasonCode switch
            {
                "provider.unconfigured" => Pick("染色组件缺失，请重新解压完整压缩包", "染色元件缺失，請重新解壓完整壓縮檔"),
                "component.missing" => Pick("染色组件不完整，请重新解压完整压缩包", "染色元件不完整，請重新解壓完整壓縮檔"),
                "storage.unavailable" => Pick("数据目录不可用，不能寻色", "資料目錄不可用，不能尋色"),
                "platform.unsupported" => Pick("寻色需要 Windows", "尋色需要 Windows"),
                var code => Pick("暂不能寻色：", "暫不能尋色：") + Localization.CodeText.Reason(code)
            };
        if (binding is null) return Pick("未绑定游戏窗口，请在左上角选择", "未綁定遊戲視窗，請在左上角選擇");
        if (binding.IsMinimized) return Pick("游戏窗口已最小化，请先还原", "遊戲視窗已最小化，請先還原");
        return null;
    }

    /// <summary>后端进度换成运行栏用的锚点：剩余时间取 deadline − updated 的单调毫秒差与 updatedAt，本机按现在推算。</summary>
    internal static ColorProgressView Progress(string runId, ColorRunProgress progress, DateTimeOffset seenAt) => new(runId, progress.Stage,
        progress.Current, progress.Total, progress.StageStartedAt,
        progress.DeadlineMonotonicMs is { } deadline ? deadline - progress.UpdatedMonotonicMs : null, progress.UpdatedAt, seenAt);

    private static List<(string RegionId, string State)> Judgements(ColorMeasurement? measurement) =>
        measurement?.Regions.Select(item => (item.RegionId, item.State.RawValue)).ToList() ?? [];

    /// <summary>方案行：各启用区域的目标色与替代色数量。</summary>
    internal static ColorProfileRow Row(ColorProfile profile) => new(profile.Id, profile.Name,
        profile.Regions.Where(region => region.Enabled).Select(region => new ProfileSwatch(region.TargetHex, region.AlternativeHex.Count)).ToList());

    public Task<BackendReply> SaveColorDraftAsync(ColorDraft draft, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            string? tag = null;
            var library = await api.GetColorLibraryAsync(token, metadata => tag = metadata.ETag);
            var next = library.Draft with { Strategy = new(draft.Strategy), Regions = draft.Regions.Select(ToRegion).ToList() };
            await api.EditColorLibraryAsync(new([], [], next), IdempotencyKey(), tag ?? string.Empty, token);
            // 页面随后重读：先写入本地快照，下一次例行刷新再取后端的新修订。
            lock (gate) pages = pages with { Colors = library with { Draft = next } };
            return string.Empty;
        }, cancellationToken, remember: false);

    public Task<BackendReply> SaveColorProfileAsync(string name, ColorDraft draft, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            string? tag = null;
            await api.GetColorLibraryAsync(token, metadata => tag = metadata.ETag);
            var profile = new ColorProfile("profile-" + Guid.NewGuid().ToString("N"), 1, name.Trim(), new(draft.Strategy),
                draft.Regions.Select(ToRegion).ToList(), new(0));
            await api.EditColorLibraryAsync(new([profile], [], null), IdempotencyKey(), tag ?? string.Empty, token);
            return Pick($"已保存染色方案：{profile.Name}", $"已儲存染色方案：{profile.Name}");
        }, cancellationToken);

    public Task<BackendReply> LoadColorProfileAsync(string profileId, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            var profile = await api.GetColorProfileAsync(profileId, token);
            string? tag = null;
            var library = await api.GetColorLibraryAsync(token, metadata => tag = metadata.ETag);
            var next = library.Draft with { Strategy = profile.Strategy, Regions = profile.Regions };
            await api.EditColorLibraryAsync(new([], [], next), IdempotencyKey(), tag ?? string.Empty, token);
            lock (gate) pages = pages with { Colors = library with { Draft = next } };
            return Pick($"已载入染色方案：{profile.Name}", $"已載入染色方案：{profile.Name}");
        }, cancellationToken);

    public Task<BackendReply> DeleteColorProfileAsync(string profileId, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            string? tag = null;
            await api.GetColorLibraryAsync(token, metadata => tag = metadata.ETag);
            await api.EditColorLibraryAsync(new([], [profileId], null), IdempotencyKey(), tag ?? string.Empty, token);
            return Pick("已删除染色方案", "已刪除染色方案");
        }, cancellationToken);

    /// <summary>
    /// 目标色集合图：先请后端出图（同步阻塞到出图，最坏约 6 秒），再按资产编号下载 PNG，核对 SHA-256 后解码。
    /// 下载时资产已被淘汰（resource.notFound）就重新请求一次。调用方的令牌取消时请求随之取消。
    /// </summary>
    public async Task<ColorAtlasReply> GetColorAtlasAsync(string targetHex, string matchMode, double tolerance, bool full,
        CancellationToken cancellationToken)
    {
        if (client is not { } api || !ReadPages().State.Connected) return new(null, Pick("未连接后端", "未連接後端"));
        var request = PaletteRequest(targetHex, matchMode, tolerance, full);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var palette = await api.RenderColorPaletteAsync(request, cancellationToken);
                await using var download = await api.DownloadAssetAsync(palette.Asset.Id, cancellationToken);
                using var buffer = new MemoryStream();
                await download.Content.CopyToAsync(buffer, cancellationToken);
                var bytes = buffer.ToArray();
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!string.Equals(hash, palette.Asset.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    download.Sha256 is { Length: > 0 } header && !string.Equals(hash, header, StringComparison.OrdinalIgnoreCase))
                    return new(null, Pick("色集图校验不一致，请重试", "色集圖校驗不一致，請重試"));
                using var image = new MemoryStream(bytes);
                return new(new ColorAtlas(palette.ColorCount, palette.Tolerance, new Bitmap(image)), string.Empty);
            }
            catch (MabiToolApiException error) when (attempt == 0 && error.Problem?.Code == "resource.notFound") { }
            catch (MabiToolApiException error)
            {
                var reason = error.Problem?.Details is { } details && details.TryGetValue("reason", out var value) ? value.ToString() : null;
                var code = error.Problem?.Code ?? $"http.{error.Status}";
                return new(null, PaletteProblem(code, reason), code is "resource.busy" or "color.paletteTimeout");
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or ArgumentException)
            {
                return new(null, Localization.CodeText.Error(error));
            }
        }
    }

    /// <summary>出图请求：大写 #RRGGBB；精准 HEX 的容差记 0（后端忽略），相似颜色夹在 1–35；缩略图 overview，全集图 atlas。</summary>
    internal static ColorPaletteRequest PaletteRequest(string targetHex, string matchMode, double tolerance, bool full) => new(
        ColorCodes.NormalizeHex(targetHex) ?? targetHex.ToUpperInvariant(), new OpenEnum<ColorMatchMode>(matchMode),
        matchMode == ColorCodes.ExactHex ? 0 : Math.Clamp(tolerance, ColorCodes.MinimumTolerance, ColorCodes.MaximumTolerance),
        full ? ColorPaletteImage.Atlas : ColorPaletteImage.Overview);

    /// <summary>出图失败的说明：排队已满与计算超时稍后重试；没有染色引擎、引擎不可用给出原因；其余显示错误码。</summary>
    internal static string PaletteProblem(string code, string? reason) => code switch
    {
        "capability.unavailable" => Pick("未配置染色引擎，无法计算色集", "未設定染色引擎，無法計算色集"),
        "resource.busy" => Pick("色板计算排队已满，稍后重试", "色板計算排隊已滿，稍後重試"),
        "color.paletteTimeout" => Pick("色板计算超时，稍后重试", "色板計算逾時，稍後重試"),
        "provider.unavailable" => Pick("染色引擎不可用", "染色引擎不可用"),
        _ => Localization.CodeText.Reason(code)
    };

    public async Task<CaptureReply> CaptureScreenAsync(CancellationToken cancellationToken)
    {
        ScreenCapture? capture = null;
        var reply = await ReplyAsync(async (api, token) =>
        {
            var metadata = await api.CaptureColorScreenAsync(token);
            // 画面分块读取，每块最多 256 KiB；读取失败时立即释放，不留冻结画面。
            try
            {
                var rgb = new byte[metadata.SizeBytes];
                var offset = 0;
                while (offset < rgb.Length)
                {
                    var chunk = await api.ReadColorCaptureAsync(metadata.Id, new(offset, Math.Min(CaptureChunkBytes, rgb.Length - offset)), token);
                    var bytes = Convert.FromBase64String(chunk.Base64);
                    if (bytes.Length == 0) throw new InvalidOperationException(Pick("冻结画面读取不完整", "凍結畫面讀取不完整"));
                    bytes.CopyTo(rgb, offset);
                    offset += bytes.Length;
                }
                var bounds = metadata.Bounds;
                if ((long)bounds.Width * bounds.Height * 3 != rgb.Length)
                    throw new InvalidOperationException(Pick("冻结画面尺寸与数据不一致", "凍結畫面尺寸與資料不一致"));
                capture = new ScreenCapture(metadata.Id, bounds.X, bounds.Y, bounds.Width, bounds.Height, rgb);
            }
            catch
            {
                try { await api.ReleaseColorCaptureAsync(metadata.Id, CancellationToken.None); }
                catch (MabiToolApiException) { }
                throw;
            }
            return string.Empty;
        }, cancellationToken, remember: false);
        return new(capture, reply.Message);
    }

    public async Task ReleaseCaptureAsync(string captureId, CancellationToken cancellationToken)
    {
        if (client is not { } api) return;
        try { await api.ReleaseColorCaptureAsync(captureId, cancellationToken); }
        catch (Exception error) when (error is MabiToolApiException or HttpRequestException or IOException or OperationCanceledException) { }
    }

    // 寻色记录只在打开该层时读取，不进入例行刷新。后端只保留最新 30 条，以最大页大小读取一页即是全部。
    public async Task<ColorHistoryReply> QueryColorHistoryAsync(string? beforeOperationId, CancellationToken cancellationToken)
    {
        ApiList<ColorHistoryItem>? page = null;
        var reply = await ReplyAsync(async (api, token) =>
        {
            page = await api.QueryColorHistoryAsync(new(ColorHistoryMapper.PageSize, beforeOperationId), token);
            return string.Empty;
        }, cancellationToken, remember: false);
        return page is null
            ? new(false, reply.Message, [], null)
            : new(true, string.Empty, page.Items.Select(ColorHistoryMapper.Map).ToList(), page.NextCursor);
    }

    private static ColorRegion ToRegion(ColorRegionDraft region) => new(region.Id, region.TargetHex, region.AlternativeHex,
        new OpenEnum<ColorMatchMode>(region.MatchMode), region.Tolerance, region.Priority, region.Enabled);

    /// <summary>
    /// 候选卡（原工具 overlay.py candidate_rows）：标题标明游戏实测或预测颜色；当前方案的按钮不可点；各区域带色块、色差与分区判定。
    /// 摘要取后端给出的最大与平均色差、达标标记、精准命中数（exactTotal 大于 0 时）与色系偏离。
    /// </summary>
    internal static IReadOnlyList<ColorCandidateCard> Candidates(ColorRun run, ColorCandidateBatch batch) =>
        batch.Candidates.OrderBy(candidate => candidate.SortIndex).Select(candidate =>
        {
            var measured = !candidate.Predicted;
            var title = measured
                ? candidate.IsCurrent ? Pick("当前方案 · 游戏实测", "目前方案 · 遊戲實測") : Pick("已测试方案 · 游戏实测", "已測試方案 · 遊戲實測")
                : Pick("候选方案 · 预测颜色", "候選方案 · 預測顏色");
            var marks = candidate.Regions.Select(item => (item.RegionId, item.State.RawValue)).ToList();
            var regions = run.RulesSnapshot.Regions.Select((region, index) => new CandidateRegion(
                Pick($"区域 {index + 1}", $"區域 {index + 1}"), candidate.HexByRegion.GetValueOrDefault(region.Id) ?? string.Empty,
                candidate.DeltaByRegion.GetValueOrDefault(region.Id) is { } delta ? ColorStages.Delta(delta) : string.Empty,
                ColorMark.Of(marks, region.Id, region.Enabled))).ToList();
            var parts = new List<string>();
            if (candidate.MaximumDelta is { } maximum)
                parts.Add(Pick("最大 ", "最大 ") + maximum.ToString("0.00", CultureInfo.InvariantCulture)
                    + (candidate.AverageDelta is { } average ? " / " + Pick("平均 ", "平均 ") + average.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty));
            parts.Add(candidate.Accepted
                ? measured ? Pick("实测达标", "實測達標") : Pick("预测达标", "預測達標")
                : Pick("妥协方案", "妥協方案"));
            if (candidate.ExactTotal is > 0 and var total)
                parts.Add(Pick($"精准命中 {candidate.ExactMatches ?? 0}/{total}", $"精準命中 {candidate.ExactMatches ?? 0}/{total}"));
            if (candidate.FamilyConsistent == false) parts.Add(Pick("存在色系偏离", "存在色系偏離"));
            var action = candidate.IsCurrent
                ? measured ? Pick("当前方案（实测）", "目前方案（實測）") : Pick("当前方案", "目前方案")
                : candidate.Available ? Pick("选择此方案", "選擇此方案") : Pick("不可用", "不可用");
            return new ColorCandidateCard(candidate.Id, title, regions, string.Join(" · ", parts), action,
                candidate is { Available: true, IsCurrent: false } && batch.Interaction.SelectionEnabled);
        }).ToList();
}
