using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MabiTool.Platform.Windows;

/// <summary>
/// 只需“有限查询”权限的进程读取：游戏以管理员身份运行时，普通权限的 Moongate 仍能读取映像路径并判断完整性级别。
/// <see cref="System.Diagnostics.Process.Handle"/> 与 <c>MainModule</c> 要求完全访问或读内存权限，对提权进程会被拒绝。
/// </summary>
/// <summary>目标进程相对本进程的完整性判定结果。</summary>
public enum ElevationVerdict { NotHigher, Higher, Unknown, UnreadableWhileElevated }

[SupportedOSPlatform("windows")]
public static class WindowsProcessAccess
{
    private const uint QueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25, AccessDenied = 5;

    /// <summary>进程映像的完整路径；进程已退出或无法查询时为空。</summary>
    public static string? ImagePath(int processId)
    {
        using var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process.IsInvalid) return null;
        var path = new StringBuilder(32768); var length = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString(0, length) : null;
    }

    /// <summary>高完整性级别（管理员）的 RID。</summary>
    public const uint HighIntegrity = 0x3000;

    /// <summary>
    /// 目标进程相对本进程的完整性判定（游戏以管理员身份运行而 Moongate 没有时为 <see cref="ElevationVerdict.Higher"/>）。
    /// Windows 会丢弃发往更高完整性窗口的模拟输入，且不报告失败。目标进程不存在时为 <see cref="ElevationVerdict.Unknown"/>。
    /// </summary>
    public static ElevationVerdict CompareIntegrity(int processId)
    {
        var own = IntegrityOf(GetCurrentProcess(), out _);
        using var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process.IsInvalid) return ElevationVerdict.Unknown;
        var target = IntegrityOf(process.DangerousGetHandle(), out var error);
        return Judge(own, target, error);
    }

    /// <summary>
    /// 判定规则：读到双方级别时按大小比较。读不到目标令牌且为拒绝访问时，本进程低于高完整性（普通权限）说明目标更高
    /// （同一用户的同级或更低进程总能读到）；本进程已是高完整性（管理员）时拒绝访问来自其他原因（如其他用户或受保护进程），
    /// 不能再提示“请以管理员身份运行”，判为 <see cref="ElevationVerdict.UnreadableWhileElevated"/>，不拦截绑定。
    /// </summary>
    public static ElevationVerdict Judge(uint? own, uint? target, int targetError)
    {
        if (own is null) return ElevationVerdict.Unknown;
        if (target is { } level) return level > own ? ElevationVerdict.Higher : ElevationVerdict.NotHigher;
        if (targetError != AccessDenied) return ElevationVerdict.Unknown;
        return own >= HighIntegrity ? ElevationVerdict.UnreadableWhileElevated : ElevationVerdict.Higher;
    }

    private static uint? IntegrityOf(nint process, out int error)
    {
        error = 0;
        if (!OpenProcessToken(process, TokenQuery, out var token)) { error = Marshal.GetLastWin32Error(); return null; }
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var size);
            if (size <= 0 || size > 1024) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) { error = Marshal.GetLastWin32Error(); return null; }
                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    private sealed class ProcessHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern ProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(ProcessHandle process, uint flags, StringBuilder name, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int kind, nint buffer, int length, out int returned);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
}
