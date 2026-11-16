using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record DesktopRgbFrame(PhysicalWindowRect Bounds, byte[] Rgb);
public interface IDesktopCaptureSource
{
    ValueTask<DesktopRgbFrame> CaptureAsync(int maximumPixels, CancellationToken cancellationToken);
}
