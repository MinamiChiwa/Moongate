using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Controls;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>替代色的一格（原工具 RegionCard 替代色行）。</summary>
public sealed record AlternativeSwatch(string Hex)
{
    public IBrush Brush => new SolidColorBrush(Color.Parse(Hex));
}

/// <summary>
/// 一张区域卡（原工具 RegionCard.qml）。卡片直接编辑后端草稿的一个区域：每次改动通知页面，由页面防抖后保存。
/// 后端读回的草稿只在没有待保存改动时写回卡片。
/// </summary>
public sealed class RegionCardModel : ObservableObject
{
    private readonly ColorPageModel owner;
    private string targetHex = "#202020";
    private string targetText = "#202020";
    private bool targetInvalid;
    private string matchMode = ColorCodes.ExactHex;
    private double tolerance = 8;
    private int priority;
    private bool enabled = true;
    private bool available = true;
    private bool alternativeEditorOpen;
    private string newAlternative = string.Empty;
    private bool newAlternativeInvalid;
    private ColorReading reading = new(null, null, null);
    private string atlasKey = string.Empty;
    private int atlasTicket;
    private CancellationTokenSource? atlasRequest;
    private ColorAtlasView atlas = ColorAtlasView.Computing(null);

    public RegionCardModel(ColorPageModel owner, int index, ColorRegionView region) : this(owner, index, region.Id)
    {
        Load(region);
    }

    /// <summary>
    /// 没有读到后端草稿时的占位卡片：卡片照常排进网格，区域设置与读数显示“未读取”，不读取目标色集合；
    /// 卡片由页面停用，读到草稿后整组换成真实卡片。
    /// </summary>
    public static RegionCardModel Unread(ColorPageModel owner, int index)
    {
        var card = new RegionCardModel(owner, index, $"region-{index + 1}") { IsUnread = true };
        card.matchMode = string.Empty;
        card.targetText = UnreadText;
        card.atlas = ColorAtlasView.Waiting(UnreadText);
        return card;
    }

    private static string UnreadText => UiText.Pick("未读取", "未讀取");

    private RegionCardModel(ColorPageModel owner, int index, string id)
    {
        this.owner = owner;
        Index = index;
        Id = id;
        Picker = new ColorPickerModel(() => targetHex, SetTarget);
        ToggleCommand = new RelayCommand(_ =>
        {
            if (!available) return;
            enabled = !enabled;
            RaiseState();
            owner.OnCardEdited();
        });
        ModeCommand = new RelayCommand(value =>
        {
            if (value is not string mode || mode == matchMode) return;
            matchMode = mode;
            RaiseMode();
            owner.OnCardEdited();
            RefreshAtlas();
        });
        PriorityCommand = new RelayCommand(value =>
        {
            if (int.TryParse(value as string, out var number)) owner.SetPriority(this, number);
        });
        RemoveAlternativeCommand = new RelayCommand(value =>
        {
            if (value is not string hex || Alternatives.All(item => item != hex)) return;
            // 原工具按 HEX 删除：同一颜色出现多次时一并去掉。
            foreach (var item in Alternatives.Where(item => item == hex).ToList()) Alternatives.Remove(item);
            RaiseAlternatives();
            owner.OnCardEdited();
        });
        ToggleAlternativeEditorCommand = new RelayCommand(_ =>
        {
            if (!CanAddAlternative && !alternativeEditorOpen) return;
            AlternativeEditorOpen = !alternativeEditorOpen;
            NewAlternative = string.Empty;
            NewAlternativeInvalid = false;
        });
        AddAlternativeCommand = new RelayCommand(_ => AddAlternative());
        CancelAlternativeCommand = new RelayCommand(_ => AlternativeEditorOpen = false);
        OpenPickerCommand = new RelayCommand(_ => owner.TogglePicker(this));
        OpenAtlasCommand = new RelayCommand(_ =>
        {
            if (atlas.CanOpen) owner.ToggleAtlas(this);
        });
        EyedropperCommand = new RelayCommand(_ => _ = owner.PickFromScreenAsync(this));
    }

    /// <summary>占位卡片（见 <see cref="Unread"/>）。</summary>
    public bool IsUnread { get; private init; }

    public int Index { get; }
    public string Id { get; }
    public ColorPickerModel Picker { get; }
    public ObservableCollection<string> Alternatives { get; } = [];

    public ICommand ToggleCommand { get; }
    public ICommand ModeCommand { get; }
    public ICommand PriorityCommand { get; }
    public ICommand RemoveAlternativeCommand { get; }
    public ICommand ToggleAlternativeEditorCommand { get; }
    public ICommand AddAlternativeCommand { get; }
    public ICommand CancelAlternativeCommand { get; }
    public ICommand OpenPickerCommand { get; }
    public ICommand OpenAtlasCommand { get; }
    public ICommand EyedropperCommand { get; }

    /// <summary>卡片标识（卡片外壳按它保存折叠状态）。</summary>
    public string CardId => $"region-{Index + 1}";
    public string Title => UiText.Pick($"区域 {Index + 1}", $"區域 {Index + 1}");

    // ---- 身份与开关 ----
    public bool Enabled => enabled;
    public bool Available => available;
    public string StatusText => IsUnread ? UnreadText : !available ? UiText.Pick("本局不可用", "本局不可用")
        : enabled ? UiText.Pick("已启用", "已啟用") : UiText.Pick("未启用", "未啟用");

    /// <summary>原工具的整卡透明度：本局不可用 0.45，未启用 0.6。</summary>
    public double CardOpacity => !available ? 0.45 : enabled ? 1 : 0.6;

    public int Priority => priority;
    public bool IsPriority1 => priority == 1;
    public bool IsPriority2 => priority == 2;
    public bool IsPriority3 => priority == 3;

    // ---- 目标色 ----
    public string TargetHex => targetHex;
    public IBrush TargetBrush => IsUnread ? Brushes.Transparent : new SolidColorBrush(Color.Parse(targetHex));

    /// <summary>HEX 输入框的文字；格式正确时立即成为目标色，否则标红、目标色保持原值。</summary>
    public string TargetText
    {
        get => targetText;
        set
        {
            if (!Set(ref targetText, value)) return;
            if (ColorCodes.NormalizeHex(value) is { } hex)
            {
                TargetInvalid = false;
                ApplyTarget(hex);
            }
            else TargetInvalid = true;
        }
    }

    public bool TargetInvalid { get => targetInvalid; private set => Set(ref targetInvalid, value); }

    // ---- 颜色要求与容差 ----
    public string MatchMode => matchMode;
    public bool IsExact => matchMode == ColorCodes.ExactHex;
    public bool IsSimilar => matchMode == ColorCodes.Similar;

    /// <summary>容差滑杆 1–35，取整数。</summary>
    public double Tolerance
    {
        get => tolerance;
        set
        {
            var rounded = Math.Clamp(Math.Round(value), ColorCodes.MinimumTolerance, ColorCodes.MaximumTolerance);
            if (!Set(ref tolerance, rounded)) return;
            Raise(nameof(ToleranceBadge));
            owner.OnCardEdited();
            RefreshAtlas();
        }
    }

    public string ToleranceBadge => "ΔE " + Math.Round(tolerance).ToString(CultureInfo.InvariantCulture);

    // ---- 替代色 ----
    public IReadOnlyList<AlternativeSwatch> VisibleAlternatives =>
        Alternatives.Take(ColorCodes.VisibleAlternatives).Select(hex => new AlternativeSwatch(hex)).ToList();
    public bool HasMoreAlternatives => Alternatives.Count > ColorCodes.VisibleAlternatives;
    public string MoreAlternatives => "+" + Math.Max(0, Alternatives.Count - ColorCodes.VisibleAlternatives).ToString(CultureInfo.InvariantCulture);
    public bool NoAlternatives => Alternatives.Count == 0;
    public bool CanAddAlternative => Alternatives.Count < ColorCodes.MaximumAlternatives;
    public bool AlternativeEditorOpen { get => alternativeEditorOpen; private set => Set(ref alternativeEditorOpen, value); }
    public string NewAlternative { get => newAlternative; set { if (Set(ref newAlternative, value)) NewAlternativeInvalid = false; } }
    public bool NewAlternativeInvalid { get => newAlternativeInvalid; private set => Set(ref newAlternativeInvalid, value); }

    // ---- 目标色集合 ----
    public ColorAtlasView Atlas { get => atlas; private set => Set(ref atlas, value); }

    // ---- 读数 ----
    public string CurrentText => reading.CurrentHex ?? ColorReading.UnreadText;
    public string MeasuredText => reading.MeasuredHex ?? ColorReading.UnreadText;
    public string MeasuredLabel => reading.MeasuredLabel;
    public IBrush CurrentDot => Dot(reading.CurrentHex);
    public IBrush MeasuredDot => Dot(reading.MeasuredHex);
    public string MeasuredDelta => reading.MeasuredDelta is { } delta ? ColorStages.Delta(delta) : "ΔE —";

    /// <summary>读数旁的徽标：后端对这份实测的分区判定；还没有实测时为“未开始”（原工具 RegionCard badge）。</summary>
    public ColorMark Mark => IsUnread ? new ColorMark(ColorMark.Unread) : reading.Mark ?? ColorMark.Waiting;

    public ColorRegionDraft ToDraft() => new(Id, targetHex, Alternatives.ToList(), matchMode, tolerance, priority, enabled);

    /// <summary>写入后端读回的区域设置，不触发保存。</summary>
    public void Load(ColorRegionView region)
    {
        targetHex = ColorCodes.NormalizeHex(region.TargetHex) ?? region.TargetHex;
        targetText = targetHex;
        targetInvalid = false;
        matchMode = region.MatchMode;
        tolerance = Math.Clamp(Math.Round(region.Tolerance), ColorCodes.MinimumTolerance, ColorCodes.MaximumTolerance);
        priority = region.Priority;
        enabled = region.Enabled;
        available = region.Available;
        if (!Alternatives.SequenceEqual(region.AlternativeHex))
        {
            Alternatives.Clear();
            foreach (var hex in region.AlternativeHex) Alternatives.Add(hex);
        }
        Raise(string.Empty);
        Picker.Sync(targetHex);
        RefreshAtlas();
    }

    public void SetReading(ColorReading value)
    {
        if (reading == value) return;
        reading = value;
        Raise(nameof(CurrentText));
        Raise(nameof(MeasuredText));
        Raise(nameof(MeasuredLabel));
        Raise(nameof(CurrentDot));
        Raise(nameof(MeasuredDot));
        Raise(nameof(MeasuredDelta));
        Raise(nameof(Mark));
    }

    /// <summary>选色浮层、吸管、默认预设写入目标色。</summary>
    public void SetTarget(string hex)
    {
        targetText = hex;
        TargetInvalid = false;
        Raise(nameof(TargetText));
        ApplyTarget(hex);
    }

    internal void SetPriorityValue(int value)
    {
        priority = value;
        Raise(nameof(Priority));
        Raise(nameof(IsPriority1));
        Raise(nameof(IsPriority2));
        Raise(nameof(IsPriority3));
    }

    /// <summary>
    /// 目标色集合按目标色、颜色要求与容差取得。精准 HEX 的集合就是目标色一种，直接以目标色铺满；
    /// 相似颜色向后端读取缩略图：规则变化时取消仍在等待的旧请求再发新请求，计算期间显示“色板计算中…”。
    /// </summary>
    internal void RefreshAtlas()
    {
        var key = string.Join('|', targetHex, matchMode, IsExact ? "-" : tolerance.ToString(CultureInfo.InvariantCulture));
        if (key == atlasKey) return;
        atlasKey = key;
        var ticket = ++atlasTicket;
        atlasRequest?.Cancel();
        atlasRequest = null;
        if (IsExact)
        {
            Atlas = ColorAtlasView.Solid(targetHex);
            owner.OnAtlasRulesChanged(this);
            return;
        }
        Atlas = ColorAtlasView.Computing(atlas.Thumbnail);
        owner.OnAtlasRulesChanged(this);
        atlasRequest = new CancellationTokenSource();
        _ = LoadAtlasAsync(ticket, targetHex, matchMode, tolerance, atlasRequest.Token);
    }

    /// <summary>页面离开或卡片被替换：取消仍在等待的集合请求。</summary>
    internal void CancelAtlas()
    {
        atlasTicket++;
        atlasRequest?.Cancel();
        atlasRequest = null;
    }

    private async Task LoadAtlasAsync(int ticket, string hex, string mode, double value, CancellationToken token)
    {
        try
        {
            // 原工具对连续输入做 80 ms 防抖，只算最后一次。
            await Task.Delay(80, token);
            for (var attempt = 0; ; attempt++)
            {
                var reply = await owner.ReadAtlasAsync(hex, mode, value, false, token);
                if (ticket != atlasTicket) return;
                if (reply.Atlas is { } result)
                {
                    Atlas = ColorAtlasView.From(result);
                    return;
                }
                // 排队已满与计算超时：稍后自动再试，最多三次。
                if (!reply.Retryable || attempt >= AtlasRetries)
                {
                    Atlas = ColorAtlasView.Waiting(reply.Message);
                    return;
                }
                Atlas = ColorAtlasView.Computing(atlas.Thumbnail, reply.Message);
                await Task.Delay(AtlasRetryDelay, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal const int AtlasRetries = 3;
    internal static readonly TimeSpan AtlasRetryDelay = TimeSpan.FromSeconds(1.5);

    private void ApplyTarget(string hex)
    {
        if (hex == targetHex) return;
        targetHex = hex;
        Raise(nameof(TargetHex));
        Raise(nameof(TargetBrush));
        Picker.Sync(hex);
        owner.OnCardEdited();
        RefreshAtlas();
    }

    private void AddAlternative()
    {
        if (newAlternative.Trim().Length == 0) return;
        if (ColorCodes.NormalizeHex(newAlternative) is not { } hex || !CanAddAlternative)
        {
            NewAlternativeInvalid = true;
            return;
        }
        Alternatives.Add(hex);
        AlternativeEditorOpen = false;
        NewAlternative = string.Empty;
        RaiseAlternatives();
        owner.OnCardEdited();
    }

    private void RaiseState()
    {
        Raise(nameof(Enabled));
        Raise(nameof(StatusText));
        Raise(nameof(CardOpacity));
    }

    private void RaiseMode()
    {
        Raise(nameof(MatchMode));
        Raise(nameof(IsExact));
        Raise(nameof(IsSimilar));
    }

    private void RaiseAlternatives()
    {
        Raise(nameof(VisibleAlternatives));
        Raise(nameof(HasMoreAlternatives));
        Raise(nameof(MoreAlternatives));
        Raise(nameof(NoAlternatives));
        Raise(nameof(CanAddAlternative));
    }

    private static IBrush Dot(string? hex) => hex is not null && ColorCodes.NormalizeHex(hex) is { } value
        ? new SolidColorBrush(Color.Parse(value)) : Brushes.Transparent;
}

/// <summary>
/// 一张卡的目标色集合缩略图与数量说明。计算中保留上一张缩略图并显示“色板计算中…”；没有取到时只有说明文字，不能打开浮层。
/// </summary>
public sealed record ColorAtlasView(Bitmap? Thumbnail, int Count, string Message, bool Exact, double Tolerance, bool Loading = false)
{
    public bool CanOpen => Thumbnail is not null && !Loading;
    public bool HasThumbnail => Thumbnail is not null;
    public bool HasMessage => Thumbnail is null;

    /// <summary>卡片标题右侧：“N 色 · 点击查看全部”，一种颜色时为“1 色（单）”；计算中为空。</summary>
    public string CountText => !CanOpen ? string.Empty : Count == 1
        ? UiText.Pick("1 色（单） · 点击查看全部", "1 色（單） · 點擊查看全部")
        : UiText.Pick($"{Count.ToString("N0", CultureInfo.InvariantCulture)} 色 · 点击查看全部",
            $"{Count.ToString("N0", CultureInfo.InvariantCulture)} 色 · 點擊查看全部");

    /// <summary>集合的说明：“精准 HEX · N 色”或“ΔE ≤ t · N 色”（t 取后端实际采用的容差）。</summary>
    public string PanelNote => Note(Exact, Tolerance, Count);

    public static string Note(bool exact, double tolerance, int count)
    {
        var colors = count == 1 ? UiText.Pick("1 色（单）", "1 色（單）")
            : UiText.Pick($"{count.ToString("N0", CultureInfo.InvariantCulture)} 色", $"{count.ToString("N0", CultureInfo.InvariantCulture)} 色");
        return exact ? UiText.Pick("精准 HEX", "精準 HEX") + " · " + colors
            : "ΔE ≤ " + tolerance.ToString("0.##", CultureInfo.InvariantCulture) + " · " + colors;
    }

    public static string ComputingText => UiText.Pick("色板计算中…", "色板計算中…");

    public static ColorAtlasView Waiting(string message) => new(null, 0, message, false, 0);

    /// <summary>后端正在出图：保留上一张缩略图（没有时只显示说明）。</summary>
    public static ColorAtlasView Computing(Bitmap? previous, string? message = null) =>
        new(previous, 0, message ?? ComputingText, false, 0, true);

    /// <summary>精准 HEX：集合只有目标色本身，以原工具 full_atlas 的最小尺寸 240×150 铺满目标色。</summary>
    public static ColorAtlasView Solid(string hex)
    {
        var (r, g, b) = ColorMath.Rgb(hex);
        var rgb = new byte[240 * 150 * 3];
        for (var i = 0; i < rgb.Length; i += 3) (rgb[i], rgb[i + 1], rgb[i + 2]) = ((byte)r, (byte)g, (byte)b);
        return new(ColorBitmaps.Create(new ColorBitmap(240, 150, rgb)), 1, string.Empty, true, 0);
    }

    public static ColorAtlasView From(ColorAtlas atlas) => new(atlas.Image, atlas.Count, string.Empty, false, atlas.Tolerance);
}
