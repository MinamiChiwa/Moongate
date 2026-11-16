using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class FileTreePackageTests
{
    [Fact]
    public async Task Manifest_identifies_managed_and_native_entries_without_using_extensions()
    {
        using var workspace = new UpdateTestWorkspace();
        var release = workspace.PathFor("entries");
        Directory.CreateDirectory(release);
        var managed = ManagedEntry();
        await File.WriteAllBytesAsync(Path.Combine(release, "entry.dll"), managed);
        var managedExe = (byte[])managed.Clone();
        using (var reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(managedExe)))
            BinaryPrimitives.WriteUInt16LittleEndian(managedExe.AsSpan(reader.PEHeaders.PEHeaderStartOffset - 2), 0x0002);
        await File.WriteAllBytesAsync(Path.Combine(release, "entry.exe"), managedExe);
        File.Copy(typeof(FileTreeArtifactBuilder).Assembly.Location, Path.Combine(release, "library.exe"));
        await File.WriteAllTextAsync(Path.Combine(release, "fake.dll"), "MZ invalid PE");
        await File.WriteAllTextAsync(Path.Combine(release, "data"), "content");
        foreach (var dll in new[] { false, true })
        {
            var native = (byte[])managed.Clone();
            using var reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(native));
            var headers = reader.PEHeaders;
            var optional = headers.PEHeaderStartOffset;
            // Remove the CLR directory while retaining a mapped native entry address.
            var directories = optional + (headers.PEHeader!.Magic == System.Reflection.PortableExecutable.PEMagic.PE32 ? 96 : 112);
            Array.Clear(native, directories + 14 * 8, 8);
            BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(optional - 2), (ushort)(0x0002 | (dll ? 0x2000 : 0)));
            await File.WriteAllBytesAsync(Path.Combine(release, dll ? "native.dll" : "native.exe"), native);
        }
        var artifacts = await new FileTreeArtifactBuilder().BuildAsync(release, workspace.PathFor("entries-built"));
        var flags = artifacts.Files.ToDictionary(item => item.File.RelativePath, item => item.File.Executable);
        Assert.True(flags["entry.dll"]);
        Assert.True(flags["entry.exe"]);
        Assert.True(flags["native.exe"]);
        Assert.False(flags["native.dll"]);
        Assert.False(flags["library.exe"]);
        Assert.False(flags["fake.dll"]);
        Assert.False(flags["data"]);
        foreach (var artifact in artifacts.Files)
            Assert.Equal(Hash(await File.ReadAllBytesAsync(Path.Combine(release, artifact.File.RelativePath))), artifact.File.Sha256);
        await FileTreePlanner.VerifyAsync(release, artifacts.Files.Select(item => item.File).ToArray());
    }

    private static byte[] ManagedEntry()
    {
        var metadata = new System.Reflection.Metadata.Ecma335.MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("entry"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("entry"), new Version(1, 0), default, default, 0, System.Reflection.AssemblyHashAlgorithm.None);
        var signature = new System.Reflection.Metadata.BlobBuilder();
        signature.WriteBytes(new byte[] { 0, 0, 1 });
        var il = new System.Reflection.Metadata.BlobBuilder();
        var instructions = new System.Reflection.Metadata.BlobBuilder();
        instructions.WriteByte(0x2a);
        var body = new System.Reflection.Metadata.Ecma335.MethodBodyStreamEncoder(il).AddMethodBody(
            new System.Reflection.Metadata.Ecma335.InstructionEncoder(instructions));
        var method = metadata.AddMethodDefinition(System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            System.Reflection.MethodImplAttributes.IL, metadata.GetOrAddString("Main"), metadata.GetOrAddBlob(signature), body,
            System.Reflection.Metadata.Ecma335.MetadataTokens.ParameterHandle(1));
        metadata.AddTypeDefinition(System.Reflection.TypeAttributes.Public, default, metadata.GetOrAddString("Program"), default,
            System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle(1), method);
        var builder = new System.Reflection.PortableExecutable.ManagedPEBuilder(
            new System.Reflection.PortableExecutable.PEHeaderBuilder(imageCharacteristics:
                System.Reflection.PortableExecutable.Characteristics.ExecutableImage | System.Reflection.PortableExecutable.Characteristics.Dll),
            new System.Reflection.Metadata.Ecma335.MetadataRootBuilder(metadata), il, entryPoint: method);
        var bytes = new System.Reflection.Metadata.BlobBuilder();
        builder.Serialize(bytes);
        return bytes.ToArray();
    }
    [Fact]
    public async Task Missing_file_assets_fall_back_to_verified_full_package_with_identical_tree()
    {
        using var workspace = new UpdateTestWorkspace();
        var release = workspace.PathFor("release");
        Directory.CreateDirectory(Path.Combine(release, "nested"));
        File.WriteAllText(Path.Combine(release, "app"), "application");
        File.WriteAllText(Path.Combine(release, "nested", "data"), "configuration");
        var artifacts = await new FileTreeArtifactBuilder().BuildAsync(release, workspace.PathFor("built"));
        var package = await File.ReadAllBytesAsync(workspace.PathFor("built/full.zip"));
        Assert.Equal(Hash(package), artifacts.FullPackage!.DownloadSha256);
        var requests = new List<string>();
        using var downloader = new ResumableAssetDownloader(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/full" ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(package) } : new(HttpStatusCode.NotFound);
        }));
        var stager = new FileTreeStager(downloader, new());
        var fallback = await stager.StageWithFallbackAsync(workspace.PathFor("current"), workspace.PathFor("fallback"), workspace.PathFor("cache"), artifacts,
            _ => Sources("missing"), _ => Sources("full"));
        Assert.Equal(new[] { "/missing", "/full" }, requests);
        var full = await stager.StagePackageAsync(workspace.PathFor("current"), workspace.PathFor("full"), workspace.PathFor("cache"), artifacts, _ => Sources("full"));
        Assert.Equal(2, requests.Count); // 第二次完整重建复用已验证内容缓存。
        Assert.Equal(fallback.TargetTreeHash, full.TargetTreeHash);
        await FileTreePlanner.VerifyAsync(fallback.StageRoot, artifacts.Files.Select(item => item.File).ToArray());
        await FileTreePlanner.VerifyAsync(full.StageRoot, artifacts.Files.Select(item => item.File).ToArray());
        Assert.Empty(Directory.GetDirectories(workspace.Root, "*.work-*"));
    }

    [Theory]
    [InlineData("../escape", false)]
    [InlineData("app", true)]
    [InlineData("extra", false)]
    [InlineData("APP", false)]
    [InlineData("CON.txt", false)]
    public async Task Rejects_traversal_symlink_extra_case_duplicate_and_reserved_entries(string name, bool symlink)
    {
        using var workspace = new UpdateTestWorkspace();
        var bytes = Package(archive =>
        {
            if (name == "APP") Add(archive, "app", "data");
            var entry = Add(archive, name, "data");
            if (symlink) entry.ExternalAttributes = unchecked((int)0xa1ff0000);
        });
        var artifacts = Artifacts(workspace, bytes);
        if (name == "APP")
        {
            var other = artifacts.Files[0].File with { RelativePath = "other" };
            var files = artifacts.Files.Append(new(other, other.SizeBytes, other.Sha256, UpdateBlobEncoding.Identity)).ToArray();
            var treeHash = FileTreePlanner.Plan(workspace.PathFor("empty"), files.Select(item => item.File).ToArray()).TargetTreeHash;
            artifacts = new(files, treeHash, artifacts.FullPackage! with { TargetTreeHash = treeHash });
        }
        using var downloader = new ResumableAssetDownloader(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var error = await Assert.ThrowsAsync<FileTreeException>(() => new FileTreeStager(downloader, new())
            .StagePackageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, _ => Sources("full")));
        Assert.Equal(name is "../escape" or "CON.txt" ? "update.pathRejected" : "update.packageEntryInvalid", error.Code);
        Assert.False(Directory.Exists(workspace.PathFor("stage")));
        Assert.False(File.Exists(workspace.PathFor("escape")));
        Assert.Empty(Directory.GetDirectories(workspace.Root, "*.work-*"));
    }

    [Theory]
    [InlineData("longer than target")]
    [InlineData("evil")]
    public async Task Rejects_declared_length_or_raw_content_mismatch(string content)
    {
        using var workspace = new UpdateTestWorkspace();
        var bytes = Package(archive => Add(archive, "app", content));
        var artifacts = Artifacts(workspace, bytes);
        using var downloader = new ResumableAssetDownloader(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        await Assert.ThrowsAsync<FileTreeException>(() => new FileTreeStager(downloader, new())
            .StagePackageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, _ => Sources("full")));
        Assert.False(Directory.Exists(workspace.PathFor("stage")));
    }

    [Fact]
    public async Task Rejects_excessive_central_directory_before_archive_enumeration()
    {
        using var workspace = new UpdateTestWorkspace();
        var bytes = Package(archive => Add(archive, "app", "data"));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 10), 65 * 1024 * 1024);
        var artifacts = Artifacts(workspace, bytes);
        using var downloader = new ResumableAssetDownloader(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var error = await Assert.ThrowsAsync<FileTreeException>(() => new FileTreeStager(downloader, new())
            .StagePackageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, _ => Sources("full")));
        Assert.Equal("update.packageInvalid", error.Code);
        Assert.False(Directory.Exists(workspace.PathFor("stage")));
    }

    [Fact]
    public async Task Empty_full_package_is_valid()
    {
        using var workspace = new UpdateTestWorkspace();
        Directory.CreateDirectory(workspace.PathFor("release"));
        var artifacts = await new FileTreeArtifactBuilder().BuildAsync(workspace.PathFor("release"), workspace.PathFor("built"));
        var bytes = await File.ReadAllBytesAsync(workspace.PathFor("built/full.zip"));
        using var downloader = new ResumableAssetDownloader(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var result = await new FileTreeStager(downloader, new()).StagePackageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, _ => Sources("full"));
        Assert.Empty(Directory.GetFiles(result.StageRoot));
    }

    [Fact]
    public async Task Unsupported_patch_selection_uses_files_and_corrupt_expansion_falls_back_to_full_package()
    {
        using var workspace = new UpdateTestWorkspace();
        Directory.CreateDirectory(workspace.PathFor("release"));
        File.WriteAllText(workspace.PathFor("release/app"), "data");
        var artifacts = await new FileTreeArtifactBuilder().BuildAsync(workspace.PathFor("release"), workspace.PathFor("built"));
        var broken = "not gzip"u8.ToArray();
        artifacts = artifacts with { Files = [artifacts.Files[0] with { DownloadLength = broken.Length, DownloadSha256 = Hash(broken) }] };
        var package = await File.ReadAllBytesAsync(workspace.PathFor("built/full.zip"));
        var requests = new List<string>();
        using var downloader = new ResumableAssetDownloader(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath == "/full" ? package : broken) };
        }));
        Assert.False(FileTreeStager.Capabilities.BinaryPatch);
        Assert.Empty(FileTreeStager.Capabilities.PatchAlgorithms);
        var result = await new FileTreeStager(downloader, new()).StageSelectedAsync(UpdateArtifactKind.BinaryPatch,
            workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, _ => Sources("file"), _ => Sources("full"));
        Assert.Equal(new[] { "/file", "/full" }, requests);
        Assert.Equal("data", File.ReadAllText(Path.Combine(result.StageRoot, "app")));
    }

    [Fact]
    public async Task Accepts_single_volume_zip64_with_bounded_directory()
    {
        using var workspace = new UpdateTestWorkspace();
        var original = Package(archive => Add(archive, "app", "data"));
        var end = original.Length - 22;
        var bytes = new byte[original.Length + 76];
        original.AsSpan(0, end).CopyTo(bytes);
        var record = bytes.AsSpan(end, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], 45);
        BinaryPrimitives.WriteUInt64LittleEndian(record[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(record[32..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(record[40..], BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 12)));
        BinaryPrimitives.WriteUInt64LittleEndian(record[48..], BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 16)));
        var locator = bytes.AsSpan(end + 56, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)end);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        original.AsSpan(end).CopyTo(bytes.AsSpan(end + 76));
        var eocd = bytes.AsSpan(end + 76);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], uint.MaxValue);
        var artifacts = Artifacts(workspace, bytes);
        using var downloader = new ResumableAssetDownloader(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var result = await new FileTreeStager(downloader, new()).StagePackageAsync(workspace.PathFor("current"), workspace.PathFor("stage"), workspace.PathFor("cache"), artifacts, _ => Sources("full"));
        Assert.Equal("data", File.ReadAllText(Path.Combine(result.StageRoot, "app")));
    }

    private static FileTreeArtifacts Artifacts(UpdateTestWorkspace workspace, byte[] package)
    {
        var file = new ManifestFile("app", 4, Hash("data"u8.ToArray()));
        var hash = FileTreePlanner.Plan(workspace.PathFor("empty"), [file]).TargetTreeHash;
        return new([new(file, 4, file.Sha256, UpdateBlobEncoding.Identity)], hash, new(hash, package.Length, Hash(package)));
    }
    private static byte[] Package(Action<ZipArchive> write)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true)) write(archive);
        return stream.ToArray();
    }
    private static ZipArchiveEntry Add(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
        return entry;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static IReadOnlyList<AssetDownloadSource> Sources(string name) => [new("direct", new Uri("https://github.com/" + name))];
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
