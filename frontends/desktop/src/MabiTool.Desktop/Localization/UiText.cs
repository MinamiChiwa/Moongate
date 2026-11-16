using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MabiTool.Desktop.Localization;

public enum UiLanguage { SimplifiedChinese, TraditionalChinese }

/// <summary>
/// 界面自己的文字，两种语言各一份。游戏来源的名称（技能、怪物、角色、道具等）不经过这里，按原文显示。
/// </summary>
public sealed record LocalizedText(string Simplified, string? Traditional = null)
{
    public string Current => UiText.Language == UiLanguage.TraditionalChinese ? Traditional ?? Simplified : Simplified;

    public static implicit operator LocalizedText(string text) => new(text);
}

/// <summary>界面语言：默认简体中文，可切换为繁体中文。切换时替换文字资源表并通知视图模型刷新。</summary>
public static class UiText
{
    private static ResourceDictionary? active;

    public static UiLanguage Language { get; private set; } = UiLanguage.SimplifiedChinese;

    public static event EventHandler? LanguageChanged;

    public static string Pick(string simplified, string traditional) =>
        Language == UiLanguage.TraditionalChinese ? traditional : simplified;

    public static void Apply(Application application, UiLanguage language)
    {
        var name = language == UiLanguage.TraditionalChinese ? "zh-Hant" : "zh-Hans";
        var next = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri($"avares://MabiTool.Desktop/Localization/Strings.{name}.axaml"));
        var merged = application.Resources.MergedDictionaries;
        if (active is not null) merged.Remove(active);
        merged.Add(next);
        active = next;
        Language = language;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
}
