using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Character;

/// <summary>
/// 角色信息页模型（人物与成长；货币在背包页）：数据来自后端适配层按页面可见性读取的游戏连接器数据（可见处 <c>character</c>，进入页面读取一次，
/// 之后环境与附近每 5 秒、属性/任务每 30 秒、成长指南每 60 秒），“刷新”按钮发 <c>character.refresh</c>。页面只做显示：任务按日常与周常两个页签，
/// 页签选择是界面上的显示选择。
/// </summary>
public sealed class CharacterPageModel : BackendPageModel<CharacterPageData>
{
    private bool weekly;

    /// <summary>页面没有自己的计时器：切走后保留，回来时直接显示并重新进入读取。</summary>
    protected override bool Cacheable => true;

    protected override string? Surface => "character";

    public CharacterPageModel(IDesktopBackend backend) : base(backend, source => source.GetCharacter())
    {
        SelectMissionCommand = new RelayCommand(tab =>
        {
            var next = tab as string == "weekly";
            if (next == weekly) return;
            weekly = next;
            Raise(nameof(ShowWeekly));
            Raise(nameof(ShowDaily));
            Raise(nameof(SelectedMission));
        });
        shownNotice = CapabilityNotice;
    }

    public ICommand SelectMissionCommand { get; }

    public bool ShowWeekly => weekly;
    public bool ShowDaily => !weekly;

    /// <summary>当前页签的任务。</summary>
    public MissionTabView SelectedMission => weekly ? Data.Weekly : Data.Daily;

    public bool HasStatus => Data.Status.Length > 0;

    /// <summary>刷新按钮：后端已连接且读到过数据接口时可点；样本、离线或后端没有数据接口时停用。</summary>
    public bool CanRefresh => Data.Read is CharacterRead.Loaded or CharacterRead.NotRead;

    /// <summary>能力 <c>character.info</c>（命令行的角色数据）不可用的原因，后面指向“游戏连接器”胶囊；可用或未知时为空。</summary>
    public string CapabilityNotice => Backend.GetCapabilityNotice(CapabilityIds.CharacterInfo) ?? string.Empty;
    public bool HasCapabilityNotice => CapabilityNotice.Length > 0;

    public bool HasStats => Data.Stats.Count > 0;
    public bool HasPaladin => Data.PaladinStats.Count > 0;
    public bool HasSkills => Data.Skills.Count > 0;
    public bool NoStats => !HasStats;
    public bool NoSkills => !HasSkills;
    public string UnreadText => CharacterText.Unread;
    public string SampleMark => Data.Sample ? UiText.Pick("样本数据，不是游戏里读到的内容", "樣本資料，不是遊戲裡讀到的內容") : string.Empty;
    public bool HasSampleMark => Data.Sample;

    // 数据对象没变时，能力说明（取自能力表，不在数据里）变了照常重读并通知。
    private string shownNotice = string.Empty;

    protected override bool OtherStateChanged()
    {
        var next = CapabilityNotice;
        if (next == shownNotice) return false;
        shownNotice = next;
        return true;
    }

    protected override void OnDataChanged() => shownNotice = CapabilityNotice;
}
