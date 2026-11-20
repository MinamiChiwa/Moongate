using System.Windows.Input;
using MabiTool.Desktop.Backend;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>标签与数值的一行，窄宽度下上下排。</summary>
public sealed record KeyValue(string Label, string Value);

/// <summary>
/// 页面模型的共同部分：数据从后端读取，后端状态变化时重读；后端适配层对未变的状态返回同一个对象，此时不通知界面。
/// 按钮通过 <see cref="IntentCommand"/> 把意图名称和参数交给后端，页面不做业务判定。
/// 意图参数写成「名称」或「名称:参数」。
/// </summary>
public abstract class BackendPageModel<T> : ObservableObject, IDisposable, IPageLifecycle
{
    private readonly Func<T> read;
    private bool active = true;

    protected BackendPageModel(IDesktopBackend backend, Func<IDesktopBackend, T> read)
    {
        Backend = backend;
        this.read = () => read(backend);
        Data = this.read();
        IntentCommand = new RelayCommand(parameter =>
        {
            if (parameter is not string text || text.Length == 0) return;
            var split = text.IndexOf(':');
            Send(split < 0 ? text : text[..split], split < 0 ? null : text[(split + 1)..]);
        });
        backend.Changed += OnBackendChanged;
        ReportShown();
    }

    /// <summary>页面在显示期间向后端报告的可见处（见 <see cref="IDesktopBackend.SetSurfaceVisible"/>）；为空时不报告。</summary>
    protected virtual string? Surface => null;

    // 页面开始显示：向后端报告可见处。Surface 由派生类给出常量，构造期间即可取用。
    private void ReportShown()
    {
        if (Surface is { } surface && active && !reported) { reported = true; Backend.SetSurfaceVisible(surface, true); }
    }

    private bool reported;

    private void ReportHidden()
    {
        if (Surface is { } surface && reported) { reported = false; Backend.SetSurfaceVisible(surface, false); }
    }

    protected IDesktopBackend Backend { get; }

    public T Data { get; private set; }

    public ICommand IntentCommand { get; }

    /// <summary>切走后是否保留页面；没有自有计时器或独占资源的页面可以保留。</summary>
    protected virtual bool Cacheable => false;

    bool IPageLifecycle.Cacheable => Cacheable;

    /// <summary>页面被切走：停止接收后端通知，保留界面与最后一次数据。</summary>
    public void Deactivate()
    {
        if (!active) return;
        active = false;
        Backend.Changed -= OnBackendChanged;
        ReportHidden();
    }

    /// <summary>回到页面：重新订阅后端通知并读取一次最近结果（内存中的状态，不等待后端调用）。</summary>
    public void Activate()
    {
        if (active) return;
        active = true;
        Backend.Changed += OnBackendChanged;
        ReportShown();
        Reload();
    }

    public virtual void Dispose()
    {
        active = false;
        Backend.Changed -= OnBackendChanged;
        ReportHidden();
    }

    protected void Send(string intent, string? argument) =>
        _ = Backend.SendIntentAsync(intent, argument, CancellationToken.None);

    /// <summary>数据重读之后调用；派生类在这里重算只属于界面的筛选结果。</summary>
    protected virtual void OnDataChanged() { }

    /// <summary>数据对象没变时，派生类显示的其他后端状态（例如活动胶囊）是否变了；变了照常重读并通知。</summary>
    protected virtual bool OtherStateChanged() => false;

    // 后端每次变化只通知一次，但大多与本页无关：数据是同一个对象且其他状态没变时，不重算、不通知。
    private void OnBackendChanged(object? sender, EventArgs e)
    {
        var next = read();
        if (ReferenceEquals(next, Data) && !OtherStateChanged()) return;
        Data = next;
        OnDataChanged();
        Raise(string.Empty);
    }

    /// <summary>重读后端数据并通知界面；也用于界面语言切换后重取由后端状态拼出的文字。</summary>
    protected void Reload()
    {
        Data = read();
        OnDataChanged();
        Raise(string.Empty);
    }
}
