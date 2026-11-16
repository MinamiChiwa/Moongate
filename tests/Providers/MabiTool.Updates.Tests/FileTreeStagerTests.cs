using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class FileTreeStagerTests
{
    [Fact]
    public async Task Incremental_and_clean_rebuild_match_release_including_rename_delete_and_corruption_fallback()
    {
        using var workspace = new UpdateTestWorkspace();
        var current = workspace.PathFor("current");
        var release = workspace.PathFor("release");
        Write(current, "same.txt", "same");
        Write(current, "changed.txt", "old");
        Write(current, "deleted.txt", "delete");
        Write(current, "before-rename.txt", "rename");
        Write(current, "corrupted.txt", "bad");
        Write(release, "same.txt", "same");
        Write(release, "changed.txt", "new");
        Write(release, "nested/new.txt", "added");
        Write(release, "after-rename.txt", "rename");
        Write(release, "corrupted.txt", "good");
        var artifacts = await new FileTreeArtifactBuilder().BuildAsync(release, workspace.PathFor("built"));
        var second = await new FileTreeArtifactBuilder().BuildAsync(release, workspace.PathFor("built-again"));
        Assert.Equal(artifacts.TargetTreeHash, second.TargetTreeHash);
        Assert.Equal(artifacts.Files, second.Files);
        Assert.Equal(artifacts.FullPackage, second.FullPackage);
        Assert.True(File.Exists(workspace.PathFor("built/files.json")));
        var requests = new List<string>();
        var budget = new UpdateIoBudget();
        using var downloader = new ResumableAssetDownloader(Blobs(workspace.PathFor("built/blobs"), requests), budget: budget);
        var stager = new FileTreeStager(downloader, budget);
        var result = await stager.StageAsync(current, workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, Sources);
        Assert.Equal(1, result.ReusedFiles);
        Assert.Equal(4, result.DownloadedFiles);
        Assert.Equal(4, requests.Count);
        var clean = await stager.StageAsync(workspace.PathFor("empty"), workspace.PathFor("clean"), workspace.PathFor("cache"), artifacts, Sources);
        Assert.Equal(0, clean.ReusedFiles);
        Assert.Equal(5, requests.Count); // 四个已验证 blob 命中共享缓存。
        await FileTreePlanner.VerifyAsync(result.StageRoot, artifacts.Files.Select(item => item.File).ToArray());
        await FileTreePlanner.VerifyAsync(clean.StageRoot, artifacts.Files.Select(item => item.File).ToArray());
        Assert.False(File.Exists(workspace.PathFor("stage/deleted.txt")));
        Assert.False(File.Exists(workspace.PathFor("stage/before-rename.txt")));
        Assert.Equal("old", File.ReadAllText(workspace.PathFor("current/changed.txt")));
        Assert.Equal("delete", File.ReadAllText(workspace.PathFor("current/deleted.txt")));
        File.WriteAllText(workspace.PathFor("stage/same.txt"), "edit");
        Assert.Equal("same", File.ReadAllText(workspace.PathFor("current/same.txt")));
    }

    [Fact]
    public async Task Revalidates_copy_when_local_bytes_changed_after_planning()
    {
        using var workspace = new UpdateTestWorkspace();
        Write(workspace.PathFor("release"), "app", "good");
        Write(workspace.PathFor("current"), "app", "good");
        var artifacts = await new FileTreeArtifactBuilder().BuildAsync(workspace.PathFor("release"), workspace.PathFor("built"));
        Assert.Equal(FileChangeKind.Reuse, (await FileTreePlanner.PlanAsync(workspace.PathFor("current"), artifacts.Files.Select(item => item.File).ToArray())).Changes.Single().Kind);
        File.WriteAllText(workspace.PathFor("current/app"), "evil");
        var requests = new List<string>();
        var budget = new UpdateIoBudget();
        using var downloader = new ResumableAssetDownloader(Blobs(workspace.PathFor("built/blobs"), requests), budget: budget);
        var result = await new FileTreeStager(downloader, budget).StageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, Sources);
        Assert.Equal(0, result.ReusedFiles);
        Assert.Single(requests);
        Assert.Equal("good", File.ReadAllText(workspace.PathFor("stage/app")));
        Assert.Equal("evil", File.ReadAllText(workspace.PathFor("current/app")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejects_expansion_over_limit_or_wrong_raw_hash_and_never_publishes_stage(bool overLimit)
    {
        using var workspace = new UpdateTestWorkspace();
        Write(workspace.PathFor("current"), "app", "old");
        var raw = overLimit ? new byte[1024 * 1024] : "evil"u8.ToArray();
        byte[] compressed;
        using (var memory = new MemoryStream())
        {
            using (var gzip = new GZipStream(memory, CompressionLevel.Optimal, true)) gzip.Write(raw);
            compressed = memory.ToArray();
        }
        var target = FileFor("app", "good"u8.ToArray());
        var plan = FileTreePlanner.Plan(workspace.PathFor("empty"), [target]);
        var artifacts = new FileTreeArtifacts([new(target, compressed.Length, Hash(compressed), UpdateBlobEncoding.Gzip)], plan.TargetTreeHash);
        var budget = new UpdateIoBudget();
        using var downloader = new ResumableAssetDownloader(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(compressed) }), budget: budget);
        var error = await Assert.ThrowsAsync<FileTreeException>(() => new FileTreeStager(downloader, budget)
            .StageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, Sources));
        Assert.Equal(overLimit ? "update.assetLengthMismatch" : "update.assetHashMismatch", error.Code);
        Assert.False(Directory.Exists(workspace.PathFor("stage")));
        Assert.Empty(Directory.GetDirectories(workspace.Root, "stage.work-*"));
        Assert.Equal("old", File.ReadAllText(workspace.PathFor("current/app")));
    }

    [Fact]
    public async Task Cancellation_during_reuse_drains_activity_cleans_private_work_and_can_retry()
    {
        using var workspace = new UpdateTestWorkspace();
        Write(workspace.PathFor("current"), "app", "good");
        var target = FileFor("app", "good"u8.ToArray());
        var artifacts = new FileTreeArtifacts([new(target, 4, target.Sha256, UpdateBlobEncoding.Identity)], FileTreePlanner.Plan(workspace.PathFor("empty"), [target]).TargetTreeHash);
        var barrier = new ActivityBarrier();
        using var game = barrier.EnterActivity("game", "game.resource");
        using var cancellation = new CancellationTokenSource();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = new UpdateIoBudget(barrier: barrier, delay: async (_, token) => { paused.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        using var downloader = new ResumableAssetDownloader(new Handler(_ => throw new InvalidOperationException()), budget: budget);
        var stager = new FileTreeStager(downloader, budget);
        var stage = stager.StageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, Sources, cancellation.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(barrier.Snapshot().Activities, item => item.Kind == "update.staging");
        Assert.Null(barrier.TryEnterMaintenance("installer"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage);
        Assert.False(Directory.Exists(workspace.PathFor("stage")));
        Assert.Single(barrier.Snapshot().Activities);
        game.Dispose();
        var result = await stager.StageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, Sources);
        Assert.Equal(1, result.ReusedFiles);
        Assert.Empty(barrier.Snapshot().Activities);
        using var maintenance = barrier.TryEnterMaintenance("installer");
        Assert.NotNull(maintenance);
        await Assert.ThrowsAsync<ActivityBarrierException>(() => stager.StageAsync(workspace.PathFor("current"), workspace.PathFor("another"), workspace.PathFor("cache"), artifacts, Sources));
    }

    [Fact]
    public async Task Rejects_overlapping_roots_existing_stage_and_invalid_metadata_before_network()
    {
        using var workspace = new UpdateTestWorkspace();
        Write(workspace.PathFor("current"), "app", "old");
        Write(workspace.PathFor("stage"), "keep", "saved");
        var target = FileFor("app", "good"u8.ToArray());
        var artifacts = new FileTreeArtifacts([new(target, 4, target.Sha256, UpdateBlobEncoding.Identity)], FileTreePlanner.Plan(workspace.PathFor("empty"), [target]).TargetTreeHash);
        var budget = new UpdateIoBudget();
        using var downloader = new ResumableAssetDownloader(new Handler(_ => throw new InvalidOperationException()), budget: budget);
        var stager = new FileTreeStager(downloader, budget);
        var overlap = await Assert.ThrowsAsync<FileTreeException>(() => stager.StageAsync(workspace.PathFor("current"), workspace.PathFor("current/stage"), workspace.PathFor("cache"), artifacts, Sources));
        Assert.Equal("update.rootOverlap", overlap.Code);
        var exists = await Assert.ThrowsAsync<FileTreeException>(() => stager.StageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, Sources));
        Assert.Equal("update.stageExists", exists.Code);
        Assert.Equal("saved", File.ReadAllText(workspace.PathFor("stage/keep")));
        var invalid = artifacts with { Files = [artifacts.Files[0] with { Encoding = (UpdateBlobEncoding)99 }] };
        var bad = await Assert.ThrowsAsync<FileTreeException>(() => stager.StageAsync(workspace.PathFor("current"), workspace.PathFor("new"), workspace.PathFor("cache"), invalid, Sources));
        Assert.Equal("update.assetMetadataInvalid", bad.Code);
    }

    [Fact]
    public async Task Empty_component_builds_and_stages_without_network()
    {
        using var workspace = new UpdateTestWorkspace();
        Directory.CreateDirectory(workspace.PathFor("release"));
        var artifacts = await new FileTreeArtifactBuilder().BuildAsync(workspace.PathFor("release"), workspace.PathFor("built"));
        var budget = new UpdateIoBudget();
        using var downloader = new ResumableAssetDownloader(new Handler(_ => throw new InvalidOperationException()), budget: budget);
        var result = await new FileTreeStager(downloader, budget).StageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, Sources);
        Assert.Empty(Directory.EnumerateFileSystemEntries(result.StageRoot));
    }

    [Fact]
    public async Task Rejects_oversized_manifest_before_creating_staging_files()
    {
        using var workspace = new UpdateTestWorkspace();
        var target = FileFor("app", "data"u8.ToArray());
        var artifact = new UpdateFileArtifact(target, 4, target.Sha256, UpdateBlobEncoding.Identity);
        var artifacts = new FileTreeArtifacts(Enumerable.Repeat(artifact, 100_001).ToArray(), new string('a', 64));
        using var downloader = new ResumableAssetDownloader(new Handler(_ => throw new InvalidOperationException()));
        var error = await Assert.ThrowsAsync<FileTreeException>(() => new FileTreeStager(downloader, new())
            .StageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, Sources));
        Assert.Equal("update.manifestTooLarge", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    private static ManifestFile FileFor(string path, byte[] bytes) => new(path, bytes.Length, Hash(bytes));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static IReadOnlyList<AssetDownloadSource> Sources(UpdateFileArtifact artifact) =>
        [new("direct", new Uri("https://github.com/blobs/" + artifact.DownloadSha256 + ".blob"))];
    private static Handler Blobs(string root, List<string> requests) => new(request =>
    {
        var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
        requests.Add(name);
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(root, name))) };
    });
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
