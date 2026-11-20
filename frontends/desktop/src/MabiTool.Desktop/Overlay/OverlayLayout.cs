using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 一个元件在版面上的摆放。X、Y 是元件在所在显示器工作区剩余空间里的比例（0 贴左或上，1 贴右或下），
/// 所以换分辨率后仍贴着原来的边；<see cref="Screen"/> 记住所在的显示器（范围与缩放比例）；Scale 是缩放倍率；
/// Width、Height 是元件外框在缩放前的尺寸（逻辑像素）。
/// </summary>
public sealed class WidgetPlacement
{
    public const double MinimumScale = 0.5;
    public const double MaximumScale = 2;
    /// <summary>保存的宽高上限，防止损坏的文件给出极大的尺寸；实际显示时另按浮窗范围限制。</summary>
    public const double MaximumSide = 4096;
    /// <summary>“－／＋”按钮每次改变的缩放倍率。</summary>
    public const double ScaleStep = 0.1;

    /// <summary>按一次“－”或“＋”之后的缩放倍率：步进 0.1，取一位小数，限制在 0.5–2。</summary>
    public static double Step(double scale, bool up) =>
        Math.Clamp(Math.Round((scale + (up ? ScaleStep : -ScaleStep)) * 10) / 10, MinimumScale, MaximumScale);

    /// <summary>背景不透明度的下限：底板可以完全不可见，内容照常显示。</summary>
    public const double MinimumBackgroundOpacity = 0;
    /// <summary>内容不透明度的下限：文字与图标再淡也认得出，不会在游戏画面上消失。</summary>
    public const double MinimumContentOpacity = 0.2;
    /// <summary>编辑页“－／＋”与浮窗上滚轮每次改变的不透明度。</summary>
    public const double OpacityStep = 0.1;
    /// <summary>游戏浮窗上不透明度按钮每点一次降低的量；降到下限以下时回到完全不透明。</summary>
    public const double OpacityCycleStep = 0.2;

    /// <summary>按一次“－”或“＋”之后的不透明度：步进 0.1，取一位小数，限制在 <paramref name="minimum"/>–1。</summary>
    public static double StepOpacity(double opacity, bool up, double minimum) =>
        Math.Clamp(Math.Round((opacity + (up ? OpacityStep : -OpacityStep)) * 10) / 10, minimum, 1);

    /// <summary>
    /// 游戏浮窗上点一次不透明度按钮之后的值：每次降 0.2，降到 <paramref name="minimum"/> 以下时回到 1。
    /// 背景 1 → 0.8 → 0.6 → 0.4 → 0.2 → 0 → 1，内容 1 → 0.8 → 0.6 → 0.4 → 0.2 → 1。
    /// </summary>
    public static double CycleOpacity(double opacity, double minimum)
    {
        var next = Math.Round((opacity - OpacityCycleStep) * 10) / 10;
        return next < minimum - 0.001 ? 1 : next;
    }

    public string WidgetId { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1;
    private double? background, content;

    /// <summary>底板（底色或自定图像、玻璃边缘与投影）的不透明度，0–1；锁、“－／＋”等控件不随之变淡。</summary>
    public double BackgroundOpacity { get => background ?? Legacy(); set => background = value; }

    /// <summary>内容（文字、数字、图标、条形、按钮）的不透明度，0.2–1。</summary>
    public double ContentOpacity { get => content ?? Legacy(); set => content = value; }

    /// <summary>
    /// 旧版面文件的单一不透明度（作用于整个元件）：读入时同时作为背景与内容的初值，限制在旧的 0.2–1，视觉与升级前一致；
    /// <see cref="Normalize"/> 之后清空，保存时只写两个新字段。两者都没有时按 1（不透明）读取。
    /// </summary>
    [JsonPropertyName("Opacity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyOpacity { get; set; }

    private double Legacy() => LegacyOpacity is { } value && double.IsFinite(value) ? Math.Clamp(value, MinimumContentOpacity, 1) : 1;
    /// <summary>
    /// 元件外框在缩放前的宽高（逻辑像素）。拖动右下角手柄改变它们，内容按新的尺寸重新排版；
    /// 旧版面文件没有这两个字段，读取为空，按元件的默认尺寸显示，不需要迁移。
    /// </summary>
    public double? Width { get; set; }
    public double? Height { get; set; }
    /// <summary>外观：glass、solid 或 image。</summary>
    public string Skin { get; set; } = "glass";
    /// <summary>自定外框图片的路径，Skin 为 image 时使用。</summary>
    public string? Image { get; set; }
    /// <summary>
    /// 游戏浮窗上的锁：上锁时元件只有锁图标接收鼠标，其余部分让鼠标落到游戏；解锁时整块接收鼠标，可以拖动、缩放和操作元件内的控件。
    /// 旧版面文件没有这个字段，读取为已上锁。
    /// </summary>
    public bool Locked { get; set; } = true;
    /// <summary>
    /// 所在显示器的标识（<see cref="ScreenInfo.Key"/>）。旧版面文件没有这个字段，按主显示器处理；
    /// 那块显示器已不在时也回到主显示器（<see cref="OverlayArea.Locate"/>）。
    /// </summary>
    public string? Screen { get; set; }
    /// <summary>在浮窗上按了关闭：浮窗不再显示它，摆放数据保留；在版面页的元件库再点一次、或编辑状态下再按它的关闭按钮即重新显示。</summary>
    public bool Closed { get; set; }
    /// <summary>
    /// DPS 元件不显示怪物血量区块（版面页详情里的“怪物血量”开关）。默认显示：只在关闭时写出，旧版面文件没有这个字段即显示。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideMonsterHealth { get; set; }

    public void Normalize()
    {
        X = double.IsFinite(X) ? Math.Clamp(X, 0, 1) : 0;
        Y = double.IsFinite(Y) ? Math.Clamp(Y, 0, 1) : 0;
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, MinimumScale, MaximumScale) : 1;
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, MinimumBackgroundOpacity, 1) : 1;
        ContentOpacity = double.IsFinite(ContentOpacity) ? Math.Clamp(ContentOpacity, MinimumContentOpacity, 1) : 1;
        LegacyOpacity = null;
        Width = Width is { } width && double.IsFinite(width) && width > 0 ? Math.Min(width, MaximumSide) : null;
        Height = Height is { } height && double.IsFinite(height) && height > 0 ? Math.Min(height, MaximumSide) : null;
        if (Skin is not ("glass" or "solid" or "image")) Skin = "glass";
    }
}

public sealed class OverlayLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<WidgetPlacement> Widgets { get; set; } = [];
}

/// <summary>
/// 多套浮窗版面及当前使用的一套，保存为前端自己的偏好文件。
/// 文件缺失或损坏时回到内建的两套默认版面，不影响启动。
/// </summary>
public sealed class OverlayLayoutStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string? path;

    private sealed class Document
    {
        public string? ActiveId { get; set; }
        public List<OverlayLayout> Layouts { get; set; } = [];
        public bool HideFromCapture { get; set; }
        public bool HideWhenGameMinimized { get; set; }
    }

    /// <param name="path">保存位置；为空时只保存在内存里。</param>
    public OverlayLayoutStore(string? path = null)
    {
        this.path = path;
        var document = Load();
        Layouts = document.Layouts;
        foreach (var widget in Layouts.SelectMany(layout => layout.Widgets)) widget.Normalize();
        Active = Layouts.FirstOrDefault(layout => layout.Id == document.ActiveId) ?? Layouts[0];
        HideFromCapture = document.HideFromCapture;
        HideWhenGameMinimized = document.HideWhenGameMinimized;
    }

    public static string DefaultPath => Infrastructure.DesktopFiles.File("overlay-layouts.json");

    public event EventHandler? Changed;

    /// <summary>拖动中元件的位置或所属显示器变了（不保存）：各显示器的浮窗据此重排，元件跨到别的显示器时由那块显示器的浮窗接着显示。</summary>
    public event EventHandler? Moved;

    /// <summary>
    /// 在屏幕上编辑浮窗：所有元件（包括关闭的与展示主题缺失的）显示在各自的显示器上，不论锁状态都可以拖动、改尺寸与缩放。
    /// 只在内存里，重新启动后不处于编辑状态。
    /// </summary>
    public bool IsEditing { get; private set; }

    public List<OverlayLayout> Layouts { get; }
    public OverlayLayout Active { get; private set; }
    /// <summary>当前版面的元件都已上锁（没有元件时也算）。</summary>
    public bool AllLocked => Active.Widgets.All(widget => widget.Locked);
    /// <summary>录屏与截图时不出现浮窗。</summary>
    public bool HideFromCapture { get; set; }
    /// <summary>游戏窗口最小化时隐藏浮窗；缺省关闭，浮窗始终显示，与游戏窗口无关。</summary>
    public bool HideWhenGameMinimized { get; set; }

    public void Select(string id)
    {
        if (Layouts.FirstOrDefault(layout => layout.Id == id) is not { } layout || layout == Active) return;
        Active = layout;
        Commit();
    }

    public OverlayLayout Add(string name)
    {
        var layout = new OverlayLayout { Name = name };
        Layouts.Add(layout);
        Active = layout;
        Commit();
        return layout;
    }

    /// <summary>至少保留一套版面。</summary>
    public bool Remove(string id)
    {
        if (Layouts.Count <= 1 || Layouts.FirstOrDefault(layout => layout.Id == id) is not { } layout) return false;
        Layouts.Remove(layout);
        if (Active == layout) Active = Layouts[0];
        Commit();
        return true;
    }

    /// <summary>把元件放进当前版面；已在版面里时返回原有的摆放，在浮窗上关闭过的重新显示。</summary>
    public WidgetPlacement Place(string widgetId)
    {
        if (Active.Widgets.FirstOrDefault(widget => widget.WidgetId == widgetId) is { } existing)
        {
            if (existing.Closed) Reopen(existing);
            return existing;
        }
        var placement = new WidgetPlacement { WidgetId = widgetId, X = 0.5, Y = 0.5 };
        Active.Widgets.Add(placement);
        Commit();
        return placement;
    }

    public void Unplace(string widgetId)
    {
        if (Active.Widgets.RemoveAll(widget => widget.WidgetId == widgetId) > 0) Commit();
    }

    /// <summary>浮窗上的关闭按钮：浮窗不再显示该元件，摆放数据保留。</summary>
    public void Close(WidgetPlacement placement)
    {
        placement.Closed = true;
        Commit();
    }

    /// <summary>重新显示在浮窗上关闭过的元件。</summary>
    public void Reopen(WidgetPlacement placement)
    {
        placement.Closed = false;
        Commit();
    }

    /// <summary>切换一个元件的锁并保存。</summary>
    public void ToggleLock(WidgetPlacement placement)
    {
        placement.Locked = !placement.Locked;
        Commit();
    }

    /// <summary>进入编辑状态。</summary>
    public void BeginEdit()
    {
        if (IsEditing) return;
        IsEditing = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>退出编辑状态并保存版面。</summary>
    public void EndEdit()
    {
        if (!IsEditing) return;
        IsEditing = false;
        Commit();
    }

    /// <summary>拖动过程中的位置变化：通知各浮窗重排，不保存（松开时 <see cref="Commit"/>）。</summary>
    public void NotifyMoved() => Moved?.Invoke(this, EventArgs.Empty);

    /// <summary>当前版面的元件全部改属主显示器，比例位置不变（找回在别的显示器上看不见的元件）。</summary>
    public void MoveAllToPrimary()
    {
        foreach (var widget in Active.Widgets) widget.Screen = null;
        Commit();
    }

    /// <summary>
    /// 元件改属另一块显示器（<see cref="ScreenInfo.Key"/>）并保存：比例位置不变，所以落在那块显示器工作区里的同一相对位置、总在可见范围内。
    /// 不需要拖动跨过显示器边界，远程桌面一次只显示一块屏幕时也能换屏。
    /// </summary>
    public void MoveTo(WidgetPlacement placement, string screenKey)
    {
        placement.Screen = screenKey;
        Commit();
    }

    /// <summary>当前版面的元件全部改属同一块显示器，比例位置不变。</summary>
    public void MoveAllTo(string screenKey)
    {
        foreach (var widget in Active.Widgets) widget.Screen = screenKey;
        Commit();
    }

    /// <summary>元件回到内建版面里同一元件的位置（没有时为主显示器正中），尺寸、缩放与外观不变。</summary>
    public void ResetPosition(WidgetPlacement placement)
    {
        var fallback = Defaults().SelectMany(layout => layout.Widgets).FirstOrDefault(widget => widget.WidgetId == placement.WidgetId);
        (placement.X, placement.Y, placement.Screen) = (fallback?.X ?? 0.5, fallback?.Y ?? 0.5, null);
        Commit();
    }

    /// <summary>当前版面有任一元件解锁时全部上锁，否则全部解锁。</summary>
    public void ToggleAllLocks()
    {
        var locked = !AllLocked;
        foreach (var widget in Active.Widgets) widget.Locked = locked;
        Commit();
    }

    /// <summary>摆放或设置改动之后调用：保存并通知画布与浮窗重排。</summary>
    public void Commit()
    {
        foreach (var widget in Active.Widgets) widget.Normalize();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>等待已提交的保存写完（读取同一文件之前与测试清理时）。</summary>
    public void Flush()
    {
        if (path is not null) LayoutFileWriter.For(path).Flush();
    }

    private Document Load()
    {
        if (path is not null) LayoutFileWriter.For(path).Flush();
        if (path is not null && File.Exists(path))
            try
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
                if (document is { Layouts.Count: > 0 })
                {
                    // 预设版面只按序号命名：名称不暗示浮窗会随游戏场景自动切换。早期按用途命名的两套预设在读入时改名。
                    foreach (var layout in document.Layouts)
                        layout.Name = (layout.Id, layout.Name) switch
                        {
                            ("performance", "演奏") => UiText.Pick("预设1", "預設1"),
                            ("raid", "团队副本" or "團隊副本") => UiText.Pick("预设2", "預設2"),
                            _ => layout.Name
                        };
                    return document;
                }
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
        return new Document { Layouts = Defaults() };
    }

    // 界面线程只序列化（得到这一刻的快照），写盘交给后台写入器。
    private void Save()
    {
        if (path is null) return;
        LayoutFileWriter.For(path).Enqueue(JsonSerializer.SerializeToUtf8Bytes(new Document
        {
            ActiveId = Active.Id, Layouts = Layouts, HideFromCapture = HideFromCapture, HideWhenGameMinimized = HideWhenGameMinimized
        }, Json));
    }

    /// <summary>
    /// 版面文件的写入器，每个文件一个：在后台线程写盘，不占用界面线程；写盘期间到达的多次保存合并为最后一次。
    /// 写盘线程是前台线程，程序正常退出前写完；先写临时文件再替换，中途退出也不留下半个文件。
    /// </summary>
    private sealed class LayoutFileWriter(string path)
    {
        private static readonly Dictionary<string, LayoutFileWriter> Writers = new(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new();
        private byte[]? pending;
        private bool running;

        public static LayoutFileWriter For(string path)
        {
            var key = Path.GetFullPath(path);
            lock (Writers)
            {
                if (!Writers.TryGetValue(key, out var writer)) Writers[key] = writer = new LayoutFileWriter(key);
                return writer;
            }
        }

        public void Enqueue(byte[] content)
        {
            lock (gate)
            {
                pending = content;
                if (running) return;
                running = true;
            }
            new Thread(Run) { IsBackground = false, Name = "Moongate overlay layout save" }.Start();
        }

        public void Flush()
        {
            lock (gate)
                while (running) Monitor.Wait(gate);
        }

        private void Run()
        {
            while (true)
            {
                byte[] content;
                lock (gate)
                {
                    if (pending is null)
                    {
                        running = false;
                        Monitor.PulseAll(gate);
                        return;
                    }
                    (content, pending) = (pending, null);
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = path + ".tmp";
                    File.WriteAllBytes(temporary, content);
                    File.Move(temporary, path, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static List<OverlayLayout> Defaults() =>
    [
        new()
        {
            Id = "performance", Name = UiText.Pick("预设1", "預設1"),
            Widgets =
            [
                new() { WidgetId = "rooms.readiness", X = 1, Y = 0 }
            ]
        },
        new()
        {
            Id = "raid", Name = UiText.Pick("预设2", "預設2"),
            Widgets =
            [
                new() { WidgetId = "combat.dps", X = 1, Y = 0 },
                new() { WidgetId = "combat.skills", X = 1, Y = 0.42 },
                new() { WidgetId = "combat.self-effects", X = 0, Y = 1, Scale = 1.2 },
                new() { WidgetId = "combat.target-effects", X = 1, Y = 1 }
            ]
        }
    ];
}
