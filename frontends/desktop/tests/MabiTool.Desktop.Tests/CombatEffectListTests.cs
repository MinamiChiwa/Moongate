using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Pages.Combat;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>效果条目：同名合并、收藏优先与出现顺序、倒计时进度与续期、本地递减归零后移除、卡片高度固定与内容滚动。</summary>
public sealed class CombatEffectListTests
{
    private static readonly EntityKey Local = new("game", new(1), new(42), null);
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    // 本机时钟刻度：从一个非零起点算起（0 表示没有读取时刻）。
    private static long Ms(double value) => Ticks(100_000 + value);
    private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private static EffectRow Row(string id, double? remaining, long at, bool infinite = false) =>
        new(string.Empty, "效果" + id, string.Empty, string.Empty, string.Empty, id, string.Empty, EffectPolarity.Positive,
            RemainingMs: remaining, ObservedAt: at, Infinite: infinite, Scope: "self");

    [AvaloniaFact]
    public void SameEffectInstancesMergeIntoOneRowWithStacksAndShortestRemaining()
    {
        CombatEffectView View(ulong instance, double remaining) => new(new(Local, new(7001), new(instance), Local, Now, null, EffectDurationKind.Finite,
            1, null, EffectPresence.Observed, ObservationQuality.Confirmed, new(1)), remaining, Now, false, false, null, "buff");
        var effects = new CombatEntityEffects("self", "owner", "obs", new(1), new(Local, true, [], Now, ObservationQuality.Confirmed),
            [View(1, 8000), View(2, 3000)]);
        var status = new CombatObservationStatus("obs", "owner", "game", "observing", new("tw", "1", "TW", "b"), Local, new("game"), new(1), Now,
            Now, 0, ObservationQuality.Confirmed, [], new(0));
        var data = CombatMapper.Map(new(true, true, new CombatWorkspace(status, null, [], effects, new(1)), [], null, null, [], EffectsAt: Ms(1000)));
        var row = Assert.Single(data.SelfEffects);
        Assert.Equal((2, 3000d), (row.Stacks, row.RemainingMs));
        var shown = EffectLine.Describe(row, Ms(1000));
        Assert.Equal(("×2", "3s"), (shown.Stacks, shown.Remaining));
    }

    [AvaloniaFact]
    public void FavoritesComeFirstAndOtherwiseRowsKeepFirstAppearanceOrder()
    {
        CombatEffectFavorites.Use(null); CombatEffectOrder.Reset();
        try
        {
            string[] Order(params EffectRow[] rows) => CombatEffectOrder.Arrange(rows, "self").Select(row => row.EffectId!).ToArray();
            Assert.Equal(["a", "b", "c"], Order(Row("a", 9000, Ms(0)), Row("b", 8000, Ms(0)), Row("c", 7000, Ms(0))));
            // 剩余时间变化不改变顺序。
            Assert.Equal(["a", "b", "c"], Order(Row("a", 9000, Ms(500)), Row("b", 1000, Ms(500)), Row("c", 7000, Ms(500))));
            // 收藏后排到最前；组内仍按出现先后。
            CombatEffectFavorites.Toggle("c");
            Assert.Equal(["c", "a", "b"], Order(Row("a", 9000, Ms(600)), Row("b", 1000, Ms(600)), Row("c", 7000, Ms(600))));
            // b 消失后再出现算新出现，排在末尾。
            Assert.Equal(["c", "a"], Order(Row("a", 9000, Ms(700)), Row("c", 7000, Ms(700))));
            Assert.Equal(["c", "a", "b"], Order(Row("b", 9000, Ms(800)), Row("a", 9000, Ms(800)), Row("c", 7000, Ms(800))));
        }
        finally { CombatEffectFavorites.Use(null); CombatEffectOrder.Reset(); }
    }

    [AvaloniaFact]
    public void ProgressFollowsTheFirstObservedDurationAndRenewalResetsIt()
    {
        CombatEffectFavorites.Use(null); CombatEffectOrder.Reset();
        try
        {
            var first = Assert.Single(CombatEffectOrder.Arrange([Row("a", 10_000, Ms(0))], "self"));
            Assert.Equal(10_000, first.TotalMs);
            Assert.Equal(0.6, CombatEffectOrder.Progress(first, Ms(4000))!.Value, 3);
            // 4 秒后后端仍给出剩余 6 秒：同一次计时，总时长不变。
            var same = Assert.Single(CombatEffectOrder.Arrange([Row("a", 6_000, Ms(4000))], "self"));
            Assert.Equal(10_000, same.TotalMs);
            // 续期：剩余回到 10 秒，总时长按新值重置，进度回满。
            var renewed = Assert.Single(CombatEffectOrder.Arrange([Row("a", 10_000, Ms(4500))], "self"));
            Assert.Equal(10_000, renewed.TotalMs);
            Assert.Equal(1, CombatEffectOrder.Progress(renewed, Ms(4500))!.Value, 3);
            Assert.True(EffectLine.Describe(renewed, Ms(10_000)).Warning);
            Assert.Null(CombatEffectOrder.Progress(Row("b", null, Ms(0), infinite: true), Ms(0)));
        }
        finally { CombatEffectOrder.Reset(); }
    }

    [AvaloniaFact]
    public void CountdownRemovesTheRowWhenItReachesZeroLocally()
    {
        var strip = new EffectStrip(EffectPolarity.Positive);
        var start = Stopwatch.GetTimestamp();
        strip.Sync([Row("a", 60_000, start) with { TotalMs = 60_000 }, Row("b", 1_000, start) with { TotalMs = 1_000 }, Row("c", null, start, infinite: true)]);
        var kept = strip.All[0];
        Assert.Equal(["a", "b", "c"], strip.All.Select(item => item.Row.EffectId));
        Assert.Equal("1s", EffectLine.Describe(strip.All[1].Row, start + Ticks(500)).Remaining);
        Assert.False(strip.Prune(start + Ticks(500)));
        Assert.True(strip.Prune(start + Ticks(1_200)));
        Assert.Equal(["a", "c"], strip.All.Select(item => item.Row.EffectId));
        // 同一效果沿用同一个条目，刷新只更新内容。
        Assert.Same(kept, strip.All[0]);
    }

    [AvaloniaFact]
    public void OverlayListShowsWhatFitsAndCountsTheRestInTheLastRow()
    {
        var panel = new EffectOverflowPanel();
        for (var i = 0; i < 10; i++) panel.Children.Add(new Border { Height = 20 });
        panel.Measure(new Avalonia.Size(200, 85));
        Assert.Equal((7, 80d), (panel.HiddenCount, panel.DesiredSize.Height));
        panel.Measure(new Avalonia.Size(200, 400));
        Assert.Equal((0, 200d), (panel.HiddenCount, panel.DesiredSize.Height));
    }

    [AvaloniaFact]
    public void EffectCardKeepsItsHeightAndContentScrollsInside()
    {
        // 效果增多时卡片高度不变（由网格尺寸档决定），列表在卡片内滚动。
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        try
        {
            ScrollViewer Body() => window.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.Name == "SelfEffectsBody");
            Body().BringIntoView(); LayoutProbe.Render(window);
            var card = window.GetVisualDescendants().OfType<MabiTool.Desktop.Controls.Card>().Single(panel => panel.Name == "SelfEffectsPanel");
            var (height, bounds) = (Body().Bounds.Height, card.Bounds);
            backend.ExtraSelfEffects = 20;
            backend.RaiseChanged();
            LayoutProbe.Render(window);
            Assert.Equal(height, Body().Bounds.Height);
            Assert.Equal(bounds, card.Bounds);
            Assert.True(Body().Extent.Height > Body().Viewport.Height);
        }
        finally { window.Close(); }
    }
}
