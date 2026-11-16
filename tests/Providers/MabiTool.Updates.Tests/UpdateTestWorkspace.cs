namespace MabiTool.Updates.Tests;

internal sealed class UpdateTestWorkspace : IDisposable
{
    public string Root { get; }
    public UpdateTestWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("找不到解决方案根目录");
        Root = Path.Combine(directory.FullName, "artifacts", "tmp", "update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }
    public string PathFor(string relative) => Path.Combine(Root, relative);
    public void Dispose() => Directory.Delete(Root, true);
}
