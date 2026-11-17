using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 用现有控件呈现扩展的声明式界面（schema 版本 1）。描述的修订变化时重建控件树，状态变化只更新绑定的控件，不重排其他内容。
/// 节点：column、row、card、text、value、table、list、button、toggle、select、input、progress、icon、spacer、divider；
/// 文字可以是字符串或以 zh-Hans／zh-Hant 为键的对象；<c>bind</c> 读取扩展推送的状态，缺失时显示“未读取”。
/// 不认识的节点显示一行说明并计入 <see cref="UnsupportedNodes"/>；扩展没有在运行时整个界面显示说明。
/// 交互（按钮点击、开关与选择的修改、输入框确认）只对带 <c>id</c> 的节点生效，经后端转给扩展。
/// </summary>
public sealed class ExtensionSurfaceView : ContentControl
{
    public const int SchemaVersion = 1;
    private readonly IDesktopBackend backend;
    private readonly string extensionId;
    private readonly string surfaceId;
    private readonly Dictionary<string, List<Action<JsonElement?>>> bindings = new(StringComparer.Ordinal);
    private readonly List<string> unsupported = [];
    private UInt64String? builtRevision;
    private bool attached;

    public ExtensionSurfaceView(IDesktopBackend backend, string extensionId, string surfaceId)
    {
        this.backend = backend;
        this.extensionId = extensionId;
        this.surfaceId = surfaceId;
        Update();
    }

    /// <summary>最近一次构建里不认识的节点类型；测试与诊断读取。</summary>
    public IReadOnlyList<string> UnsupportedNodes => unsupported;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (attached) return;
        attached = true;
        backend.Changed += OnBackendChanged;
        UiText.LanguageChanged += OnLanguageChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        attached = false;
        backend.Changed -= OnBackendChanged;
        UiText.LanguageChanged -= OnLanguageChanged;
    }

    private void OnBackendChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        builtRevision = null;
        Update();
    }

    /// <summary>按后端当前的界面目录更新：描述修订变化时重建，否则只把状态写进已绑定的控件。</summary>
    public void Update()
    {
        var catalog = backend.GetExtensionSurfaces();
        var surface = catalog?.Surfaces.FirstOrDefault(x => x.ExtensionId == extensionId && x.Id == surfaceId);
        if (surface is null)
        {
            if (builtRevision is not null || Content is null)
            {
                builtRevision = null;
                bindings.Clear();
                Content = new TextBlock
                {
                    Classes = { "note" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = UiText.Pick("扩展没有在运行，界面暂不可用", "擴充沒有在執行，介面暫不可用")
                };
            }
            return;
        }
        if (builtRevision != surface.Revision)
        {
            bindings.Clear();
            unsupported.Clear();
            Control root = surface.SchemaVersion == SchemaVersion ? Build(surface.Root, 0)
                : Note(UiText.Pick($"界面描述版本 {surface.SchemaVersion} 不受支持", $"介面描述版本 {surface.SchemaVersion} 不受支援"));
            if (unsupported.Count > 0)
            {
                System.Diagnostics.Trace.WriteLine($"扩展 {extensionId} 的界面 {surfaceId} 含不支持的节点：{string.Join("、", unsupported)}");
                root = new StackPanel { Spacing = 6, Children = { root, Note(UiText.Pick($"有 {unsupported.Count} 个界面元素不受支持，已略过",
                    $"有 {unsupported.Count} 個介面元素不受支援，已略過")) } };
            }
            Content = root;
            builtRevision = surface.Revision;
        }
        var values = catalog!.States.FirstOrDefault(x => x.ExtensionId == extensionId)?.Values;
        foreach (var (key, updates) in bindings)
        {
            JsonElement? value = values is not null && values.TryGetValue(key, out var found) ? found : null;
            foreach (var update in updates) update(value);
        }
    }

    private Control Build(JsonElement node, int depth)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String || depth > 24)
            return Unsupported("?");
        var type = typeElement.GetString()!;
        var id = node.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
        IEnumerable<Control> Children() => node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array
            ? children.EnumerateArray().Select(child => Build(child, depth + 1)).ToArray() : [];
        double Gap() => node.TryGetProperty("gap", out var gap) && gap.TryGetDouble(out var value) ? Math.Clamp(value, 0, 48) : 8;
        switch (type)
        {
            case "column":
            {
                var panel = new StackPanel { Spacing = Gap() };
                panel.Children.AddRange(Children());
                return panel;
            }
            case "row":
            {
                var panel = new WrapPanel { ItemSpacing = Gap(), LineSpacing = Gap() };
                panel.Children.AddRange(Children());
                return panel;
            }
            case "card":
            {
                var panel = new StackPanel { Spacing = 8 };
                if (Localized(node, "title") is { } title) panel.Children.Add(new TextBlock { Classes = { "name", "strong" }, Text = title });
                panel.Children.AddRange(Children());
                return new Border { Classes = { "inset" }, Padding = new Thickness(12, 10), Child = panel };
            }
            case "text":
            {
                var block = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                var style = node.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                foreach (var name in style switch { "title" => new[] { "name", "strong" }, "muted" => ["body", "muted"], "caption" => ["note"], _ => ["body"] })
                    block.Classes.Add(name);
                BindText(node, block, v => block.Text = v);
                return block;
            }
            case "value":
            {
                var label = new TextBlock { Classes = { "note" }, Text = Localized(node, "label") ?? string.Empty, IsVisible = node.TryGetProperty("label", out _) };
                var value = new TextBlock { Classes = { "name", "strong" }, FontSize = 22 };
                var unit = Localized(node, "unit");
                BindText(node, value, v => value.Text = unit is null || v == Unread ? v : v + " " + unit);
                return new StackPanel { Spacing = 2, Children = { label, value } };
            }
            case "progress":
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
                var label = new TextBlock { Classes = { "note" }, Text = Localized(node, "label") ?? string.Empty, IsVisible = node.TryGetProperty("label", out _) };
                Bind(node, v => { bar.IsIndeterminate = v is not { ValueKind: JsonValueKind.Number }; if (v is { ValueKind: JsonValueKind.Number } n) bar.Value = Math.Clamp(n.GetDouble(), 0, 1); });
                return new StackPanel { Spacing = 4, Children = { label, bar } };
            }
            case "list":
            {
                var panel = new StackPanel { Spacing = 4 };
                Bind(node, v =>
                {
                    panel.Children.Clear();
                    if (v is not { ValueKind: JsonValueKind.Array } items) { panel.Children.Add(Note(Unread)); return; }
                    foreach (var item in items.EnumerateArray().Take(200)) panel.Children.Add(new TextBlock { Classes = { "body" }, Text = Plain(item), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                });
                return panel;
            }
            case "table": return Table(node);
            case "button":
            {
                var button = new Button { Classes = { "chip" }, Content = Localized(node, "text") ?? id ?? string.Empty, IsEnabled = id is not null };
                if (id is not null) button.Click += (_, _) => Send(id, "click", null);
                return button;
            }
            case "toggle":
            {
                var box = new CheckBox { Content = Localized(node, "text") ?? string.Empty, IsEnabled = id is not null };
                var updating = false;
                Bind(node, v => { updating = true; box.IsChecked = v is { ValueKind: JsonValueKind.True }; updating = false; });
                if (id is not null) box.IsCheckedChanged += (_, _) => { if (!updating) Send(id, "change", JsonSerializer.SerializeToElement(box.IsChecked == true)); };
                return box;
            }
            case "select":
            {
                var options = node.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                        .Select(x => (Value: x.GetProperty("value").GetString()!, Text: Localized(x, "text") ?? x.GetProperty("value").GetString()!)).Take(64).ToArray()
                    : [];
                var combo = new ComboBox { ItemsSource = options.Select(x => x.Text).ToArray(), IsEnabled = id is not null, MinWidth = 120 };
                var updating = false;
                Bind(node, v => { updating = true; combo.SelectedIndex = v is { ValueKind: JsonValueKind.String } s ? Array.FindIndex(options, x => x.Value == s.GetString()) : -1; updating = false; });
                if (id is not null) combo.SelectionChanged += (_, _) =>
                {
                    if (!updating && combo.SelectedIndex >= 0) Send(id, "change", JsonSerializer.SerializeToElement(options[combo.SelectedIndex].Value));
                };
                return combo;
            }
            case "input":
            {
                var box = new TextBox { PlaceholderText = Localized(node, "placeholder"), IsEnabled = id is not null, MaxLength = 256, MinWidth = 160 };
                Bind(node, v => { if (!box.IsFocused) box.Text = v is { ValueKind: JsonValueKind.String } s ? s.GetString() : string.Empty; });
                void Commit() { if (id is not null) Send(id, "change", JsonSerializer.SerializeToElement(box.Text ?? string.Empty)); }
                box.LostFocus += (_, _) => Commit();
                box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };
                return box;
            }
            case "icon": return new TextBlock { Classes = { "body" }, Text = Localized(node, "text") ?? "•" };
            case "spacer": return new Border { Height = node.TryGetProperty("size", out var size) && size.TryGetDouble(out var height) ? Math.Clamp(height, 0, 64) : 8 };
            case "divider": return new Border { Height = 1, Background = Avalonia.Media.Brushes.Gray, Opacity = 0.3, Margin = new Thickness(0, 4) };
            default: return Unsupported(type);
        }
    }

    private Control Table(JsonElement node)
    {
        var columns = node.TryGetProperty("columns", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
                .Select(x => (Key: x.GetProperty("key").GetString()!, Title: Localized(x, "title") ?? x.GetProperty("key").GetString()!)).Take(12).ToArray()
            : [];
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        foreach (var _ in columns) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Bind(node, v =>
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns.Length; c++)
            {
                var header = new TextBlock { Classes = { "note" }, Text = columns[c].Title };
                Grid.SetColumn(header, c);
                grid.Children.Add(header);
            }
            if (v is not { ValueKind: JsonValueKind.Array } rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var unread = Note(Unread);
                Grid.SetRow(unread, 1);
                Grid.SetColumnSpan(unread, Math.Max(1, columns.Length));
                grid.Children.Add(unread);
                return;
            }
            var r = 1;
            foreach (var row in rows.EnumerateArray().Take(200))
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var c = 0; c < columns.Length; c++)
                {
                    var cell = new TextBlock { Classes = { "body" },
                        Text = row.ValueKind == JsonValueKind.Object && row.TryGetProperty(columns[c].Key, out var cellValue) ? Plain(cellValue) : Unread };
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
                r++;
            }
        });
        return grid;
    }

    private static string Unread => UiText.Pick("未读取", "未讀取");

    private void BindText(JsonElement node, TextBlock block, Action<string> set)
    {
        if (node.TryGetProperty("bind", out _)) Bind(node, v => set(v is { } value ? Plain(value) : Unread));
        else set(Localized(node, "text") ?? string.Empty);
    }

    private void Bind(JsonElement node, Action<JsonElement?> update)
    {
        if (!node.TryGetProperty("bind", out var key) || key.ValueKind != JsonValueKind.String || key.GetString() is not { Length: > 0 } name)
        {
            update(node.TryGetProperty("value", out var fixedValue) ? fixedValue.Clone() : null);
            return;
        }
        if (!bindings.TryGetValue(name, out var list)) bindings[name] = list = [];
        list.Add(update);
    }

    private Control Unsupported(string type)
    {
        unsupported.Add(type);
        return Note(UiText.Pick("不支持的界面元素：", "不支援的介面元素：") + type);
    }

    private static TextBlock Note(string text) => new() { Classes = { "note" }, Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    private void Send(string node, string kind, JsonElement? value) =>
        _ = backend.SendExtensionUiEventAsync(extensionId, new(surfaceId, node, kind, value), CancellationToken.None);

    /// <summary>字符串原样；以语言标记为键的对象按界面语言选取；数字、布尔按不变区域格式化；其他显示为 JSON。</summary>
    internal static string Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer.ToString("N0", CultureInfo.CurrentCulture) : value.GetDouble().ToString("0.##", CultureInfo.CurrentCulture),
        JsonValueKind.True => UiText.Pick("是", "是"),
        JsonValueKind.False => UiText.Pick("否", "否"),
        JsonValueKind.Null => Unread,
        JsonValueKind.Object when LocalizedOf(value) is { } text => text,
        _ => value.GetRawText()
    };

    private static string? Localized(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : LocalizedOf(value) : null;

    private static string? LocalizedOf(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        string? Get(string key) => value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        return UiText.Language == UiLanguage.TraditionalChinese ? Get("zh-Hant") ?? Get("zh-Hans") : Get("zh-Hans") ?? Get("zh-Hant");
    }
}
