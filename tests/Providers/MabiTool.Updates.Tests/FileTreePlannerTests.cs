using System.Security.Cryptography;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class FileTreePlannerTests : IDisposable
{
    private readonly UpdateTestWorkspace workspace = new();
    private string root => workspace.PathFor("tree");

    [Fact]
    public async Task Plans_reuse_download_delete_and_verifies_final_tree()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "old");
        var target = new[] { ManifestFileFor("same.txt", "same"), ManifestFileFor("new.txt", "new") };
        var plan = FileTreePlanner.Plan(root, target);
        Assert.Equal(FileChangeKind.Reuse, plan.Changes.Single(item => item.RelativePath == "same.txt").Kind);
        Assert.Equal(FileChangeKind.Download, plan.Changes.Single(item => item.RelativePath == "new.txt").Kind);
        Assert.Equal(FileChangeKind.Delete, plan.Changes.Single(item => item.RelativePath == "old.txt").Kind);
        await Assert.ThrowsAsync<FileTreeException>(() => FileTreePlanner.VerifyAsync(root, target));
        await File.WriteAllTextAsync(Path.Combine(root, "new.txt"), "new");
        File.Delete(Path.Combine(root, "old.txt"));
        await FileTreePlanner.VerifyAsync(root, target);
    }

    [Fact]
    public void Rejects_traversal_absolute_ads_duplicate_and_bad_hash_paths()
    {
        Assert.Throws<FileTreeException>(() => FileTreePlanner.Plan(root, [ManifestFileFor("../x", "x")]));
        Assert.Throws<FileTreeException>(() => FileTreePlanner.Plan(root, [ManifestFileFor("C:/x", "x")]));
        Assert.Throws<FileTreeException>(() => FileTreePlanner.Plan(root, [ManifestFileFor("x:y", "x")]));
        Assert.Throws<FileTreeException>(() => FileTreePlanner.Plan(root, [ManifestFileFor("x", "x"), ManifestFileFor("x", "x")]));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("nested/COM1.log")]
    [InlineData("LPT².txt")]
    [InlineData("NUL.txt")]
    [InlineData("a?/x")]
    [InlineData("a\u0001b")]
    [InlineData("a.")]
    [InlineData("a ")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    public void Rejects_windows_unsafe_paths(string path) =>
        Assert.Throws<FileTreeException>(() => FileTreePlanner.Plan(root, [ManifestFileFor(path, "x")]));

    [Theory]
    [InlineData("A.txt", "a.txt")]
    [InlineData("Dir", "dir/file")]
    [InlineData("Dir/file", "DIR")]
    public void Rejects_case_collisions_and_file_directory_conflicts(string first, string second) =>
        Assert.Throws<FileTreeException>(() => FileTreePlanner.Plan(root, [ManifestFileFor(first, "x"), ManifestFileFor(second, "y")]));

    [Fact]
    public async Task Verification_honors_already_cancelled_token_even_for_empty_tree()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileTreePlanner.VerifyAsync(root, [], cancellation.Token));
    }

    [Fact]
    public async Task Verifies_multiple_files_and_detects_changed_content()
    {
        Directory.CreateDirectory(root);
        var target = new List<ManifestFile>();
        for (var index = 0; index < 24; index++)
        {
            var path = $"file-{index}.txt";
            var content = new string((char)('a' + index), index * 8192 + 1);
            await File.WriteAllTextAsync(Path.Combine(root, path), content);
            target.Add(ManifestFileFor(path, content));
        }
        await FileTreePlanner.VerifyAsync(root, target);
        await File.WriteAllTextAsync(Path.Combine(root, "file-13.txt"), new string('z', 13 * 8192 + 1));
        Assert.Equal("update.targetTreeMismatch", (await Assert.ThrowsAsync<FileTreeException>(
            () => FileTreePlanner.VerifyAsync(root, target))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Four_inflight_files_settle_and_release_streams_and_activity_on_cancel_or_failure(bool fail)
    {
        Directory.CreateDirectory(root);
        var target = new List<ManifestFile>();
        for (var index = 0; index < 12; index++)
        {
            var path = $"file-{index}.txt";
            await File.WriteAllTextAsync(Path.Combine(root, path), "data");
            target.Add(ManifestFileFor(path, "data"));
        }
        var barrier = new ActivityBarrier();
        using var game = barrier.EnterActivity("game", "game.resource");
        var fourOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var triggerFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var budget = new UpdateIoBudget(barrier: barrier, delay: async (_, token) =>
        {
            var ordinal = Interlocked.Increment(ref calls);
            Interlocked.Increment(ref active);
            try
            {
                if (ordinal == 4) fourOpened.TrySetResult();
                if (fail && ordinal == 1)
                {
                    await triggerFailure.Task.WaitAsync(token);
                    throw new FileTreeException("update.assetLengthMismatch");
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var cancellation = new CancellationTokenSource();
        var pending = FileTreePlanner.VerifyAsync(root, target, cancellation.Token, budget);
        try
        {
            await fourOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, Volatile.Read(ref calls));
            Assert.Equal(4, Volatile.Read(ref active));
            Assert.False(pending.IsCompleted);
            var locked = 0;
            foreach (var file in target)
            {
                try { using var stream = File.Open(Path.Combine(root, file.RelativePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { locked++; }
            }
            Assert.Equal(4, locked);
            Assert.Null(barrier.TryEnterMaintenance("installer"));
            if (fail)
            {
                triggerFailure.TrySetResult();
                Assert.Equal("update.assetLengthMismatch", (await Assert.ThrowsAsync<FileTreeException>(
                    () => pending.WaitAsync(TimeSpan.FromSeconds(5)))).Code);
            }
            else
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.Equal(0, Volatile.Read(ref active));
            Assert.Equal(4, Volatile.Read(ref calls));
            foreach (var file in target)
            {
                using var stream = File.Open(Path.Combine(root, file.RelativePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            game.Dispose();
            Assert.Empty(barrier.Snapshot().Activities);
            using var maintenance = barrier.TryEnterMaintenance("installer");
            Assert.NotNull(maintenance);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        }
    }

    private static ManifestFile ManifestFileFor(string path, string content) =>
        new(path, content.Length, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant());

    public void Dispose() => workspace.Dispose();
}
