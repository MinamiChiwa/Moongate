using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using MabiTool.Desktop.Backend;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Music;

public sealed partial class MusicPage : UserControl
{
    private MusicPageModel? watched;

    public MusicPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnModelChanged;
            watched = DataContext as MusicPageModel;
            if (watched is not null) watched.PropertyChanged += OnModelChanged;
        };
        // 乐谱表与清单条目里的按钮在模板里，点击沿路由事件冒泡到页面，按按钮的标记分发。
        AddHandler(Button.ClickEvent, OnTemplateButtonClick, handledEventsToo: true);
    }

    // 打开时焦点落在层内的关闭按钮，关闭后回到入口按钮。
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (watched is null) return;
        // 层的可见性随绑定更新，等布局完成后再移焦点。
        switch (e.PropertyName)
        {
            case nameof(MusicPageModel.IsGameOpen):
                MoveFocus(watched.IsGameOpen, GameLayerClose, GameLayerButton);
                break;
            case nameof(MusicPageModel.IsListsOpen):
                MoveFocus(watched.IsListsOpen, ListsLayerClose, ListsButton);
                break;
        }
    }

    private static void MoveFocus(bool open, Control inside, Control entry) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (open ? inside : entry).Focus(NavigationMethod.Tab), Avalonia.Threading.DispatcherPriority.Loaded);

    private void OnGameLayerOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.CloseGameCommand.Execute(null);
        e.Handled = true;
    }

    private void OnGameLayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is not { IsGameOpen: true }) return;
        watched.CloseGameCommand.Execute(null);
        e.Handled = true;
    }

    private void OnListsLayerOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.CloseListsCommand.Execute(null);
        e.Handled = true;
    }

    private void OnListsLayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is not { IsListsOpen: true }) return;
        watched.CloseListsCommand.Execute(null);
        e.Handled = true;
    }

    // 乐谱表的“加入清单”：列出全部清单；没有清单时提示先去“管理清单”新建。清单条目的乐器按钮列出清单默认与全部乐器；
    // 演奏队列的乐器按钮列出“游戏当前乐器”与乐器目录，当前选择打勾。
    private void OnTemplateButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string tag, CommandParameter: string id } button || watched is null) return;
        var lists = watched.Lists;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var items = new List<MenuItem>();
        switch (tag)
        {
            case "add-to-list":
                foreach (var playlist in lists.Playlists)
                {
                    var target = playlist.Id;
                    var item = new MenuItem { Header = playlist.FolderPath.Length == 0 ? playlist.Name : playlist.FolderPath + " / " + playlist.Name };
                    item.Click += (_, _) => _ = lists.AddScoreAsync(target, id);
                    items.Add(item);
                }
                if (items.Count == 0) items.Add(new MenuItem { Header = Pick("还没有清单，先点“管理清单”新建", "還沒有清單，先點「管理清單」新增"), IsEnabled = false });
                break;
            case "item-instrument":
                items.Add(InstrumentItem(lists, id, string.Empty, Pick("沿用清单", "沿用清單")));
                foreach (var instrument in lists.InstrumentChoices) items.Add(InstrumentItem(lists, id, instrument.Id, instrument.Name));
                if (lists.InstrumentsNote is { } listNote) items.Add(new MenuItem { Header = listNote, IsEnabled = false });
                break;
            case "queue-instrument":
            {
                var model = watched;
                var current = model.Data.Queue.FirstOrDefault(row => row.Id == id)?.InstrumentId ?? string.Empty;
                MenuItem Choice(string instrumentId, string name)
                {
                    var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = instrumentId == current };
                    item.Click += (_, _) => model.SetQueueInstrument(id, instrumentId.Length == 0 ? null : instrumentId);
                    return item;
                }
                items.Add(Choice(string.Empty, Pick("游戏当前乐器", "遊戲目前樂器")));
                foreach (var instrument in model.Instruments) items.Add(Choice(instrument.Id, instrument.Name));
                if (model.Lists.InstrumentsNote is { } queueNote) items.Add(new MenuItem { Header = queueNote, IsEnabled = false });
                break;
            }
            default:
                return;
        }
        flyout.ItemsSource = items;
        flyout.ShowAt(button);
    }

    private static MenuItem InstrumentItem(MusicListsModel lists, string itemId, string instrumentId, string name)
    {
        var item = new MenuItem { Header = name };
        item.Click += (_, _) => _ = lists.SetItemInstrumentAsync(itemId, instrumentId);
        return item;
    }
}
