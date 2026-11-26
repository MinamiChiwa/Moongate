using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record DesktopRgbFrame(PhysicalWindowRect Bounds, byte[] Rgb);
public interface IDesktopCaptureSource
{
    /// <summary>
    /// 按物理像素冻结 <paramref name="region"/>（为空时整个虚拟桌面），返回的边界等于所截区域。
    /// 区域没有完整落在当前虚拟桌面内时抛出 <see cref="ArgumentOutOfRangeException"/>；
    /// 像素超过 <paramref name="maximumPixels"/> 时抛出 <see cref="ArgumentException"/>；
    /// 设备不可用或截屏前后显示布局变化时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    ValueTask<DesktopRgbFrame> CaptureAsync(PhysicalWindowRect? region, int maximumPixels, CancellationToken cancellationToken);
}
