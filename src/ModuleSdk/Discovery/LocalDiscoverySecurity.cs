using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MabiTool.ModuleSdk;

/// <summary>
/// 工具根内私有目录与文件的访问控制：目录与文件只授予当前 Windows 用户，所有者为当前用户或本机 Administrators 组
/// （以管理员身份运行时系统把新建文件的所有者记为 Administrators 组，该组本就能取得任何文件的所有权）。
/// exFAT、FAT32 等不保存访问控制的文件系统上不设置也不校验访问控制。
/// </summary>
[SupportedOSPlatform("windows")]
public static class LocalDiscoverySecurity
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly ConcurrentDictionary<string, bool> AclVolumes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>路径所在卷保存访问控制（NTFS、ReFS 等）。exFAT、FAT32 与部分网络或虚拟磁盘不保存，返回 false。</summary>
    public static bool SupportsAccessControl(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return true;
        return AclVolumes.GetOrAdd(root, static volume =>
            !GetVolumeInformationW(volume, null, 0, out _, out _, out var flags, null, 0) || (flags & PersistentAcls) != 0);
    }

    /// <summary>路径所在卷的文件系统名称（NTFS、exFAT、FAT32 等）；读取失败时为空。</summary>
    public static string? FileSystemName(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return null;
        var name = new char[64];
        return GetVolumeInformationW(root, null, 0, out _, out _, out _, name, name.Length) ? new string(name).Split('\0')[0] : null;
    }

    public static void EnsureDirectory(string path)
    {
        RejectLinks(path);
        var directory = new DirectoryInfo(path);
        if (!SupportsAccessControl(path))
        {
            if (!directory.Exists) directory.Create();
            return;
        }
        if (!directory.Exists)
        {
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法取得当前 Windows 用户");
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security);
        }
        if (Problem(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), requireProtected: true) is { } problem)
            throw new IOException($"“{path}”的权限不符合当前 Windows 用户独占要求：{problem}");
    }

    /// <summary>
    /// 校验文件只属于当前用户。所在目录已是当前用户独占、只有这个文件不符（例如安全软件给新建文件加了访问规则）时，
    /// 先把它改为当前用户独占再校验；修复不了或目录本身不符时抛出 <see cref="IOException"/>，写明文件与原因。
    /// </summary>
    public static void ValidateFile(string path)
    {
        RejectLinks(path);
        if (!SupportsAccessControl(path)) return;
        var file = new FileInfo(path);
        if (Problem(file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), requireProtected: false) is not { } problem) return;
        var parent = file.Directory;
        if (parent is null || Problem(parent.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), requireProtected: true) is not null)
            throw new IOException($"“{path}”的权限不符合当前 Windows 用户独占要求：{problem}");
        try { MakeFilePrivate(file); }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            throw new IOException($"“{path}”的权限不符合当前 Windows 用户独占要求：{problem}；自动改为当前用户独占失败：{error.Message}", error);
        }
        if (Problem(file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), requireProtected: false) is { } remaining)
            throw new IOException($"“{path}”的权限不符合当前 Windows 用户独占要求：{remaining}");
    }

    /// <summary>
    /// 路径及其各级上级不能是重解析点：联接、符号链接与挂载点会把写入引到别处；OneDrive 等同步盘的占位目录也是重解析点，
    /// 同步程序会在宿主写数据库时上传或替换文件。两种情况分别给出原因（<see cref="LinkedPathException"/>）。
    /// </summary>
    public static void RejectLinks(string path)
    {
        FileSystemInfo? current = File.Exists(path) ? new FileInfo(Path.GetFullPath(path)) : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new LinkedPathException(current.FullName, current.LinkTarget is null);
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
    }

    /// <summary>目录（及其直接包含的文件）是否已是当前用户独占：所有者为当前用户、目录不继承上级权限、没有授予其他主体。</summary>
    public static bool IsPrivate(string directory)
    {
        try
        {
            RejectLinks(directory);
            if (!SupportsAccessControl(directory)) return true;
            var info = new DirectoryInfo(directory);
            Validate(info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), requireProtected: true);
            foreach (var file in info.EnumerateFiles())
                Validate(file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), requireProtected: false);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// 把目录树改为当前用户独占：每个目录与文件的所有者设为当前用户，访问规则只保留当前用户完全控制并停止继承。
    /// 用于整体复制或换用户后的工具根目录；树中有联接或符号链接时拒绝，不修改任何内容。当前用户无权改所有者时抛出
    /// <see cref="UnauthorizedAccessException"/> 或 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static void MakePrivate(string directory)
    {
        RejectLinks(directory);
        if (!SupportsAccessControl(directory)) return;
        var root = new DirectoryInfo(directory);
        var entries = root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList();
        if (entries.Any(entry => (entry.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("目录中包含联接或符号链接");
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法取得当前 Windows 用户");
        foreach (var directoryInfo in entries.OfType<DirectoryInfo>().Prepend(root))
        {
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directoryInfo.SetAccessControl(security);
        }
        foreach (var file in entries.OfType<FileInfo>()) MakeFilePrivate(file);
    }

    private static void MakeFilePrivate(FileInfo file)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法取得当前 Windows 用户");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    private static void Validate(FileSystemSecurity security, bool requireProtected)
    {
        if (Problem(security, requireProtected) is { } problem) throw new IOException(problem);
    }

    /// <summary>不符合当前用户独占的具体原因（所有者、继承、额外授权的主体）；符合时为空。</summary>
    private static string? Problem(FileSystemSecurity security, bool requireProtected)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法取得当前 Windows 用户");
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!IsAcceptedOwner(owner, user)) return $"所有者是 {Name(owner)}，不是当前用户 {Name(user)}";
        if (requireProtected && !security.AreAccessRulesProtected) return "继承了上级文件夹的权限";
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference))
                return $"还授予了 {Name(rule.IdentityReference)} 访问权限";
        return null;
    }

    private static string Name(IdentityReference? identity)
    {
        if (identity is null) return "（无法读取）";
        try { return $"{identity.Translate(typeof(NTAccount)).Value}（{identity.Value}）"; }
        catch (Exception error) when (error is IdentityNotMappedException or SystemException) { return identity.Value; }
    }

    /// <summary>可接受的所有者：当前用户，或本机 Administrators 组（以管理员身份运行时新建文件的默认所有者）。</summary>
    public static bool IsAcceptedOwner(IdentityReference? owner, SecurityIdentifier user) =>
        owner is not null && (user.Equals(owner) || Administrators.Equals(owner));

    private const uint PersistentAcls = 0x00000008;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPathName, char[]? volumeName, int volumeNameSize,
        out uint serialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char[]? fileSystemName, int fileSystemNameSize);
}

/// <summary>路径中含有重解析点：<see cref="CloudPlaceholder"/> 为真时是同步盘等非链接型重解析点，否则是联接、符号链接或挂载点。</summary>
public sealed class LinkedPathException(string path, bool cloudPlaceholder)
    : IOException(cloudPlaceholder ? "路径位于 OneDrive 等同步盘的占位目录中" : "本机发现路径不能包含联接或符号链接")
{
    public string LinkedPath { get; } = path;
    public bool CloudPlaceholder { get; } = cloudPlaceholder;
}
