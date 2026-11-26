// 移植自 MabiAgent 82bc0ef 的 dotnet/MabiAgent.Windows/TemplateMatcher.cs 的 MatchCore（MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：原实现调用 OpenCvSharp 的 MatchTemplate（TM_CCOEFF_NORMED）并缓存结果；Moongate 不引入原生依赖，
// 这里只保留钓鱼用到的模式（light_text、light_text + 相对对比度、red_glyph、grayscale），用托管代码逐位置计算同一个归一化相关系数，
// 并用积分图与行块提前终止加速（见 Search）。
//
// 与 OpenCV 实现的差异：
// 1. 候选剪枝：峰值处窗口均值与模板均值之比须在 [0.65, 1.5]（OpenCV 在取峰值之后才检验）。这里先按该比值剪枝候选位置，
//    再从候选里取峰值，所以“全图相关最高处亮度不符、另有亮度合格的位置同样超过阈值”时，这里命中而 OpenCV 返回空；
//    次峰唯一性同样只在候选位置内比较。
// 2. 提前终止：逐行块累计点积时，用 Cauchy–Schwarz 上界判断该位置不可能达到阈值就停止；只丢弃低于阈值的位置，
//    因此峰值、阈值判定与命中分数不受影响，低于阈值的位置不产生分数。
// 3. 数值：点积用 float SIMD，窗口统计用积分图的整数和（精确），归一化沿用 OpenCV 的 |num| ≥ 1.125t 钳制；与 OpenCvSharp 4.13 在 160 个随机场景 × 8 张模板上对照，命中位置一致、分数差异不超过 3e-6。
// 4. 变换与边界：见 PlaneTransformer；搜索区先裁出再变换，模糊与形态学的边界落在搜索区边缘，与原实现一致。

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MabiTool.LifeVision.Worker;

internal readonly record struct TemplateHit(PixelBox Box, double Score);

/// <summary>
/// 在 1280×960 帧的固定搜索区内匹配一张模板。持有固定大小的工作缓冲区（按最大搜索区一次分配），同一实例不可并发使用。
/// </summary>
internal sealed class TemplateMatcher
{
    /// <summary>窗口均值与模板均值之比的接受范围。</summary>
    private const double MinimumLuminanceRatio = .65, MaximumLuminanceRatio = 1.5;
    private const double GlyphThresholdCeiling = .93;
    private const double MinimumStandardDeviation = 2;
    /// <summary>早停比较的容差：只在上界低于阈值（含相对 1e-4 余量）时丢弃候选，吸收 float 点积的舍入误差。</summary>
    private const double BoundSlack = 1e-4;

    private readonly TemplateCatalog catalog;
    private readonly PlaneTransformer transformer;
    private readonly byte[] region;
    private readonly int[] sums;
    private readonly long[] squares;
    private readonly float[] regionFloats;
    private readonly List<(int X, int Y, double Score)> survivors = [];

    public TemplateMatcher(TemplateCatalog catalog)
    {
        this.catalog = catalog;
        var specs = FishingProfile.Templates;
        var pixels = specs.Max(spec => spec.Search.Width * spec.Search.Height);
        var integralSize = specs.Max(spec => (spec.Search.Width + 1) * (spec.Search.Height + 1));
        var width = specs.Max(spec => spec.Search.Width);
        var contrastPixels = specs.Where(spec => spec.RelativeContrast).Select(spec => spec.Search.Width * spec.Search.Height).DefaultIfEmpty(0).Max();
        transformer = new PlaneTransformer(pixels, width, contrastPixels);
        region = new byte[pixels];
        sums = new int[integralSize];
        squares = new long[integralSize];
        regionFloats = new float[pixels];
    }

    /// <summary>最近一次 <see cref="Match"/> 的统计：扫描位置数、通过均值剪枝的候选数、完整计算相关的位置数。</summary>
    public (int Positions, int Candidates, int Evaluations) LastStatistics { get; private set; }

    /// <summary>
    /// 阈值规则（对应原实现）：先取 <paramref name="thresholdOverride"/> 或 max(<paramref name="minimum"/>, 配置阈值)；
    /// 字形类模式再取配置的字形阈值，没有时不超过 0.93。
    /// </summary>
    internal static double ResolveThreshold(TemplateSpec spec, double minimum, double? thresholdOverride)
    {
        var threshold = thresholdOverride ?? Math.Max(minimum, spec.Threshold);
        return spec.IsGlyph ? spec.GlyphThreshold ?? Math.Min(threshold, GlyphThresholdCeiling) : threshold;
    }

    public TemplateHit? Match(RgbImage image, TemplateSpec spec, double minimum = FishingProfile.DefaultMinimum, bool unique = true, double? thresholdOverride = null)
    {
        LastStatistics = default;
        var search = spec.Search;
        if (!search.IsInside(image.Width, image.Height)) return null;
        var template = catalog.Get(spec);
        if (search.Width < template.Width || search.Height < template.Height || template.Std < MinimumStandardDeviation) return null;
        var width = search.Width;
        var height = search.Height;
        var plane = region.AsSpan(0, width * height);
        if (!transformer.Transform(image, search, spec, plane)) return null;
        BuildIntegrals(plane, width, height);
        var stride = width + 1;
        var count = (double)width * height;
        var total = sums[height * stride + width];
        var totalSquares = squares[height * stride + width];
        var mean = total / count;
        if (Math.Sqrt(Math.Max(0, totalSquares / count - mean * mean)) < MinimumStandardDeviation) return null;

        var threshold = ResolveThreshold(spec, minimum, thresholdOverride);
        var (positions, candidates, evaluations) = Search(template, width, height, threshold);
        LastStatistics = (positions, candidates, evaluations);
        if (survivors.Count == 0) return null;
        var best = survivors[0];
        foreach (var item in survivors)
            if (item.Score > best.Score) best = item;
        var area = (double)template.Width * template.Height;
        var ratio = WindowSum(best.X, best.Y, template.Width, template.Height, stride) / area / Math.Max(1, template.Mean);
        if (!double.IsFinite(best.Score) || best.Score < threshold || ratio < MinimumLuminanceRatio || ratio > MaximumLuminanceRatio) return null;
        if (unique)
        {
            // 峰值周围 ±(模板宽/2, 模板高/2) 视为同一目标；之外还有达到阈值的位置，说明目标不唯一。
            var scoreWidth = width - template.Width + 1;
            var scoreHeight = height - template.Height + 1;
            int left = Math.Max(0, best.X - template.Width / 2), top = Math.Max(0, best.Y - template.Height / 2);
            int right = Math.Min(scoreWidth, best.X + template.Width / 2 + 1), bottom = Math.Min(scoreHeight, best.Y + template.Height / 2 + 1);
            foreach (var item in survivors)
                if (item.X < left || item.X >= right || item.Y < top || item.Y >= bottom) return null;
        }
        return new TemplateHit(new PixelBox(search.Left + best.X, search.Top + best.Y, search.Left + best.X + template.Width,
            search.Top + best.Y + template.Height), best.Score);
    }

    private void BuildIntegrals(ReadOnlySpan<byte> plane, int width, int height)
    {
        var stride = width + 1;
        Array.Clear(sums, 0, stride);
        Array.Clear(squares, 0, stride);
        for (var y = 0; y < height; y++)
        {
            var row = plane.Slice(y * width, width);
            var above = y * stride;
            var current = above + stride;
            sums[current] = 0;
            squares[current] = 0;
            var rowSum = 0;
            var rowSquares = 0L;
            for (var x = 0; x < row.Length; x++)
            {
                int value = row[x];
                rowSum += value;
                rowSquares += value * value;
                sums[current + x + 1] = sums[above + x + 1] + rowSum;
                squares[current + x + 1] = squares[above + x + 1] + rowSquares;
            }
        }
    }

    private long WindowSum(int x, int y, int windowWidth, int windowHeight, int stride) =>
        (long)sums[(y + windowHeight) * stride + x + windowWidth] - sums[y * stride + x + windowWidth] -
        sums[(y + windowHeight) * stride + x] + sums[y * stride + x];

    /// <summary>
    /// 逐位置计算 TM_CCOEFF_NORMED，把达到阈值的位置放入 <see cref="survivors"/>（行主序）。
    /// 先用积分图的窗口和按亮度比值剪枝，再把窗口按行块逐块累计点积：每块之后用已累计部分加上剩余行的 Cauchy–Schwarz 上界判断是否仍可能达到阈值。
    /// </summary>
    private (int Positions, int Candidates, int Evaluations) Search(PreparedTemplate template, int width, int height, double threshold)
    {
        survivors.Clear();
        int templateWidth = template.Width, templateHeight = template.Height;
        int columns = width - templateWidth + 1, rows = height - templateHeight + 1;
        var stride = width + 1;
        var area = (double)templateWidth * templateHeight;
        var referenceMean = Math.Max(1, template.Mean);
        var lowSum = MinimumLuminanceRatio * referenceMean * area;
        var highSum = MaximumLuminanceRatio * referenceMean * area;
        var blockRows = Math.Max(1, (templateHeight + 3) / 4);
        var centered = template.Centered;
        var floatsReady = false;
        int candidates = 0, evaluations = 0;
        for (var y = 0; y < rows; y++)
        {
            var topRow = y * stride;
            var bottomRow = (y + templateHeight) * stride;
            for (var x = 0; x < columns; x++)
            {
                var sum = (long)sums[bottomRow + x + templateWidth] - sums[topRow + x + templateWidth] - sums[bottomRow + x] + sums[topRow + x];
                if (sum < lowSum || sum > highSum) continue;
                candidates++;
                var squareSum = squares[bottomRow + x + templateWidth] - squares[topRow + x + templateWidth] - squares[bottomRow + x] + squares[topRow + x];
                var windowMean = sum / area;
                var windowVariance = squareSum - sum * windowMean;
                if (windowVariance <= 1e-6) continue;
                var norm = Math.Sqrt(windowVariance) * template.Norm;
                if (!floatsReady) { ConvertRegion(width * height); floatsReady = true; }
                evaluations++;
                var dot = 0d;
                var pruned = false;
                for (var row = 0; row < templateHeight;)
                {
                    var end = Math.Min(templateHeight, row + blockRows);
                    for (; row < end; row++)
                        dot += Dot(centered, row * templateWidth, regionFloats, (y + row) * width + x, templateWidth);
                    if (end == templateHeight) break;
                    var restTop = (y + end) * stride;
                    var restSum = (long)sums[bottomRow + x + templateWidth] - sums[restTop + x + templateWidth] - sums[bottomRow + x] + sums[restTop + x];
                    var restSquares = squares[bottomRow + x + templateWidth] - squares[restTop + x + templateWidth] - squares[bottomRow + x] + squares[restTop + x];
                    var restVariance = restSquares - 2 * windowMean * restSum + windowMean * windowMean * templateWidth * (templateHeight - end);
                    var bound = dot - windowMean * template.PrefixSum[end] +
                        Math.Sqrt((template.PrefixSquares[templateHeight] - template.PrefixSquares[end]) * Math.Max(0, restVariance));
                    if (bound < (threshold - BoundSlack) * norm) { pruned = true; break; }
                }
                if (pruned) continue;
                var numerator = dot - windowMean * template.PrefixSum[templateHeight];
                // OpenCV 的归一化：分母以内取比值，略超出视为 ±1，明显超出（数值异常）取 0。
                var score = Math.Abs(numerator) < norm ? numerator / norm : Math.Abs(numerator) < norm * 1.125 ? Math.Sign(numerator) : 0;
                if (score >= threshold) survivors.Add((x, y, score));
            }
        }
        return (columns * rows, candidates, evaluations);
    }

    /// <summary>把搜索区灰度平面转成 float，点积循环直接用它（每次匹配最多转换一次，且只在存在候选位置时转换）。</summary>
    private void ConvertRegion(int count)
    {
        var bytes = region.AsSpan(0, count);
        var floats = regionFloats.AsSpan(0, count);
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var step = Vector<byte>.Count;
            var quarter = Vector<float>.Count;
            for (; i <= count - step; i += step)
            {
                Vector.Widen(new Vector<byte>(bytes[i..]), out Vector<ushort> low, out Vector<ushort> high);
                Vector.Widen(low, out Vector<uint> a, out Vector<uint> b);
                Vector.Widen(high, out Vector<uint> c, out Vector<uint> d);
                Vector.ConvertToSingle(a).CopyTo(floats[i..]);
                Vector.ConvertToSingle(b).CopyTo(floats[(i + quarter)..]);
                Vector.ConvertToSingle(c).CopyTo(floats[(i + 2 * quarter)..]);
                Vector.ConvertToSingle(d).CopyTo(floats[(i + 3 * quarter)..]);
            }
        }
        for (; i < count; i++) floats[i] = bytes[i];
    }

    private static float Dot(float[] template, int templateOffset, float[] source, int sourceOffset, int length)
    {
        ref var a = ref MemoryMarshal.GetArrayDataReference(template);
        ref var b = ref MemoryMarshal.GetArrayDataReference(source);
        var accumulator = Vector<float>.Zero;
        var step = Vector<float>.Count;
        var i = 0;
        for (; i <= length - step; i += step)
            accumulator += Vector.LoadUnsafe(ref a, (nuint)(templateOffset + i)) * Vector.LoadUnsafe(ref b, (nuint)(sourceOffset + i));
        var total = Vector.Sum(accumulator);
        for (; i < length; i++) total += Unsafe.Add(ref a, templateOffset + i) * Unsafe.Add(ref b, sourceOffset + i);
        return total;
    }
}
