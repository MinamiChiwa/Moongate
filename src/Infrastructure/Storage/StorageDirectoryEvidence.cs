using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.Infrastructure;

public sealed record StorageDirectoryEvidence(string Path, string Identity)
{
    public static StorageDirectoryEvidence Capture(string selectedPath, bool requireEmpty = true)
    {
        var path = StoragePaths.ValidateLocal(selectedPath);
        if (!Directory.Exists(path) || path == System.IO.Path.GetPathRoot(path)) throw new StorageException("storage.pathRejected", "迁移目标须为已存在的本地目录");
        using var handle = Open(path);
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("无法读取目录身份", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.Attributes & 0x10) == 0 || (info.Attributes & 0x400) != 0) throw new StorageException("storage.pathRejected", "迁移目标必须是普通目录");
        if (requireEmpty && Directory.EnumerateFileSystemEntries(path).Any()) throw new StorageException("storage.targetChanged", "迁移目标须保持为空目录");
        return new(path, $"{info.VolumeSerial:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}:{info.CreatedHigh:x8}{info.CreatedLow:x8}");
    }
    public void Verify(bool requireEmpty = true)
    {
        if (Capture(Path, requireEmpty) != this) throw new StorageException("storage.targetChanged", "已选择的迁移目标被替换，请重新选择");
    }
    public IDisposable Hold() => Open(Path);
    internal static SafeFileHandle Open(string path)
    {
        var handle = CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose(); throw new IOException("目录身份不可访问或已被占用", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WrittenLow, WrittenHigh,
            VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
