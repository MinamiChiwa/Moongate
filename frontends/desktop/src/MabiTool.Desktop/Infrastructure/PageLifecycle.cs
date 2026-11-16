namespace MabiTool.Desktop.Infrastructure;

/// <summary>
/// 可保留的页面模型：外壳切走页面时调用 <see cref="Deactivate"/>（停止接收后端通知与页面自己的计时器），回到页面时调用
/// <see cref="Activate"/>（重新订阅并读取一次内存中的最近结果）。<see cref="Cacheable"/> 为假的页面在切走时释放，
/// 回来时页面外框先显示骨架、再重新建立页面。
/// </summary>
public interface IPageLifecycle
{
    bool Cacheable { get; }
    void Activate();
    void Deactivate();

    /// <summary>
    /// 回到页面后，页面自己发起的读取（只在页面显示时进行的轮询）尚未返回：页面外框在右上角显示“更新中”，读取返回后页面把它置回假。
    /// 实现 <see cref="System.ComponentModel.INotifyPropertyChanged"/> 的模型以属性名 <c>PendingRefresh</c> 通知变化。
    /// 数据完全来自后端适配层常驻刷新的页面不需要它，回来时读到的就是最近结果。
    /// </summary>
    bool PendingRefresh => false;
}
