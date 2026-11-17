using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 设置页“扩展”区：每个授予了 ui.settings 的启用扩展一组表单，按清单声明的设置项（开关、数字、文字、选择）生成；
/// 修改后立即交给后端保存，后端核对类型与范围，结果写在该组下方。表单修订不变时不重建，正在编辑的输入不被刷新打断。
/// </summary>
public sealed class ExtensionSettingsView : ContentControl
{
    private IDesktopBackend? backend;
    private string? builtSignature;
    private readonly Dictionary<string, TextBlock> results = new(StringComparer.Ordinal);

    public void Attach(IDesktopBackend source)
    {
        if (backend is not null) backend.Changed -= OnChanged;
        backend = source;
        backend.Changed += OnChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (backend is not null) backend.Changed -= OnChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (backend is not null) { backend.Changed -= OnChanged; backend.Changed += OnChanged; Update(); }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    private void Update()
    {
        var forms = backend?.GetExtensionSurfaces()?.Settings ?? [];
        var signature = UiText.Language + "|" + string.Join(";", forms.Select(x => x.ExtensionId + ":" + string.Join(",", x.Fields.Select(f => f.Key + "=" + f.Type))));
        if (signature == builtSignature) return;
        builtSignature = signature;
        results.Clear();
        if (forms.Count == 0)
        {
            Content = new TextBlock { Classes = { "note" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = UiText.Pick("没有提供设置项的扩展；扩展启用并授予“提供设置项”后显示在这里", "沒有提供設定項的擴充；擴充啟用並授予「提供設定項」後顯示在這裡") };
            return;
        }
        var panel = new StackPanel { Spacing = 12 };
        foreach (var form in forms) panel.Children.Add(Form(form));
        Content = panel;
    }

    private Control Form(ExtensionSettingsForm form)
    {
        var group = new StackPanel { Spacing = 8 };
        group.Children.Add(new TextBlock { Classes = { "name", "strong" }, Text = Pick(form.Titles) ?? form.ExtensionId });
        foreach (var field in form.Fields)
        {
            var label = Pick(field.Labels) ?? field.Key;
            Control editor;
            switch (field.Type)
            {
                case "toggle":
                {
                    var box = new CheckBox { Content = label, IsChecked = field.Value.ValueKind == JsonValueKind.True };
                    box.IsCheckedChanged += (_, _) => Save(form.ExtensionId, field.Key, JsonSerializer.SerializeToElement(box.IsChecked == true));
                    group.Children.Add(box);
                    continue;
                }
                case "number":
                {
                    var input = new NumericUpDown { Value = field.Value.TryGetDouble(out var number) ? (decimal)number : null, MinWidth = 140,
                        Minimum = field.Minimum is { } min ? (decimal)min : decimal.MinValue, Maximum = field.Maximum is { } max ? (decimal)max : decimal.MaxValue };
                    input.LostFocus += (_, _) => { if (input.Value is { } value) Save(form.ExtensionId, field.Key, JsonSerializer.SerializeToElement((double)value)); };
                    editor = input;
                    break;
                }
                case "choice":
                {
                    var options = field.Options ?? [];
                    var combo = new ComboBox { ItemsSource = options.Select(x => Pick(x.Labels) ?? x.Value).ToArray(), MinWidth = 140,
                        SelectedIndex = options.ToList().FindIndex(x => field.Value.ValueKind == JsonValueKind.String && x.Value == field.Value.GetString()) };
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) Save(form.ExtensionId, field.Key, JsonSerializer.SerializeToElement(options[combo.SelectedIndex].Value)); };
                    editor = combo;
                    break;
                }
                default:
                {
                    var box = new TextBox { Text = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : string.Empty, MaxLength = 256, MinWidth = 180 };
                    box.LostFocus += (_, _) => Save(form.ExtensionId, field.Key, JsonSerializer.SerializeToElement(box.Text ?? string.Empty));
                    editor = box;
                    break;
                }
            }
            var row = new DockPanel();
            editor.SetValue(DockPanel.DockProperty, Dock.Right);
            row.Children.Add(editor);
            row.Children.Add(new TextBlock { Classes = { "body" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Text = label,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
            group.Children.Add(row);
        }
        var result = new TextBlock { Classes = { "note" }, IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        results[form.ExtensionId] = result;
        group.Children.Add(result);
        return new Border { Classes = { "inset" }, Padding = new Thickness(12, 10), Child = group };
    }

    private async void Save(string extension, string key, JsonElement value)
    {
        if (backend is null) return;
        var reply = await backend.SaveExtensionSettingsAsync(extension, new Dictionary<string, JsonElement> { [key] = value }, CancellationToken.None);
        if (results.TryGetValue(extension, out var result))
        {
            result.Text = reply.Message;
            result.IsVisible = reply.Message.Length > 0;
        }
    }

    private static string? Pick(IReadOnlyDictionary<string, string>? texts)
    {
        if (texts is null || texts.Count == 0) return null;
        var (first, second) = UiText.Language == UiLanguage.TraditionalChinese ? ("zh-Hant", "zh-Hans") : ("zh-Hans", "zh-Hant");
        return texts.TryGetValue(first, out var text) ? text : texts.TryGetValue(second, out text) ? text : null;
    }
}
