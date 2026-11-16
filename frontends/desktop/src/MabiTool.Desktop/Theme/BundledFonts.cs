using Avalonia;
using Avalonia.Media.Fonts;

namespace MabiTool.Desktop.Theme;

/// <summary>
/// 随应用分发的界面字体：Noto Sans SC 与 Noto Sans TC（思源黑体的 Google Fonts 发行版，SIL OFL 1.1，可商用），
/// 各含常规、粗、特粗三个字重，由 scripts/build/subset-desktop-fonts.py 生成。
/// 简体界面优先 SC、缺字用 TC，繁体界面相反；两份都没有的字由系统字体补位。
/// </summary>
public static class BundledFonts
{
    public static AppBuilder UseBundledFonts(this AppBuilder builder) => builder.ConfigureFonts(manager =>
        manager.AddFontCollection(new EmbeddedFontCollection(new Uri("fonts:Moongate"),
            new Uri("avares://MabiTool.Desktop/Assets/Fonts"))));
}
