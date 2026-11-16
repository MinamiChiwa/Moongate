using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Overlay;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.Desktop.Pages.Settings;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>主题令牌与对比度、深浅色切换、模块色调、偏好文件，以及设置页的动效开关。</summary>
public sealed class ThemeAndMotionTests
{
    private static readonly string[] TextBrushes = ["InkBrush", "MutedBrush", "ConfirmedBrush", "UnknownBrush"];

    /// <summary>
    /// 面板叠在窗口底色上，内嵌块再叠在面板上；四种文字色与叠出来的实际颜色比较，都不低于 4.5:1。
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TextColorsKeepContrastOnTheirSurfaces(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var panel = Over(Token(variant, "PanelBrush"), Token(variant, "CanvasBrush"));
        var low = new List<string>();
        foreach (var (surface, background) in new[] { ("PanelBrush", panel), ("InsetBrush", Over(Token(variant, "InsetBrush"), panel)) })
            foreach (var text in TextBrushes)
                if (Contrast(Token(variant, text), background) is var ratio && ratio < 4.5)
                    low.Add($"{text} 在 {surface} 上 {ratio:F2}:1");
        Assert.True(low.Count == 0, $"{theme} 对比度低于 4.5:1：{string.Join("；", low)}");
    }

    /// <summary>弹出层底色不透明，文字色与它（以及叠在它上面的内嵌块）的对比度同样不低于 4.5:1。</summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TextColorsKeepContrastOnPopups(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var popup = Token(variant, "PopupBrush");
        Assert.Equal(255, popup.A);
        var low = new List<string>();
        foreach (var (surface, background) in new[] { ("弹出层", popup), ("弹出层内嵌块", Over(Token(variant, "InsetBrush"), popup)) })
            foreach (var text in TextBrushes)
                if (Contrast(Token(variant, text), background) is var ratio && ratio < 4.5)
                    low.Add($"{text} 在{surface}上 {ratio:F2}:1");
        Assert.True(low.Count == 0, $"{theme} 对比度低于 4.5:1：{string.Join("；", low)}");
    }

    [AvaloniaFact]
    public void BothThemesDefineTheSameTokens()
    {
        var tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://MabiTool.Desktop/Theme/Tokens.axaml"));
        var light = (ResourceDictionary)tokens.ThemeDictionaries[ThemeVariant.Light];
        var dark = (ResourceDictionary)tokens.ThemeDictionaries[ThemeVariant.Dark];
        var lightKeys = light.Keys.Select(key => key.ToString()).Order().ToList();
        Assert.Equal(lightKeys, dark.Keys.Select(key => key.ToString()).Order().ToList());
        Assert.Contains("OnInkBrush", lightKeys);
    }

    [AvaloniaFact]
    public void SettingsSwitchesThemeWithoutRebuildingThePage()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 760, 600, "shell.settings");
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
            var scroller = page.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PageScroller");
            scroller.Offset = new Vector(0, 120);
            LayoutProbe.Settle();
            var offset = scroller.Offset;
            Assert.True(offset.Y > 0);
            var title = page.GetVisualDescendants().OfType<TextBlock>().First(block => block.Classes.Contains("h1"));
            var halo = shell.ModuleHaloBrush;
            Assert.Equal(Token(ThemeVariant.Light, "InkBrush"), ColorOf(title.Foreground));

            Press(Named(page, "ThemeDarkButton"));
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal(UiThemeChoice.Dark, UiTheme.Choice);
            Assert.Same(page, window.GetVisualDescendants().OfType<SettingsPage>().Single());
            Assert.Equal(offset, scroller.Offset);
            Assert.Equal(Token(ThemeVariant.Dark, "InkBrush"), ColorOf(title.Foreground));
            Assert.NotSame(halo, shell.ModuleHaloBrush);
            Assert.Contains("active", Named(page, "ThemeDarkButton").Classes);
            LayoutProbe.SaveScreenshot(window, "shell.settings-theme-switched-760x600-dark");

            Press(Named(page, "ThemeSystemButton"));
            Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);
            Assert.Equal(UiThemeChoice.System, UiTheme.Choice);

            Press(Named(page, "ThemeLightButton"));
            Assert.Equal(ThemeVariant.Light, Application.Current.RequestedThemeVariant);
            Assert.Equal(Token(ThemeVariant.Light, "InkBrush"), ColorOf(title.Foreground));
        }
        finally
        {
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    [AvaloniaFact]
    public void ModuleToneUsedAsTextIsLightenedInDarkTheme()
    {
        var catalog = BuiltInModules.CreateCatalog(new SampleBackend());
        var combat = catalog.Modules.Single(module => module.Id == "combat-observation");
        Assert.Equal(combat.Accent, ColorOf(combat.TextFor(dark: false)));
        Assert.Equal(UiTheme.Mix(combat.Accent, Colors.White, 0.45), ColorOf(combat.TextFor(dark: true)));
        // 背景主光晕：浅色随模块色调 60%；深色下各模块相同，是固定的 #3F6BFF 38%。
        var color = catalog.Modules.Single(module => module.Id == "color");
        var lightStop = ((RadialGradientBrush)combat.HaloFor(dark: false)).GradientStops[0].Color;
        Assert.Equal(Color.FromArgb(0x99, combat.Accent.R, combat.Accent.G, combat.Accent.B), lightStop);
        Assert.NotEqual(lightStop, ((RadialGradientBrush)color.HaloFor(dark: false)).GradientStops[0].Color);
        foreach (var module in catalog.Modules)
            Assert.Same(combat.HaloFor(dark: true), module.HaloFor(dark: true));
        var dark = (RadialGradientBrush)combat.HaloFor(dark: true);
        Assert.Equal(Color.Parse("#613F6BFF"), dark.GradientStops[0].Color);
        Assert.Equal(0, dark.GradientStops[^1].Color.A);

        // 深色的两处环境光晕也是冷色：右上 #35B6FF 20%，底部 #6E5BFF 18%。
        Assert.Equal(Color.Parse("#3335B6FF"), StopOf(ThemeVariant.Dark, "AmberHaloBrush"));
        Assert.Equal(Color.Parse("#2E6E5BFF"), StopOf(ThemeVariant.Dark, "BlueHaloBrush"));
        Assert.Equal(Color.Parse("#66FFBE50"), StopOf(ThemeVariant.Light, "AmberHaloBrush"));
    }

    private static Color StopOf(ThemeVariant variant, string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value));
        return ((RadialGradientBrush)value!).GradientStops[0].Color;
    }

    [AvaloniaFact]
    public void WindowThemeButtonTogglesThemeWithoutRebuildingThePage()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.settings");
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
            Button Visible(string name) => window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

            // 浅色时显示月亮（切换为深色）。
            Assert.True(Visible("ThemeToDarkButton").IsVisible);
            Assert.False(Visible("ThemeToLightButton").IsVisible);
            Press(Visible("ThemeToDarkButton"));
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal(UiThemeChoice.Dark, UiTheme.Choice);
            Assert.Same(page, window.GetVisualDescendants().OfType<SettingsPage>().Single());
            Assert.Contains("active", Named(page, "ThemeDarkButton").Classes);
            Assert.True(Visible("ThemeToLightButton").IsVisible);
            Assert.Same(UiTheme.DarkMainHalo, shell.ModuleHaloBrush);

            Press(Visible("ThemeToLightButton"));
            Assert.Equal(UiThemeChoice.Light, UiTheme.Choice);
            Assert.Contains("active", Named(page, "ThemeLightButton").Classes);
            Assert.DoesNotContain("active", Named(page, "ThemeDarkButton").Classes);
        }
        finally
        {
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    [AvaloniaFact]
    public void WindowThemeButtonFromFollowSystemSwitchesToTheOpposite()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.settings", theme: UiThemeChoice.System);
        try
        {
            var wasDark = UiTheme.IsDark;
            var name = wasDark ? "ThemeToLightButton" : "ThemeToDarkButton";
            Press(window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name));
            Assert.Equal(wasDark ? UiThemeChoice.Light : UiThemeChoice.Dark, UiTheme.Choice);
            Assert.Equal(!wasDark, UiTheme.IsDark);
            var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
            Assert.DoesNotContain("active", Named(page, "ThemeSystemButton").Classes);
        }
        finally
        {
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    [Fact]
    public void PreferencesRoundTripThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "moongate-preferences-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Equal(DesktopPreferences.Default, new DesktopPreferenceStore(path).Load());
            var chosen = new DesktopPreferences(UiLanguage.TraditionalChinese, UiThemeChoice.Dark, false);
            new DesktopPreferenceStore(path).Save(chosen);
            Assert.Equal(chosen, new DesktopPreferenceStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"language\": \"Klingon\", \"theme\": 42, \"motion\": \"yes\"}")]
    public void DamagedPreferencesFallBackToDefaults(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "moongate-preferences-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, content);
            Assert.Equal(DesktopPreferences.Default, new DesktopPreferenceStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnrecognizedPreferenceFallsBackOnlyForThatItem()
    {
        var path = Path.Combine(Path.GetTempPath(), "moongate-preferences-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"language\": \"TraditionalChinese\", \"theme\": 42, \"motion\": false}");
            Assert.Equal(new DesktopPreferences(UiLanguage.TraditionalChinese, UiThemeChoice.System, false),
                new DesktopPreferenceStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Color Token(ThemeVariant variant, string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), $"{variant} 下没有 {key}");
        return ColorOf(value as IBrush);
    }

    private static Color ColorOf(IBrush? brush) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>把半透明的前景色叠在不透明的底色上。</summary>
    private static Color Over(Color top, Color bottom)
    {
        var alpha = top.A / 255.0;
        byte Channel(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromRgb(Channel(top.R, bottom.R), Channel(top.G, bottom.G), Channel(top.B, bottom.B));
    }

    private static Button Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

    // 直接执行按钮绑定的命令：点击前把按钮滚入视野会改变滚动位置，而这里要核对滚动位置保持不变。
    private static void Press(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        button.Command!.Execute(button.CommandParameter);
        LayoutProbe.Settle();
    }

    [AvaloniaFact]
    public void MotionSwitchInSettingsTurnsTransitionsOnAndOff()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.settings");
        var host = window.GetVisualDescendants().OfType<PageHost>().Single();
        Assert.Null(host.PageTransition);

        var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
        Button Named(string name) => page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        Named("MotionOnButton").Command!.Execute(null);
        LayoutProbe.Settle();
        Assert.True(UiMotion.Enabled);
        Assert.NotNull(host.PageTransition);
        Assert.Contains("active", Named("MotionOnButton").Classes);

        Named("MotionOffButton").Command!.Execute(null);
        LayoutProbe.Settle();
        Assert.False(UiMotion.Enabled);
        Assert.Null(host.PageTransition);
        window.Close();
    }
}
