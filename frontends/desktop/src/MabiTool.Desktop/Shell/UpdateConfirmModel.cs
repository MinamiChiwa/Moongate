using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 设置页点“下载更新”后的确认窗口（窗口内弹出层）：显示新版本、本次下载方式与大小，以及该版本的更新说明——
/// 只显示当前界面语言（简体或繁体）的那一份，不混排；确认后才发出下载请求，取消、点外面或按 Esc 都不下载。
/// 更新说明由后端读取（公开仓库发行标签下的文件，读不到时取 Release 正文里对应语言的段），前端不直接联网；
/// 读不到时窗口写明“未能读取更新说明”，仍可下载。没有可下载的更新（没有新版本、正在下载、已下载等）时不弹出。
/// </summary>
public sealed class UpdateConfirmModel : ObservableObject, IDisposable
{
    private readonly IDesktopBackend backend;
    private CancellationTokenSource? loading;
    private bool isOpen;
    private bool notesDone;
    private UpdateOfferView? offer;
    private UpdateNotesView? loaded;
    private IReadOnlyList<MarkdownBlock> notes = [];

    public UpdateConfirmModel(IDesktopBackend backend)
    {
        this.backend = backend;
        ConfirmCommand = new RelayCommand(_ => Confirm());
        CancelCommand = new RelayCommand(_ => Close());
        OpenLinkCommand = new RelayCommand(parameter => OpenLink(parameter as string));
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenLinkCommand { get; }

    /// <summary>用系统默认浏览器打开链接；测试替换为记录。</summary>
    public Action<string> OpenUrl { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    public string VersionLine => offer is null ? string.Empty : UiText.Pick($"新版本 {offer.Release}", $"新版本 {offer.Release}");

    /// <summary>本次下载方式与大小；后端还没比对完本机文件时只写比对时机。</summary>
    public string MethodLine => offer switch
    {
        { DownloadKind: "incremental", DownloadBytes: { } bytes } =>
            UiText.Pick($"下载方式：增量更新，{Megabytes(bytes)}", $"下載方式：增量更新，{Megabytes(bytes)}"),
        { DownloadKind: "full", DownloadBytes: { } bytes } =>
            UiText.Pick($"下载方式：完整下载，{Megabytes(bytes)}", $"下載方式：完整下載，{Megabytes(bytes)}"),
        _ => UiText.Pick("下载方式：开始后按本机文件比对", "下載方式：開始後按本機檔案比對")
    };

    /// <summary>当前界面语言的更新说明已排好版；读取中或读不到时为空。</summary>
    public IReadOnlyList<MarkdownBlock> Notes => notes;
    public bool HasNotes => notes.Count > 0;
    public bool HasNotesStatus => notes.Count == 0;

    /// <summary>更新说明读取中或读不到时的说明文字。</summary>
    public string NotesStatus => notes.Count > 0 ? string.Empty : notesDone
        ? UiText.Pick("未能读取更新说明，仍可下载", "未能讀取更新說明，仍可下載")
        : UiText.Pick("正在读取更新说明…", "正在讀取更新說明…");

    /// <summary>有可下载的更新时弹出；其他状态不弹出，已经弹出时不重复。</summary>
    public void Open()
    {
        if (isOpen) return;
        var settings = backend.GetSettings();
        if (!settings.CanDownloadUpdate || settings.Offer is not { } current) return;
        UiText.LanguageChanged += OnLanguageChanged;
        Show(current);
    }

    public void Close()
    {
        if (!isOpen) return;
        UiText.LanguageChanged -= OnLanguageChanged;
        Cancel();
        loaded = null;
        notes = [];
        IsOpen = false;
        RaiseNotes();
    }

    /// <summary>
    /// 随后端状态：不能再下载（已开始下载、更新被取消等）时关闭；出现不同版本的更新时换成新版本并重新读取更新说明，
    /// 需要重新确认；同一版本的下载方式或大小算出后刷新这一行。
    /// </summary>
    public void OnBackendChanged()
    {
        if (!isOpen) return;
        var settings = backend.GetSettings();
        if (!settings.CanDownloadUpdate || settings.Offer is not { } current) { Close(); return; }
        if (current.Release != offer?.Release) { Show(current); return; }
        if (current == offer) return;
        offer = current;
        Raise(nameof(MethodLine));
    }

    public void Dispose()
    {
        Close();
    }

    private void Show(UpdateOfferView next)
    {
        Cancel();
        offer = next;
        loaded = null;
        notes = [];
        notesDone = false;
        IsOpen = true;
        Raise(nameof(VersionLine));
        Raise(nameof(MethodLine));
        RaiseNotes();
        var source = loading = new CancellationTokenSource();
        _ = LoadAsync(source.Token);
    }

    private void Cancel()
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = null;
    }

    private async Task LoadAsync(CancellationToken token)
    {
        UpdateNotesView? result = null;
        try { result = await backend.LoadUpdateNotesAsync(token); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException) { }
        if (token.IsCancellationRequested) return;
        if (Dispatcher.UIThread.CheckAccess()) Apply(result, token);
        else Dispatcher.UIThread.Post(() => Apply(result, token));
    }

    private void Apply(UpdateNotesView? result, CancellationToken token)
    {
        if (token.IsCancellationRequested || !isOpen) return;
        loaded = result;
        notesDone = true;
        SelectLanguage();
    }

    // 只取当前界面语言的那一份；这一语言没有读到时写明读不到，不拿另一种语言顶替。
    private void SelectLanguage()
    {
        var wanted = UiText.Language == UiLanguage.TraditionalChinese ? "zh-Hant" : "zh-Hans";
        var text = loaded?.Texts.FirstOrDefault(item => item.Language == wanted)?.Markdown;
        notes = MarkdownDocument.Parse(text);
        RaiseNotes();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Raise(nameof(VersionLine));
        Raise(nameof(MethodLine));
        if (notesDone) SelectLanguage();
        else RaiseNotes();
    }

    private void Confirm()
    {
        var settings = backend.GetSettings();
        if (!settings.CanDownloadUpdate || settings.Offer is not { } current) { Close(); return; }
        // 确认前出现了更新的版本：窗口换成新版本，由用户对新版本重新确认。
        if (offer is not null && current.Release != offer.Release) { Show(current); return; }
        Close();
        _ = backend.SendIntentAsync("update.download", null, CancellationToken.None);
    }

    private void OpenLink(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { OpenUrl(uri.AbsoluteUri); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
    }

    private void RaiseNotes()
    {
        Raise(nameof(Notes));
        Raise(nameof(HasNotes));
        Raise(nameof(HasNotesStatus));
        Raise(nameof(NotesStatus));
    }

    private static string Megabytes(ulong bytes) => (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}
