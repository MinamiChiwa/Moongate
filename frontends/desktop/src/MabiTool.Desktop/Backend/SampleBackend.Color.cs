using System.Globalization;
using Avalonia;
using MabiTool.Desktop.Pages.Dyeing;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的染色页：一份草稿、三个命名方案、一次已结束（或进行中）的寻色、测试用的小位图集合与样本截屏。只模拟后端的回执形状。
// 样本截屏铺满本机的物理虚拟桌面，吸管在样本数据下也能走完整流程（取到的是样本色块）。
public sealed partial class SampleBackend
{
    private ColorDraft colorDraft = new(ColorCodes.Screenshot,
    [
        new("region-1", "#1F3A6B", [], ColorCodes.ExactHex, 3, 1, true),
        new("region-2", "#F2E9D8", ["#E6DCC2", "#D9CDB0"], ColorCodes.Similar, 3, 2, true),
        new("region-3", "#8C2F45", ["#A8455A"], ColorCodes.Similar, 12, 3, true)
    ]);

    private int colorRevision = 1;

    private readonly Dictionary<string, (string Name, ColorDraft Draft)> colorProfiles = new(StringComparer.Ordinal)
    {
        ["winter"] = ("冬夜禮服", new(ColorCodes.Precise,
        [
            new("region-1", "#1F3A6B", [], ColorCodes.ExactHex, 3, 1, true),
            new("region-2", "#F2E9D8", ["#E6DCC2"], ColorCodes.Similar, 3, 2, true),
            new("region-3", "#8C2F45", [], ColorCodes.Similar, 12, 3, false)
        ])),
        ["uniform"] = ("樂團制服白", new(ColorCodes.Screenshot,
        [
            new("region-1", "#F4F1EA", ["#E9E4D8"], ColorCodes.Similar, 6, 1, true),
            new("region-2", "#1B1B1F", [], ColorCodes.ExactHex, 3, 2, true),
            new("region-3", "#202020", [], ColorCodes.ExactHex, 8, 3, false)
        ])),
        ["lute"] = ("純黑魯特琴", new(ColorCodes.Precise,
        [
            new("region-1", "#101012", [], ColorCodes.ExactHex, 2, 1, true),
            new("region-2", "#2A2A2E", ["#333338"], ColorCodes.Similar, 5, 2, true),
            new("region-3", "#202020", [], ColorCodes.ExactHex, 8, 3, false)
        ]))
    };

    private ScreenCapture? sampleCapture;

    /// <summary>测试用：寻色进行中，显示阶段与候选方案，区域卡停用。</summary>
    internal bool ColorRunning { get; set; }

    /// <summary>测试用：草稿编辑被后端拒绝时的错误码。</summary>
    internal string? ColorDraftReject { get; set; }

    /// <summary>测试用：后端没有提供目标色集合。</summary>
    internal bool NoColorAtlas { get; set; }

    /// <summary>页面交给后端的草稿，按发送顺序记录。</summary>
    public List<ColorDraft> SavedColorDrafts { get; } = [];

    /// <summary>页面读取目标色集合的请求（目标色、模式、容差、是否全集图），按发送顺序记录。</summary>
    public List<(string Hex, string Mode, double Tolerance, bool Full)> ColorAtlasRequests { get; } = [];

    /// <summary>测试用：读取目标色集合时等这个任务完成再回答（模拟后端出图耗时）；被取消的请求计入 <see cref="CancelledAtlasRequests"/>。</summary>
    internal TaskCompletionSource? AtlasGate { get; set; }

    public int CancelledAtlasRequests { get; private set; }

    /// <summary>测试用：样本截屏的物理边界；为空时取本机的物理虚拟桌面（读不到时为 (-160, 0) 160×90）。</summary>
    internal PixelRect? SampleCaptureBounds { get; set; }

    /// <summary>测试用：进行中的寻色在 <see cref="ColorProgressAt"/> 时的剩余毫秒。</summary>
    internal double ColorRemainingMs { get; set; } = 83_000;

    internal DateTimeOffset ColorProgressAt { get; set; } = DateTimeOffset.Now;

    public ColorPageData GetColor()
    {
        var regions = colorDraft.Regions;
        string Hex(string id, int shift) => id switch
        {
            "region-1" => shift == 0 ? "#213D70" : "#203B6D",
            "region-2" => shift == 0 ? "#E6DCC2" : "#EFE5D1",
            _ => shift == 0 ? "#A8455A" : "#96384D"
        };
        var readings = regions.ToDictionary(region => region.Id, region => new ColorReading(Hex(region.Id, 0), Hex(region.Id, 1),
            region.Id switch { "region-1" => 0.92, "region-2" => 2.13, _ => 4.21 },
            new ColorMark(!region.Enabled ? ColorMark.Disabled : region.Id == "region-3" ? ColorMark.Unmet : ColorMark.Met)));
        IReadOnlyList<ColorCandidateCard> candidates = ColorRunning
            ?
            [
                new("c1", Pick("当前方案 · 游戏实测", "目前方案 · 遊戲實測"), Regions(regions, ["#213D70", "#E6DCC2", "#A8455A"], [1.44, 4.83, 9.61],
                        [ColorMark.Met, ColorMark.Unmet, ColorMark.Unmet]),
                    Pick("最大 9.61 / 平均 5.29 · 妥协方案 · 存在色系偏离", "最大 9.61 / 平均 5.29 · 妥協方案 · 存在色系偏離"),
                    Pick("当前方案（实测）", "目前方案（實測）"), false),
                new("c2", Pick("候选方案 · 预测颜色", "候選方案 · 預測顏色"), Regions(regions, ["#203B6D", "#EFE5D1", "#96384D"], [0.92, 2.13, 4.21],
                        [ColorMark.Met, ColorMark.Met, ColorMark.Met]),
                    Pick("最大 4.21 / 平均 2.42 · 预测达标", "最大 4.21 / 平均 2.42 · 預測達標"), Pick("选择此方案", "選擇此方案"), true),
                new("c3", Pick("候选方案 · 预测颜色", "候選方案 · 預測顏色"), Regions(regions, ["#1F3A6B", "#D9CDB0", "#8C2F45"], [0, 8.31, 0],
                        [ColorMark.Met, ColorMark.Unmet, ColorMark.Met]),
                    Pick("最大 8.31 / 平均 2.77 · 妥协方案 · 精准命中 1/1", "最大 8.31 / 平均 2.77 · 妥協方案 · 精準命中 1/1"),
                    Pick("选择此方案", "選擇此方案"), true)
            ]
            : [];
        return new(
            new(colorDraft.Strategy, regions.Select(region => new ColorRegionView(region.Id, region.TargetHex, region.AlternativeHex,
                region.MatchMode, region.Tolerance, region.Priority, region.Enabled)).ToList(), colorRevision.ToString(CultureInfo.InvariantCulture)),
            readings,
            ColorRunning ? ColorStages.Name("search") : Pick("寻色已结束：", "尋色已結束：") + ColorStages.Outcome("compromise"),
            ColorRunning ? ColorTone.Live : ColorTone.Idle, false, candidates,
            colorProfiles.Select(pair => new ColorProfileRow(pair.Key, pair.Value.Name, pair.Value.Draft.Regions.Where(region => region.Enabled)
                .Select(region => new ProfileSwatch(region.TargetHex, region.AlternativeHex.Count)).ToList())).ToList(),
            new(!ColorRunning, !ColorRunning, ColorRunning, ColorRunning, !ColorRunning),
            Progress: ColorRunning
                ? new ColorProgressView("sample-run", "search", 3, 8, ColorProgressAt.AddSeconds(-12), ColorRemainingMs, ColorProgressAt,
                    ColorProgressAt.AddSeconds(-45))
                : null);
    }

    public Task<BackendReply> SaveColorDraftAsync(ColorDraft draft, CancellationToken cancellationToken)
    {
        SavedColorDrafts.Add(draft);
        if (ColorDraftReject is { } code) return Task.FromResult(new BackendReply(false, Pick($"请求未完成：{code}", $"請求未完成：{code}")));
        colorDraft = draft;
        colorRevision++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    public Task<BackendReply> SaveColorProfileAsync(string name, ColorDraft draft, CancellationToken cancellationToken)
    {
        UiIntents.Add(("color.profile-save", name));
        if (colorProfiles.Values.Any(profile => string.Equals(profile.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(new BackendReply(false, Pick("请求未完成：resource.conflict", "請求未完成：resource.conflict")));
        colorProfiles["profile-" + (colorProfiles.Count + 1).ToString(CultureInfo.InvariantCulture)] = (name.Trim(), draft);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, Pick("已保存", "已儲存")));
    }

    public Task<BackendReply> LoadColorProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        UiIntents.Add(("color.profile-load", profileId));
        if (!colorProfiles.TryGetValue(profileId, out var profile))
            return Task.FromResult(new BackendReply(false, Pick("请求未完成：resource.notFound", "請求未完成：resource.notFound")));
        colorDraft = profile.Draft;
        colorRevision++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, Pick($"已载入染色方案：{profile.Name}", $"已載入染色方案：{profile.Name}")));
    }

    public Task<BackendReply> DeleteColorProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        UiIntents.Add(("color.profile-delete", profileId));
        if (!colorProfiles.Remove(profileId))
            return Task.FromResult(new BackendReply(false, Pick("请求未完成：resource.notFound", "請求未完成：resource.notFound")));
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    /// <summary>测试专用的小位图：以目标色为中心按容差变化亮度，只用来检查界面的取图与显示流程，不是实际的颜色集合。</summary>
    public async Task<ColorAtlasReply> GetColorAtlasAsync(string targetHex, string matchMode, double tolerance, bool full,
        CancellationToken cancellationToken)
    {
        ColorAtlasRequests.Add((targetHex, matchMode, tolerance, full));
        if (AtlasGate is { } gate)
            try { await gate.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                CancelledAtlasRequests++;
                throw;
            }
        if (NoColorAtlas) return new ColorAtlasReply(null, Pick("后端没有配置染色引擎，无法计算目标色集合", "後端沒有設定染色引擎，無法計算目標色集合"));
        var exact = matchMode == ColorCodes.ExactHex;
        var (r, g, b) = ColorMath.Rgb(targetHex);
        ColorBitmap Shades(int width, int height)
        {
            var rgb = new byte[width * height * 3];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var shift = (int)Math.Round((x - width / 2.0) / width * tolerance * 2 + (y - height / 2.0) / height * tolerance);
                    var offset = (y * width + x) * 3;
                    rgb[offset] = (byte)Math.Clamp(r + shift, 0, 255);
                    rgb[offset + 1] = (byte)Math.Clamp(g + shift, 0, 255);
                    rgb[offset + 2] = (byte)Math.Clamp(b + shift, 0, 255);
                }
            return new(width, height, rgb);
        }
        var image = exact ? new ColorBitmap(1, 1, [(byte)r, (byte)g, (byte)b]) : full ? Shades(30, 20) : Shades(24, 9);
        return new ColorAtlasReply(new ColorAtlas(exact ? 1 : 600, exact ? 0 : tolerance, ColorBitmaps.Create(image)), string.Empty);
    }

    /// <summary>
    /// 样本画面：铺满 <see cref="SampleCaptureBounds"/>（默认本机物理虚拟桌面），左上、右上、左下、右下四块分别是
    /// #1F3A6B、#F2E9D8、#8C2F45、#0E8C7D。
    /// </summary>
    public Task<CaptureReply> CaptureScreenAsync(CancellationToken cancellationToken)
    {
        var bounds = SampleCaptureBounds ?? DesktopScreenPickHost.Instance.DesktopBounds() ?? new PixelRect(-160, 0, 160, 90);
        var (width, height) = (bounds.Width, bounds.Height);
        var rgb = new byte[width * height * 3];
        byte[][] colors = [[0x1F, 0x3A, 0x6B], [0xF2, 0xE9, 0xD8], [0x8C, 0x2F, 0x45], [0x0E, 0x8C, 0x7D]];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                colors[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)].CopyTo(rgb, (y * width + x) * 3);
        sampleCapture = new ScreenCapture("capture-" + (UiIntents.Count + 1).ToString(CultureInfo.InvariantCulture), bounds.X, bounds.Y, width, height, rgb);
        UiIntents.Add(("color.capture", sampleCapture.Id));
        return Task.FromResult(new CaptureReply(sampleCapture, string.Empty));
    }

    public Task ReleaseCaptureAsync(string captureId, CancellationToken cancellationToken)
    {
        UiIntents.Add(("color.release", captureId));
        if (sampleCapture?.Id == captureId) sampleCapture = null;
        return Task.CompletedTask;
    }

    private static IReadOnlyList<CandidateRegion> Regions(IReadOnlyList<ColorRegionDraft> regions, string[] hex, double[] deltas, string[] marks) =>
        regions.Select((region, index) => new CandidateRegion(Pick($"区域 {index + 1}", $"區域 {index + 1}"), hex[index],
            ColorStages.Delta(deltas[index]), new ColorMark(region.Enabled ? marks[index] : ColorMark.Disabled))).ToList();
}
