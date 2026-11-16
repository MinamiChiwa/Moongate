using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

/// <summary>由受信桌面宿主注册；窗口对象及 UI 调度保持在具体适配器内。</summary>
public interface IDesktopSurface
{
    DesktopSurfaceCapabilities Capabilities { get; }
    ValueTask<DesktopSurfaceState> ReadAsync(CancellationToken cancellationToken);
    ValueTask<DesktopSurfaceState> ApplyAsync(DesktopSurfaceChange change, CancellationToken cancellationToken);
}

public sealed class DesktopSurfaceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

