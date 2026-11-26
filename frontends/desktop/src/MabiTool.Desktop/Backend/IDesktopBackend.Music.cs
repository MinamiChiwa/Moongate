using MabiTool.Desktop.Pages.Music;

namespace MabiTool.Desktop.Backend;

// 乐谱库的清单编辑与乐谱导入：结构化意图，结果原样交回页面，是否接受由后端判定。
// 成员都带默认实现，只提供部分接口的后端（测试替身）不必逐个实现。
public partial interface IDesktopBackend
{
    /// <summary>
    /// 清单与文件夹的一次编辑（新建、重命名、备注、默认乐器、移动、删除、加入乐谱、移除、排序、逐条乐器）。
    /// 后端适配层带当前曲库修订（If-Match）与新的幂等键发送一次；修订已变时回执的 <see cref="BackendReply.Code"/> 为 <c>revision.stale</c>。
    /// </summary>
    Task<BackendReply> EditMusicLibraryAsync(MusicListEdit edit, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>
    /// 用 MobiFolio 数据文件生成导入预览（不写入曲库）；<paramref name="region"/> 为 <c>KR</c> 或 <c>TW</c>。
    /// 桌面界面不提供导入入口；适配层保留这一对接口，对接后端已有的导入能力，集成用例用它准备导入来源。
    /// </summary>
    Task<MusicImportPreviewReply> PreviewMusicImportAsync(IReadOnlyList<MusicImportFile> files, string region, CancellationToken cancellationToken) =>
        Task.FromResult(new MusicImportPreviewReply(false, string.Empty, null));

    /// <summary>提交一份导入预览：后端以异步操作执行，这里等到结束；曲库修订在预览后变化时回执为 <c>revision.stale</c>。</summary>
    Task<MusicImportCommitReply> CommitMusicImportAsync(string previewId, CancellationToken cancellationToken) =>
        Task.FromResult(new MusicImportCommitReply(false, string.Empty));
}
