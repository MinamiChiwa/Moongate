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

/// <summary>设置页：语言切换、快捷键录入与后端回执。</summary>
public sealed class SettingsTests
{
    [AvaloniaFact]
    public void SettingsPageSwitchesLanguageAndListsShortcutFailures()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.settings");
        var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
        var model = (SettingsPageModel)page.DataContext!;
        Assert.Contains(model.Data.Shortcuts, shortcut => !shortcut.Registered);

        var traditional = page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsTraditional");
        Click(window, traditional);
        Assert.Equal(UiLanguage.TraditionalChinese, UiText.Language);
        var rebuilt = window.GetVisualDescendants().OfType<SettingsPage>().Single();
        Assert.Contains(rebuilt.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "全域快捷鍵");
        Assert.Empty(LayoutProbe.FindTextOverflow(window));
        window.Close();
    }

    [AvaloniaFact]
    public void ShortcutRowRecordsChordAndShowsBackendResult()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        var model = (SettingsPageModel)Page<SettingsPage>(window).DataContext!;
        Assert.Equal("emergency-stop", model.Lines[0].Row.Id);
        Button Change(string id) => Named<ItemsControl>(window, "ShortcutList").GetVisualDescendants().OfType<Button>()
            .Single(button => button.CommandParameter as string == id && button.IsVisible);
        // 后端不接受修改的动作没有可用的修改按钮。
        Assert.False(Change("music.toggle").IsEnabled);

        Click(window, Change("emergency-stop"));
        Assert.True(model.Lines[0].Recording);
        window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        Assert.True(model.IsRecording);
        window.KeyPress(Key.F8, RawInputModifiers.Control, PhysicalKey.F8, null);
        LayoutProbe.Settle();
        Assert.Equal(("shortcut.change", "emergency-stop:Ctrl + F8"), backend.UiIntents[^1]);
        Assert.False(model.IsRecording);
        Assert.Equal("Ctrl + F8", model.Lines[0].Row.Chord);
        Assert.Contains("已注册", model.Lines[0].Message);

        // 与其他快捷键相同的组合：后端回执注册失败，这一行显示失败与错误码。
        Click(window, Change("emergency-stop"));
        window.KeyPress(Key.P, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.P, "p");
        LayoutProbe.Settle();
        Assert.True(model.Lines[0].Failed);
        Assert.Contains("shortcut.conflict", model.Lines[0].Message);
        Assert.False(model.Lines[0].Row.Registered);
        Assert.Empty(LayoutProbe.FindTextOverflow(window));
        LayoutProbe.SaveScreenshot(window, "shell.settings-shortcut-failed-1280x860");
        window.Close();
    }

    [AvaloniaFact]
    public void KeyNamesFollowBackendShortcutTable()
    {
        Assert.Null(SettingsPageModel.KeyName(Key.LeftShift));
        Assert.Equal("F9", SettingsPageModel.KeyName(Key.F9));
        Assert.Equal("D1", SettingsPageModel.KeyName(Key.D1));
        Assert.Equal("Backspace", SettingsPageModel.KeyName(Key.Back));
        Assert.Equal("Enter", SettingsPageModel.KeyName(Key.Enter));
        Assert.Equal("PageUp", SettingsPageModel.KeyName(Key.PageUp));
        Assert.Equal("Ctrl + Alt + 1", new KeyChord("D1", true, true, false, false).ToString());
    }
}
