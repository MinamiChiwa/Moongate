using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MabiTool.ModuleSdk;

[SupportedOSPlatform("windows")]
public static class LocalDiscoverySecurity
{
    public static void EnsureDirectory(string path)
    {
        RejectLinks(path);
        var directory = new DirectoryInfo(path);
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
        Validate(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), requireProtected: true);
    }

    public static void ValidateFile(string path)
    {
        RejectLinks(path);
        Validate(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), requireProtected: false);
    }

    public static void RejectLinks(string path)
    {
        FileSystemInfo? current = File.Exists(path) ? new FileInfo(Path.GetFullPath(path)) : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("本机发现路径不能包含联接或符号链接");
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
    }

    /// <summary>目录（及其直接包含的文件）是否已是当前用户独占：所有者为当前用户、目录不继承上级权限、没有授予其他主体。</summary>
    public static bool IsPrivate(string directory)
    {
        try
        {
            RejectLinks(directory);
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
        foreach (var file in entries.OfType<FileInfo>())
        {
            var security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
    }

    private static void Validate(FileSystemSecurity security, bool requireProtected)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法取得当前 Windows 用户");
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier))) || (requireProtected && !security.AreAccessRulesProtected))
            throw new IOException("本机发现目录的所有者或继承策略不符合当前用户保护要求");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference))
                throw new IOException("本机发现路径授予了其他主体访问权限");
    }
}
