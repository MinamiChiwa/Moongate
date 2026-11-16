using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class UpdateFileReplacementTests
{
    [Fact]
    public async Task TransientWindowsLockRetriesBeforeReplacingCompleteFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new UpdateTestWorkspace();
        var source = workspace.PathFor("new");
        var target = workspace.PathFor("current");
        File.WriteAllText(source, "verified-new");
        File.WriteAllText(target, "old");
        using var held = File.OpenRead(target);
        var waits = new List<double>();
        await UpdatePaths.ReplaceOwnedFileAsync(source, target, default, delay: (duration, _) =>
        {
            waits.Add(duration.TotalMilliseconds);
            Assert.Equal("old", File.ReadAllText(target));
            held.Dispose();
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { 50d }, waits);
        Assert.Equal("verified-new", File.ReadAllText(target));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task PersistentLockHasFourWaitsAndCancellationRetainsBothFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new UpdateTestWorkspace();
        var source = workspace.PathFor("new");
        var target = workspace.PathFor("current");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");
        using var held = File.OpenRead(target);
        var waits = new List<double>();
        await Assert.ThrowsAsync<IOException>(() => UpdatePaths.ReplaceOwnedFileAsync(source, target, default,
            delay: (duration, _) => { waits.Add(duration.TotalMilliseconds); return Task.CompletedTask; }));
        Assert.Equal(new[] { 50d, 100d, 200d, 400d }, waits);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdatePaths.ReplaceOwnedFileAsync(source, target, cancel.Token,
            delay: (_, token) => { cancel.Cancel(); return Task.FromCanceled(token); }));
        Assert.Equal("new", File.ReadAllText(source));
        Assert.Equal("old", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    [InlineData(1175, true)]
    [InlineData(1176, false)]
    [InlineData(1177, false)]
    [InlineData(112, false)]
    public async Task RetryClassificationIsBoundedAndKeepsReplacementFailureVisible(int errorCode, bool retries)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new UpdateTestWorkspace();
        var source = workspace.PathFor("new");
        var target = workspace.PathFor("current");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");
        var attempts = 0;
        var waits = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => UpdatePaths.ReplaceOwnedFileAsync(source, target, default,
            (_, _) => { attempts++; throw new IOException("injected", unchecked((int)0x80070000) | errorCode); },
            (_, _) => { waits++; return Task.CompletedTask; }));
        Assert.Equal(errorCode, error.HResult & 0xffff);
        Assert.Equal(retries ? 5 : 1, attempts);
        Assert.Equal(retries ? 4 : 0, waits);
        Assert.Equal("new", File.ReadAllText(source));
        Assert.Equal("old", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingFileAfterFailureStopsInsteadOfRetryingUnknownState(bool sourceMissing)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new UpdateTestWorkspace();
        var source = workspace.PathFor("new");
        var target = workspace.PathFor("current");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => UpdatePaths.ReplaceOwnedFileAsync(source, target, default,
            (_, _) =>
            {
                attempts++;
                File.Delete(sourceMissing ? source : target);
                throw new IOException("injected", unchecked((int)0x80070000) | 1175);
            }, (_, _) => throw new InvalidOperationException("不得重试未知状态")));
        Assert.Equal(1, attempts);
        Assert.Equal(sourceMissing ? "old" : "new", File.ReadAllText(sourceMissing ? target : source));
    }
}
