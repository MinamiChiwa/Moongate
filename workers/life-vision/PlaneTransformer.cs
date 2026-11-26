// 移植自 MabiAgent 82bc0ef 的 dotnet/MabiAgent.Windows/TemplateMatcher.cs 中的 Transform（MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：原实现调用 OpenCvSharp；这里用托管代码复现模板匹配用到的四种灰度变换（light_text、light_text + 相对对比度、red_glyph、grayscale），
// 每一步输出 8 位灰度并按字节取整，与 OpenCV 的整数/定点实现保持同一量化：
// - RGB 转灰度：(9798R + 19235G + 3735B + 16384) >> 15，与 OpenCV 4.13 的 RGB2GRAY 逐字节一致（对 200 万随机像素零差异）；
// - 二值化：OpenCV Threshold Binary 为严格大于阈值才置 255；
// - 5×5 高斯模糊（sigma 1.2，边界 reflect101）：核按 1/256 定点量化为 (22, 62, 88, 62, 22)，
//   水平通道保留 16 位中间值，垂直通道累加后一次四舍五入到 8 位，与 OpenCV 8 位输入的定点高斯路径相同；
// - 15×15 矩形核开运算（TopHat 的基础）：边界外的像素不参与比较，与 OpenCV 形态学的默认边界等价；
// - MinMax 归一化：按 float 的比例与偏移计算并四舍五入（偶数优先）。
// 与 OpenCvSharp 4.13 对照：8 张模板的搜索区（随机图像与两张夹具整帧）与模板自身，四种变换的输出逐字节一致。

using System.Numerics;

namespace MabiTool.LifeVision.Worker;

/// <summary>
/// 把 RGB 图像的一块矩形变换为模板匹配用的 8 位灰度平面。持有固定大小的工作缓冲区，同一实例不可并发使用；
/// 输出平面由调用方提供，缓冲区大小上限在构造时按最大搜索区确定。
/// </summary>
internal sealed class PlaneTransformer
{
    // sigma 1.2 的 5 点高斯核归一化后约为 .0856 .2426 .3434 .2426 .0856，按 1/256 定点量化为 22 62 88 62 22，总和恰为 256。
    private const int Kernel0 = 22, Kernel1 = 62, Kernel2 = 88;
    private const int MorphologyRadius = 7;
    private const int MinimumContrast = 16;

    private readonly byte[] planeA, planeB, planeC, planeD, rowPad;
    private readonly ushort[] blurRows;

    /// <param name="maximumPixels">单次变换的最大像素数（最大搜索区与模板中较大者）。</param>
    /// <param name="maximumWidth">单次变换的最大宽度。</param>
    /// <param name="maximumContrastPixels">需要相对对比度（形态学）的最大像素数，0 表示不需要。</param>
    public PlaneTransformer(int maximumPixels, int maximumWidth, int maximumContrastPixels)
    {
        planeA = new byte[maximumPixels];
        planeB = new byte[maximumPixels];
        planeC = new byte[maximumContrastPixels];
        planeD = new byte[maximumContrastPixels];
        blurRows = new ushort[maximumPixels];
        rowPad = new byte[maximumWidth + 2 * MorphologyRadius + 32];
    }

    /// <summary>
    /// 变换 <paramref name="box"/> 内的像素并写入 <paramref name="output"/>（宽×高字节）。
    /// 返回 false 表示结果必为全零平面（没有任何置位像素），调用方可以直接判定标准差不足而跳过后续计算。
    /// </summary>
    public bool Transform(RgbImage image, PixelBox box, TemplateSpec spec, Span<byte> output)
    {
        var width = box.Width;
        var height = box.Height;
        var count = width * height;
        output = output[..count];
        switch (spec.Mode)
        {
            case TransformMode.Grayscale:
                Grayscale(image, box, output);
                return true;
            case TransformMode.RedGlyph:
                if (BinarizeRed(image, box, planeA.AsSpan(0, count)) == 0) { output.Clear(); return false; }
                Blur(planeA.AsSpan(0, count), output, width, height);
                return true;
            case TransformMode.LightText when !spec.RelativeContrast:
                if (BinarizeLight(image, box, spec.LightThreshold, planeA.AsSpan(0, count)) == 0) { output.Clear(); return false; }
                Blur(planeA.AsSpan(0, count), output, width, height);
                return true;
            case TransformMode.LightText:
                return RelativeContrast(image, box, output);
            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }
    }

    private static void Grayscale(RgbImage image, PixelBox box, Span<byte> output)
    {
        var pixels = image.Pixels;
        for (var row = 0; row < box.Height; row++)
        {
            var offset = ((box.Top + row) * image.Width + box.Left) * 3;
            var target = output.Slice(row * box.Width, box.Width);
            for (var x = 0; x < target.Length; x++, offset += 3)
                target[x] = (byte)((pixels[offset] * 9798 + pixels[offset + 1] * 19235 + pixels[offset + 2] * 3735 + 16384) >> 15);
        }
    }

    private static int BinarizeLight(RgbImage image, PixelBox box, int threshold, Span<byte> output)
    {
        var pixels = image.Pixels;
        var set = 0;
        for (var row = 0; row < box.Height; row++)
        {
            var offset = ((box.Top + row) * image.Width + box.Left) * 3;
            var target = output.Slice(row * box.Width, box.Width);
            for (var x = 0; x < target.Length; x++, offset += 3)
            {
                var minimum = Math.Min(pixels[offset], Math.Min(pixels[offset + 1], pixels[offset + 2]));
                // OpenCV Threshold Binary：严格大于阈值才置位。
                var on = minimum > threshold;
                target[x] = on ? (byte)255 : (byte)0;
                if (on) set++;
            }
        }
        return set;
    }

    private static int BinarizeRed(RgbImage image, PixelBox box, Span<byte> output)
    {
        var pixels = image.Pixels;
        var set = 0;
        for (var row = 0; row < box.Height; row++)
        {
            var offset = ((box.Top + row) * image.Width + box.Left) * 3;
            var target = output.Slice(row * box.Width, box.Width);
            for (var x = 0; x < target.Length; x++, offset += 3)
            {
                int r = pixels[offset], g = pixels[offset + 1], b = pixels[offset + 2];
                var on = r > 130 && r - g > 50 && r - b > 40;
                target[x] = on ? (byte)255 : (byte)0;
                if (on) set++;
            }
        }
        return set;
    }

    private bool RelativeContrast(RgbImage image, PixelBox box, Span<byte> output)
    {
        var width = box.Width;
        var height = box.Height;
        var count = width * height;
        var minimum = planeA.AsSpan(0, count);
        var pixels = image.Pixels;
        for (var row = 0; row < height; row++)
        {
            var offset = ((box.Top + row) * image.Width + box.Left) * 3;
            var target = minimum.Slice(row * width, width);
            for (var x = 0; x < target.Length; x++, offset += 3)
                target[x] = Math.Min(pixels[offset], Math.Min(pixels[offset + 1], pixels[offset + 2]));
        }
        // TopHat = 原图 − 开运算（先腐蚀后膨胀）：只保留比 15×15 邻域整体更亮的细节。
        var eroded = planeC.AsSpan(0, count);
        var opened = planeD.AsSpan(0, count);
        var scratch = planeB.AsSpan(0, count);
        Extremum(minimum, eroded, scratch, width, height, isMin: true);
        Extremum(eroded, opened, scratch, width, height, isMin: false);
        var topHat = planeB.AsSpan(0, count);
        byte lowest = 255, highest = 0;
        for (var i = 0; i < count; i++)
        {
            var value = (byte)(minimum[i] - opened[i]);
            topHat[i] = value;
            if (value < lowest) lowest = value;
            if (value > highest) highest = value;
        }
        // 对比度太弱的区域整体置零，避免把噪声拉伸到 0–255。
        if (highest < MinimumContrast || highest == lowest) { output.Clear(); return false; }
        var scale = 255f / (highest - lowest);
        var shift = -lowest * scale;
        for (var i = 0; i < count; i++)
            topHat[i] = (byte)Math.Clamp((int)MathF.Round(topHat[i] * scale + shift, MidpointRounding.ToEven), 0, 255);
        Blur(topHat, output, width, height);
        return true;
    }

    /// <summary>15×15 矩形核的可分离腐蚀（<paramref name="isMin"/>）或膨胀；<paramref name="scratch"/> 保存水平通道结果。</summary>
    private void Extremum(ReadOnlySpan<byte> source, Span<byte> destination, Span<byte> scratch, int width, int height, bool isMin)
    {
        var neutral = isMin ? (byte)255 : (byte)0;
        var diameter = 2 * MorphologyRadius + 1;
        var pad = rowPad.AsSpan(0, width + 2 * MorphologyRadius);
        for (var y = 0; y < height; y++)
        {
            pad[..MorphologyRadius].Fill(neutral);
            source.Slice(y * width, width).CopyTo(pad[MorphologyRadius..]);
            pad[(MorphologyRadius + width)..].Fill(neutral);
            var row = scratch.Slice(y * width, width);
            pad[..width].CopyTo(row);
            for (var k = 1; k < diameter; k++) Fold(row, pad.Slice(k, width), isMin);
        }
        for (var y = 0; y < height; y++)
        {
            var from = Math.Max(0, y - MorphologyRadius);
            var to = Math.Min(height - 1, y + MorphologyRadius);
            var row = destination.Slice(y * width, width);
            scratch.Slice(from * width, width).CopyTo(row);
            for (var yy = from + 1; yy <= to; yy++) Fold(row, scratch.Slice(yy * width, width), isMin);
        }
    }

    private static void Fold(Span<byte> target, ReadOnlySpan<byte> other, bool isMin)
    {
        var i = 0;
        var step = Vector<byte>.Count;
        for (; i <= target.Length - step; i += step)
        {
            var a = new Vector<byte>(target[i..]);
            var b = new Vector<byte>(other[i..]);
            (isMin ? Vector.Min(a, b) : Vector.Max(a, b)).CopyTo(target[i..]);
        }
        for (; i < target.Length; i++) target[i] = isMin ? Math.Min(target[i], other[i]) : Math.Max(target[i], other[i]);
    }

    /// <summary>5×5 高斯模糊（reflect101 边界）：水平通道保留 16 位中间值，垂直通道累加后一次四舍五入。</summary>
    private void Blur(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height)
    {
        var pad = rowPad.AsSpan(0, width + 4);
        var rows = blurRows.AsSpan(0, width * height);
        for (var y = 0; y < height; y++)
        {
            var row = source.Slice(y * width, width);
            pad[0] = row[2];
            pad[1] = row[1];
            row.CopyTo(pad[2..]);
            pad[width + 2] = row[width - 2];
            pad[width + 3] = row[width - 3];
            var target = rows.Slice(y * width, width);
            for (var x = 0; x < target.Length; x++)
                target[x] = (ushort)(Kernel0 * (pad[x] + pad[x + 4]) + Kernel1 * (pad[x + 1] + pad[x + 3]) + Kernel2 * pad[x + 2]);
        }
        for (var y = 0; y < height; y++)
        {
            var above2 = rows.Slice(Reflect(y - 2, height) * width, width);
            var above1 = rows.Slice(Reflect(y - 1, height) * width, width);
            var center = rows.Slice(y * width, width);
            var below1 = rows.Slice(Reflect(y + 1, height) * width, width);
            var below2 = rows.Slice(Reflect(y + 2, height) * width, width);
            var target = destination.Slice(y * width, width);
            for (var x = 0; x < target.Length; x++)
            {
                var sum = Kernel0 * (above2[x] + below2[x]) + Kernel1 * (above1[x] + below1[x]) + Kernel2 * center[x];
                target[x] = (byte)((sum + 32768) >> 16);
            }
        }
    }

    private static int Reflect(int index, int length) => index < 0 ? -index : index >= length ? 2 * length - 2 - index : index;
}
