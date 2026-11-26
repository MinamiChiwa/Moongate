// 移植自 MabiAgent 82bc0ef 的 dotnet/MabiAgent.Windows/ScreenTypes.cs（MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：Moongate 只识别 1280×960 客户区，不再依赖 OpenCvSharp，也不需要按 DPI 缩放的布局类型；
// 像素缓冲由调用方持有并复用（RgbImage 不再复制、不再计算整图 SHA-256），PixelBox 只保留识别用到的成员。

using MabiTool.Contracts;

namespace MabiTool.LifeVision.Worker;

/// <summary>客户区内的整数矩形，左闭右开，坐标原点在客户区左上角。</summary>
internal readonly record struct PixelBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public VisionPoint Center => new((Left + Right) / 2, (Top + Bottom) / 2);
    public bool IsInside(int width, int height) => Left >= 0 && Top >= 0 && Right <= width && Bottom <= height && Width > 0 && Height > 0;
}

/// <summary>
/// RGB24 图像（行主序，每像素 R、G、B 三字节）。像素数组由创建者持有：截图源在每帧覆盖同一个数组以限制内存，
/// 因此识别只能在一次 <c>CaptureAsync</c> 返回到下一次调用之间读取它。
/// </summary>
public sealed class RgbImage
{
    public const int MaximumPixels = 16_777_216;

    public RgbImage(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels || pixels.LongLength != (long)width * height * 3)
            throw new ArgumentException("需要尺寸与像素数组一致的 RGB24 图像");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}
