using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 点击窗口里的空白处时让输入框失去焦点：输入框据此提交（例如下载限速在失去焦点时保存）。
/// 按下处或其祖先是按钮、输入框等可交互控件时不处理，文本选择与按钮点击照常。
/// 焦点交给窗口本身（可获得焦点但不在 Tab 顺序里）。
/// </summary>
public static class FocusRelease
{
    public static void Attach(TopLevel window)
    {
        window.Focusable = true;
        KeyboardNavigation.SetIsTabStop(window, false);
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnPressed(window, e), RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private static void OnPressed(TopLevel window, PointerPressedEventArgs e)
    {
        if (window.FocusManager?.GetFocusedElement() is not TextBox) return;
        for (var visual = e.Source as Visual; visual is not null && visual != window; visual = visual.GetVisualParent())
            if (WindowCaption.IsInteractive(visual)) return;
        window.Focus();
    }
}
