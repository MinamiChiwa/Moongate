using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Modules;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>扩展的声明式界面：绑定状态与“未读取”、未知节点降级、按钮事件回传，以及运行中扩展的页面与浮窗元件登记与撤下。</summary>
public sealed class ExtensionSurfaceTests
{
    [AvaloniaFact]
    public void SurfaceRendersBoundStateDegradesUnknownNodesAndSendsClicks()
    {
        var backend = SurfaceBackend.Create(Catalog("""
            {"type":"column","children":[
              {"type":"value","label":"用时","bind":"current"},
              {"type":"text","bind":"missing"},
              {"type":"sparkline"},
              {"type":"button","id":"reset","text":{"zh-Hans":"清空","zh-Hant":"清空"}}]}
            """, new() { ["current"] = JsonSerializer.SerializeToElement("1:23") }), out var proxy);
        var view = new ExtensionSurfaceView(backend, "example.timer", "main");
        var window = new Window { Content = view };
        window.Show();

        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(x => x.Text).ToArray();
        Assert.Contains("1:23", texts);
        Assert.Contains("未读取", texts);
        Assert.Equal(["sparkline"], view.UnsupportedNodes);
        Assert.Contains(texts, x => x?.Contains("sparkline", StringComparison.Ordinal) == true);

        view.GetLogicalDescendants().OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var sent = Assert.Single(proxy.Events);
        Assert.Equal(("example.timer", "main", "reset", "click"), (sent.Id, sent.Value.Surface, sent.Value.Node, sent.Value.Kind));
        window.Close();
    }

    [AvaloniaFact]
    public void RunningExtensionSurfacesBecomeModulesAndWidgetsAndLeaveWhenStopped()
    {
        var backend = SurfaceBackend.Create(Catalog("""{"type":"text","text":"x"}""", [], overlay: true), out var proxy);
        var catalog = new ModuleCatalog();
        var modules = ExtensionModules.Attach(catalog, backend);
        var module = Assert.Single(catalog.Modules);
        Assert.Equal(("extension.example.timer", ModuleOrigin.Extension), (module.Id, module.Origin));
        Assert.Equal("extension.example.timer.mini", Assert.Single(module.Widgets).Id);

        proxy.Surfaces = null;
        modules.Sync();
        Assert.Empty(catalog.Modules);
    }

    [AvaloniaFact]
    public async Task FileRequestOpensOnePickerNamingTheExtensionAndAnswersOnce()
    {
        var backend = SurfaceBackend.Create(Catalog("""{"type":"text","text":"x"}""", []) with
        {
            FileRequests = [new("req-1", "example.timer", new Dictionary<string, string> { ["zh-Hans"] = "计时器" }, "选择存档", new(512 * 1024), DateTimeOffset.UtcNow.AddMinutes(2))]
        }, out var proxy);
        var titles = new List<string>();
        var modules = ExtensionModules.Attach(new ModuleCatalog(), backend, title => { titles.Add(title); return Task.FromResult<string?>(null); });
        modules.Sync();
        modules.Sync();
        await Task.Delay(50);
        Assert.Equal("扩展 计时器 请求读取一个文件：选择存档", Assert.Single(titles));
        Assert.Equal(("example.timer", "req-1", (string?)null), Assert.Single(proxy.Answers));
    }

    private static ExtensionSurfaceCatalog Catalog(string root, Dictionary<string, JsonElement> values, bool overlay = false) => new(
        [new("example.timer", overlay ? "mini" : "main", overlay ? "overlay" : "page", new Dictionary<string, string> { ["zh-Hans"] = "计时" }, 1,
            JsonDocument.Parse(root).RootElement.Clone(), overlay ? "small" : null, new(1))],
        [new("example.timer", values, new(1))], [], [], [], new(1));

    /// <summary>只替换扩展界面相关的方法，其余转给样本后端。</summary>
    public class SurfaceBackend : DispatchProxy
    {
        private readonly SampleBackend sample = new();
        public ExtensionSurfaceCatalog? Surfaces { get; set; }
        public List<(string Id, ExtensionUiEvent Value)> Events { get; } = [];
        public List<(string Extension, string Request, string? Path)> Answers { get; } = [];

        public static IDesktopBackend Create(ExtensionSurfaceCatalog surfaces, out SurfaceBackend proxy)
        {
            var backend = Create<IDesktopBackend, SurfaceBackend>();
            proxy = (SurfaceBackend)(object)backend;
            proxy.Surfaces = surfaces;
            return backend;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IDesktopBackend.GetExtensionSurfaces): return Surfaces;
                case nameof(IDesktopBackend.AnswerExtensionFileRequestAsync):
                    Answers.Add(((string)args![0]!, (string)args[1]!, (string?)args[2]));
                    return Task.FromResult(new BackendReply(true, string.Empty));
                case nameof(IDesktopBackend.SendExtensionUiEventAsync):
                    Events.Add(((string)args![0]!, (ExtensionUiEvent)args[1]!));
                    return Task.FromResult(new BackendReply(true, string.Empty));
                default: return method.Invoke(sample, args);
            }
        }
    }
}
