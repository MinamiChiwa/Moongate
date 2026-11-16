using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using MabiTool.Desktop.Controls.Tables;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Rooms;

public sealed partial class RoomsPage : UserControl
{
    private TopLevel? host;
    private RoomsPageModel? model;

    public RoomsPage()
    {
        InitializeComponent();
        RosterReorder.Attach(EnsembleRows.List, () => DataContext as RoomsPageModel);
        // 成员表按成员标识恢复所选行；行上右键先给行菜单（合奏且房主连接时），其余时候是表格的复制菜单。
        EnsembleRows.RowKey = row => (row as RosterRow)?.Id;
        EmoteRows.RowKey = row => (row as RosterRow)?.Id;
        EnsembleRows.RowContextRequested += OnRowContext;
        // 输入框在按回车或离开时提交（昵称、地址、倒计时、时长、尾音、时间微调）。
        foreach (var (box, commit) in new (TextBox, Action<RoomsPageModel, TextBox>)[]
                 {
                     (NicknameBox, (page, _) => page.CommitConnection()), (InvitationBox, (_, _) => { }),
                     (EndpointBox, (page, _) => page.CommitConnection()),
                     (CountdownBox, (page, _) => page.CommitNumber("countdown")), (DurationBox, (page, _) => page.CommitNumber("duration")),
                     (TailBox, (page, _) => page.CommitNumber("tail")), (OffsetBox, (page, box) => page.OffsetText = box.Text ?? string.Empty)
                 })
        {
            box.LostFocus += (_, _) => { if (DataContext is RoomsPageModel page) commit(page, box); };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || DataContext is not RoomsPageModel page) return;
                e.Handled = true;
                commit(page, box);
            };
        }
        LanAddressBox.LostFocus += (_, _) => (DataContext as RoomsPageModel)?.CommitConnection();
        // 状态卡的详情区按页面高度给出上限（原工具状态卡最多占窗口一半），矮窗口里成员与设置卡片仍有空间。
        SizeChanged += (_, e) => StatusDetails.MaxHeight = Math.Clamp(e.NewSize.Height * 0.2, 60, 260);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (model is not null) model.PickRequested -= OnPickRequested;
        model = DataContext as RoomsPageModel;
        if (model is not null) model.PickRequested += OnPickRequested;
    }

    // 录入按键时在窗口层先于其他控件拿到按键，空格、回车也不会被当成点击；Esc 关闭打开的二级层。
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        host = TopLevel.GetTopLevel(this);
        host?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        host?.RemoveHandler(KeyDownEvent, OnKeyDown);
        host = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RoomsPageModel page) return;
        if (page.OnKey(e.Key, e.KeyModifiers)) e.Handled = true;
        else if (e.Key == Key.Escape && (page.LanGuideOpen || page.AdvancedOpen))
        {
            page.CloseLayerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLayerBackdrop(object? sender, PointerPressedEventArgs e) => (DataContext as RoomsPageModel)?.CloseLayerCommand.Execute(null);

    private async void OnPickRequested(object? sender, string slot)
    {
        if (DataContext is not RoomsPageModel page) return;
        var hint = this.TryFindResource("R.PickOverlay", ActualThemeVariant, out var text) ? text as string ?? string.Empty : string.Empty;
        await page.PickAsync(slot, area => ButtonPickerWindow.PickAsync(area, hint, this));
    }

    // 组号与顺序的单元格（TableInput）：回车或离开时提交一次。
    private void OnCellCommitted(object? sender, RoutedEventArgs e)
    {
        if (sender is not TableInput cell || cell.IsReadOnly || cell.DataContext is not RosterRow row || DataContext is not RoomsPageModel page
            || cell.Tag is not string field) return;
        page.EditCell(row, field, cell.Text ?? string.Empty);
    }

    private void OnRowMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RosterRow row } control) OpenMenu(control, row);
    }

    private void OnRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: RosterRow row } control && OpenMenu(control, row)) e.Handled = true;
    }

    // 行菜单（原工具 RosterMenuOpening）：只在合奏且房主连接时出现。
    private bool OpenMenu(Control anchor, RosterRow row)
    {
        if (DataContext is not RoomsPageModel page || !page.IsEnsemble || !page.IsHost || !page.Data.RoomConnected) return false;
        var editable = page.CanEditRoster;
        string Text(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as string ?? key : key;
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        var items = new List<Control>
        {
            Item(Text(row.Selected ? "R.MenuDeselect" : "R.MenuSelect"), () => page.EditCell(row, "selected", row.Selected ? "false" : "true"), page.CanSelect),
            Item(Text("R.MenuConductor"), () => page.SetConductor(row), page.CanSelectConductor && row.Id != page.Data.Room?.LeaderId),
            Item(Text(row.Member.Initiator ? "R.MenuJoiner" : "R.MenuInitiator"),
                () => page.EditCell(row, "role", row.Member.Initiator ? "joiner" : "initiator"), editable)
        };
        var characters = new MenuItem { Header = Text("R.MenuCharacter") };
        var choices = page.Characters.Select(character => (Control)Item(UiText.Pick($"{character.Nickname} · 区服 {character.RealmId}",
            $"{character.Nickname} · 區服 {character.RealmId}"), () => page.BindCharacter(row, character))).ToList();
        if (choices.Count == 0) choices.Add(new MenuItem { Header = Text("R.MenuNoCharacter"), IsEnabled = false });
        choices.Add(Item(Text("R.MenuUnbind"), () => page.BindCharacter(row, null)));
        characters.ItemsSource = choices;
        items.Add(characters);
        var same = page.Rows.Where(item => item.Member.Group == row.Member.Group).OrderBy(item => item.Member.Order).ToList();
        var index = same.FindIndex(item => item.Id == row.Id);
        if (index > 0) items.Add(Item(Text("R.MenuUp"), () => page.MoveInGroup(row, -1), editable));
        if (index >= 0 && index < same.Count - 1) items.Add(Item(Text("R.MenuDown"), () => page.MoveInGroup(row, 1), editable));
        var existing = page.Rows.Select(item => item.Member.Group).Distinct().Order().ToList();
        var groups = existing.Select(group => (Control)Item(UiText.Pick($"第 {group} 组（排在末尾）", $"第 {group} 組（排在末尾）"),
            () => page.MoveToGroup(row, group), group != row.Member.Group)).ToList();
        var next = Enumerable.Range(1, 24).FirstOrDefault(group => !existing.Contains(group));
        if (next != 0) groups.Add(Item(UiText.Pick($"新建第 {next} 组", $"新建第 {next} 組"), () => page.MoveToGroup(row, next)));
        items.Add(new MenuItem { Header = Text("R.MenuGroups"), IsEnabled = editable, ItemsSource = groups });
        if (!row.IsSelf)
        {
            items.Add(new Separator());
            items.Add(Item(Text("R.MenuKick"), () => page.Kick(row)));
        }
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Open(anchor);
        return true;
    }
}
