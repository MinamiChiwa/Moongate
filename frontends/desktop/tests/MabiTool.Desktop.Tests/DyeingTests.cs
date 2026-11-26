using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Dyeing;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>染色页：草稿自动保存、颜色要求与容差、替代色、优先级、选色浮层、目标色集合、吸管与游戏窗口激活、方案与寻色记录、进度、候选。</summary>
public sealed class DyeingTests
{
    private static (Window Window, ColorPageModel Model) Open(SampleBackend backend, IScreenPickHost? host = null)
    {
        var window = LayoutProbe.Open(backend, 1280, 860, "color");
        var page = Page<ColorPage>(window);
        if (host is not null)
        {
            page.DataContext = new ColorPageModel(backend, host);
            LayoutProbe.Settle();
        }
        return (window, (ColorPageModel)page.DataContext!);
    }

    [AvaloniaFact]
    public void CardEditsAreSavedOnceAfterTheDebounceAndInvalidHexIsNotSent()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            var card = model.Cards[0];
            card.TargetText = "#112233";
            card.TargetText = "445566";
            Assert.Empty(backend.SavedColorDrafts);
            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            var saved = Assert.Single(backend.SavedColorDrafts);
            Assert.Equal("#445566", saved.Regions[0].TargetHex);
            Assert.Equal(["region-1", "region-2", "region-3"], saved.Regions.Select(region => region.Id));

            // 不是六位十六进制：标红，目标色保持原值，不发送。
            card.TargetText = "#12";
            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            Assert.True(card.TargetInvalid);
            Assert.Equal("#445566", card.TargetHex);
            Assert.Single(backend.SavedColorDrafts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MatchModeAndToleranceFollowTheOriginalRules()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            var card = model.Cards[0];
            var slider = window.GetVisualDescendants().OfType<DyeSlider>().First(item => item.Name == "ToleranceSlider" && item.DataContext == card);
            // 精准 HEX：容差折叠。
            Assert.True(card.IsExact);
            Assert.False(slider.IsEffectivelyVisible);
            card.ModeCommand.Execute(ColorCodes.Similar);
            LayoutProbe.Settle();
            Assert.True(slider.IsEffectivelyVisible);
            Assert.Equal("ΔE 3", card.ToleranceBadge);

            card.Tolerance = 40;
            Assert.Equal(35, card.Tolerance);
            card.Tolerance = 7.6;
            Assert.Equal(8, card.Tolerance);
            card.Tolerance = 0;
            Assert.Equal(1, card.Tolerance);
            card.Tolerance = 8;
            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            var region = Assert.Single(backend.SavedColorDrafts).Regions[0];
            Assert.Equal((ColorCodes.Similar, 8.0), (region.MatchMode, region.Tolerance));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AlternativesAddRemoveAndStopAtSixtyThree()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            var card = model.Cards[0];
            Assert.True(card.NoAlternatives);
            card.ToggleAlternativeEditorCommand.Execute(null);
            card.NewAlternative = "#GG0000";
            card.AddAlternativeCommand.Execute(null);
            Assert.True(card.NewAlternativeInvalid);
            Assert.Empty(card.Alternatives);
            card.NewAlternative = "a8455a";
            card.AddAlternativeCommand.Execute(null);
            Assert.Equal(["#A8455A"], card.Alternatives);
            Assert.False(card.AlternativeEditorOpen);

            card.RemoveAlternativeCommand.Execute("#A8455A");
            Assert.Empty(card.Alternatives);

            for (var index = 0; index < ColorCodes.MaximumAlternatives; index++)
            {
                card.ToggleAlternativeEditorCommand.Execute(null);
                card.NewAlternative = $"#0000{index:X2}";
                card.AddAlternativeCommand.Execute(null);
            }
            Assert.Equal(63, card.Alternatives.Count);
            Assert.False(card.CanAddAlternative);
            Assert.Equal(6, card.VisibleAlternatives.Count);
            Assert.Equal("+57", card.MoreAlternatives);
            card.ToggleAlternativeEditorCommand.Execute(null);
            Assert.False(card.AlternativeEditorOpen);

            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            Assert.Equal(63, backend.SavedColorDrafts[^1].Regions[0].AlternativeHex.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ChoosingATakenPrioritySwapsTheTwoRegions()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            model.Cards[2].PriorityCommand.Execute("1");
            Assert.Equal([3, 2, 1], model.Cards.Select(card => card.Priority));
            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            Assert.Equal([3, 2, 1], backend.SavedColorDrafts.Single().Regions.Select(region => region.Priority));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RejectedDraftKeepsTheInputAndShowsTheReason()
    {
        var backend = new SampleBackend { ColorDraftReject = "request.invalid" };
        var (window, model) = Open(backend);
        try
        {
            model.Cards[1].TargetText = "#00FF00";
            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            // 状态行写中文原因，不显示原码。
            Assert.Equal("请求未完成：请求内容无效", model.StatusText);
            Assert.DoesNotContain("request.invalid", model.StatusText);
            // 后端状态刷新不覆盖被拒绝后保留的输入。
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal("#00FF00", model.Cards[1].TargetHex);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void StartSavesThePendingDraftBeforeStarting()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            model.Cards[0].TargetText = "#334455";
            Click(window, Named<Button>(window, "StartButton"));
            Assert.Equal("#334455", Assert.Single(backend.SavedColorDrafts).Regions[0].TargetHex);
            Assert.Equal(("color.start", null), backend.UiIntents[^1]);

            // 寻色方式属于草稿，选精准寻色时显示警示。
            Click(window, Named<Button>(window, "PreciseStrategy"));
            Assert.Contains(UiText.Pick("精准寻色会读取游戏内存", "精準尋色會讀取遊戲記憶體"), model.StrategyNote);
            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            Assert.Equal(ColorCodes.Precise, backend.SavedColorDrafts[^1].Strategy);
        }
        finally { window.Close(); }
    }

    // 按住滑杆或色相环上的某一点（相对位置）并松开。
    private static void Drag(Window window, Control control, double x, double y)
    {
        LayoutProbe.Render(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width * x, control.Bounds.Height * y), window)!.Value;
        PressAt(window, point);
    }

    private static ColorPickerModel OpenPicker(Window window, RegionCardModel card)
    {
        Click(window, window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "TargetSwatch" && button.DataContext == card));
        return card.Picker;
    }

    [AvaloniaFact]
    public void FineSlidersInputsAndWheelWriteTheCardTargetAsTheOnlySource()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            var card = model.Cards[0];
            var picker = OpenPicker(window, card);
            Assert.Equal("#1F3A6B", card.TargetHex);
            var hue = picker.Hue;

            // 明度滑杆：按在一半处，目标色、色相环、标题色块与卡片 HEX 一起变。
            Drag(window, Named<DyeSlider>(window, "BrightnessSlider"), 0.5, 0.5);
            Assert.Equal(50, picker.Brightness);
            Assert.Equal(ColorMath.FromHsv(hue, picker.Saturation, 50), card.TargetHex);
            Assert.Equal(50, Named<HsvWheel>(window, "PickerWheel").Brightness);
            Assert.Equal(card.TargetHex, Named<TextBlock>(window, "PickerResultHex").Text);
            Assert.Equal(card.TargetHex, card.TargetText);

            // 数值输入：合法整数写回，不是整数时标红、目标色不变。
            Named<TextBox>(window, "SaturationInput").Text = "100";
            LayoutProbe.Settle();
            Assert.Equal(ColorMath.FromHsv(hue, 100, 50), card.TargetHex);
            Named<TextBox>(window, "SaturationInput").Text = "ab";
            LayoutProbe.Settle();
            Assert.True(picker.SaturationInvalid);
            Assert.Equal(ColorMath.FromHsv(hue, 100, 50), card.TargetHex);

            // 卡片 HEX 改成中性灰：浮层按新颜色重算，保留原色相；之后的精调以新颜色为起点。
            card.TargetText = "#808080";
            Assert.Equal((0, Math.Round(128 / 2.55, 6)), (picker.Saturation, Math.Round(picker.Brightness, 6)));
            Assert.Equal(hue, picker.Hue);
            Assert.False(picker.SaturationInvalid);
            Named<TextBox>(window, "BrightnessInput").Text = "100";
            LayoutProbe.Settle();
            Assert.Equal("#FFFFFF", card.TargetHex);

            // 方块右上角：饱和度、明度都是 100，色相不变。
            var wheel = Named<HsvWheel>(window, "PickerWheel");
            var square = wheel.Square;
            Drag(window, wheel, (square.Right - 1) / wheel.Bounds.Width, (square.Top + 1) / wheel.Bounds.Height);
            Assert.Equal(ColorMath.FromHsv(hue, picker.Saturation, picker.Brightness), card.TargetHex);
            Assert.True(picker.Saturation > 98 && picker.Brightness > 98);
            Assert.Equal(hue, picker.Hue);

            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            Assert.Equal(card.TargetHex, backend.SavedColorDrafts[^1].Regions[0].TargetHex);

            // 关闭时的颜色排到最近选择首位，另一张卡的浮层也能看到。
            var chosen = card.TargetHex;
            Click(window, Named<Button>(window, "PickerConfirm"));
            Assert.False(model.IsPickerOpen);
            Assert.Equal(chosen, ColorRecent.Items[0]);
            var other = OpenPicker(window, model.Cards[1]);
            Assert.Equal(chosen, other.Recent[0].Swatch?.Hex);
            Assert.Equal(ColorRecent.Capacity, other.Recent.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CommonColorsRevertAndBuiltInPresetsSetTheTargets()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            Assert.Equal(20, ColorMath.CommonColors.Count);
            Assert.Contains(ColorMath.CommonColors, color => color.Hex == "#000000");
            var card = model.Cards[0];
            var picker = OpenPicker(window, card);
            Click(window, Named<ItemsControl>(window, "PickerCommon").GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is PickerSwatch { Hex: "#FFFFFF" }));
            Assert.Equal("#FFFFFF", card.TargetHex);
            Click(window, Named<Button>(window, "PickerRevert"));
            Assert.Equal("#1F3A6B", card.TargetHex);
            Assert.Equal("#1F3A6B", picker.Target);
            model.ClosePickerCommand.Execute(null);

            var modes = model.Cards.Select(item => (item.MatchMode, item.Tolerance, item.Enabled, item.Alternatives.Count)).ToList();
            Click(window, Named<Button>(window, "PresetsButton"));
            MabiTool.Desktop.Infrastructure.CardStateStore.Restore(null);
            Assert.False(model.PresetsExpanded);
            Click(window, Named<Button>(window, "PresetsToggle"));
            Assert.True(model.PresetsExpanded);
            // 日系插画配色一组排在时尚设计配色之前。
            var list = Named<StackPanel>(window, "BuiltInPresetList");
            Assert.True(list.Children.IndexOf(Named<ItemsControl>(window, "IllustrationPresetList"))
                < list.Children.IndexOf(Named<ItemsControl>(window, "FashionPresetList")));
            var preset = ColorPreset.BuiltIn.Single(item => item.Id == "illust-wafu");
            Click(window, Named<ItemsControl>(window, "IllustrationPresetList").GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is ColorPreset { Id: "illust-wafu" }));
            Assert.False(model.IsLayerOpen);
            Assert.Equal(preset.Colors, model.Cards.Select(item => item.TargetHex));
            Assert.Equal(modes, model.Cards.Select(item => (item.MatchMode, item.Tolerance, item.Enabled, item.Alternatives.Count)));
            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            Assert.Equal(preset.Colors, backend.SavedColorDrafts[^1].Regions.Select(region => region.TargetHex));
        }
        finally
        {
            window.Close();
            // “默认预设”的展开状态存入卡片状态，测试结束时清掉。
            MabiTool.Desktop.Infrastructure.CardStateStore.Restore(null);
        }
    }

    [AvaloniaFact]
    public void PickerSwatchColumnsSplitTheContentWidthWithoutOverflow()
    {
        // 十列均分：列宽相差不超过 1，最后一列止于容器右边（UniformGrid 向上取整时十列共宽 310 > 306）。
        var columns = Enumerable.Range(0, 10).Select(column => EvenColumns.Edges(column, 10, 306)).ToList();
        Assert.Equal(0, columns[0].Left);
        Assert.Equal(306, columns[^1].Right);
        Assert.All(columns.Skip(1).Zip(columns), pair => Assert.Equal(pair.Second.Right, pair.First.Left));
        Assert.True(columns.Max(item => item.Right - item.Left) - columns.Min(item => item.Right - item.Left) <= 1.01);

        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            OpenPicker(window, model.Cards[0]);
            LayoutProbe.Render(window);
            foreach (var name in new[] { "PickerCommon", "PickerRecent" })
            {
                var list = Named<ItemsControl>(window, name);
                var cells = list.GetVisualDescendants().OfType<Control>().Where(control => control.GetVisualParent() is EvenColumns).ToList();
                Assert.NotEmpty(cells);
                var right = cells.Max(cell => cell.TranslatePoint(new Point(cell.Bounds.Width, 0), list)!.Value.X);
                Assert.True(right <= list.Bounds.Width + 0.01, $"{name} 的最右一格止于 {right}，超出宽度 {list.Bounds.Width}");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void PopupCloseButtonsStayInsideEveryClipAtEachScaling(double scaling)
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            Avalonia.Headless.HeadlessWindowExtensions.SetRenderScaling(window, scaling);
            LayoutProbe.Settle();
            // 按钮矩形按悬停放大（外观层 1.03）外扩后，必须落在每个裁切祖先（滚动区、页面、窗口）的范围内。
            void InsideClips(string name)
            {
                LayoutProbe.Render(window);
                var button = Named<Button>(window, name);
                Assert.True(button.IsEffectivelyVisible, name);
                var rect = new Rect(button.Bounds.Size).Inflate(new Thickness(button.Bounds.Width * 0.015, button.Bounds.Height * 0.015));
                foreach (var clip in button.GetVisualAncestors().OfType<Visual>().Where(visual => visual.ClipToBounds))
                {
                    var inClip = rect.TransformToAABB(button.TransformToVisual(clip)!.Value);
                    Assert.True(inClip.Top >= -0.01 && inClip.Left >= -0.01 && inClip.Bottom <= clip.Bounds.Height + 0.01 && inClip.Right <= clip.Bounds.Width + 0.01,
                        $"{scaling}：{name} 的矩形 {inClip} 超出 {clip.GetType().Name} {clip.Bounds.Size}");
                }
            }

            model.Cards[0].OpenPickerCommand.Execute(null);
            InsideClips("PickerClose");
            model.ClosePickerCommand.Execute(null);
            model.Cards[0].OpenAtlasCommand.Execute(null);
            InsideClips("AtlasZoomOut");
            model.CloseAtlasCommand.Execute(null);
            model.OpenLayerCommand.Execute("presets");
            InsideClips("PresetsClose");
            model.OpenLayerCommand.Execute("history");
            InsideClips("HistoryClose");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClosingPickerKeepsItsContentAndPlaceUntilHidden()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            MabiTool.Desktop.Theme.UiMotion.Enabled = true;
            LayoutProbe.Settle();
            var card = model.Cards[0];
            var picker = OpenPicker(window, card);
            picker.Pick("#2E8B57");
            Pump(window, 300);
            var panel = Named<Border>(window, "PickerPanel");
            var wheel = Named<HsvWheel>(window, "PickerWheel");
            var before = (panel.Bounds, wheel.Hue, wheel.Saturation, wheel.Brightness, Named<TextBlock>(window, "PickerResultHex").Text);
            Assert.Equal(1, panel.Opacity);

            // 关闭过渡的每一帧（不透明度在 0 与 1 之间时）取样，其间有一次后端刷新：色相、方块颜色、标题 HEX、位置与内容都与关闭前相同。
            // 无界面模式下渲染计时器按真实时间走，按固定等待取样可能已经走完，所以在不透明度变化时取样。
            var frames = new List<(Rect, double, double, double, string?, bool, bool)>();
            var content = Named<DockPanel>(window, "PickerContent");
            void OnFrame(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property != Visual.OpacityProperty || panel.Opacity is <= 0.01 or >= 0.99) return;
                frames.Add((panel.Bounds, wheel.Hue, wheel.Saturation, wheel.Brightness, Named<TextBlock>(window, "PickerResultHex").Text,
                    ReferenceEquals(picker, content.DataContext), Named<Panel>(window, "PickerLayer").IsVisible));
            }
            panel.PropertyChanged += OnFrame;
            model.ClosePickerCommand.Execute(null);
            _ = backend.ListCaptureChannelsAsync(CancellationToken.None);
            Pump(window, 400);
            panel.PropertyChanged -= OnFrame;
            Assert.NotEmpty(frames);
            Assert.All(frames, frame => Assert.Equal((before.Bounds, before.Hue, before.Saturation, before.Brightness, before.Text, true, true), frame));

            // 过渡结束后隐藏并清空内容。
            Assert.False(Named<Panel>(window, "PickerLayer").IsVisible);
            Assert.Null(Named<DockPanel>(window, "PickerContent").DataContext);
        }
        finally
        {
            MabiTool.Desktop.Theme.UiMotion.Enabled = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PickerOpensAtTheSameAnchoredPlaceAndStaysThroughRefreshes()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        // 窗口高度足以把整个浮层（约 500）放在色块下方；860 高时下方放不下，浮层按定位规则夹在容器内，不是本用例要验的情形。
        // 主窗口打开时按设计尺寸收缩（WindowFit），所以在显示之后再加高。
        window.Height = 960;
        LayoutProbe.Settle();
        try
        {
            var panel = Named<Border>(window, "PickerPanel");
            Rect OpenAt(int index)
            {
                OpenPicker(window, model.Cards[index]);
                LayoutProbe.Render(window);
                return panel.Bounds;
            }

            // 同一锚点连续打开三次位置相同；三张卡并排时浮层都贴在各自色块下方、同一高度。
            var first = OpenAt(0);
            var swatch = window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "TargetSwatch" && button.DataContext == model.Cards[0]);
            // 窗口里还有别的控件名为 Root（如 DPS 检测页的层），按染色页自己的名称范围查找。
            var root = Page<ColorPage>(window).FindControl<Panel>("Root") ?? throw new InvalidOperationException("染色页缺少 Root");
            var anchor = swatch.TranslatePoint(default, root)!.Value;
            Assert.Equal(anchor.Y + swatch.Bounds.Height + AnchoredLayer.Gap, first.Y);
            model.ClosePickerCommand.Execute(null);
            var third = OpenAt(2);
            Assert.Equal(first.Y, third.Y);
            model.ClosePickerCommand.Execute(null);
            Assert.Equal(first, OpenAt(0));
            model.ClosePickerCommand.Execute(null);
            Assert.Equal(first, OpenAt(0));

            // 打开期间改色触发保存与后端重读、另有一次无关的后端刷新：位置不变。
            model.Cards[0].Picker.Brightness = 30;
            Pump(window, ColorPageModel.SaveDelay.Milliseconds + 200);
            _ = backend.ListCaptureChannelsAsync(CancellationToken.None);
            Pump(window, 300);
            Assert.True(model.IsPickerOpen);
            Assert.Equal(first, panel.Bounds);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AtlasIsSolidForExactAndReadFromTheBackendForSimilar()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            var exact = model.Cards[0];
            Assert.True(exact.Atlas.CanOpen);
            Assert.Equal(1, exact.Atlas.Count);
            Assert.Equal(UiText.Pick("1 色（单） · 点击查看全部", "1 色（單） · 點擊查看全部"), exact.Atlas.CountText);
            Assert.DoesNotContain(backend.ColorAtlasRequests, request => request.Hex == "#1F3A6B");

            // 相似颜色：卡片只读缩略图；全集图在打开集合浮层时才读。
            var similar = model.Cards[1];
            Pump(window, 200);
            Assert.Contains(("#F2E9D8", ColorCodes.Similar, 3.0, false), backend.ColorAtlasRequests);
            Assert.DoesNotContain(backend.ColorAtlasRequests, request => request.Full);
            Assert.True(similar.Atlas.CanOpen);
            Assert.Equal(600, similar.Atlas.Count);
            Assert.Equal("ΔE ≤ 3 · 600 " + UiText.Pick("色", "色"), similar.Atlas.PanelNote);

            // 规则变化重新读取；集合不含替代色。
            similar.Tolerance = 9;
            Pump(window, 200);
            Assert.Equal(("#F2E9D8", ColorCodes.Similar, 9.0, false), backend.ColorAtlasRequests[^1]);
            similar.OpenAtlasCommand.Execute(null);
            Assert.Same(similar, model.AtlasCard);
            Pump(window, 50);
            Assert.Equal(("#F2E9D8", ColorCodes.Similar, 9.0, true), backend.ColorAtlasRequests[^1]);
            Assert.NotNull(model.AtlasImage);
            Assert.Equal("ΔE ≤ 9 · 600 " + UiText.Pick("色", "色"), model.AtlasNote);
            model.AtlasZoomCommand.Execute("one");
            Assert.Equal(1, model.AtlasZoom);
            model.AtlasZoomCommand.Execute("out");
            Assert.Equal(1 / 1.5, model.AtlasZoom, 3);
            model.AtlasZoomCommand.Execute("fit");
            Assert.True(model.AtlasFitted);

            // 精准 HEX 的集合浮层直接用目标色，不发请求。
            var count = backend.ColorAtlasRequests.Count;
            exact.OpenAtlasCommand.Execute(null);
            Assert.Same(exact.Atlas.Thumbnail, model.AtlasImage);
            Assert.Equal(UiText.Pick("精准 HEX · 1 色（单）", "精準 HEX · 1 色（單）"), model.AtlasNote);
            Assert.Equal(count, backend.ColorAtlasRequests.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ChangedAtlasRulesCancelTheWaitingRequestAndShowComputing()
    {
        var gate = new TaskCompletionSource();
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            Pump(window, 200);
            backend.AtlasGate = gate;
            var card = model.Cards[1];
            card.Tolerance = 10;
            Pump(window, 150);
            Assert.True(card.Atlas.Loading);
            Assert.Equal(ColorAtlasView.ComputingText, card.Atlas.Message);
            Assert.False(card.Atlas.CanOpen);

            // 后端还在出图时改容差：旧请求取消，只等新请求。
            card.Tolerance = 12;
            Pump(window, 150);
            Assert.Equal(1, backend.CancelledAtlasRequests);
            Assert.Equal(("#F2E9D8", ColorCodes.Similar, 12.0, false), backend.ColorAtlasRequests[^1]);
            gate.SetResult();
            Pump(window, 50);
            Assert.False(card.Atlas.Loading);
            Assert.Equal("ΔE ≤ 12 · 600 " + UiText.Pick("色", "色"), card.Atlas.PanelNote);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PaletteRequestsUseTheBackendShape()
    {
        var overview = LiveBackend.PaletteRequest("#f2e9d8", ColorCodes.Similar, 40, false);
        Assert.Equal(("#F2E9D8", "similar", 35.0, "overview"),
            (overview.TargetHex, overview.MatchMode.RawValue, overview.Tolerance, overview.Image.RawValue));
        var exact = LiveBackend.PaletteRequest("#1F3A6B", ColorCodes.ExactHex, 8, true);
        Assert.Equal(("exact-hex", 0.0, "atlas"), (exact.MatchMode.RawValue, exact.Tolerance, exact.Image.RawValue));
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        Assert.Equal("染色引擎不可用", LiveBackend.PaletteProblem("provider.unavailable", "worker.crashed"));
        Assert.Equal("请求内容无效", LiveBackend.PaletteProblem("request.invalid", null));
    }

    [AvaloniaFact]
    public void MissingAtlasShowsTheReasonAndCannotOpen()
    {
        var (window, model) = Open(new SampleBackend { NoColorAtlas = true });
        try
        {
            Pump(window, 200);
            var card = model.Cards[1];
            Assert.False(card.Atlas.CanOpen);
            Assert.Equal(UiText.Pick("未配置染色引擎，无法计算色集", "未設定染色引擎，無法計算色集"), card.Atlas.Message);
            Assert.Empty(card.Atlas.CountText);
            card.OpenAtlasCommand.Execute(null);
            Assert.False(model.IsAtlasOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ScreenCoordinatesMapToFrozenPixelsIncludingNegativeOrigins()
    {
        var capture = new PixelRect(-1920, -200, 3840, 1280);
        Assert.Equal(new PixelPoint(0, 0), ScreenPickMath.ImagePoint(new(-1920, -200), capture));
        Assert.Equal(new PixelPoint(1920, 200), ScreenPickMath.ImagePoint(new(0, 0), capture));
        Assert.Equal(new PixelPoint(3839, 1279), ScreenPickMath.ImagePoint(new(1919, 1079), capture));
        Assert.Null(ScreenPickMath.ImagePoint(new(1920, 0), capture));
        Assert.Null(ScreenPickMath.ImagePoint(new(-1921, 0), capture));
        // 后端单张上限 16,777,216 像素（4096×4096）。
        Assert.False(ScreenPickMath.ExceedsLimit(new(0, 0, 4096, 4096)));
        Assert.False(ScreenPickMath.ExceedsLimit(new(-3840, 0, 7680, 2160)));
        Assert.True(ScreenPickMath.ExceedsLimit(new(0, 0, 4097, 4096)));

        var rgb = new byte[3 * 2 * 3];
        rgb[(1 * 3 + 2) * 3] = 0xAB;
        rgb[(1 * 3 + 2) * 3 + 1] = 0xCD;
        rgb[(1 * 3 + 2) * 3 + 2] = 0xEF;
        var frozen = new ScreenCapture("c", -3, 10, 3, 2, rgb);
        Assert.Equal("#ABCDEF", ScreenPickMath.HexAt(frozen, new(-1, 11)));
        Assert.Null(ScreenPickMath.HexAt(frozen, new(0, 11)));
    }

    [AvaloniaFact]
    public void MixedDpiMonitorsMapEveryPhysicalPixelAndMismatchesNameTheCause()
    {
        // 混合缩放：左边 1920×1080（100%）在 (-1920, 360)，主屏 2560×1440（150%）在 (0, 0)，右边 1920×1200（125%）在 (2560, -200)。
        // 每块显示器各自一张画面；交界、四角与显示器之间的空隙都落在正确的显示器与画面像素上。
        PixelRect[] monitors = [new(-1920, 360, 1920, 1080), new(0, 0, 2560, 1440), new(2560, -200, 1920, 1200)];
        Assert.Equal(0, ScreenPickMath.MonitorAt(monitors, new(-1920, 360)));
        Assert.Equal(0, ScreenPickMath.MonitorAt(monitors, new(-1, 1439)));
        Assert.Equal(1, ScreenPickMath.MonitorAt(monitors, new(0, 0)));
        Assert.Equal(1, ScreenPickMath.MonitorAt(monitors, new(2559, 1439)));
        Assert.Equal(2, ScreenPickMath.MonitorAt(monitors, new(2560, -200)));
        Assert.Equal(2, ScreenPickMath.MonitorAt(monitors, new(4479, 999)));
        Assert.Equal(-1, ScreenPickMath.MonitorAt(monitors, new(4480, 0)));
        Assert.Equal(-1, ScreenPickMath.MonitorAt(monitors, new(-1, 359)));
        Assert.Equal(-1, ScreenPickMath.MonitorAt(monitors, new(0, -1)));
        Assert.Equal(new PixelPoint(1919, 1079), ScreenPickMath.ImagePoint(new(-1, 1439), monitors[0]));
        Assert.Equal(new PixelPoint(0, 0), ScreenPickMath.ImagePoint(new(2560, -200), monitors[2]));
        Assert.Equal(new PixelPoint(1919, 1199), ScreenPickMath.ImagePoint(new(4479, 999), monitors[2]));
        // 单块显示器上限 16,777,216 像素：5K 可截，6K（6016×3384）超限。
        Assert.False(ScreenPickMath.ExceedsLimit(new(0, 0, 5120, 2880)));
        Assert.True(ScreenPickMath.ExceedsLimit(new(0, 0, 6016, 3384)));

        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        Assert.Null(ScreenPickMath.CheckCapture(monitors, monitors.ToList(), monitors[1], monitors[1]));
        // 截屏后读到的显示器布局与开始时不同或读不到：显示布局在取色期间变化。
        Assert.Contains("显示布局变化", ScreenPickMath.CheckCapture(monitors, [monitors[0], monitors[1]], monitors[1], monitors[1]));
        Assert.Contains("显示布局变化", ScreenPickMath.CheckCapture(monitors, null, monitors[1], monitors[1]));
        // 画面与请求的区域不一致（如按缩放后的逻辑像素截取）：两边的实际边界都写进原因。
        var other = ScreenPickMath.CheckCapture(monitors, monitors, monitors[1], new(0, 0, 1707, 960));
        Assert.Contains("(0, 0) 1707×960", other);
        Assert.Contains("(0, 0) 2560×1440", other);
    }

    [AvaloniaTheory]
    [InlineData(1.0, 0, 0, 1920, 1080)]
    [InlineData(1.25, -1920, 0, 1920, 1200)]
    [InlineData(1.5, 0, -1620, 2880, 1620)]
    [InlineData(1.75, -2560, -1440, 2560, 1440)]
    [InlineData(1.75, 1920, -200, 2880, 1620)]
    public void FrozenPixelsStayOneToOneOnEachMonitorScale(double scale, int x, int y, int width, int height)
    {
        // 每块显示器的冻结画面按“物理 ÷ 当前界面缩放”换成逻辑尺寸，乘回缩放正好是物理宽高：不放大、不裁切。
        var monitor = new PixelRect(x, y, width, height);
        var frozen = new ScreenCapture("c", x, y, width, height, new byte[width * height * 3]);
        var logical = ScreenPickMath.LogicalSize(monitor.Size, scale);
        var shown = ScreenPickMath.FrozenRect(frozen, scale);
        Assert.Equal(new Rect(logical), shown);
        Assert.Equal(width, shown.Width * scale, 9);
        Assert.Equal(height, shown.Height * scale, 9);
        Assert.False(ScreenPickMath.ExceedsLimit(monitor));

        // 物理光标落在哪个物理像素，采样格就从那个像素左上角起、边长 1 物理像素；四角都对齐，屏外为空。
        var first = ScreenPickMath.CursorCell(new(x, y), monitor, scale)!.Value;
        Assert.Equal(new Rect(0, 0, 1 / scale, 1 / scale), first);
        var last = ScreenPickMath.CursorCell(new(x + width - 1, y + height - 1), monitor, scale)!.Value;
        Assert.Equal(width - 1, last.X * scale, 9);
        Assert.Equal(height - 1, last.Y * scale, 9);
        Assert.Equal(width, last.Right * scale, 9);
        Assert.Equal(height, last.Bottom * scale, 9);
        var middle = ScreenPickMath.CursorCell(new(x + 701, y + 333), monitor, scale)!.Value;
        Assert.Equal(701, middle.X * scale, 9);
        Assert.Equal(333, middle.Y * scale, 9);
        Assert.Null(ScreenPickMath.CursorCell(new(x - 1, y), monitor, scale));
        Assert.Null(ScreenPickMath.CursorCell(new(x, y + height), monitor, scale));

        // 采样格与取到的颜色是同一个画面像素。
        var rgb = frozen.Rgb;
        var offset = (333 * width + 701) * 3;
        (rgb[offset], rgb[offset + 1], rgb[offset + 2]) = (0x12, 0x34, 0x56);
        Assert.Equal("#123456", ScreenPickMath.HexAt(frozen, new(x + 701, y + 333)));

        // 界面缩放等于窗口 DPI 才算就位；读不到 DPI（0）不算。
        Assert.True(ScreenPickMath.SameScaling(scale, (uint)Math.Round(scale * 96)));
        Assert.False(ScreenPickMath.SameScaling(scale, (uint)Math.Round(scale * 96) + 24));
        Assert.False(ScreenPickMath.SameScaling(scale, 0));
    }

    /// <summary>
    /// 记录吸管流程调用顺序的桌面实现：<paramref name="layouts"/> 依次是每次读取的显示器布局（最后一个重复使用），
    /// 取色层按 <paramref name="script"/> 依次给出事件。
    /// </summary>
    private sealed class FakePickHost(IReadOnlyList<PickEvent> script, params IReadOnlyList<PixelRect>?[] layouts) : IScreenPickHost
    {
        private int reads;
        public List<string> Calls { get; } = [];
        public List<string> Notices { get; } = [];
        public IDisposable HideWindows()
        {
            Calls.Add("hide");
            return new Restore(() => Calls.Add("restore"));
        }
        public Task SettleAsync(CancellationToken cancellationToken)
        {
            Calls.Add("settle");
            return Task.CompletedTask;
        }
        public IReadOnlyList<PixelRect>? Monitors() => layouts[Math.Min(reads++, layouts.Length - 1)];
        public IPickLayers ShowLayers(IReadOnlyList<PixelRect> monitors)
        {
            Calls.Add("layers:" + monitors.Count);
            return new Layers(this, script);
        }
        private sealed class Layers(FakePickHost host, IReadOnlyList<PickEvent> script) : IPickLayers
        {
            private int next;
            public ValueTask<PickEvent> NextAsync(CancellationToken cancellationToken) =>
                ValueTask.FromResult(next < script.Count ? script[next++] : new PickEvent.Failed("脚本已结束"));
            public void ShowCapturing(int monitor) => host.Calls.Add("capturing:" + monitor);
            public void ShowFrozen(int monitor, ScreenCapture capture) =>
                host.Calls.Add("frozen:" + monitor + "@" + ScreenPickMath.Describe(ScreenPickMath.Bounds(capture)));
            public void ShowNotice(int monitor, string message)
            {
                host.Calls.Add("notice:" + monitor);
                host.Notices.Add(message);
            }
            public void Dispose() => host.Calls.Add("close");
        }
        private sealed class Restore(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }

    // 左边一块 160×90（负原点），右边游戏所在的一块 200×100。样本画面在每块区域里分四色：左上 #1F3A6B、右上 #F2E9D8、左下 #8C2F45、右下 #0E8C7D。
    private static readonly PixelRect LeftMonitor = new(-160, 0, 160, 90);
    private static readonly PixelRect GameMonitor = new(0, 0, 200, 100);
    private static readonly PixelRect[] TwoMonitors = [LeftMonitor, GameMonitor];

    private static PickEvent Entered(int monitor) => new PickEvent.Entered(monitor);
    private static PickEvent Confirmed(int x, int y) => new PickEvent.Confirmed(new PixelPoint(x, y));

    /// <summary>每次截屏后紧接着释放同一张画面，且没有留下未释放的画面；返回截屏的画面 ID。</summary>
    private static List<string> AssertEachCaptureReleasedRightAway(SampleBackend backend)
    {
        var captures = new List<string>();
        for (var index = 0; index < backend.UiIntents.Count; index++)
        {
            if (backend.UiIntents[index].Intent != "color.capture") continue;
            captures.Add(backend.UiIntents[index].Argument!);
            Assert.Equal(("color.release", backend.UiIntents[index].Argument), backend.UiIntents[index + 1]);
        }
        Assert.Equal(0, backend.UnreleasedCaptures);
        return captures;
    }

    [AvaloniaFact]
    public async Task EyedropperFreezesOnlyTheCursorMonitorThenEachNewMonitorOnceAndPicksOnTheSecond()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend { Activation = new GameWindowActivation(true, true, true, true, null, string.Empty) };
        // 光标从游戏所在的屏开始，移到左边一块，再来回移动，最后在左边一块的右下区域确认。
        var host = new FakePickHost([Entered(1), Entered(0), Entered(1), Entered(0), Confirmed(-30, 60)], TwoMonitors);
        var result = await EyedropperSession.RunAsync(backend, host, CancellationToken.None);
        Assert.Equal(new ScreenPickResult("#0E8C7D", null), result);
        // 先只截光标所在的游戏屏，移到左边一块时补截一次，回到截过的屏不重截。
        Assert.Equal(["hide", "layers:2", "settle", "capturing:1", "frozen:1@(0, 0) 200×100", "capturing:0", "frozen:0@(-160, 0) 160×90",
            "close", "restore"], host.Calls);
        Assert.Equal(2, AssertEachCaptureReleasedRightAway(backend).Count);
        // 先激活游戏窗口（Moongate 仍在前台），再截屏。
        var intents = backend.UiIntents.Select(item => item.Intent).ToList();
        Assert.True(intents.IndexOf("game.activate") < intents.IndexOf("color.capture"));

        // 没能切到游戏窗口：照常取色，结果注明原因。
        var missed = new SampleBackend();
        var picked = await EyedropperSession.RunAsync(missed, new FakePickHost([Entered(0), Confirmed(-150, 10)], TwoMonitors), CancellationToken.None);
        Assert.Equal("#1F3A6B", picked.Hex);
        Assert.Equal("未能切到游戏窗口（模拟宿主没有真实的游戏窗口）", picked.Note);
    }

    [AvaloniaFact]
    public async Task EyedropperExplainsAnOversizedMonitorOnlyThereAndKeepsOtherMonitorsUsable()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend();
        PixelRect big = new(0, 0, 4097, 4096);
        // 超限的屏上点击不结束取色；回到超限的屏不再重复说明；左边一块照常截取并取色。
        var host = new FakePickHost([Entered(1), Confirmed(10, 10), Entered(0), Entered(1), Confirmed(-150, 10)], [LeftMonitor, big]);
        var result = await EyedropperSession.RunAsync(backend, host, CancellationToken.None);
        Assert.Equal("#1F3A6B", result.Hex);
        Assert.Equal(["hide", "layers:2", "settle", "notice:1", "capturing:0", "frozen:0@(-160, 0) 160×90", "close", "restore"], host.Calls);
        Assert.Equal(ScreenPickMath.TooLarge(big), Assert.Single(host.Notices));
        Assert.Contains("16,777,216", host.Notices[0]);
        Assert.Single(AssertEachCaptureReleasedRightAway(backend));
    }

    [AvaloniaTheory]
    [InlineData("cancel")]
    [InlineData("layer-error")]
    [InlineData("capture-refused")]
    [InlineData("layout-changed")]
    [InlineData("layout-changed-at-confirm")]
    [InlineData("no-monitors")]
    public async Task EyedropperClosesLayersReleasesAndRestoresOnEveryOtherPath(string path)
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend();
        if (path == "capture-refused") backend.CaptureRefusal = region => region == LeftMonitor ? "请求未完成：功能不可用" : null;
        IReadOnlyList<PixelRect> changed = [LeftMonitor, new(0, 0, 200, 120)];
        IReadOnlyList<PickEvent> script = path switch
        {
            "cancel" => [Entered(1), Entered(0), PickEvent.Cancelled.Instance],
            "layer-error" => [Entered(1), new PickEvent.Failed("取色視窗與螢幕不一致，請重試。")],
            "layout-changed-at-confirm" => [Entered(1), Confirmed(10, 10)],
            _ => [Entered(1), Entered(0), Confirmed(-150, 10)]
        };
        IReadOnlyList<PixelRect>?[] layouts = path switch
        {
            "no-monitors" => [null],
            // 开始时、第一块屏截好后各读一次，第二块屏截好后（layout-changed）或确认时（layout-changed-at-confirm）读到的布局变了。
            "layout-changed" or "layout-changed-at-confirm" => [TwoMonitors, TwoMonitors, changed],
            _ => [TwoMonitors]
        };
        var host = new FakePickHost(script, layouts);
        var result = await EyedropperSession.RunAsync(backend, host, CancellationToken.None);
        Assert.Null(result.Hex);
        Assert.Equal(path == "cancel", result.Error is null);
        if (path is "layout-changed" or "layout-changed-at-confirm") Assert.Equal(ScreenPickMath.LayoutChanged, result.Error);
        if (path == "capture-refused") Assert.Equal("请求未完成：功能不可用", result.Error);
        Assert.Equal("restore", host.Calls[^1]);
        Assert.Single(host.Calls, call => call == "restore");
        if (path == "no-monitors")
        {
            Assert.Equal(["hide", "restore"], host.Calls);
            Assert.DoesNotContain(backend.UiIntents, item => item.Intent == "color.capture");
        }
        else Assert.Equal("close", host.Calls[^2]);
        // 已截取的画面全部释放；拒绝或出错的那一块不留画面。
        var captures = AssertEachCaptureReleasedRightAway(backend);
        Assert.Equal(path switch { "no-monitors" => 0, "layer-error" or "capture-refused" or "layout-changed-at-confirm" => 1, _ => 2 }, captures.Count);
    }

    [AvaloniaFact]
    public async Task EyedropperWritesTheCardTargetOrShowsTheFailure()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend();
        var host = new FakePickHost([Entered(0), Confirmed(-150, 10)], TwoMonitors);
        var (window, model) = Open(backend, host);
        try
        {
            var card = model.Cards[2];
            await model.PickFromScreenAsync(card);
            Assert.Equal("#1F3A6B", card.TargetHex);
            Assert.True(model.CanPick);
            Assert.StartsWith("未能切到游戏窗口", model.StatusText);

            var failing = new FakePickHost([], (IReadOnlyList<PixelRect>?)null);
            var (_, second) = (window, new ColorPageModel(backend, failing));
            await second.PickFromScreenAsync(second.Cards[0]);
            Assert.StartsWith("屏幕取色失败：", second.StatusText);
            Assert.EndsWith(" · 未能切到游戏窗口（模拟宿主没有真实的游戏窗口）", second.StatusText);
            second.Dispose();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ActivationReceiptsAndRejectionsExplainWhyTheGameIsNotInFront()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var observed = DateTimeOffset.UnixEpoch;
        var front = LiveBackend.Activation(new("epoch", false, "s", new(1), true, true, true, observed));
        Assert.False(front.Missed);
        Assert.Empty(front.Message);
        var simulated = LiveBackend.Activation(new("epoch", true, "s", new(1), false, false, false, observed));
        Assert.True(simulated.Missed);
        Assert.Equal("模拟宿主没有真实的游戏窗口", simulated.Message);
        Assert.Equal("系统没有把游戏窗口切到前台", LiveBackend.Activation(new("epoch", false, "s", new(1), true, true, false, observed)).Message);
        var busy = LiveBackend.Rejected("resource.busy", "color.search");
        Assert.Equal(("resource.busy", "游戏输入被染色寻色占用"), (busy.ErrorCode, busy.Message));
        Assert.Equal("窗口绑定已变化，请重新选择", LiveBackend.Rejected("input.bindingChanged", null).Message);
        Assert.False(GameWindowActivation.Unbound.Missed);
    }

    [AvaloniaFact]
    public void StartBlockerNamesTheFirstMissingPrecondition()
    {
        CapabilitySnapshot Capability(CapabilityStatus status, string reason) => new("color.search", "color", status, null, null, null, null,
            reason, DateTimeOffset.UnixEpoch, []);
        var session = new GameSession("s", "TW", 1, DateTimeOffset.UnixEpoch, new(500), new(1), null);
        var bound = new GameWindowBinding(session, new(0, 0, 1280, 720), 96, 96, false, true);
        Assert.StartsWith("染色组件缺失", LiveBackend.StartBlocker(Capability(CapabilityStatus.Unavailable, "provider.unconfigured"), bound));
        Assert.StartsWith("染色组件不完整", LiveBackend.StartBlocker(Capability(CapabilityStatus.Unavailable, "component.missing"), bound));
        Assert.StartsWith("未绑定游戏窗口", LiveBackend.StartBlocker(Capability(CapabilityStatus.Available, "available"), null));
        Assert.StartsWith("游戏窗口已最小化", LiveBackend.StartBlocker(Capability(CapabilityStatus.Available, "available"),
            bound with { IsMinimized = true, CanAcceptInput = false }));
        Assert.Null(LiveBackend.StartBlocker(Capability(CapabilityStatus.Available, "available"), bound));
    }

    /// <summary>运行栏状态：引擎发出的阶段码都有中文名，等待与重试给出指引，各种结束情形给出结论、原因与下一步。</summary>
    /// <summary>寻色期间只排除与游戏客户区相交的窗口：跨显示器、负坐标、部分相交算相交，相邻或分离不算。</summary>
    [Fact]
    public void OnlyWindowsCoveringTheGameClientAreExcludedFromCapture()
    {
        var game = new Avalonia.PixelRect(-1920, 200, 1600, 900); // 主屏左侧副屏上的游戏
        Assert.True(CaptureExclusion.Overlaps(new(-400, 1000, 800, 600), game));   // 跨两块显示器、部分相交
        Assert.True(CaptureExclusion.Overlaps(new(-2000, 100, 300, 200), game));   // 负坐标、盖住左上角
        Assert.False(CaptureExclusion.Overlaps(new(-320, 200, 800, 600), game));   // 紧挨右边缘
        Assert.False(CaptureExclusion.Overlaps(new(0, 0, 1920, 1080), game));      // 主屏上的主窗口
    }

    // 文案随界面语言变化：断言界面文字的用例放在界面线程上，与切换语言的用例依次执行，不在线程池上并行读到别的语言。
    [AvaloniaFact]
    public void RunStatusNamesEveryStageAndExplainsEachEnding()
    {
        var session = new GameSession("s", "TW", 1, DateTimeOffset.UnixEpoch, new(500), new(1), null);
        var profile = new ColorProfile("p", 1, "方案", ColorStrategy.Precise,
            [new ColorRegion("r0", "#202020", [], ColorMatchMode.ExactHex, 8, 1, true), new ColorRegion("r1", "#303030", [], ColorMatchMode.Similar, 8, 2, true)], new(1));
        var at = DateTimeOffset.UnixEpoch;
        ColorRun Run(string phase) => new("run", "op", session, profile, phase, 0, null, null, null, null, null);
        ColorRunProgress Stage(string stage) => new(stage, null, null, 0, at, null, 0, at);

        foreach (var stage in new[] { "waiting", "discovery", "native_validate", "binding", "capture", "search", "observe",
                     "position", "restore", "verify", "refinement" })
            Assert.NotEqual(stage, ColorStages.Name(stage));
        Assert.Equal("未知阶段", ColorRunText.Describe(Run("new_stage") with { Progress = Stage("new_stage") }).Title);

        // 引擎说明在说明行显示短句，全文进悬停提示。
        var waiting = ColorRunText.Describe(Run("waiting") with { Progress = Stage("waiting") with { Message = "寻色已就绪，请进入染色界面；工具正在等待活动色板。F9 可停止。" } });
        Assert.Equal(("等待染色界面", ColorTone.Live), (waiting.Title, waiting.Tone));
        Assert.Equal("请进入染色界面", waiting.Detail);
        Assert.Contains("工具正在等待活动色板", waiting.Tip);
        Assert.Contains("请在游戏内进入染色", ColorRunText.Describe(Run("waiting") with { Progress = Stage("waiting") }).Detail);

        // 截图寻色出一局结果后等待重试：运行仍在进行，说明不能重试的原因与当前实测。
        var reading = new ColorRunReading(new Dictionary<string, string?> { ["r0"] = "#202020", ["r1"] = "#313131" },
            new Dictionary<string, double?> { ["r0"] = 0, ["r1"] = 1.5 });
        var retry = Run("waiting-user") with
        {
            Result = new("run", 2, "compromise", "time_used", null, null, null),
            Progress = Stage("refinement") with { Status = "retry-waiting", Reason = "insufficient_retry_time", Reading = reading }
        };
        Assert.True(ColorRunText.IsActive(retry));
        var waitingRetry = ColorRunText.Describe(retry);
        Assert.Equal("剩余时间不够重试", waitingRetry.Title);
        Assert.Contains("区域 2  #313131 · ΔE 1.50", waitingRetry.Detail);
        Assert.Contains("已保留当前颜色", waitingRetry.Detail);

        var denied = ColorRunText.Describe(Run("failed") with
        {
            StopReason = "unavailable",
            Result = new ColorRunResult("run", 0, "stopped", "unavailable", null, null, null) { StopDetail = "process_access", FailedStage = "game_build" }
        });
        Assert.Equal(("无法读取游戏进程", ColorTone.Warn), (denied.Title, denied.Tone));
        Assert.Equal("以相同权限运行 Moongate", denied.Detail);
        Assert.Contains("以管理员身份运行", denied.Tip);
        Assert.EndsWith("停在：核对游戏版本", denied.Tip);

        // 精准寻色的跨机原因：结论、做法与涉及的区域，不出现原因码。
        ColorRunStatus Ended(string phase, string stop, string detail) => ColorRunText.Describe(Run(phase) with
        {
            StopReason = stop, Result = new ColorRunResult("run", 0, "stopped", stop, null, null, null) { StopDetail = detail }
        });
        var mismatch = Ended("failed", "initial_visual_timeout", "hex_mismatch.2");
        Assert.Equal(("色码核对不一致", "关闭画面滤镜后重试"), (mismatch.Title, mismatch.Detail));
        Assert.Contains("区域 2", mismatch.Tip);
        Assert.Equal("显示缩放导致画面尺寸不一致", Ended("failed", "observation_failed", "display_scale").Title);
        var engineUpdated = Ended("failed", "unavailable", "unity_engine");
        Assert.Equal(("游戏引擎已更新", "可改用截图寻色"), (engineUpdated.Title, engineUpdated.Detail));
        Assert.Contains("请改为窗口化或把分辨率设为与显示器一致", Ended("failed", "unavailable", "render_scale").Tip);
        Assert.Equal("文字识别读不到路径", Ended("failed", "unavailable", "ocr.pathUnreadable").Title);
        Assert.Equal("缺少 Windows 媒体功能包", ColorRunText.Describe(Run("failed") with { StopReason = "color.mediaFeaturesMissing" }).Title);
        var covered = ColorRunText.Describe(Run("native_validate") with { Progress = Stage("native_validate") with { Reason = "occluded" } });
        Assert.Equal("移开遮挡窗口后重试", covered.Detail);
        Assert.Contains("色卡被其他窗口遮挡", covered.Tip);
        foreach (var status in new[] { mismatch, engineUpdated, covered })
            Assert.DoesNotMatch("[A-Za-z]{3,}_[a-z]", status.Title + status.Detail + status.Tip);

        var palette = ColorRunText.Describe(Run("failed") with
        {
            StopReason = "no_active_palette_timeout", Result = new ColorRunResult("run", 0, "stopped", "no_active_palette_timeout", null, null, null)
        });
        Assert.Equal("等待染色界面超时", palette.Title);
        Assert.Equal("游戏失去焦点，已停止", ColorRunText.Describe(Run("interrupted") with { StopReason = "input.focusChanged" }).Title);
        Assert.Equal("界面失联，寻色已停止", ColorRunText.Describe(Run("cancelled") with { StopReason = "frontend-lost" }).Title);
        Assert.Equal("已停止寻色", ColorRunText.Describe(Run("cancelled") with { StopReason = "user" }).Title);
        var port = ColorRunText.Describe(Run("failed") with { StopReason = "worker.portFailed" });
        Assert.Equal("与染色引擎的连接中断", port.Title);
        Assert.Contains("组件请求游戏失败", port.Tip);
        var unknown = ColorRunText.Describe(Run("failed") with { StopReason = "worker.newCode" });
        Assert.Contains("未知原因", unknown.Tip);
        Assert.DoesNotContain("worker.newCode", unknown.Title + unknown.Detail + unknown.Tip);
    }

    /// <summary>
    /// 区域卡与浮窗取同一份实测：进行中取引擎最新读数；结束时没有复核当前颜色时取上一次实测并标“上次实测（当前未确认）”，
    /// 最佳实测不作为当前或实测颜色（实机：截图寻色色板结束，最佳 #000101 未能恢复，游戏停在 #443035）。
    /// </summary>
    [AvaloniaFact]
    public void ReadingsShowTheEngineReadOfTheCurrentScreenNotTheBestMeasurement()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var session = new GameSession("s", "TW", 1, DateTimeOffset.UnixEpoch, new(500), new(1), null);
        var profile = new ColorProfile("p", 1, "方案", ColorStrategy.Screenshot,
        [
            new ColorRegion("r0", "#000000", [], ColorMatchMode.ExactHex, 8, 1, true),
            new ColorRegion("r1", "#000000", [], ColorMatchMode.ExactHex, 8, 2, true)
        ], new(1));
        var source = new ObservationSource("sample", "1", "TW", null);
        ColorMeasurement Measured(string a, string b, double da, double db) => new(new Dictionary<string, string> { ["r0"] = a, ["r1"] = b },
            DateTimeOffset.UnixEpoch, source, new(1), ObservationQuality.Confirmed)
        {
            DeltaByRegion = new Dictionary<string, double?> { ["r0"] = da, ["r1"] = db },
            Regions = [new("r0", ColorRegionState.Unmet), new("r1", ColorRegionState.Unmet)]
        };
        var best = Measured("#000101", "#000004", 0.35, 1.6);
        var last = Measured("#443035", "#00000A", 24.38, 4.12);
        var ended = new ColorRun("run", "op", session, profile, "interrupted", 0, null, best, last, null, "palette_ended")
        {
            Result = new ColorRunResult("run", 2, "unverified", "palette_ended", best, last, null) { BestCurrent = false }
        };
        var (readings, accepted) = LiveBackend.Readings(profile, ended, false);
        Assert.Equal(((string?)null, "#443035", (double?)24.38, true), (readings["r0"].CurrentHex, readings["r0"].MeasuredHex, readings["r0"].MeasuredDelta, readings["r0"].Unconfirmed));
        Assert.Equal("上次实测（当前未确认）", readings["r0"].MeasuredLabel);
        Assert.Null(accepted);
        Assert.DoesNotContain(readings.Values, reading => reading.CurrentHex == "#000101" || reading.MeasuredHex == "#000101");

        var at = DateTimeOffset.UnixEpoch;
        var running = ended with
        {
            Phase = "position", Result = null,
            Progress = new ColorRunProgress("position", null, null, 0, at, null, 0, at)
            {
                Reading = new ColorRunReading(new Dictionary<string, string?> { ["r0"] = "#443035", ["r1"] = null },
                    new Dictionary<string, double?> { ["r0"] = 24.38, ["r1"] = null }) { Regions = [new("r0", ColorRegionState.Unmet), new("r1", ColorRegionState.Unread)] }
            }
        };
        var (live, _) = LiveBackend.Readings(profile, running, true);
        Assert.Equal(("#443035", "#443035", "未达标"), (live["r0"].CurrentHex, live["r0"].MeasuredHex, live["r0"].Mark!.Text));
        Assert.Equal(((string?)null, "未读取"), (live["r1"].MeasuredHex, live["r1"].Mark!.Text));
    }

    [AvaloniaFact]
    public void PresetsSaveLoadAndDeleteThroughTheBackend()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            Click(window, Named<Button>(window, "PresetsButton"));
            Assert.True(model.IsPresetsOpen);
            Assert.False(Named<Button>(window, "SaveProfileButton").IsEnabled);
            model.ProfileName = "冬夜禮服";
            LayoutProbe.Settle();
            ClickInPlace(window, Named<Button>(window, "SaveProfileButton"));
            Assert.Contains("resource.conflict", model.PresetMessage);
            Assert.Equal("冬夜禮服", model.ProfileName);

            model.ProfileName = "新的方案";
            ClickInPlace(window, Named<Button>(window, "SaveProfileButton"));
            Assert.Equal(("color.profile-save", "新的方案"), backend.UiIntents[^1]);
            Assert.Contains(model.Data.Profiles, row => row.Name == "新的方案");

            model.LoadProfileCommand.Execute("uniform");
            LayoutProbe.Settle();
            Assert.False(model.IsLayerOpen);
            Assert.Equal("#F4F1EA", model.Cards[0].TargetHex);
            Assert.False(model.Cards[2].Enabled);

            model.DeleteProfileCommand.Execute("lute");
            LayoutProbe.Settle();
            Assert.DoesNotContain(model.Data.Profiles, row => row.Id == "lute");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ProfileRowsPreviewEnabledTargetsWithAlternativeCounts()
    {
        var row = LiveBackend.Row(new ColorProfile("p", 1, "冬夜禮服", ColorStrategy.Precise,
        [
            new("r1", "#1F3A6B", [], ColorMatchMode.ExactHex, 3, 1, true),
            new("r2", "#F2E9D8", ["#E6DCC2", "#D9CDB0"], ColorMatchMode.Similar, 3, 2, true),
            new("r3", "#8C2F45", ["#A8455A"], ColorMatchMode.Similar, 3, 3, false)
        ], new(1)));
        Assert.Equal([("#1F3A6B", 0), ("#F2E9D8", 2)], row.Swatches.Select(swatch => (swatch.Hex, swatch.Alternatives)));
        Assert.Equal("+2", row.Swatches[1].AlternativesText);
        Assert.Equal("#1F3A6B, #F2E9D8", row.Tip);
    }

    [AvaloniaFact]
    public void HistoryRowsShowTargetsMeasuredColorsDeltasAndJudgements()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var completed = new DateTimeOffset(2026, 11, 10, 13, 5, 0, TimeSpan.Zero);
        var profile = new ColorProfile("p", 1, "冬夜禮服", ColorStrategy.Precise,
        [
            new("r1", "#1F3A6B", [], ColorMatchMode.ExactHex, 3, 1, true),
            new("r2", "#F2E9D8", [], ColorMatchMode.Similar, 3, 2, false),
            new("r3", "#8C2F45", [], ColorMatchMode.Similar, 3, 3, true)
        ], new(1));
        var source = new ObservationSource("sample", "1", "TW", null);
        var best = new ColorMeasurement(new Dictionary<string, string> { ["r1"] = "#213D70", ["r3"] = "#A8455A" }, completed, source, new(1),
            ObservationQuality.Confirmed)
        {
            DeltaByRegion = new Dictionary<string, double?> { ["r1"] = 1.44, ["r3"] = 3.56 },
            Accepted = false,
            Regions = [new("r1", ColorRegionState.Met), new("r3", ColorRegionState.Unmet)]
        };
        // 结束时复核过的游戏颜色优先于最佳实测。
        var verified = best with { HexByRegion = new Dictionary<string, string> { ["r1"] = "#203B6D", ["r3"] = "#96384D" } };
        var time = completed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var row = ColorHistoryMapper.Map(new("run", "op-1", "owner", OperationState.Succeeded, completed, profile,
            new("run", 3, "compromise", null, best, null, verified), best, null));
        Assert.Equal(("op-1", time, "妥协结果", (bool?)false), (row.Id, row.Time, row.Result, row.Accepted));
        Assert.Equal(["#1F3A6B", "#F2E9D8", "#8C2F45"], row.Regions.Select(region => region.TargetHex));
        Assert.Equal(["#203B6D", null, "#96384D"], row.Regions.Select(region => region.MeasuredHex));
        Assert.Equal(["ΔE 1.44", "", "ΔE 3.56"], row.Regions.Select(region => region.Delta));
        Assert.Equal(["达标", "未启用", "未达标"], row.Regions.Select(region => region.Mark.Text));
        Assert.True(row.Regions[0].Mark.Good);
        Assert.True(row.Regions[2].Mark.Warn);

        // 没有实测：只有目标色，判定为未开始。
        var cancelled = ColorHistoryMapper.Map(new("run", "op-2", "owner", OperationState.Cancelled, completed, profile, null, null, null));
        Assert.Equal(("已取消", (bool?)null), (cancelled.Result, cancelled.Accepted));
        Assert.All(cancelled.Regions, region => Assert.Null(region.MeasuredHex));
        Assert.Equal("未开始", cancelled.Regions[0].Mark.Text);
        // 未登记的状态只显示中文概括，原码进诊断页的待补清单。
        Assert.Equal("未知状态", ColorHistoryMapper.State(new OpenEnum<OperationState>("expired")));
        Assert.Contains("state:expired", CodeText.Unmapped);
    }

    [AvaloniaFact]
    public void HistoryShowsTheBackendRowsAndEmptyState()
    {
        var backend = new SampleBackend();
        var (window, model) = Open(backend);
        try
        {
            Assert.Empty(backend.ColorHistoryQueries);
            ClickInPlace(window, Named<Button>(window, "RecordsButton"));
            Assert.Equal(new string?[] { null }, backend.ColorHistoryQueries);
            Assert.Equal(23, model.History.Rows.Count);
            Assert.True(Named<ItemsControl>(window, "HistoryList").IsEffectivelyVisible);
        }
        finally { window.Close(); }

        var (empty, _) = Open(new SampleBackend { EmptyColorHistory = true });
        try
        {
            ClickInPlace(empty, Named<Button>(empty, "RecordsButton"));
            Assert.True(Named<TextBlock>(empty, "HistoryEmpty").IsEffectivelyVisible);
            Assert.False(Named<ScrollViewer>(empty, "HistoryScroller").IsVisible);
        }
        finally { empty.Close(); }
    }

    [AvaloniaFact]
    public void RunningSearchDisablesCardsAndOnlyOtherCandidatesCanBeSelected()
    {
        var backend = new SampleBackend { ColorRunning = true };
        var (window, model) = Open(backend);
        try
        {
            Assert.False(model.CanEdit);
            Assert.False(model.CanPick);
            Assert.All(Named<ItemsControl>(window, "CardList").GetVisualDescendants().OfType<Card>(), card => Assert.False(card.IsEffectivelyEnabled));
            Assert.Equal(3, Named<ItemsControl>(window, "CardList").GetVisualDescendants().OfType<Card>().Count());
            var current = window.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == "color.select:c1");
            Assert.False(current.IsEnabled);
            Click(window, window.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == "color.select:c2"));
            Assert.Equal(("color.select", "c2"), backend.UiIntents[^1]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ProgressRemainingTimeFollowsTheHostAnchorAndTheLocalClock()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var updated = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var progress = LiveBackend.Progress("run", new("search", 3, 8, 1_000, updated.AddSeconds(-20), 91_000, 31_000, updated),
            updated.AddSeconds(-50));
        // 剩余 = deadline − updated − (现在 − updatedAt)：91000 − 31000 − 15000 = 45000 ms。
        Assert.Equal(TimeSpan.FromSeconds(45), progress.Remaining(updated.AddSeconds(15)));
        Assert.Equal("剩余 00:45", progress.RemainingText(updated.AddSeconds(15)));
        Assert.False(progress.Low(updated.AddSeconds(15)));
        Assert.True(progress.Low(updated.AddSeconds(50)));
        Assert.Equal(TimeSpan.Zero, progress.Remaining(updated.AddSeconds(75)));
        Assert.Equal("3/8", progress.Count);
        Assert.Equal("已用 65 秒 · 本阶段 35 秒", progress.ElapsedText(updated.AddSeconds(15)));

        var open = LiveBackend.Progress("run", new("waiting", null, null, 0, updated, null, 0, updated), updated);
        Assert.Null(open.Remaining(updated));
        Assert.Empty(open.RemainingText(updated));
        Assert.Empty(open.Count);
    }

    [AvaloniaFact]
    public void CandidateCardsUseTheBackendSummaryAndRegionJudgements()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var profile = new ColorProfile("draft", 1, "当前草稿", ColorStrategy.Screenshot,
        [
            new("r1", "#1F3A6B", [], ColorMatchMode.Similar, 3, 1, true),
            new("r2", "#F2E9D8", [], ColorMatchMode.ExactHex, 3, 2, true)
        ], new(1));
        ColorCandidate Candidate(string id, bool predicted, bool current, bool available, bool accepted) =>
            new(id, 0, new Dictionary<string, string?> { ["r1"] = "#213D70", ["r2"] = "#F2E9D8" },
                new Dictionary<string, double?> { ["r1"] = 1.5, ["r2"] = 0 }, predicted, available, current, accepted, false)
            {
                Regions = [new("r1", ColorRegionState.Unmet), new("r2", ColorRegionState.Met)],
                MaximumDelta = 4.25, AverageDelta = 2.5, ExactMatches = 1, ExactTotal = 1, FamilyConsistent = false
            };
        var batch = new ColorCandidateBatch("b", [Candidate("a", false, true, true, true),
            Candidate("b", true, false, true, false) with { ExactTotal = 0, FamilyConsistent = true, MaximumDelta = null },
            Candidate("c", true, false, false, true)], "a", DateTimeOffset.UnixEpoch, new(new(1), "b", true, false));
        var run = new ColorRun("run", "op", new GameSession("s", "TW", 1, DateTimeOffset.UnixEpoch, new(1), new(1), null), profile, "search", 0, "b",
            null, null, null, null);
        var cards = LiveBackend.Candidates(run, batch);
        Assert.Equal(["当前方案 · 游戏实测", "候选方案 · 预测颜色", "候选方案 · 预测颜色"], cards.Select(card => card.Title));
        Assert.Equal(["当前方案（实测）", "选择此方案", "不可用"], cards.Select(card => card.ActionLabel));
        Assert.Equal([false, true, false], cards.Select(card => card.CanSelect));
        // 最大与平均取后端值（不是由各区域 1.5 与 0 自行汇总）。
        Assert.Equal("最大 4.25 / 平均 2.50 · 实测达标 · 精准命中 1/1 · 存在色系偏离", cards[0].Summary);
        Assert.Equal("妥协方案", cards[1].Summary);
        Assert.Equal("ΔE 1.50", cards[1].Regions[0].Delta);
        Assert.Equal(["未达标", "达标"], cards[0].Regions.Select(region => region.Badge.Text));
    }

    [AvaloniaFact]
    public void RegionCardsStayInTheGridAndAreDisabledWhileTheBackendIsNotConnected()
    {
        // 首次运行没有保存的卡片布局，后端也没有连接（发现文件不存在，不启动连接循环）。
        CardStateStore.Restore(null);
        var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "runtime", "instance.json"),
            action => action());
        var window = LayoutProbe.Open(backend, 1280, 860, "color");
        try
        {
            var model = (ColorPageModel)Page<ColorPage>(window).DataContext!;
            var cards = window.GetVisualDescendants().OfType<Card>().Where(card => card.IsEffectivelyVisible).ToList();
            Assert.Equal(["region-1", "region-2", "region-3"], cards.Select(card => card.CardId));
            Assert.All(cards, card =>
            {
                Assert.False(card.IsEffectivelyEnabled);
                Assert.True(card.Bounds.Height > 100);
            });
            Assert.All(model.Cards, card => Assert.Equal("未读取", card.StatusText));
            Assert.Contains("未连接后端", Named<TextBlock>(window, "NoDraftNote").Text);
            Assert.False(Named<Button>(window, "StartButton").IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }
}
