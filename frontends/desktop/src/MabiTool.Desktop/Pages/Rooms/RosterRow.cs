using Avalonia;
using Avalonia.Media;
using MabiTool.Contracts;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>
/// 成员表的一行（原工具 MemberRow）。单元格编辑通过 <see cref="Edit"/> 交给页面模型，每次一个字段；名单以后端重读为准。
/// 同一成员在刷新之间保持同一个行对象（<see cref="Update"/> 就地换内容并通知），表格不替换行、不重建单元格，
/// 行菜单与行内下拉在后端每秒刷新时不会被关闭。
/// </summary>
public sealed class RosterRow : MabiTool.Desktop.Infrastructure.ObservableObject
{
    // 同组同底色（原工具 GroupColors）：色块数据，低透明度叠在面板上，两种主题下文字对比不受影响。
    private static readonly Color[] GroupColors =
    [
        Color.Parse("#2E6FD6"), Color.Parse("#D43D74"), Color.Parse("#0E8C7D"), Color.Parse("#6F51E0"),
        Color.Parse("#C97A10"), Color.Parse("#2A9CB8"), Color.Parse("#B8457E"), Color.Parse("#5E8F1E")
    ];

    private readonly Action<RosterRow, string, string> edit;

    public RosterRow(RoomMember member, int index, bool self, bool conductor, bool canEdit, bool ensemble,
        IReadOnlyList<string> instruments, Action<RosterRow, string, string> edit)
    {
        Member = member;
        Index = index;
        IsSelf = self;
        Conductor = conductor;
        CanEdit = canEdit;
        Ensemble = ensemble;
        Instruments = instruments;
        this.edit = edit;
        GroupText = member.Group.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OrderText = member.Order.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public RoomMember Member { get; private set; }
    public string Id => Member.Id;
    public int Index { get; private set; }
    public bool IsSelf { get; private set; }
    public bool Conductor { get; private set; }
    public bool CanEdit { get; private set; }
    public bool ReadOnly => !CanEdit;
    public bool Ensemble { get; private set; }
    public string Nickname => Member.Nickname;
    public IReadOnlyList<string> Instruments { get; private set; }

    /// <summary>
    /// 同一成员的新数据：内容不变时什么都不做；乐器列表内容相同时保留原列表（下拉不因换数据源而关闭）；
    /// 组号与顺序输入框只在后端的值变化时改写，不覆盖正在输入的内容。返回内容是否有变化。
    /// </summary>
    public bool Update(RoomMember member, int index, bool self, bool conductor, bool canEdit, bool ensemble, IReadOnlyList<string> instruments)
    {
        if (member.Id != Member.Id) throw new ArgumentException("行只能更新同一成员", nameof(member));
        var sameInstruments = instruments.SequenceEqual(Instruments);
        if (member == Member && index == Index && self == IsSelf && conductor == Conductor && canEdit == CanEdit && ensemble == Ensemble && sameInstruments)
            return false;
        if (member.Group != Member.Group) GroupText = member.Group.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (member.Order != Member.Order) OrderText = member.Order.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (Member, Index, IsSelf, Conductor, CanEdit, Ensemble) = (member, index, self, conductor, canEdit, ensemble);
        if (!sameInstruments) Instruments = instruments;
        Raise(string.Empty);
        return true;
    }

    public IBrush GroupBrush => new SolidColorBrush(GroupColors[(Math.Max(1, Member.Group) - 1) % GroupColors.Length], 0.2);
    public IBrush GroupStripe => new SolidColorBrush(GroupColors[(Math.Max(1, Member.Group) - 1) % GroupColors.Length]);

    /// <summary>组号与顺序的输入框内容；按回车或离开输入框时提交。</summary>
    public string GroupText { get; set; }
    public string OrderText { get; set; }

    public string RoomRole => Member.IsHost ? UiText.Pick("房主", "房主") : UiText.Pick("成员", "成員");

    public string State => !Member.Connected ? UiText.Pick("已离线", "已離線")
        : Member.IsHost ? UiText.Pick("房主", "房主")
        : Member.Ready ? UiText.Pick("已就绪", "已就緒") : UiText.Pick("未就绪", "未就緒");

    public bool StateReady => Member.Connected && !Member.IsHost && Member.Ready;
    public bool StateWaiting => Member.Connected && !Member.IsHost && !Member.Ready;
    public bool StateNeutral => !StateReady && !StateWaiting;

    public string Role => Member.Initiator ? UiText.Pick("乐谱使用者", "樂譜使用者") : UiText.Pick("参与者", "參與者");
    public IReadOnlyList<string> Roles { get; } = [UiText.Pick("乐谱使用者", "樂譜使用者"), UiText.Pick("参与者", "參與者")];

    /// <summary>合奏身份下拉：0 为乐谱使用者，1 为参与者；改变即提交。</summary>
    public int RoleIndex
    {
        get => Member.Initiator ? 0 : 1;
        set
        {
            if (value is 0 or 1 && value != RoleIndex) edit(this, "role", value == 0 ? "initiator" : "joiner");
        }
    }

    /// <summary>乐器下拉：第一项“未指定”提交空字符串；改变即提交。</summary>
    public string Instrument
    {
        get => Member.Instrument.Length == 0 ? Instruments[0] : Member.Instrument;
        set
        {
            if (value is null) return;
            var next = value == Instruments[0] ? string.Empty : value;
            if (next != Member.Instrument) edit(this, "instrument", next);
        }
    }

    /// <summary>参与勾选；改变即提交。</summary>
    public bool Selected
    {
        get => Member.Selected;
        set
        {
            if (value != Member.Selected) edit(this, "selected", value ? "true" : "false");
        }
    }

    public string Tip => Nickname;
    /// <summary>复制成员表时“参与”一列的文字。</summary>
    public string SelectedText => Selected ? UiText.Pick("参与", "參與") : UiText.Pick("不参与", "不參與");
}

/// <summary>拖动落位的换算：把拖到的位置换成目标分组与组内顺序。纯函数。</summary>
public static class RosterDrop
{
    /// <param name="groups">按显示顺序排列的各行组号。</param>
    /// <param name="from">拖起的行。</param>
    /// <param name="target">落位后在列表里的位置（0 到行数减一）。</param>
    /// <returns>新的组号与组内顺序（从 1 开始）；没有变化时为空。</returns>
    /// <remarks>向下拖时排在经过的最后一行之后、属于那一行的组；向上拖时排在经过的最后一行之前、属于那一行的组。</remarks>
    public static (int Group, int Order)? Resolve(IReadOnlyList<int> groups, int from, int target)
    {
        if (from < 0 || from >= groups.Count || target < 0 || target >= groups.Count || target == from) return null;
        var others = groups.Where((_, index) => index != from).ToList();
        var group = target > from ? others[target - 1] : others[target];
        var order = 1 + others.Take(target).Count(value => value == group);
        var original = 1 + groups.Take(from).Count(value => value == groups[from]);
        return group == groups[from] && order == original ? null : (group, order);
    }
}

/// <summary>点选按钮位置的坐标换算：屏幕物理像素换成游戏客户区坐标。纯函数。</summary>
public static class ButtonPick
{
    /// <returns>游戏按钮绑定；点在客户区外时为空。</returns>
    public static InputBindingPreference? ToBinding(RoomGameWindow client, PixelPoint screen)
    {
        var x = screen.X - client.X;
        var y = screen.Y - client.Y;
        return x < 0 || y < 0 || x >= client.Width || y >= client.Height ? null
            : new(LocalBindingKind.Button, X: x, Y: y, ClientWidth: client.Width, ClientHeight: client.Height, Dpi: client.Dpi);
    }
}
