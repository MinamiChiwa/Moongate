using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsGameInputDevice : IGameInputDevice
{
    private readonly HashSet<InputControl> held = [];
    public UInt64String? ForegroundWindowIdentity => GetForegroundWindow() is var window && window != 0
        ? new(unchecked((ulong)window)) : null;
    public void Press(InputControl control)
    {
        if (held.Contains(control)) return;
        var key = InputControls.VirtualKeyOf(control) ?? control switch
        { InputControl.MouseLeft => (ushort)1, InputControl.MouseRight => (ushort)2, InputControl.MouseMiddle => (ushort)4, _ => (ushort)0 };
        if (key == 0) throw new GameInputException("input.actionInvalid");
        if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new GameInputException("input.controlOccupied");
        Send(control, false); held.Add(control);
    }
    public void Release(InputControl control)
    {
        if (!held.Contains(control)) return;
        Send(control, true); held.Remove(control);
    }
    public void ReleaseAll()
    {
        Exception? failure = null;
        foreach (var control in held.ToArray())
            try { Release(control); } catch (Win32Exception error) { failure = error; }
        if (failure is not null) throw failure;
    }
    public void MovePointer(PhysicalScreenPoint point)
    { if (!SetCursorPos(point.X, point.Y)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void MovePointerRelative(PhysicalScreenPoint delta) => SendMouse(0x0001, delta.X, delta.Y, 0);
    public void Wheel(int delta) => SendMouse(0x0800, 0, 0, unchecked((uint)delta));
    private static void Send(InputControl control, bool release)
    {
        if (InputControls.VirtualKeyOf(control) is { } key)
        {
            var scan = MapVirtualKey(key, 0);
            var extended = key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0xA3 or 0xA5;
            SendOne(new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Scan = (ushort)scan,
                Flags = (release ? 2U : 0) | (extended ? 1U : 0) } } });
        }
        else SendMouse(control switch
        { InputControl.MouseLeft => release ? 4U : 2U, InputControl.MouseRight => release ? 16U : 8U,
            InputControl.MouseMiddle => release ? 64U : 32U, _ => throw new GameInputException("input.actionInvalid") }, 0, 0, 0);
    }
    private static void SendMouse(uint flags, int x, int y, uint data) => SendOne(new()
    { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags } } });
    private static void SendOne(NativeInput input)
    { if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
