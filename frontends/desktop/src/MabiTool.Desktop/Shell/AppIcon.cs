using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform;
using MabiTool.ModuleSdk;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 程序图标与任务栏身份。全应用共用一个 <see cref="WindowIcon"/>（packaging/Moongate.ico，与两个 exe 的图标资源同一份），
/// 每个窗口在构造时直接设为本地值：主题、语言与素材包切换不经过它，图标不随样式或资源字典的变化而改变。
/// <para>
/// 目录包里另给每个窗口写入任务栏身份：固定的 AppUserModelID，加上重新启动命令与图标都指向包根的 <c>Moongate.exe</c>。
/// 没有这些属性时任务栏按前端程序 <c>components\desktop\MabiTool.Desktop.exe</c> 的路径识别程序、取图标，
/// “固定到任务栏”固定的也是这个前端程序，而它离开启动器设置的随包运行时无法单独启动。
/// </para>
/// </summary>
public static class AppIcon
{
    private const string Resource = "avares://MabiTool.Desktop/Assets/Moongate.ico";
    private const string Launcher = "Moongate.exe";

    private static readonly Lazy<WindowIcon?> Loaded = new(Load);
    private static readonly Lazy<Identity?> PackageIdentity = new(() => IdentityFor(AppContext.BaseDirectory));

    /// <summary>共用的程序图标；加载失败时为空，窗口保持未设置图标，系统按程序文件显示。</summary>
    public static WindowIcon? Shared => Loaded.Value;

    /// <summary>窗口构造时调用：设置共用图标，并在目录包里写入任务栏身份。</summary>
    public static void Apply(Window window)
    {
        if (Shared is { } icon) window.Icon = icon;
        if (OperatingSystem.IsWindows() && PackageIdentity.Value is { } identity &&
            window.TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: var hwnd } && hwnd != IntPtr.Zero)
            Describe(hwnd, identity);
    }

    private static WindowIcon? Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(Resource));
            return new WindowIcon(stream);
        }
        catch (Exception error)
        {
            Trace.TraceWarning($"Moongate: 程序图标 {Resource} 未能加载，窗口不设置图标：{error}");
            return null;
        }
    }

    /// <param name="AppId">任务栏分组与固定使用的身份；同一包根稳定，不同包根各自独立（与单实例的范围一致）。</param>
    internal sealed record Identity(string AppId, string RelaunchCommand, string IconResource);

    /// <summary>目录包布局且包根有 <c>Moongate.exe</c> 时给出任务栏身份；开发与其他布局返回空，保持系统默认行为。</summary>
    internal static Identity? IdentityFor(string programDirectory)
    {
        if (ToolRoot.PackageRoot(programDirectory) is not { } package) return null;
        var launcher = Path.Combine(package, Launcher);
        if (!File.Exists(launcher)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(package).ToUpperInvariant())))[..16];
        return new("MinamiChiwa.Moongate." + key, "\"" + launcher + "\"", launcher + ",0");
    }

    // 窗口属性写入失败（旧系统、外壳不可用）时任务栏回到按程序路径识别，不影响窗口本身。
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Describe(IntPtr hwnd, Identity identity)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return;
        try
        {
            // 重新启动属性先于 AppUserModelID 写入，任务栏读取身份时它们已就绪。
            Set(store, 2, identity.RelaunchCommand);
            Set(store, 3, identity.IconResource);
            Set(store, 4, "Moongate");
            Set(store, 5, identity.AppId);
            store.Commit();
        }
        catch (COMException error)
        {
            Trace.TraceWarning($"Moongate: 任务栏身份未能写入：{error.Message}");
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void Set(IPropertyStore store, uint id, string value)
    {
        var key = new PropertyKey { Format = AppUserModelFormat, Id = id };
        var variant = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref variant); }
        finally { Marshal.FreeCoTaskMem(variant.Pointer); }
    }

    /// <summary>PKEY_AppUserModel_*：2 重新启动命令，3 重新启动图标，4 重新启动显示名称，5 AppUserModelID。</summary>
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Extra;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
}
