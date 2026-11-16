using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class UpdateDirectoryPublicationTests
{
    [Fact]
    public async Task Windows_transient_lock_retries_then_publishes_same_tree()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new UpdateTestWorkspace();
        var work = workspace.PathFor("work");
        var target = workspace.PathFor("published");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "app"), "verified");
        var handle = File.OpenRead(Path.Combine(work, "app"));
        try
        {
            var waits = new List<TimeSpan>();
            await UpdatePaths.PublishOwnedDirectoryAsync(work, target, default, (duration, token) =>
            {
                waits.Add(duration); handle.Dispose(); return Task.CompletedTask;
            });
            Assert.NotEmpty(waits);
            Assert.Equal(TimeSpan.FromMilliseconds(50), waits[0]);
            Assert.False(Directory.Exists(work));
            Assert.Equal("verified", File.ReadAllText(Path.Combine(target, "app")));
        }
        finally { handle.Dispose(); }
    }

    [Fact]
    public async Task Windows_persistent_lock_has_bounded_retries_and_cancel_can_stop_waiting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new UpdateTestWorkspace();
        var work = workspace.PathFor("work");
        var target = workspace.PathFor("published");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "app"), "verified");
        using (var handle = File.OpenRead(Path.Combine(work, "app")))
        {
            var waits = new List<TimeSpan>();
            await Assert.ThrowsAsync<IOException>(() => UpdatePaths.PublishOwnedDirectoryAsync(work, target, default, (duration, token) =>
            { waits.Add(duration); return Task.CompletedTask; }));
            Assert.Equal(new[] { 50d, 100d, 200d, 400d }, waits.Select(item => item.TotalMilliseconds));
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdatePaths.PublishOwnedDirectoryAsync(work, target,
                cancellation.Token, (duration, token) => { cancellation.Cancel(); return Task.FromCanceled(token); }));
        }
        Assert.True(Directory.Exists(work));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Existing_destination_is_never_overwritten_or_retried()
    {
        using var workspace = new UpdateTestWorkspace();
        var work = workspace.PathFor("work");
        var target = workspace.PathFor("published");
        Directory.CreateDirectory(work); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "app"), "existing");
        var error = await Assert.ThrowsAsync<FileTreeException>(() => UpdatePaths.PublishOwnedDirectoryAsync(work, target, default,
            (_, _) => throw new InvalidOperationException("不得重试既有目标")));
        Assert.Equal("update.stageExists", error.Code);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "app")));
        Assert.True(Directory.Exists(work));
    }

    [Fact]
    public void Extended_length_assembly_location_matches_long_install_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = @"C:\Install\" + new string('a', 120) + @"\" + new string('b', 120);
        var expected = Path.Combine(root, "components", "updater", "MabiTool.Updater.dll");
        Assert.True(expected.Length > 260);
        Assert.True(UpdatePaths.SameLocalFile(@"\\?\" + expected, expected));
        Assert.True(UpdatePaths.SameLocalFile(@"\\?\UNC\server\share\app.dll", @"\\server\share\APP.dll"));
        Assert.False(UpdatePaths.SameLocalFile(@"\\?\" + Path.Combine(root, "components", "launcher", "MabiTool.Updater.dll"), expected));
    }
}
