using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.GameIconExtractor;

/// <summary>
/// 客户端里没有对应图标的项按客户端 Icon64 的风格自绘：64×64、白色实心字形、圆角、粗线宽，留白约 4 像素。
/// 形状按顺序叠加（Add 并入、Cut 挖去），每像素 4×4 超采样得到边缘透明度，输出与提取图标同样的 RGBA。
/// 这些图形是 Moongate 自己的作品，随项目的 MIT 许可发布。
/// </summary>
internal static class DrawnIcons
{
    private abstract record Shape(bool Cut)
    {
        public abstract bool Contains(double x, double y);
    }

    private sealed record Circle(double X, double Y, double R, bool Cut = false) : Shape(Cut)
    {
        public override bool Contains(double x, double y) => (x - X) * (x - X) + (y - Y) * (y - Y) <= R * R;
    }

    private sealed record Ring(double X, double Y, double R, double Width, bool Cut = false) : Shape(Cut)
    {
        public override bool Contains(double x, double y)
        {
            var d = Math.Sqrt((x - X) * (x - X) + (y - Y) * (y - Y));
            return Math.Abs(d - R) <= Width / 2;
        }
    }

    private sealed record Box(double X, double Y, double W, double H, double R = 0, bool Cut = false) : Shape(Cut)
    {
        public override bool Contains(double x, double y)
        {
            if (x < X || y < Y || x > X + W || y > Y + H) return false;
            var cx = Math.Clamp(x, X + R, X + W - R);
            var cy = Math.Clamp(y, Y + R, Y + H - R);
            return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= R * R;
        }
    }

    private sealed record Capsule(double X1, double Y1, double X2, double Y2, double Width, bool Cut = false) : Shape(Cut)
    {
        public override bool Contains(double x, double y)
        {
            var (dx, dy) = (X2 - X1, Y2 - Y1);
            var t = Math.Clamp(((x - X1) * dx + (y - Y1) * dy) / Math.Max(1e-9, dx * dx + dy * dy), 0, 1);
            var (px, py) = (X1 + t * dx - x, Y1 + t * dy - y);
            return px * px + py * py <= Width * Width / 4;
        }
    }

    /// <summary>自绘图标的键 → 形状。</summary>
    private static readonly Dictionary<string, Shape[]> Shapes = new(StringComparer.Ordinal)
    {
        // 日常：与客户端周常（W）、月常（M）日历同一造型（实心日历、字母挖空），字母为 D。
        ["mission/daily"] =
        [
            new Box(7, 11, 50, 47, 9),
            new Capsule(21, 5, 21, 15, 7), new Capsule(43, 5, 43, 15, 7),
            new Ring(31, 36, 9.5, 7, Cut: true), new Box(13, 18, 9, 38),
            new Box(22, 23, 8, 26, 1.5, Cut: true)
        ],
        // 服务器：三层机柜，每层一个指示灯与一条槽。
        ["character/server"] =
        [
            new Box(8, 6, 48, 15, 4), new Box(8, 24.5, 48, 15, 4), new Box(8, 43, 48, 15, 4),
            new Circle(17, 13.5, 3, Cut: true), new Circle(17, 32, 3, Cut: true), new Circle(17, 50.5, 3, Cut: true),
            new Capsule(27, 13.5, 47, 13.5, 4, Cut: true), new Capsule(27, 32, 47, 32, 4, Cut: true), new Capsule(27, 50.5, 47, 50.5, 4, Cut: true)
        ],
        // 多云：与客户端雨天图标的云同一造型，不带雨滴。
        ["weather/cloudy"] =
        [
            new Circle(23, 36, 12), new Circle(37, 27, 15), new Circle(48, 38, 10), new Box(11, 34, 47, 16, 8)
        ],
        // 雾：小一号的云加两道雾带。
        ["weather/fog"] =
        [
            new Circle(23, 27, 10), new Circle(35, 20, 12.5), new Circle(45, 29, 8.5), new Box(13, 26, 41, 13, 6.5),
            new Capsule(10, 47, 44, 47, 5), new Capsule(20, 56, 54, 56, 5)
        ]
    };

    public static IEnumerable<string> Keys => Shapes.Keys;

    public static ClientIconBitmap Draw(string file)
    {
        const int side = 64, samples = 4;
        var shapes = Shapes[file];
        var rgba = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var hits = 0;
            for (var sy = 0; sy < samples; sy++)
            for (var sx = 0; sx < samples; sx++)
            {
                var (px, py) = (x + (sx + 0.5) / samples, y + (sy + 0.5) / samples);
                var inside = false;
                foreach (var shape in shapes)
                    if (shape.Cut) inside &= !shape.Contains(px, py);
                    else inside |= shape.Contains(px, py);
                if (inside) hits++;
            }
            var at = (y * side + x) * 4;
            rgba[at] = rgba[at + 1] = rgba[at + 2] = 255;
            rgba[at + 3] = (byte)(hits * 255 / (samples * samples));
        }
        return new ClientIconBitmap(side, side, rgba);
    }
}
