using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal sealed class TemporaryWorkspace : IDisposable
{
    private readonly string identity;
    private bool disposed;
    public string ParentPath { get; }
    public string DirectoryPath { get; }
    private TemporaryWorkspace(string parent, string path, string identity) => (ParentPath, DirectoryPath, this.identity) = (parent, path, identity);

    public static TemporaryWorkspace Create(string parent)
    {
        if (!Path.IsPathFullyQualified(parent)) throw new ArgumentException("临时父目录必须是绝对路径");
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (parent == Path.GetPathRoot(parent)) throw new ArgumentException("临时父目录不能是卷根");
        EnsureNoLinks(parent); Directory.CreateDirectory(parent); EnsureNoLinks(parent);
        var identity = Guid.NewGuid().ToString("N"); var path = Path.Combine(parent, "backend-budget-" + identity);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("独占临时目录已存在");
        Directory.CreateDirectory(path); EnsureNoLinks(path);
        try
        {
            using var marker = new FileStream(Path.Combine(path, ".probe-owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(marker); writer.Write(identity);
        }
        catch
        {
            EnsureNoLinks(path); ValidateTree(path); Directory.Delete(path, true); throw;
        }
        return new(parent, path, identity);
    }

    public static void EnsureNoLinks(string path)
    {
        for (string? candidate = Path.GetFullPath(path); candidate is not null; candidate = Path.GetDirectoryName(candidate))
        {
            try
            {
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) throw new IOException("测量路径包含链接或重解析点：" + candidate);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateTree(string path)
    {
        EnsureNoLinks(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("临时目录包含链接或重解析点：" + entry);
            if ((attributes & FileAttributes.Directory) != 0) ValidateTree(entry);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(DirectoryPath)), ParentPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(DirectoryPath).Equals("backend-budget-" + identity, StringComparison.Ordinal)) throw new IOException("临时目录越界");
        EnsureNoLinks(DirectoryPath);
        if (Directory.Exists(DirectoryPath))
        {
            ValidateTree(DirectoryPath);
            if (File.ReadAllText(Path.Combine(DirectoryPath, ".probe-owner")) != identity) throw new IOException("临时目录所有权已变化");
            Directory.Delete(DirectoryPath, true);
        }
        disposed = true;
    }
}

internal static class RuntimeProbeSelfTest
{
    public static async Task RunAsync()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent ?? throw new IOException("自检需要仓库构建输出");
        using var parent = TemporaryWorkspace.Create(Path.Combine(repository.FullName, "artifacts", "tmp"));
        var sentinel = Path.Combine(parent.DirectoryPath, "keep.txt"); File.WriteAllText(sentinel, "parent");
        using var sibling = TemporaryWorkspace.Create(parent.DirectoryPath);
        var siblingSentinel = Path.Combine(sibling.DirectoryPath, "keep.txt"); File.WriteAllText(siblingSentinel, "sibling");
        using (var completed = TemporaryWorkspace.Create(parent.DirectoryPath))
        {
            File.WriteAllText(Path.Combine(completed.DirectoryPath, "sample.txt"), "sample"); completed.Dispose();
            if (Directory.Exists(completed.DirectoryPath)) throw new InvalidOperationException("本次目录未清理");
        }
        if (File.ReadAllText(sentinel) != "parent" || File.ReadAllText(siblingSentinel) != "sibling") throw new InvalidOperationException("父目录或兄弟内容改变");

        using var linked = TemporaryWorkspace.Create(parent.DirectoryPath);
        var link = Path.Combine(linked.DirectoryPath, "link");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:PROBE_LINK_PATH -Target $env:PROBE_LINK_TARGET | Out-Null");
        start.Environment["PROBE_LINK_PATH"] = link; start.Environment["PROBE_LINK_TARGET"] = sibling.DirectoryPath;
        using (var process = Process.Start(start) ?? throw new IOException("无法创建测试联接"))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); await stdout;
            if (process.ExitCode != 0) throw new IOException("测试联接创建失败：" + await stderr);
            await stderr;
        }
        try
        {
            ExpectIOException(() => TemporaryWorkspace.Create(Path.Combine(link, "child")));
            ExpectIOException(linked.Dispose);
            if (File.ReadAllText(siblingSentinel) != "sibling") throw new InvalidOperationException("链接目标被修改");
        }
        finally { TemporaryWorkspace.EnsureNoLinks(linked.DirectoryPath); Directory.Delete(link); }
        linked.Dispose();

        var flavor = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var hostAssembly = Path.Combine(repository.FullName, "artifacts", "build", "bin", "MabiTool.Host", flavor, "MabiTool.Host.dll");
        if (!File.Exists(hostAssembly)) throw new IOException("自检需要同配置 Host 输出");
        var exitedBefore = RunningHost.ExitedProcesses.Count;
        using (var failed = TemporaryWorkspace.Create(parent.DirectoryPath))
        {
            try { await using var host = await RunningHost.StartAsync(Path.Combine(failed.DirectoryPath, "missing.dll"), Path.Combine(failed.DirectoryPath, "host"), true); throw new InvalidOperationException("错误宿主未失败"); }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("测试宿主提前退出", StringComparison.Ordinal))
            {
                if (!exception.Message.Contains("stderr 尾部：", StringComparison.Ordinal) ||
                    !(exception.Message.Contains("找不到指定的命令或文件", StringComparison.Ordinal) || exception.Message.Contains("specified command or file", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("缺失程序集的具体错误未显示", exception);
            }
            failed.Dispose(); if (Directory.Exists(failed.DirectoryPath)) throw new InvalidOperationException("失败目录未释放");
        }
        using (var cancelled = TemporaryWorkspace.Create(parent.DirectoryPath))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try { await using var host = await RunningHost.StartAsync(hostAssembly, Path.Combine(cancelled.DirectoryPath, "host"), true, cancellation.Token); throw new InvalidOperationException("启动取消未生效"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            cancelled.Dispose(); if (Directory.Exists(cancelled.DirectoryPath)) throw new InvalidOperationException("取消目录未释放");
        }
        using (var running = TemporaryWorkspace.Create(parent.DirectoryPath))
        {
            var processId = 0;
            using var cancellation = new CancellationTokenSource();
            try
            {
                await using var host = await RunningHost.StartAsync(hostAssembly, Path.Combine(running.DirectoryPath, "host"), true);
                var dataPaths = ProbeDataPaths.FromPortableRoot(Path.Combine(running.DirectoryPath, "host"));
                if (!Directory.Exists(dataPaths.DataRoot) || !File.Exists(dataPaths.Sqlite)) throw new InvalidOperationException("报告数据路径未指向实际 SQLite");
                using (var database = new FileStream(dataPaths.Sqlite, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var header = new byte[16]; await database.ReadExactlyAsync(header);
                    if (System.Text.Encoding.ASCII.GetString(header) != "SQLite format 3\0") throw new InvalidOperationException("报告路径内容不是 SQLite 数据库");
                }
                var timing = host.StartupTiming;
                if (timing.TotalMs != host.StartMilliseconds || timing.ProcessToDiscoveryMs < 0 || timing.DiscoveryToBootstrapMs < 0 || timing.BootstrapToReadyMs < 0 ||
                    Math.Abs(timing.TotalMs - timing.ProcessToDiscoveryMs - timing.DiscoveryToBootstrapMs - timing.BootstrapToReadyMs) > .000001)
                    throw new InvalidOperationException("实际启动分段与总计时不一致");
                processId = host.Process.Id; cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            if (processId == 0 || !RunningHost.ExitedProcesses.ContainsKey(processId)) throw new InvalidOperationException("ready 后取消未释放宿主");
            running.Dispose(); if (Directory.Exists(running.DirectoryPath)) throw new InvalidOperationException("ready 后取消目录未释放");
        }
        if (RunningHost.ExitedProcesses.Count != exitedBefore + 3 || File.ReadAllText(sentinel) != "parent" || File.ReadAllText(siblingSentinel) != "sibling")
            throw new InvalidOperationException("失败/取消进程或隔离验证失败");
        var errorTail = await RunningHost.DrainErrorAsync(new StringReader("token=private-value\n" + new string('伤', 10000) + "\nlast-error"));
        if (System.Text.Encoding.UTF8.GetByteCount(errorTail) > 8192 || !errorTail.EndsWith("last-error", StringComparison.Ordinal))
            throw new InvalidOperationException("标准错误尾部预算或最后错误丢失");
        var redacted = await RunningHost.DrainErrorAsync(new StringReader("Authorization: Bearer private-value\nfailed https://user:password@example.invalid/private\nlast-error"));
        if (redacted.Contains("private-value", StringComparison.Ordinal) || redacted.Contains("password", StringComparison.Ordinal) || !redacted.EndsWith("last-error", StringComparison.Ordinal))
            throw new InvalidOperationException("标准错误包含凭据");
        var partialCredential = await RunningHost.DrainErrorAsync(new StringReader("credential=" + new string('x', 10000) + "private-value\nlast-error"));
        if (partialCredential != "last-error") throw new InvalidOperationException("截断标准错误保留了不完整凭据行");
        const string stackFrame = "   at LocalDiscoveryService.StopAsync(CancellationToken cancellationToken)";
        var diagnostic = await RunningHost.DrainErrorAsync(new StringReader(stackFrame + "\n token=private-value\n {\"clientSecret\":\"private-value\"}\n failed https://example.invalid/private\nlast-error"));
        if (!diagnostic.Contains(stackFrame, StringComparison.Ordinal) || diagnostic.Contains("private-value", StringComparison.Ordinal) ||
            diagnostic.Contains("https://", StringComparison.Ordinal) || !diagnostic.Contains("[address]", StringComparison.Ordinal))
            throw new InvalidOperationException("标准错误堆栈或凭据过滤不符");
        var eventHost = await EventHostIdentity.CaptureAsync();
        var loadedAssembly = typeof(MabiTool.Host.BackendApplication).Assembly;
        using (var assemblyFile = File.OpenRead(eventHost.Path))
        using (var executable = new PEReader(assemblyFile))
        {
            var metadata = executable.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != loadedAssembly.ManifestModule.ModuleVersionId)
                throw new InvalidOperationException("事件宿主路径内容与实际加载模块不一致");
        }
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(eventHost.Path))).ToLowerInvariant();
        if (eventHost.Path != loadedAssembly.Location || eventHost.Sha256 != actualHash) throw new InvalidOperationException("事件宿主来源路径或哈希不符");
        var selectedHostHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(hostAssembly))).ToLowerInvariant();
        if (selectedHostHash != eventHost.Sha256) throw new InvalidOperationException("自检所选宿主与实际加载事件宿主内容不一致");
        Console.WriteLine($"事件宿主身份验证通过：{eventHost.Path}；SHA-256={eventHost.Sha256}；PE MVID={loadedAssembly.ManifestModule.ModuleVersionId}；与所选宿主内容一致");
    }

    private static void ExpectIOException(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new InvalidOperationException("链接路径未被拒绝");
    }
}
