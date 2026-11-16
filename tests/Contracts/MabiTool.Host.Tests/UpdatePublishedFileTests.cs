using System.Text;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class UpdatePublishedFileTests
{
    [Fact]
    public async Task OpenedRecordRetainsSnapshotWhileReplacementAndDeletionProceed()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var path = Path.Combine(workspace.Root, "published.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(path, "old-record");
        await File.WriteAllTextAsync(temporary, "new-record");
        await using var previous = await UpdatePaths.OpenPublishedReadAsync(path, default);
        await UpdatePaths.ReplaceOwnedFileAsync(temporary, path, default);
        await using var current = await UpdatePaths.OpenPublishedReadAsync(path, default);
        File.Delete(path);
        using var oldReader = new StreamReader(previous, Encoding.UTF8, leaveOpen: true);
        using var newReader = new StreamReader(current, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("old-record", await oldReader.ReadToEndAsync());
        Assert.Equal("new-record", await newReader.ReadToEndAsync());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("release", 1)]
    [InlineData("exhausted", 4)]
    [InlineData("cancel", 1)]
    public async Task TransientSharingLockRetriesAreBoundedAndCancellationStopsReads(string outcome, int expectedDelays)
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var path = Path.Combine(workspace.Root, "published.json");
        await File.WriteAllTextAsync(path, "record");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var delays = 0;
        Task Delay(TimeSpan _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            delays++;
            if (outcome == "release") exclusive.Dispose();
            if (outcome == "cancel") cancellation.Cancel();
            return Task.CompletedTask;
        }
        if (outcome == "release")
        {
            await using var opened = await UpdatePaths.OpenPublishedReadAsync(path, cancellation.Token, Delay);
            using var reader = new StreamReader(opened);
            Assert.Equal("record", await reader.ReadToEndAsync());
        }
        else if (outcome == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdatePaths.OpenPublishedReadAsync(path, cancellation.Token, Delay));
        else
            Assert.Contains((await Assert.ThrowsAsync<IOException>(() => UpdatePaths.OpenPublishedReadAsync(path, cancellation.Token, Delay))).HResult & 0xffff, new[] { 32, 33 });
        Assert.Equal(expectedDelays, delays);
    }
}
