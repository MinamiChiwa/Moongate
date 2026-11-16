using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.Infrastructure;

internal static class StorageFileDeletion
{
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        StoragePaths.ValidateLocal(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>固定可删除句柄后复验内容；按句柄标记删除，避免路径被替换后删错文件。</summary>
    public static async Task<StorageDeletion> DeleteAsync(string path, long bytes, string revision, string hash, CancellationToken ct)
    {
        StoragePaths.ValidateLocal(path);
        using var handle = CreateFileW(path, 0x80010000, 1, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3) return new(false, 0, "missing");
            if (error is 5 or 32 or 33) return new(false, 0, "fileInUse");
            throw new StorageException("storage.unavailable", "清理文件无法取得固定删除句柄");
        }
        await using var stream = new FileStream(handle, FileAccess.Read, 65536, isAsync: true);
        if (stream.Length != bytes || FileRevision(path, stream.Length) != revision ||
            Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != hash)
            return new(false, 0, "changed");
        ct.ThrowIfCancellationRequested();
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            return new(false, 0, "fileInUse");
        return new(true, bytes);
    }

    public static string FileRevision(string path, long bytes) => bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
        File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
        ref FileDisposition info, uint bytes);
}
