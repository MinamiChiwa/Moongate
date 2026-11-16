using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class UpdatePathSafetyTests
{
    [Fact]
    public async Task Rejects_link_in_current_tree_and_linked_root_before_read_or_build()
    {
        using var workspace = new UpdateTestWorkspace();
        var outside = workspace.PathFor("outside");
        var current = workspace.PathFor("current");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "private");
        var link = Path.Combine(current, "linked");
        CreateDirectoryLink(link, outside);
        try
        {
            Assert.Equal("update.pathRejected", (await Assert.ThrowsAsync<FileTreeException>(() => FileTreePlanner.PlanAsync(current, []))).Code);
            await Assert.ThrowsAsync<FileTreeException>(() => FileTreePlanner.VerifyAsync(link, []));
            await Assert.ThrowsAsync<FileTreeException>(() => new FileTreeArtifactBuilder().BuildAsync(current, workspace.PathFor("built")));
            Assert.False(Directory.Exists(workspace.PathFor("built")));
            Assert.Equal("private", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Rejects_linked_cache_and_stage_ancestor_without_network_or_target_writes()
    {
        using var workspace = new UpdateTestWorkspace();
        var outside = workspace.PathFor("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "saved");
        var link = workspace.PathFor("linked");
        CreateDirectoryLink(link, outside);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData("data"u8));
            var file = new ManifestFile("app", 4, hash);
            var artifacts = new FileTreeArtifacts([new(file, 4, hash, UpdateBlobEncoding.Identity)], FileTreePlanner.Plan(workspace.PathFor("empty"), [file]).TargetTreeHash);
            var budget = new UpdateIoBudget();
            using var downloader = new ResumableAssetDownloader(new RejectNetwork(), budget: budget);
            var stager = new FileTreeStager(downloader, budget);
            await Assert.ThrowsAsync<FileTreeException>(() => stager.StageAsync(workspace.PathFor("empty"), workspace.PathFor("stage"), link, artifacts, _ => throw new InvalidOperationException()));
            await Assert.ThrowsAsync<FileTreeException>(() => stager.StageAsync(workspace.PathFor("empty"), Path.Combine(link, "stage"), workspace.PathFor("cache"), artifacts, _ => throw new InvalidOperationException()));
            await Assert.ThrowsAsync<FileTreeException>(() => downloader.DownloadAsync(new([new("direct", new Uri("https://github.com/blob"))], Path.Combine(link, "asset"), 4, hash)));
            Assert.Single(Directory.GetFiles(outside));
            Assert.Equal("saved", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.False(Directory.Exists(workspace.PathFor("stage")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Builder_rejects_output_inside_input_and_preserves_existing_output()
    {
        using var workspace = new UpdateTestWorkspace();
        var release = workspace.PathFor("release");
        Directory.CreateDirectory(release);
        File.WriteAllText(Path.Combine(release, "app"), "data");
        var builder = new FileTreeArtifactBuilder();
        await Assert.ThrowsAsync<FileTreeException>(() => builder.BuildAsync(release, Path.Combine(release, "output")));
        await builder.BuildAsync(release, workspace.PathFor("built"));
        var bytes = await File.ReadAllBytesAsync(workspace.PathFor("built/files.json"));
        await Assert.ThrowsAsync<FileTreeException>(() => builder.BuildAsync(release, workspace.PathFor("built")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(workspace.PathFor("built/files.json")));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("不应发起网络请求");
    }
}
