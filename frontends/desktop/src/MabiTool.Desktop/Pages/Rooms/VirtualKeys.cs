using Avalonia.Input;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>
/// 合奏本机设置的按键以 Windows 虚拟键码保存（1–254）。这里把界面按下的键换成虚拟键码，并把键码显示成键名。
/// </summary>
public static class VirtualKeys
{
    private static readonly Dictionary<Key, ushort> Codes = Build();
    private static readonly Dictionary<ushort, string> Names = Codes
        .GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => Label(group.First().Key));

    /// <summary>按下的键对应的虚拟键码；修饰键本身和不支持的键返回空。</summary>
    public static ushort? FromKey(Key key) => Codes.TryGetValue(key, out var code) ? code : null;

    /// <summary>键码的显示名；表里没有的键码显示为十六进制。</summary>
    public static string Name(ushort code) => Names.TryGetValue(code, out var name) ? name : $"0x{code:X2}";

    private static Dictionary<Key, ushort> Build()
    {
        var map = new Dictionary<Key, ushort>();
        for (var key = Key.A; key <= Key.Z; key++) map[key] = (ushort)(0x41 + (key - Key.A));
        for (var key = Key.D0; key <= Key.D9; key++) map[key] = (ushort)(0x30 + (key - Key.D0));
        for (var key = Key.NumPad0; key <= Key.NumPad9; key++) map[key] = (ushort)(0x60 + (key - Key.NumPad0));
        for (var key = Key.F1; key <= Key.F24; key++) map[key] = (ushort)(0x70 + (key - Key.F1));
        foreach (var (key, code) in new (Key, ushort)[]
                 {
                     (Key.Back, 0x08), (Key.Tab, 0x09), (Key.Return, 0x0D), (Key.Pause, 0x13), (Key.Escape, 0x1B),
                     (Key.Space, 0x20), (Key.PageUp, 0x21), (Key.PageDown, 0x22), (Key.End, 0x23), (Key.Home, 0x24),
                     (Key.Left, 0x25), (Key.Up, 0x26), (Key.Right, 0x27), (Key.Down, 0x28), (Key.Insert, 0x2D),
                     (Key.Delete, 0x2E), (Key.Multiply, 0x6A), (Key.Add, 0x6B), (Key.Subtract, 0x6D),
                     (Key.Decimal, 0x6E), (Key.Divide, 0x6F), (Key.OemSemicolon, 0xBA), (Key.OemPlus, 0xBB),
                     (Key.OemComma, 0xBC), (Key.OemMinus, 0xBD), (Key.OemPeriod, 0xBE), (Key.OemQuestion, 0xBF),
                     (Key.OemTilde, 0xC0), (Key.OemOpenBrackets, 0xDB), (Key.OemPipe, 0xDC),
                     (Key.OemCloseBrackets, 0xDD), (Key.OemQuotes, 0xDE)
                 })
            map[key] = code;
        return map;
    }

    private static string Label(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
        Key.Back => "Backspace",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.OemSemicolon => ";",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemMinus => "-",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemTilde => "`",
        Key.OemOpenBrackets => "[",
        Key.OemPipe => "\\",
        Key.OemCloseBrackets => "]",
        Key.OemQuotes => "'",
        Key.Multiply => "Num *",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Decimal => "Num .",
        Key.Divide => "Num /",
        _ => key.ToString()
    };
}
