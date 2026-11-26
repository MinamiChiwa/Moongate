// 移植自 MabiAgent 82bc0ef 的 dotnet/MabiAgent.Windows/FishingGauge.cs（MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：原实现按 JSON 校准对象读取参数并支持逐列扫描与反向轨道；Moongate 只识别台服 1280×960 的浮动进度条，
// 校准参数固定为常量（fill RGB(40,220,88)、empty RGB(39,42,50)、容差 35、空槽容差 15、从左到右、逐行扫描），
// 只保留逐行扫描分支。Find 额外返回中位行的填充与轨道像素数，供观察输出使用。

namespace MabiTool.LifeVision.Worker;

/// <summary>读取浮动进度条：只认显式校准的左→右两色轨道，场景颜色不会读成零。</summary>
internal static class FishingGauge
{
    internal const int FillRed = 40, FillGreen = 220, FillBlue = 88;
    internal const int EmptyRed = 39, EmptyGreen = 42, EmptyBlue = 50;
    internal const int FillTolerance = 35;
    internal const int EmptyTolerance = 15;
    /// <summary>读数相邻一致的最少行数。</summary>
    internal const int MinimumReadRows = 3;
    /// <summary>定位时双色条形需要的相邻行数。</summary>
    internal const int MinimumFindRows = 5;

    /// <summary>一次读数：填充像素数与轨道（框）宽度。</summary>
    internal readonly record struct Reading(int FillPixels, int TrackPixels)
    {
        public double Value => FillPixels / (double)TrackPixels;
    }

    internal readonly record struct Located(PixelBox Box, double Value, int FillPixels, int TrackPixels);

    /// <summary>
    /// 在 <paramref name="box"/> 内逐行读取：一行必须是“连续填充色 + 连续空槽色”才有效，填充出现在空槽之后或出现其他颜色即无效。
    /// 有效行按填充数分组，取相邻不少于 3 行的组，优先非零组，其次行数多者；暗背景整行读成零不会压过轨道内部的相邻行。
    /// </summary>
    internal static Reading? Read(RgbImage image, PixelBox box)
    {
        if (!box.IsInside(image.Width, image.Height) || box.Width < 2) return null;
        var pixels = image.Pixels;
        var readings = new List<(int Y, int Count)>();
        for (var y = box.Top; y < box.Bottom; y++)
        {
            var count = 0;
            var emptySeen = false;
            var valid = true;
            for (var column = 0; column < box.Width; column++)
            {
                var offset = (y * image.Width + box.Left + column) * 3;
                if (MatchFill(pixels, offset))
                {
                    if (emptySeen) { valid = false; break; }
                    count++;
                }
                else if (MatchEmpty(pixels, offset)) emptySeen = true;
                else { valid = false; break; }
            }
            if (valid) readings.Add((y, count));
        }
        // 浮动轨道随上钩图标垂直移动：优先相邻的填充内部行，黑色轮廓与周围背景可能以假零读数压过轨道。
        var group = readings.GroupBy(r => r.Count).Select(g => new { Count = g.Key, Rows = ConsecutiveRows(g.Select(r => r.Y)) })
            .Where(g => g.Rows >= MinimumReadRows).OrderByDescending(g => g.Count > 0).ThenByDescending(g => g.Rows).FirstOrDefault();
        return group is null ? null : new Reading(group.Count, box.Width);
    }

    /// <summary>
    /// 在 <paramref name="search"/> 内定位条形：每行找“填充段 + 空槽段”且总宽度在期望宽度的 75%–130%，
    /// 相邻不少于 5 行、左右边界几乎一致的一组才算轨道，孤立的场景颜色不会通过。填充为零的条形不会被定位。
    /// </summary>
    internal static Located? Find(RgbImage image, PixelBox search, int expectedWidth)
    {
        if (!search.IsInside(image.Width, image.Height) || expectedWidth < 8) return null;
        var minimum = Math.Max(8, (int)Math.Floor(expectedWidth * .75));
        var maximum = (int)Math.Ceiling(expectedWidth * 1.3);
        var minimumSegment = Math.Max(3, (int)Math.Ceiling(expectedWidth * .05));
        var rows = new List<(int Y, int Left, int Right, int Filled)>();
        var pixels = image.Pixels;
        for (var y = search.Top; y < search.Bottom; y++)
        {
            var x = search.Left;
            while (x < search.Right)
            {
                while (x < search.Right && !MatchFill(pixels, (y * image.Width + x) * 3)) x++;
                var left = x;
                while (x < search.Right && MatchFill(pixels, (y * image.Width + x) * 3)) x++;
                var filled = x - left;
                var emptyStart = x;
                while (x < search.Right && MatchEmpty(pixels, (y * image.Width + x) * 3)) x++;
                var emptyCount = x - emptyStart;
                var width = x - left;
                if (filled >= minimumSegment && emptyCount >= minimumSegment && width >= minimum && width <= maximum)
                    rows.Add((y, left, x, filled));
                if (x == left) x++;
            }
        }
        // 条形内部有几行边界几乎一致的相邻行；要求这种形状可以拒绝孤立的场景颜色，而不必扫描整页。
        var group = rows.GroupBy(r => ((r.Left + 2) / 4, (r.Right + 2) / 4)).Select(g => g.OrderBy(r => r.Y).ToArray())
            .Where(g => ConsecutiveRows(g.Select(r => r.Y)) >= MinimumFindRows)
            .OrderByDescending(g => g.Length).ThenBy(g => Math.Abs(g.Select(r => r.Right - r.Left).Average() - expectedWidth)).FirstOrDefault();
        if (group is null) return null;
        var ordered = group.OrderBy(r => r.Filled / (double)(r.Right - r.Left)).ToArray();
        var middle = ordered[ordered.Length / 2];
        var leftBound = (int)Math.Round(group.Average(r => r.Left));
        var rightBound = (int)Math.Round(group.Average(r => r.Right));
        var trackWidth = middle.Right - middle.Left;
        return new(new(leftBound, group.Min(r => r.Y), rightBound, group.Max(r => r.Y) + 1), middle.Filled / (double)trackWidth, middle.Filled, trackWidth);
    }

    private static int ConsecutiveRows(IEnumerable<int> rows)
    {
        var longest = 0;
        var current = 0;
        int? previous = null;
        foreach (var y in rows)
        {
            current = previous is not null && y == previous + 1 ? current + 1 : 1;
            longest = Math.Max(longest, current);
            previous = y;
        }
        return longest;
    }

    private static bool MatchFill(byte[] pixels, int offset) =>
        Math.Abs(pixels[offset] - FillRed) <= FillTolerance && Math.Abs(pixels[offset + 1] - FillGreen) <= FillTolerance &&
        Math.Abs(pixels[offset + 2] - FillBlue) <= FillTolerance;

    private static bool MatchEmpty(byte[] pixels, int offset) =>
        Math.Abs(pixels[offset] - EmptyRed) <= EmptyTolerance && Math.Abs(pixels[offset + 1] - EmptyGreen) <= EmptyTolerance &&
        Math.Abs(pixels[offset + 2] - EmptyBlue) <= EmptyTolerance;
}
