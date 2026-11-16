using System.Windows.Input;
using MabiTool.Desktop.Backend;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>标签与数值的一行，窄宽度下上下排。</summary>
public sealed record KeyValue(string Label, string Value);

/// <summary>
/// 页面模型的共同部分：数据从后端读取，后端状态变化时整份重读；
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
    }

    /// <summary>回到页面：重新订阅后端通知并读取一次最近结果（内存中的状态，不等待后端调用）。</summary>
    public void Activate()
    {
        if (active) return;
        active = true;
        Backend.Changed += OnBackendChanged;
        Reload();
    }

    public virtual void Dispose()
    {
        active = false;
        Backend.Changed -= OnBackendChanged;
    }

    protected void Send(string intent, string? argument) =>
        _ = Backend.SendIntentAsync(intent, argument, CancellationToken.None);

    /// <summary>数据重读之后调用；派生类在这里重算只属于界面的筛选结果。</summary>
    protected virtual void OnDataChanged() { }

    private void OnBackendChanged(object? sender, EventArgs e) => Reload();

    /// <summary>重读后端数据并通知界面；也用于界面语言切换后重取由后端状态拼出的文字。</summary>
    protected void Reload()
    {
        Data = read();
        OnDataChanged();
        Raise(string.Empty);
    }
}
