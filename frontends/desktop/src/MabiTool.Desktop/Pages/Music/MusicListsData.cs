namespace MabiTool.Desktop.Pages.Music;

/// <summary>清单管理层读到的文件夹；<see cref="Path"/> 是从根到该文件夹的“甲 / 乙”路径，<see cref="Depth"/> 用于缩进。</summary>
public sealed record FolderRow(string Id, string Name, string Path, int Depth);

/// <summary>清单条目：<see cref="Title"/> 取自乐谱表，找不到乐谱（来源不在当前显示范围）时为条目指向的乐谱 ID。</summary>
public sealed record PlaylistItemRow(string Id, string ScoreId, string Title, string? InstrumentId, string Instrument, bool Missing);

/// <summary>一个清单的完整内容；<see cref="FolderPath"/> 为空表示在根目录。<see cref="Instrument"/> 是默认乐器的名称（未指定时为“未指定”）。</summary>
public sealed record PlaylistDetail(string Id, string Name, string Notes, string? FolderId, string FolderPath, string? DefaultInstrumentId,
    string DefaultInstrument, IReadOnlyList<PlaylistItemRow> Items);

public sealed record InstrumentChoice(string Id, string Name);

/// <summary>
/// 乐谱库页的清单与文件夹：全部文件夹层级里的清单（条目逐页读完）、所选清单，以及编辑入口是否可用。
/// <see cref="EditBlocked"/> 非空时编辑停用，其内容是写给用户的原因。
/// </summary>
/// <see cref="Instruments"/> 是当前角色背包里的乐器（游戏原文名）；为空时 <see cref="InstrumentsNote"/> 写明原因。
public sealed record MusicListsView(IReadOnlyList<FolderRow> Folders, IReadOnlyList<PlaylistDetail> Playlists, PlaylistDetail? Selected,
    IReadOnlyList<InstrumentChoice> Instruments, string? EditBlocked, string? InstrumentsNote = null)
{
    public static MusicListsView Empty { get; } = new([], [], null, [], null);
    public bool CanEdit => EditBlocked is null;
}

public enum MusicListEditKind
{
    CreatePlaylist, RenamePlaylist, SetNotes, SetDefaultInstrument, MovePlaylist, DeletePlaylist,
    AddItem, RemoveItem, MoveItem, SetItemInstrument,
    CreateFolder, RenameFolder, DeleteFolder
}

/// <summary>
/// 一次清单编辑。<see cref="PlaylistId"/>/<see cref="FolderId"/>/<see cref="ItemId"/>/<see cref="ScoreId"/> 是被编辑对象；
/// <see cref="Text"/> 是名称、备注或乐器 ID（空表示沿用默认）；<see cref="FolderId"/> 在新建与移动时是目标文件夹（空为根），
/// 在重命名与删除文件夹时是被编辑的文件夹；<see cref="Direction"/> 为 -1 上移、1 下移。
/// </summary>
public sealed record MusicListEdit(MusicListEditKind Kind, string? PlaylistId = null, string? FolderId = null, string? ItemId = null,
    string? ScoreId = null, string? Text = null, int Direction = 0);

/// <summary>导入用的一个源文件；名称是 MobiFolio 数据文件名。</summary>
public sealed record MusicImportFile(string Name, byte[] Content);

/// <summary>导入预览：<see cref="Issues"/> 是前几条问题的说明，<see cref="IssueCount"/> 是问题总数。</summary>
public sealed record MusicImportPreviewView(string Id, int Scores, int Folders, int Playlists, int PlaylistItems, int Artists, int History,
    int Durations, int Unresolved, IReadOnlyList<string> Issues, int IssueCount);

public sealed record MusicImportPreviewReply(bool Ok, string Message, MusicImportPreviewView? Preview);

/// <summary>导入提交结果：成功时 <see cref="Message"/> 写明导入的数量。</summary>
public sealed record MusicImportCommitReply(bool Ok, string Message);
