using System.Collections.ObjectModel;
using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 染色页模型（原工具 Main.qml）：三张区域卡直接编辑后端草稿，改动停止约 300 ms 后保存一次；
/// 运行栏的寻色方式同属草稿。“选色方案”“寻色记录”是两个二级层，选色与目标色集合是锚定在卡片上的浮层。
/// </summary>
public sealed class ColorPageModel : BackendPageModel<ColorPageData>, IPageLifecycle
{
    /// <summary>改动到保存之间的防抖间隔。</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly IScreenPickHost pickHost;
    private int saveTicket;
    private string strategy = ColorCodes.Screenshot;
    private bool dirty, saving, saveAgain, held, picking, disposed;
    // 取色后的说明（如没能切到游戏窗口）保留到取色引起的那次保存之后，下一次编辑保存成功时才清除。
    private bool keepPickNote;
    private string message = string.Empty;
    private RegionCardModel? pickerCard, atlasCard;
    private string pickerOriginal = string.Empty;
    private string? layer;
    private string profileName = string.Empty;
    private string presetMessage = string.Empty;
    private double atlasZoom;
    private Avalonia.Media.Imaging.Bitmap? atlasImage;
    private string atlasNote = string.Empty;
    private string atlasMessage = string.Empty;
    private CancellationTokenSource? atlasRequest;
    private readonly Avalonia.Threading.DispatcherTimer progressClock = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// 切走后保留：寻色运行状态的读取与交互租约续约在后端适配层每秒进行（<c>LiveBackend.RefreshColorRunAsync</c>），
    /// 与页面是否显示无关；切走时只停掉本页的进度计时器，回来时按最近数据重新开始。
    /// </summary>
    protected override bool Cacheable => true;

    private bool inactive;

    void IPageLifecycle.Deactivate()
    {
        Deactivate();
        inactive = true;
        progressClock.Stop();
    }

    void IPageLifecycle.Activate()
    {
        inactive = false;
        Activate();
        SyncProgressClock();
    }

    public ColorPageModel(IDesktopBackend backend) : this(backend, DesktopScreenPickHost.Instance) { }

    public ColorPageModel(IDesktopBackend backend, IScreenPickHost pickHost) : base(backend, source => source.GetColor())
    {
        this.pickHost = pickHost;
        StrategyCommand = new RelayCommand(value =>
        {
            if (value is not string next || next == strategy || !CanEdit) return;
            strategy = next;
            RaiseStrategy();
            OnCardEdited();
        });
        StartCommand = new RelayCommand(_ => _ = StartAsync());
        History = new ColorHistoryModel(backend);
        OpenLayerCommand = new RelayCommand(value => OpenLayer(value as string));
        CloseLayerCommand = new RelayCommand(_ => OpenLayer(null));
        SaveProfileCommand = new RelayCommand(_ => _ = SaveProfileAsync());
        LoadProfileCommand = new RelayCommand(value => _ = LoadProfileAsync(value as string));
        DeleteProfileCommand = new RelayCommand(value => _ = DeleteProfileAsync(value as string));
        TogglePresetsCommand = new RelayCommand(_ => PresetsExpanded = !PresetsExpanded);
        ApplyPresetCommand = new RelayCommand(value => ApplyPreset(value as string));
        ClosePickerCommand = new RelayCommand(_ => ClosePicker());
        CloseAtlasCommand = new RelayCommand(_ => AtlasCard = null);
        AtlasZoomCommand = new RelayCommand(value => Zoom(value as string));
        // 运行栏的剩余时间与已用时间每秒在界面线程重算一次，不增加请求。
        progressClock.Tick += (_, _) => RaiseProgress();
        Adopt();
        SyncProgressClock();
    }

    public ObservableCollection<RegionCardModel> Cards { get; } = [];

    public ICommand StrategyCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand OpenLayerCommand { get; }
    public ICommand CloseLayerCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand TogglePresetsCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand ClosePickerCommand { get; }
    public ICommand CloseAtlasCommand { get; }
    public ICommand AtlasZoomCommand { get; }

    /// <summary>寻色记录层；打开时读取，不随页面刷新。</summary>
    public ColorHistoryModel History { get; }

    /// <summary>“开始寻色”不可用的具体原因：页面数据给出的前置条件优先，其次是后端能力表的说明；可以开始时为空。</summary>
    public string CapabilityNotice => Data.StartBlocker.Length > 0 ? Data.StartBlocker
        : Backend.GetCapabilityNotice(CapabilityIds.ColorSearch) ?? string.Empty;
    public bool HasCapabilityNotice => CapabilityNotice.Length > 0;

    public bool HasDraft => Data.Draft is not null;
    public bool NoDraft => Data.Draft is null;
    /// <summary>没有草稿时卡片上方的说明：原因，以及区域设置未读取、卡片暂不能编辑。</summary>
    public string NoDraftText => (Data.Notice.Length > 0 ? Data.Notice : Data.Status) +
        UiText.Pick("：区域设置未读取，读到后端的染色草稿后才能编辑。", "：區域設定未讀取，讀到後端的染色草稿後才能編輯。");

    /// <summary>草稿可以编辑：后端提供了草稿且没有寻色在进行。</summary>
    public bool CanEdit => Data.Actions.CanEdit && Data.Draft is not null;

    /// <summary>吸管可用：后端允许取色，且没有寻色进行、没有另一次取色。</summary>
    public bool CanPick => Data.Actions.CanPick && !picking;

    // ---- 运行栏 ----
    public bool IsScreenshot => strategy == ColorCodes.Screenshot;
    public bool IsPrecise => strategy == ColorCodes.Precise;

    /// <summary>寻色方式的说明（原工具 host.py）：精准寻色时为警示。</summary>
    public string StrategyNote => IsPrecise
        ? UiText.Pick("警示：精准寻色会读取游戏内存，风险较高。", "警示：精準尋色會讀取遊戲記憶體，風險較高。")
        : UiText.Pick("寻色方式：截图寻色。不读取游戏内存。", "尋色方式：截圖尋色。不讀取遊戲記憶體。");

    /// <summary>状态行：保存或取色失败的说明优先，其余是后端的阶段与结果。</summary>
    public string StatusText => message.Length > 0 ? message : Data.Status;
    /// <summary>说明行：当前阶段的指引、引擎读数，或结束原因与下一步；显示本地操作的失败说明时不显示。</summary>
    public string DetailText => message.Length > 0 ? string.Empty : Data.Detail;
    public bool HasDetailText => DetailText.Length > 0;
    public bool StatusLive => message.Length == 0 && Data.Tone == ColorTone.Live;
    public bool StatusWarn => message.Length > 0 || Data.Tone == ColorTone.Warn;
    public bool HasAccepted => Data.Accepted is not null;
    public bool Accepted => Data.Accepted == true;
    public string AcceptedText => Data.Accepted == true ? UiText.Pick("整体达标", "整體達標") : UiText.Pick("整体未达标", "整體未達標");
    public bool HasCandidates => Data.Candidates.Count > 0;

    // ---- 寻色进度（后端 ColorRun.progress；原工具浮窗的步骤、倒计时与“已用 / 本阶段”）----
    public bool HasProgress => Data.Progress is not null;
    public string ProgressCount => Data.Progress?.Count ?? string.Empty;
    public bool HasProgressCount => ProgressCount.Length > 0;
    public string RemainingText => Data.Progress?.RemainingText(DateTimeOffset.Now) ?? string.Empty;
    public bool HasRemaining => RemainingText.Length > 0;

    /// <summary>剩 10 秒内显示为琥珀色（原工具浮窗 lowTime）。</summary>
    public bool RemainingLow => Data.Progress?.Low(DateTimeOffset.Now) == true;
    public string ElapsedText => Data.Progress?.ElapsedText(DateTimeOffset.Now) ?? string.Empty;

    // ---- 浮层 ----
    public RegionCardModel? PickerCard
    {
        get => pickerCard;
        private set
        {
            if (!Set(ref pickerCard, value)) return;
            Raise(nameof(IsPickerOpen));
        }
    }

    public bool IsPickerOpen => pickerCard is not null;

    public RegionCardModel? AtlasCard
    {
        get => atlasCard;
        private set
        {
            if (!Set(ref atlasCard, value)) return;
            // 关闭时只取消仍在等待的读取，图像、说明与缩放保持原样，浮层淡出期间内容不变；下次打开时重置。
            if (value is null)
            {
                atlasRequest?.Cancel();
                atlasRequest = null;
            }
            else
            {
                atlasZoom = 0;
                LoadFullAtlas();
                Raise(nameof(AtlasZoom));
                Raise(nameof(AtlasFitted));
            }
            Raise(nameof(IsAtlasOpen));
        }
    }

    public bool IsAtlasOpen => atlasCard is not null;

    /// <summary>集合浮层的全集图：打开浮层时向后端读取（精准 HEX 直接用目标色），读取中为空。</summary>
    public Avalonia.Media.Imaging.Bitmap? AtlasImage { get => atlasImage; private set { Set(ref atlasImage, value); Raise(nameof(HasAtlasImage)); } }
    public bool HasAtlasImage => atlasImage is not null;

    /// <summary>集合浮层标题旁的说明：“精准 HEX · N 色”或“ΔE ≤ T · N 色”。</summary>
    public string AtlasNote { get => atlasNote; private set => Set(ref atlasNote, value); }

    /// <summary>全集图读取中（“色板计算中…”）或失败的说明；有图时为空。</summary>
    public string AtlasMessage { get => atlasMessage; private set { Set(ref atlasMessage, value); Raise(nameof(HasAtlasMessage)); } }
    public bool HasAtlasMessage => atlasMessage.Length > 0;

    /// <summary>集合浮层的缩放：0 表示铺满（“适合”），其余是图像像素的放大倍数。</summary>
    public double AtlasZoom => atlasZoom;
    public bool AtlasFitted => atlasZoom <= 0;

    /// <summary>集合浮层可见区域的尺寸，由界面在排版后写入，用于“＋”的倍数（原工具 AtlasPanel）。</summary>
    public Avalonia.Size AtlasViewport { get; set; }

    // ---- 二级层 ----
    public bool IsPresetsOpen => layer == "presets";
    public bool IsHistoryOpen => layer == "history";
    public bool IsLayerOpen => layer is not null;

    public string ProfileName
    {
        get => profileName;
        set
        {
            if (Set(ref profileName, value)) Raise(nameof(CanSaveProfile));
        }
    }

    /// <summary>方案名称 1–60 个字符（原工具 profile_store）；是否重复由后端判定。</summary>
    public bool CanSaveProfile => profileName.Trim().Length is >= 1 and <= 60 && Data.Draft is not null;
    public string PresetMessage { get => presetMessage; private set { Set(ref presetMessage, value); Raise(nameof(HasPresetMessage)); } }
    public bool HasPresetMessage => presetMessage.Length > 0;
    public bool NoProfiles => Data.Profiles.Count == 0;

    /// <summary>选色方案层顶部“默认预设”的两组内置只读预设：日系插画配色在前，时尚设计配色在后。</summary>
    public IReadOnlyList<ColorPreset> IllustrationPresets { get; } = ColorPreset.BuiltIn.Where(preset => preset.Group == ColorPresetGroup.Illustration).ToList();
    public IReadOnlyList<ColorPreset> FashionPresets { get; } = ColorPreset.BuiltIn.Where(preset => preset.Group == ColorPresetGroup.Fashion).ToList();

    /// <summary>“默认预设”折叠区是否展开；默认收起。</summary>
    /// <summary>“默认预设”折叠区是否展开（缺省收起）；存入卡片状态存取处，下次打开保持。</summary>
    public bool PresetsExpanded
    {
        get => CardStateStore.IsExpanded("dye", "default-presets", false);
        private set { CardStateStore.SetExpanded("dye", "default-presets", value, false); Raise(); }
    }

    public override void Dispose()
    {
        disposed = true;
        saveTicket++;
        if (dirty) _ = SaveNowAsync();
        History.Close();
        progressClock.Stop();
        atlasRequest?.Cancel();
        foreach (var card in Cards) card.CancelAtlas();
        base.Dispose();
    }

    /// <summary>立即保存待保存的改动（开始寻色前、离开页面时）。</summary>
    public async Task FlushAsync()
    {
        saveTicket++;
        await SaveNowAsync();
        while (saving) await Task.Delay(10);
    }

    internal void OnCardEdited()
    {
        dirty = true;
        held = false;
        if (!disposed) _ = DebounceAsync(++saveTicket);
    }

    private async Task DebounceAsync(int ticket)
    {
        await Task.Delay(SaveDelay);
        if (ticket == saveTicket && !disposed) await SaveNowAsync();
    }

    /// <summary>选了某个优先级时与原本占用它的区域互换，三个区域始终各占一个优先级（原工具 set_card_priority）。</summary>
    internal void SetPriority(RegionCardModel card, int value)
    {
        if (card.Priority == value || value is < 1 or > 3) return;
        if (Cards.FirstOrDefault(other => other != card && other.Priority == value) is { } holder) holder.SetPriorityValue(card.Priority);
        card.SetPriorityValue(value);
        OnCardEdited();
    }

    internal void TogglePicker(RegionCardModel card)
    {
        if (pickerCard == card)
        {
            ClosePicker();
            return;
        }
        ClosePicker();
        AtlasCard = null;
        pickerOriginal = card.TargetHex;
        card.Picker.Open();
        PickerCard = card;
    }

    internal void ToggleAtlas(RegionCardModel card)
    {
        ClosePicker();
        AtlasCard = atlasCard == card ? null : card;
    }

    internal Task<ColorAtlasReply> ReadAtlasAsync(string hex, string mode, double tolerance, bool full, CancellationToken cancellationToken) =>
        Backend.GetColorAtlasAsync(hex, mode, tolerance, full, cancellationToken);

    /// <summary>卡片的目标色、颜色要求或容差变化：集合浮层正开着这张卡时重新读取全集图。</summary>
    internal void OnAtlasRulesChanged(RegionCardModel card)
    {
        if (card == atlasCard) LoadFullAtlas();
    }

    // 全集图只在浮层打开时读取；关闭、换卡或规则变化时取消仍在等待的请求。精准 HEX 的集合就是目标色本身，不发请求。
    private void LoadFullAtlas()
    {
        atlasRequest?.Cancel();
        atlasRequest = null;
        AtlasImage = null;
        AtlasNote = string.Empty;
        AtlasMessage = string.Empty;
        if (atlasCard is not { } card) return;
        if (card.IsExact)
        {
            AtlasImage = card.Atlas.Thumbnail;
            AtlasNote = ColorAtlasView.Note(true, 0, 1);
            return;
        }
        AtlasMessage = ColorAtlasView.ComputingText;
        atlasRequest = new CancellationTokenSource();
        _ = ReadFullAtlasAsync(card, atlasRequest.Token);
    }

    private async Task ReadFullAtlasAsync(RegionCardModel card, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var reply = await Backend.GetColorAtlasAsync(card.TargetHex, card.MatchMode, card.Tolerance, true, token);
                if (token.IsCancellationRequested || card != atlasCard) return;
                if (reply.Atlas is { } atlas)
                {
                    AtlasMessage = string.Empty;
                    AtlasImage = atlas.Image;
                    AtlasNote = ColorAtlasView.Note(false, atlas.Tolerance, atlas.Count);
                    Raise(nameof(AtlasZoom));
                    return;
                }
                if (!reply.Retryable || attempt >= RegionCardModel.AtlasRetries)
                {
                    AtlasMessage = reply.Message;
                    return;
                }
                AtlasMessage = reply.Message;
                await Task.Delay(RegionCardModel.AtlasRetryDelay, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 屏幕吸管：取到的颜色写入该区域目标色；失败时状态行显示“屏幕取色失败：…”。
    /// 没能切到游戏窗口不影响取色，状态行注明原因。
    /// </summary>
    internal async Task PickFromScreenAsync(RegionCardModel card)
    {
        if (!CanPick) return;
        picking = true;
        Raise(nameof(CanPick));
        ClosePicker();
        AtlasCard = null;
        try
        {
            var result = await EyedropperSession.RunAsync(Backend, pickHost, CancellationToken.None);
            if (result.Hex is { } hex)
            {
                Message = result.Note ?? string.Empty;
                keepPickNote = result.Note is not null;
                card.SetTarget(hex);
            }
            else if (result.Error is { } error)
                Message = UiText.Pick("屏幕取色失败：", "螢幕取色失敗：") + error + (result.Note is { } note ? " · " + note : string.Empty);
            else Message = result.Note ?? string.Empty;
        }
        finally
        {
            picking = false;
            Raise(nameof(CanPick));
        }
    }

    protected override void OnDataChanged()
    {
        Adopt();
        foreach (var card in Cards) card.SetReading(Data.Readings.GetValueOrDefault(card.Id) ?? new(null, null, null));
        SyncProgressClock();
    }

    private void SyncProgressClock()
    {
        if (Data.Progress is not null && !disposed && !inactive) progressClock.Start();
        else progressClock.Stop();
        RaiseProgress();
    }

    private void RaiseProgress()
    {
        Raise(nameof(HasProgress));
        Raise(nameof(ProgressCount));
        Raise(nameof(HasProgressCount));
        Raise(nameof(RemainingText));
        Raise(nameof(HasRemaining));
        Raise(nameof(RemainingLow));
        Raise(nameof(ElapsedText));
    }

    private string Message
    {
        get => message;
        set
        {
            if (!Set(ref message, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(DetailText));
            Raise(nameof(HasDetailText));
            Raise(nameof(StatusLive));
            Raise(nameof(StatusWarn));
        }
    }

    /// <summary>染色界面固定三个区域（原工具三张 RegionCard）；没有读到草稿时按这个数目排占位卡片。</summary>
    internal const int RegionCount = 3;

    // 后端草稿只在没有待保存、保存中或被拒绝后保留的本地改动时写回卡片。
    // 从未读到草稿（未连接后端、后端没有提供草稿）时排三张停用的占位卡片，页面结构与布局编辑照常；
    // 读到过草稿后再失去连接时保留最后读到的卡片，由 CanEdit 停用。
    private void Adopt()
    {
        if (Data.Draft is null && Cards.Count == 0)
            for (var index = 0; index < RegionCount; index++) Cards.Add(RegionCardModel.Unread(this, index));
        if (Data.Draft is not { } draft || dirty || saving || held) return;
        if (Cards.Any(card => card.IsUnread) || !Cards.Select(card => card.Id).SequenceEqual(draft.Regions.Select(region => region.Id)))
        {
            ClosePicker();
            AtlasCard = null;
            foreach (var card in Cards) card.CancelAtlas();
            Cards.Clear();
            for (var index = 0; index < draft.Regions.Count; index++) Cards.Add(new RegionCardModel(this, index, draft.Regions[index]));
        }
        else
            // ColorRegionDraft 按内容比较（含替代色列表）：设置没变的卡片不重新载入。
            for (var index = 0; index < draft.Regions.Count; index++)
                if (Cards[index].ToDraft() != draft.Regions[index].ToDraft() || Cards[index].Available != draft.Regions[index].Available)
                    Cards[index].Load(draft.Regions[index]);
        foreach (var card in Cards) card.SetReading(Data.Readings.GetValueOrDefault(card.Id) ?? new(null, null, null));
        if (strategy != draft.Strategy)
        {
            strategy = draft.Strategy;
            RaiseStrategy();
        }
    }

    private ColorDraft BuildDraft() => new(strategy, Cards.Select(card => card.ToDraft()).ToList());

    private async Task SaveNowAsync()
    {
        if (!dirty || Data.Draft is null) return;
        if (saving)
        {
            saveAgain = true;
            return;
        }
        saving = true;
        dirty = false;
        var reply = await Backend.SaveColorDraftAsync(BuildDraft(), CancellationToken.None);
        saving = false;
        if (reply.Ok)
        {
            if (message.Length > 0 && !picking && !keepPickNote) Message = string.Empty;
            keepPickNote = false;
        }
        else
        {
            // 被拒绝时保留卡片上的输入，等用户再改；错误码显示在状态行。
            held = !dirty;
            Message = reply.Message;
        }
        if (saveAgain || dirty)
        {
            saveAgain = false;
            dirty = true;
            await SaveNowAsync();
        }
        else Reload();
    }

    private async Task StartAsync()
    {
        if (!Data.Actions.CanStart) return;
        await FlushAsync();
        Send("color.start", null);
    }

    private void OpenLayer(string? value)
    {
        if (layer == value) value = null;
        layer = value;
        PresetMessage = string.Empty;
        if (value == "history") _ = History.OpenAsync();
        else History.Close();
        Raise(nameof(IsPresetsOpen));
        Raise(nameof(IsHistoryOpen));
        Raise(nameof(IsLayerOpen));
    }

    private async Task SaveProfileAsync()
    {
        if (!CanSaveProfile) return;
        await FlushAsync();
        var reply = await Backend.SaveColorProfileAsync(profileName.Trim(), BuildDraft(), CancellationToken.None);
        if (reply.Ok) ProfileName = string.Empty;
        PresetMessage = reply.Ok ? UiText.Pick("已保存", "已儲存") : reply.Message;
    }

    // 载入以方案覆盖草稿：放弃卡片上尚未保存的改动，成功后关闭层，卡片随后端读回的草稿更新。
    private async Task LoadProfileAsync(string? id)
    {
        if (id is null) return;
        saveTicket++;
        dirty = false;
        held = false;
        var reply = await Backend.LoadColorProfileAsync(id, CancellationToken.None);
        if (!reply.Ok)
        {
            PresetMessage = reply.Message;
            return;
        }
        Message = string.Empty;
        OpenLayer(null);
        Reload();
    }

    // 套用默认预设：区域 1–3 的目标色依次设为预设颜色，其余设置不变；经卡片的目标色路径自动保存，随后关闭层。
    private void ApplyPreset(string? id)
    {
        if (!CanEdit || ColorPreset.BuiltIn.FirstOrDefault(preset => preset.Id == id) is not { } preset) return;
        for (var index = 0; index < Math.Min(Cards.Count, preset.Colors.Count); index++) Cards[index].SetTarget(preset.Colors[index]);
        OpenLayer(null);
    }

    private async Task DeleteProfileAsync(string? id)
    {
        if (id is null) return;
        var reply = await Backend.DeleteColorProfileAsync(id, CancellationToken.None);
        PresetMessage = reply.Ok ? string.Empty : reply.Message;
    }

    private void ClosePicker()
    {
        if (pickerCard is { } card && card.TargetHex != pickerOriginal) ColorRecent.Remember(card.TargetHex);
        PickerCard = null;
    }

    // 原工具 AtlasPanel：适合＝铺满；1:1；＋按可见区域与图像的比例放大 1.5 倍（最多 6 倍）；－每次缩小到 1/1.5（最少 0.2）。
    private void Zoom(string? command)
    {
        if (atlasImage is not { } image) return;
        var size = image.PixelSize;
        atlasZoom = command switch
        {
            "fit" => 0,
            "one" => 1,
            "in" => Math.Min(6, Math.Max(size.Width / Math.Max(1, AtlasViewport.Width), size.Height / Math.Max(1, AtlasViewport.Height)) * 1.5),
            "out" => Math.Max(0.2, atlasZoom <= 0 ? 0.8 : atlasZoom / 1.5),
            _ => atlasZoom
        };
        Raise(nameof(AtlasZoom));
        Raise(nameof(AtlasFitted));
    }

    private void RaiseStrategy()
    {
        Raise(nameof(IsScreenshot));
        Raise(nameof(IsPrecise));
        Raise(nameof(StrategyNote));
    }
}
