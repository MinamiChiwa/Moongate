using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

/// <summary>图标动效与素材包：预设、内建图标、图标与素材槽的替换和拒绝规则、窗口背景与遮罩、素材包的导入与记忆。</summary>
public sealed class ThemeAndMotionAssetTests
{
    [Fact]
    public void EveryPresetStartsAndEndsAtRest()
    {
        foreach (var preset in IconMotion.Presets)
        {
            Assert.Equal(Matrix.Identity, IconMotion.Transform(preset, 0, 30));
            Assert.Equal(Matrix.Identity, IconMotion.Transform(preset, 1, 30));
            if (preset != IconMotion.None) Assert.NotEqual(Matrix.Identity, IconMotion.Transform(preset, 0.3, 30));
        }
        Assert.Equal(IconMotion.None, IconMotion.Normalize("teleport"));
        Assert.Equal(IconMotion.None, IconMotion.Normalize(null));
        Assert.Equal("bounce", IconMotion.Normalize("bounce"));
    }

    [AvaloniaFact]
    public void BuiltInSlotsHaveVectorIconsAndUnknownSlotsFallBack()
    {
        IconRegistry.Reset();
        foreach (var module in BuiltInModules.CreateCatalog(new SampleBackend()).Modules)
        {
            var spec = IconRegistry.Resolve(module.IconSlot);
            Assert.NotNull(spec?.Vector);
            Assert.NotEqual(IconMotion.None, spec.HoverMotion);
        }
        Assert.Null(IconRegistry.Resolve("module.sample.fishing"));
    }

    [AvaloniaFact]
    public void AssetPackReplacesImageAndMotionAndRejectsUnsafeEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "moongate-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var bitmap = new RenderTargetBitmap(new PixelSize(32, 32)))
                bitmap.Save(Path.Combine(directory, "note.png"), new PngBitmapEncoderOptions());
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "not an image");
            File.WriteAllText(Path.Combine(directory, "pack.json"), """
                {
                  "name": "测试素材包",
                  "icons": {
                    "module.music-library": { "image": "note.png", "hover": "spin", "selected": "pulse" },
                    "module.color": { "hover": "shake" },
                    "module.rooms": { "image": "../outside.png" },
                    "module.inventory": { "image": "notes.txt" },
                    "module.sample.fishing": { "image": "missing.png", "hover": "teleport" }
                  }
                }
                """);

            var problems = IconRegistry.LoadPack(directory);
            Assert.Equal("测试素材包", IconRegistry.PackName);

            var music = IconRegistry.Resolve("module.music-library")!;
            Assert.NotNull(music.Image);
            Assert.Equal(("spin", "pulse"), (music.HoverMotion, music.ActiveMotion));

            // 只换动效时保留内建图标。
            var color = IconRegistry.Resolve("module.color")!;
            Assert.NotNull(color.Vector);
            Assert.Equal("shake", color.HoverMotion);

            // 包目录之外的路径和不支持的格式被拒绝，内建图标照常使用。
            Assert.Null(IconRegistry.Resolve("module.rooms")!.Image);
            Assert.Null(IconRegistry.Resolve("module.inventory")!.Image);
            Assert.Contains(problems, problem => problem.StartsWith("module.rooms"));
            Assert.Contains(problems, problem => problem.StartsWith("module.inventory"));
            Assert.Contains(problems, problem => problem.StartsWith("module.sample.fishing"));

            IconRegistry.Reset();
            Assert.Null(IconRegistry.PackName);
            Assert.Null(IconRegistry.Resolve("module.music-library")!.Image);
        }
        finally
        {
            IconRegistry.Reset();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void IconPlaysOnHoverOnlyWhenMotionIsEnabled()
    {
        IconRegistry.Reset();
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860);
        var tile = window.GetVisualDescendants().OfType<IconTile>().First(icon => icon.Slot == "module.color");
        var button = tile.FindAncestorOfType<Button>()!;
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;

        // 动效关闭：悬停不播放。
        window.MouseMove(center);
        LayoutProbe.Settle();
        Assert.Equal(IconMotion.None, tile.CurrentMotion);
        Assert.Equal(0, tile.Progress);

        window.MouseMove(new Point(700, 400));
        UiMotion.Enabled = true;
        LayoutProbe.Settle();
        Assert.Contains("motion", window.Classes);
        window.MouseMove(center);
        LayoutProbe.Settle();
        Assert.Equal("bounce", tile.CurrentMotion);

        // 选中另一个模块时，它的图标播放选中动效。
        var rooms = window.GetVisualDescendants().OfType<IconTile>().First(icon => icon.Slot == "module.rooms");
        LayoutProbe.Select(window, "rooms");
        Assert.True(rooms.IsActive);
        Assert.Equal("pop", rooms.CurrentMotion);

        UiMotion.Enabled = false;
        LayoutProbe.Settle();
        Assert.DoesNotContain("motion", window.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void AssetPackLoadsListsSkippedEntriesAndIsRemoved()
    {
        using var pack = new TemporaryPack();
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.modules");
        try
        {
            var model = (ModulesPageModel)Page<ModulesPage>(window).DataContext!;
            Assert.False(Named<Button>(window, "RemoveAssetsButton").IsEnabled);
            model.LoadPack(pack.Directory);
            LayoutProbe.Settle();
            Assert.Contains("測試素材包", model.PackStatus);
            Assert.Equal(IconRegistry.PackDirectory, IconRegistry.ChosenDirectory);
            Assert.Contains(model.SkippedEntries, entry => entry.StartsWith("module.rooms"));
            Assert.Equal("shake", IconRegistry.Resolve("module.color")!.HoverMotion);

            // 没有 pack.json 的目录不载入，也不改变已选的素材包。
            model.LoadPack(Path.GetTempPath());
            Assert.Contains("pack.json", model.PackMessage);
            Assert.Contains("測試素材包", model.PackStatus);

            Click(window, Named<Button>(window, "RemoveAssetsButton"));
            Assert.Null(IconRegistry.PackName);
            Assert.Null(IconRegistry.ChosenDirectory);
            Assert.Empty(model.SkippedEntries);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ChosenAssetPackIsRememberedAndLoadedOnNextStart()
    {
        var path = Path.Combine(Path.GetTempPath(), "moongate-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var chosen = new DesktopPreferences(UiLanguage.SimplifiedChinese, UiThemeChoice.System, true, @"D:\Packs\Mine");
            new DesktopPreferenceStore(path).Save(chosen);
            Assert.Equal(chosen, new DesktopPreferenceStore(path).Load());

            // 工具根内的素材包存为相对工具根的路径，程序文件夹整体搬移后按新位置解析。
            var inside = Path.Combine(DesktopFiles.Root, "desktop", "assets", "pack");
            new DesktopPreferenceStore(path).Save(chosen with { AssetPack = inside });
            Assert.Equal(Path.Combine("desktop", "assets", "pack"),
                (string?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["AssetPack"]);
            Assert.Equal(inside, new DesktopPreferenceStore(path).Load().AssetPack);
        }
        finally { File.Delete(path); }

        // 启动时：--assets 优先，其次是保存的选择，最后是默认目录。
        Assert.Equal(@"D:\Cli", App.AssetPackDirectory(["--assets", @"D:\Cli"], @"D:\Packs\Mine"));
        Assert.Equal(@"D:\Packs\Mine", App.AssetPackDirectory([], @"D:\Packs\Mine"));
        Assert.Equal(App.DefaultAssetDirectory, App.AssetPackDirectory([], null));
    }

    [AvaloniaFact]
    public void PackReplacesImagesAndSkipsInvalidEntriesWithReasons()
    {
        using var pack = new ImagePack();
        var problems = IconRegistry.LoadPack(pack.Directory);

        Assert.NotNull(IconRegistry.Find(AssetKind.Effect, "40217"));
        Assert.NotNull(IconRegistry.Find(AssetKind.Instrument, "lute"));
        Assert.NotNull(IconRegistry.Find(AssetKind.Playlist, "campfire"));
        Assert.NotNull(IconRegistry.Backdrop("music-library"));
        // 不合格的条目逐项跳过并说明原因，其余照常生效。
        Assert.Contains(problems, line => line.StartsWith("effects.outside") && line.Contains("不在素材包目录内"));
        Assert.Contains(problems, line => line.StartsWith("instruments.harp") && line.Contains(".gif"));
        Assert.Contains(problems, line => line.StartsWith("backgrounds.color") && line.Contains("找不到图片"));
        Assert.Contains(problems, line => line.StartsWith("playlists.huge") && line.Contains("2 MB"));
        Assert.Null(IconRegistry.Find(AssetKind.Effect, "outside"));
        Assert.Null(IconRegistry.Backdrop("color"));
        Assert.Equal(1, IconRegistry.Count(AssetKind.Effect));
        Assert.Equal(2, IconRegistry.Count(AssetKind.Background));

        IconRegistry.Remove();
        Assert.Null(IconRegistry.Find(AssetKind.Effect, "40217"));
        Assert.Equal(0, IconRegistry.Count(AssetKind.Background));
    }

    [AvaloniaFact]
    public void EffectAndInstrumentIconsFallBackWhenPackHasNoImage()
    {
        using var pack = new ImagePack();
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            PackImage Effect(string id) => window.GetVisualDescendants().OfType<PackImage>()
                .First(image => image.Kind == AssetKind.Effect && image.Key == id);
            // 效果行的图标位没有字章：素材包没有图片时不画图，露出效果行自绘的极性标记。
            Image Picture(string id) => Effect(id).Children.OfType<Image>().Single();
            Assert.False(Effect("40217").HasImage);
            Assert.False(Picture("40217").IsVisible);

            // 载入素材包后不重建页面也换成图片，盖住极性标记；没有图片的效果仍显示极性标记。
            IconRegistry.LoadPack(pack.Directory);
            LayoutProbe.Settle();
            Assert.True(Effect("40217").HasImage);
            Assert.True(Picture("40217").IsVisible);
            Assert.False(Effect("1201").HasImage);
            Assert.False(Picture("1201").IsVisible);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
            LayoutProbe.SaveScreenshot(window, "combat-observation-asset-pack-1280x860");

            LayoutProbe.Select(window, "music-library");
            var instruments = window.GetVisualDescendants().OfType<PackImage>().Where(image => image.Kind == AssetKind.Instrument).ToList();
            Assert.True(instruments.Single(image => image.Key == "lute").IsVisible);
            Assert.All(instruments.Where(image => image.Key != "lute"), image => Assert.False(image.IsVisible));
            Assert.True(window.GetVisualDescendants().OfType<PackImage>()
                .Single(image => image.Kind == AssetKind.Playlist && image.Key == "campfire").HasImage);

            IconRegistry.Remove();
            LayoutProbe.Settle();
            Assert.All(window.GetVisualDescendants().OfType<PackImage>().Where(image => image.Kind == AssetKind.Instrument),
                image => Assert.False(image.IsVisible));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(UiThemeChoice.Light)]
    [InlineData(UiThemeChoice.Dark)]
    public void ModuleBackdropSitsUnderPanelsWithAScrimThatKeepsContrast(UiThemeChoice theme)
    {
        using var pack = new ImagePack();
        IconRegistry.LoadPack(pack.Directory);
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "music-library", theme: theme);
        var mini = (Window?)null;
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            var backdrop = window.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "Backdrop");
            Assert.True(backdrop.IsVisible);
            Assert.True(shell.HasBackdrop);
            var suffix = theme == UiThemeChoice.Dark ? "dark" : "light";
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
            LayoutProbe.SaveScreenshot(window, $"music-library-backdrop-1280x860-{suffix}");

            // 没有背景的模块保持原来的底色与光晕。
            LayoutProbe.Select(window, "color");
            Assert.False(backdrop.IsVisible);
            LayoutProbe.Select(window, "combat-observation");
            Assert.True(backdrop.IsVisible);
            LayoutProbe.SaveScreenshot(window, $"combat-observation-backdrop-1280x860-{suffix}");

            mini = OpenMini(new SampleBackend(), 440, 920, theme, UiLanguage.SimplifiedChinese);
            Assert.Empty(LayoutProbe.FindTextOverflow(mini));
            LayoutProbe.SaveScreenshot(mini, $"mini-backdrop-440x920-{suffix}");
        }
        finally
        {
            mini?.Close();
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    /// <summary>
    /// 遮罩叠在图里最暗与最亮处，文字直接在遮罩上、在面板上、在面板内的内嵌块上，两种主题都不低于 4.5:1；
    /// 深色主题下暗图的遮罩比亮图的淡，背景看得见。
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ScrimKeepsTextContrastOverTheImagesExtremes(string themeName)
    {
        using var pack = new ImagePack();
        IconRegistry.LoadPack(pack.Directory);
        var variant = themeName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var module in new[] { "music-library", "combat-observation" })
        {
            var backdrop = IconRegistry.Backdrop(module)!;
            var scrim = UiTheme.ScrimFor(backdrop, variant);
            foreach (var pixel in new[] { backdrop.Darkest, backdrop.Brightest })
            {
                var bare = UiTheme.Over(scrim, pixel);
                var panel = UiTheme.Over(Token(variant, "PanelBrush"), bare);
                var inset = UiTheme.Over(Token(variant, "InsetBrush"), panel);
                foreach (var text in new[] { "InkBrush", "MutedBrush", "ConfirmedBrush", "UnknownBrush" })
                    foreach (var surface in new[] { bare, panel, inset })
                        Assert.True(UiTheme.Contrast(Token(variant, text), surface) >= 4.5,
                            $"{themeName} {module} {text} 在 {surface} 上的对比度不足");
            }
        }
        if (variant == ThemeVariant.Dark)
            Assert.True(UiTheme.ScrimFor(IconRegistry.Backdrop("combat-observation")!, variant).A
                        < UiTheme.ScrimFor(IconRegistry.Backdrop("music-library")!, variant).A);
    }

    [AvaloniaFact]
    public void AssetSlotCardsShowWhetherTheyComeFromThePack()
    {
        using var pack = new ImagePack();
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.modules");
        try
        {
            var model = (ModulesPageModel)window.GetVisualDescendants().OfType<ModulesPage>().Single().DataContext!;
            Assert.All(model.AssetSlots.Where(slot => slot.Name != "浮窗外框"), slot => Assert.Equal("目前：内建", slot.Current));
            model.LoadPack(pack.Directory);
            LayoutProbe.Settle();
            string Slot(string name) => model.AssetSlots.Single(slot => slot.Name == name).Current;
            Assert.Equal("素材包：2 张", Slot("窗口背景"));
            Assert.Equal("素材包：1 张", Slot("效果图标包"));
            Assert.Equal("素材包：1 张", Slot("乐器图标"));
            Assert.Equal("素材包：1 张", Slot("清单封面"));
            Assert.Equal("目前：逐个元件", Slot("浮窗外框"));
            Assert.Contains(model.SkippedEntries, entry => entry.StartsWith("playlists.huge"));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
            LayoutProbe.SaveScreenshot(window, "shell.modules-asset-slots-1280x860");

            model.RemoveAssetsCommand.Execute(null);
            Assert.Equal("目前：内建", Slot("效果图标包"));
            Assert.Equal("目前：内建", Slot("窗口背景"));
        }
        finally { window.Close(); }
    }

    private static Color Token(ThemeVariant variant, string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value));
        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>含窗口背景、效果图标、乐器图标、清单封面及各种不合格条目的临时素材包；用完移除。</summary>
    internal sealed class ImagePack : IDisposable
    {
        public ImagePack()
        {
            Directory = Path.Combine(Path.GetTempPath(), "moongate-images-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            // 背景一张从白到深蓝（有最亮也有最暗处），一张整体偏暗。
            Png("bright.png", 96, 64, (x, y) => UiTheme.Mix(Colors.White, Color.Parse("#1E3A8A"), (x + y) / 158.0));
            Png("dark.png", 96, 64, (x, y) => UiTheme.Mix(Color.Parse("#0B0B12"), Color.Parse("#4A2416"), x / 95.0));
            Png("effect.png", 24, 24, (x, y) => (x / 6 + y / 6) % 2 == 0 ? Color.Parse("#E05A2B") : Color.Parse("#FFD08A"));
            Png("lute.png", 24, 24, (_, _) => Color.Parse("#C97A10"));
            Png("cover.png", 32, 32, (x, _) => UiTheme.Mix(Color.Parse("#E7A23A"), Color.Parse("#7C3AED"), x / 31.0));
            File.WriteAllBytes(Path.Combine(Directory, "huge.png"), new byte[2 * 1024 * 1024 + 1]);
            File.WriteAllText(Path.Combine(Directory, "harp.gif"), "GIF89a");
            File.WriteAllText(Path.Combine(Directory, "pack.json"), """
                {
                  "name": "圖片素材包",
                  "backgrounds": {
                    "music-library": { "image": "bright.png" },
                    "combat-observation": { "image": "dark.png" },
                    "color": { "image": "missing.png" }
                  },
                  "effects": {
                    "40217": { "image": "effect.png" },
                    "outside": { "image": "../effect.png" }
                  },
                  "instruments": {
                    "lute": { "image": "lute.png" },
                    "harp": { "image": "harp.gif" }
                  },
                  "playlists": {
                    "campfire": { "image": "cover.png" },
                    "huge": { "image": "huge.png" }
                  }
                }
                """);
        }

        public string Directory { get; }

        public void Dispose()
        {
            IconRegistry.Remove();
            try { System.IO.Directory.Delete(Directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Png(string name, int width, int height, Func<int, int, Color> color)
        {
            using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);
            using (var locked = bitmap.Lock())
            {
                var row = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var value = color(x, y);
                        (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]) = (value.B, value.G, value.R, 255);
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, locked.Address + y * locked.RowBytes, row.Length);
                }
            }
            bitmap.Save(Path.Combine(Directory, name), new PngBitmapEncoderOptions());
        }
    }

    /// <summary>一个含合格与不合格条目的临时素材包；用完移除并回到内建外观。</summary>
    internal sealed class TemporaryPack : IDisposable
    {
        public TemporaryPack()
        {
            Directory = Path.Combine(Path.GetTempPath(), "moongate-pack-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "pack.json"), """
                {
                  "name": "測試素材包",
                  "icons": {
                    "module.color": { "hover": "shake" },
                    "module.rooms": { "image": "../outside.png" }
                  }
                }
                """);
        }

        public string Directory { get; }

        public void Dispose()
        {
            IconRegistry.Remove();
            try { System.IO.Directory.Delete(Directory, true); }
            catch (IOException) { }
        }
    }
}
