using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Music;

/// <summary>层里的一个单选项（文件夹、乐器）：<see cref="Id"/> 为空表示“根目录”或“未指定”。</summary>
public sealed record ChoiceRow(string Id, string Label, bool Selected, int Indent = 0)
{
    public Avalonia.Thickness Padding => new(Indent * 14, 0, 0, 0);
}

/// <summary>
/// 乐谱库页的“管理清单”层：新建清单与文件夹，对所选清单重命名、备注、默认乐器、移动与删除，清单条目的移除、排序与逐条乐器，
/// 文件夹的重命名与删除。页面只发出编辑意图，是否接受由后端判定；每次编辑发送一次，不自动重发，成功后由后端状态变化带来新数据。
/// 所选清单就是页面清单栏里选中的清单。
/// </summary>
public sealed class MusicListsModel : ObservableObject
{
    private readonly IDesktopBackend backend;
    private MusicListsView view = MusicListsView.Empty;
    private string newName = string.Empty, playlistName = string.Empty, notes = string.Empty, folderName = string.Empty, message = string.Empty;
    private string syncedPlaylistId = string.Empty, syncedName = string.Empty, syncedNotes = string.Empty, folderId = string.Empty, syncedFolderName = string.Empty;
    private bool confirmDeletePlaylist, confirmDeleteFolder, busy, messageIsError;

    public MusicListsModel(IDesktopBackend backend)
    {
        this.backend = backend;
        CreatePlaylistCommand = new RelayCommand(_ => _ = CreatePlaylistAsync());
        CreateFolderCommand = new RelayCommand(_ => _ = CreateFolderAsync());
        SelectFolderCommand = new RelayCommand(value => { FolderId = value as string ?? string.Empty; });
        RenameFolderCommand = new RelayCommand(_ => _ = RunAsync(new(MusicListEditKind.RenameFolder, FolderId: folderId, Text: folderName)));
        DeleteFolderCommand = new RelayCommand(_ => _ = DeleteFolderAsync());
        RenamePlaylistCommand = new RelayCommand(_ => _ = RunPlaylistAsync(MusicListEditKind.RenamePlaylist, playlistName));
        SaveNotesCommand = new RelayCommand(_ => _ = RunPlaylistAsync(MusicListEditKind.SetNotes, notes));
        SetDefaultInstrumentCommand = new RelayCommand(value => _ = RunPlaylistAsync(MusicListEditKind.SetDefaultInstrument, value as string ?? string.Empty));
        MovePlaylistCommand = new RelayCommand(value => _ = RunAsync(new(MusicListEditKind.MovePlaylist, PlaylistId: Playlist?.Id, FolderId: value as string ?? string.Empty)));
        DeletePlaylistCommand = new RelayCommand(_ => _ = DeletePlaylistAsync());
        MoveItemUpCommand = new RelayCommand(value => _ = MoveItemAsync(value as string, -1));
        MoveItemDownCommand = new RelayCommand(value => _ = MoveItemAsync(value as string, 1));
        RemoveItemCommand = new RelayCommand(value => _ = RunAsync(new(MusicListEditKind.RemoveItem, PlaylistId: Playlist?.Id, ItemId: value as string)));
    }

    public ICommand CreatePlaylistCommand { get; }
    public ICommand CreateFolderCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand RenameFolderCommand { get; }
    public ICommand DeleteFolderCommand { get; }
    public ICommand RenamePlaylistCommand { get; }
    public ICommand SaveNotesCommand { get; }
    public ICommand SetDefaultInstrumentCommand { get; }
    public ICommand MovePlaylistCommand { get; }
    public ICommand DeletePlaylistCommand { get; }
    public ICommand MoveItemUpCommand { get; }
    public ICommand MoveItemDownCommand { get; }
    public ICommand RemoveItemCommand { get; }

    /// <summary>页面数据变化后调用：换了清单时输入框改成新清单的值，没改过的输入框跟随后端的新值，已被删除的文件夹退回根目录。</summary>
    public void Update(MusicListsView? next)
    {
        view = next ?? MusicListsView.Empty with { EditBlocked = Pick("后端没有提供清单编辑", "後端沒有提供清單編輯") };
        var current = view.Selected;
        if ((current?.Id ?? string.Empty) != syncedPlaylistId)
        {
            syncedPlaylistId = current?.Id ?? string.Empty;
            playlistName = syncedName = current?.Name ?? string.Empty;
            notes = syncedNotes = current?.Notes ?? string.Empty;
            confirmDeletePlaylist = false;
        }
        else if (current is not null)
        {
            if (playlistName == syncedName) playlistName = current.Name;
            if (notes == syncedNotes) notes = current.Notes;
            syncedName = current.Name; syncedNotes = current.Notes;
        }
        if (folderId.Length > 0 && view.Folders.All(folder => folder.Id != folderId)) { folderId = string.Empty; confirmDeleteFolder = false; }
        var folder = view.Folders.FirstOrDefault(item => item.Id == folderId);
        if (folder is not null)
        {
            if (folderName == syncedFolderName) folderName = folder.Name;
            syncedFolderName = folder.Name;
        }
        else folderName = syncedFolderName = string.Empty;
        Raise(string.Empty);
    }

    public bool Busy
    {
        get => busy;
        private set { if (Set(ref busy, value)) Raise(nameof(CanEdit)); Raise(string.Empty); }
    }

    /// <summary>编辑入口能否使用：连接着后端、曲库可读、没有请求在途。</summary>
    public bool CanEdit => view.CanEdit && !busy;
    public string EditNotice => view.EditBlocked ?? string.Empty;
    public bool HasEditNotice => view.EditBlocked is not null;

    public string Message => message;
    public bool HasMessage => message.Length > 0;
    public bool MessageIsError => messageIsError;

    // ---- 新建与文件夹 ----

    public string NewName
    {
        get => newName;
        set { if (Set(ref newName, value)) Raise(nameof(CanCreate)); }
    }

    public bool CanCreate => CanEdit && newName.Trim().Length is >= 1 and <= 200;

    /// <summary>层里所选的文件夹（根为空）：新建清单与文件夹放进这里，重命名与删除作用于它。</summary>
    public string FolderId
    {
        get => folderId;
        set
        {
            if (!Set(ref folderId, value)) return;
            confirmDeleteFolder = false;
            var folder = view.Folders.FirstOrDefault(item => item.Id == value);
            folderName = syncedFolderName = folder?.Name ?? string.Empty;
            Raise(string.Empty);
        }
    }

    public IReadOnlyList<ChoiceRow> FolderChoices =>
        [new(string.Empty, Pick("根目录", "根目錄"), folderId.Length == 0),
         .. view.Folders.Select(folder => new ChoiceRow(folder.Id, folder.Name, folder.Id == folderId, folder.Depth + 1))];

    public string TargetLabel => folderId.Length == 0 ? Pick("新建位置：根目录", "新增位置：根目錄")
        : Pick("新建位置：", "新增位置：") + (view.Folders.FirstOrDefault(folder => folder.Id == folderId)?.Path ?? string.Empty);

    public bool HasFolder => folderId.Length > 0;

    public string FolderName
    {
        get => folderName;
        set { if (Set(ref folderName, value)) Raise(nameof(CanRenameFolder)); }
    }

    public bool CanRenameFolder => CanEdit && HasFolder && folderName.Trim().Length is >= 1 and <= 200 && folderName != syncedFolderName;
    public bool CanDeleteFolder => CanEdit && HasFolder;
    public string DeleteFolderLabel => confirmDeleteFolder ? Pick("确认删除", "確認刪除") : Pick("删除文件夹", "刪除資料夾");

    // ---- 所选清单 ----

    public PlaylistDetail? Playlist => view.Selected;
    public bool HasPlaylist => view.Selected is not null;
    public bool NoPlaylist => view.Selected is null;

    public string PlaylistHeader => view.Selected is not { } playlist ? string.Empty
        : playlist.FolderPath.Length == 0 ? playlist.Name : playlist.FolderPath + " / " + playlist.Name;

    public string PlaylistSummary => view.Selected is not { } playlist ? string.Empty
        : Pick($"{playlist.Items.Count} 首，默认乐器：{playlist.DefaultInstrument}", $"{playlist.Items.Count} 首，預設樂器：{playlist.DefaultInstrument}");

    public string PlaylistName
    {
        get => playlistName;
        set { if (Set(ref playlistName, value)) Raise(nameof(CanRename)); }
    }

    public bool CanRename => CanEdit && HasPlaylist && playlistName.Trim().Length is >= 1 and <= 200 && playlistName != syncedName;

    public string Notes
    {
        get => notes;
        set { if (Set(ref notes, value)) Raise(nameof(CanSaveNotes)); }
    }

    public bool CanSaveNotes => CanEdit && HasPlaylist && notes != syncedNotes;

    public IReadOnlyList<ChoiceRow> DefaultInstrumentChoices =>
        [new(string.Empty, Pick("未指定", "未指定"), view.Selected?.DefaultInstrumentId is null),
         .. view.Instruments.Select(item => new ChoiceRow(item.Id, item.Name, view.Selected?.DefaultInstrumentId == item.Id))];

    /// <summary>层里选择清单的标签：全部清单（清单栏同一次序，所在文件夹写在名称前），选中的就是页面清单栏选中的清单。</summary>
    public IReadOnlyList<ChoiceRow> PlaylistChoices =>
        [.. view.Playlists.Select(item => new ChoiceRow(item.Id, item.FolderPath.Length == 0 ? item.Name : item.FolderPath + " / " + item.Name, item.Id == view.Selected?.Id))];

    public bool HasPlaylists => view.Playlists.Count > 0;

    public IReadOnlyList<ChoiceRow> MoveChoices =>
        [new(string.Empty, Pick("根目录", "根目錄"), view.Selected?.FolderId is null),
         .. view.Folders.Select(folder => new ChoiceRow(folder.Id, folder.Name, view.Selected?.FolderId == folder.Id, folder.Depth + 1))];

    public IReadOnlyList<PlaylistItemRow> Items => view.Selected?.Items ?? [];
    public bool NoItems => HasPlaylist && Items.Count == 0;
    public string DeletePlaylistLabel => confirmDeletePlaylist ? Pick("确认删除", "確認刪除") : Pick("删除清单", "刪除清單");
    public IReadOnlyList<InstrumentChoice> InstrumentChoices => view.Instruments;
    /// <summary>没有乐器选项时的原因（背包没有同步等）；有选项时为空。</summary>
    public string? InstrumentsNote => view.Instruments.Count == 0 ? view.InstrumentsNote : null;

    /// <summary>加入清单菜单的选项：全部清单（清单栏同一次序）。</summary>
    public IReadOnlyList<PlaylistDetail> Playlists => view.Playlists;

    // ---- 编辑 ----

    public Task AddScoreAsync(string playlistId, string scoreId) =>
        RunAsync(new(MusicListEditKind.AddItem, PlaylistId: playlistId, ScoreId: scoreId));

    public Task SetItemInstrumentAsync(string itemId, string? instrumentId) =>
        RunAsync(new(MusicListEditKind.SetItemInstrument, PlaylistId: Playlist?.Id, ItemId: itemId, Text: instrumentId ?? string.Empty));

    private Task CreatePlaylistAsync() => !CanCreate ? Task.CompletedTask
        : RunAsync(new(MusicListEditKind.CreatePlaylist, FolderId: folderId.Length == 0 ? null : folderId, Text: newName), () => NewName = string.Empty);

    private Task CreateFolderAsync() => !CanCreate ? Task.CompletedTask
        : RunAsync(new(MusicListEditKind.CreateFolder, FolderId: folderId.Length == 0 ? null : folderId, Text: newName), () => NewName = string.Empty);

    private Task RunPlaylistAsync(MusicListEditKind kind, string text) =>
        view.Selected is { } playlist ? RunAsync(new(kind, PlaylistId: playlist.Id, Text: text)) : Task.CompletedTask;

    private Task MoveItemAsync(string? itemId, int direction) =>
        view.Selected is { } playlist && itemId is not null ? RunAsync(new(MusicListEditKind.MoveItem, PlaylistId: playlist.Id, ItemId: itemId, Direction: direction)) : Task.CompletedTask;

    // 删除要点两次：第一次只把按钮变成“确认删除”，切换清单后恢复。
    private async Task DeletePlaylistAsync()
    {
        if (view.Selected is not { } playlist || !CanEdit) return;
        if (!confirmDeletePlaylist) { confirmDeletePlaylist = true; Raise(nameof(DeletePlaylistLabel)); return; }
        confirmDeletePlaylist = false;
        await RunAsync(new(MusicListEditKind.DeletePlaylist, PlaylistId: playlist.Id));
        Raise(nameof(DeletePlaylistLabel));
    }

    private async Task DeleteFolderAsync()
    {
        if (!CanDeleteFolder) return;
        if (!confirmDeleteFolder) { confirmDeleteFolder = true; Raise(nameof(DeleteFolderLabel)); return; }
        confirmDeleteFolder = false;
        var target = folderId;
        await RunAsync(new(MusicListEditKind.DeleteFolder, FolderId: target), () => FolderId = string.Empty);
        Raise(nameof(DeleteFolderLabel));
    }

    private async Task RunAsync(MusicListEdit edit, Action? succeeded = null)
    {
        if (!CanEdit) return;
        Busy = true;
        try
        {
            var reply = await backend.EditMusicLibraryAsync(edit, CancellationToken.None);
            if (reply.Ok) { SetMessage(reply.Message, false); succeeded?.Invoke(); }
            else SetMessage(Failure(reply), true);
        }
        finally { Busy = false; }
    }

    private static string Failure(BackendReply reply) => reply.Code == "revision.stale"
        ? Pick("曲库已被其他前端修改，已重新读取，请再试一次", "曲庫已被其他前端修改，已重新讀取，請再試一次")
        : reply.Message.Length > 0 ? reply.Message : Pick("后端没有提供清单编辑", "後端沒有提供清單編輯");

    private void SetMessage(string text, bool error)
    {
        message = text; messageIsError = error;
        Raise(nameof(Message)); Raise(nameof(HasMessage)); Raise(nameof(MessageIsError));
    }
}
