using System.IO.Compression;
using System.Text;
using MabiTool.Host.Extensions;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>扩展模块包检查：合规包的清单与文件、路径穿越与清单不合规的拒绝原因。</summary>
public sealed class ExtensionPackageInspectorTests
{
    private static readonly Version HostApi = new(1, 0);

    private const string ValidManifest = """
        {
          "schemaVersion": 1,
          "id": "example.fishing-timer",
          "version": "1.0.0",
          "hostApi": { "minimum": "1.0", "maximumExclusive": "2.0" },
          "displayNames": { "zh-Hans": "钓鱼计时", "zh-Hant": "釣魚計時" },
          "provides": ["example.fishing-timer.timer"],
          "permissions": ["capture.selected-window"],
          "dependencies": [{ "id": "game-access", "minimumVersion": "0.1.0" }]
        }
        """;

    [Fact]
    public async Task ValidPackageReturnsManifestFilesAndTreeHashWithoutExecutableContent()
    {
        using var package = Zip(("extension.json", ValidManifest), ("assets/icon.png", "png-bytes"));

        var result = await ExtensionPackageInspector.InspectAsync(package, HostApi, CancellationToken.None);

        Assert.Equal("example.fishing-timer", result.Manifest.Id);
        Assert.Equal(new Version(1, 0, 0), result.Manifest.Version);
        Assert.Equal(["capture.selected-window"], result.Manifest.Permissions);
        Assert.Equal(["assets/icon.png", "extension.json"], result.Files.Select(x => x.Path));
        Assert.All(result.Files, file => Assert.Equal(64, file.Sha256.Length));
        Assert.Equal(64, result.TreeSha256.Length);
        Assert.False(result.ContainsExecutableCode);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("assets/../../evil.txt")]
    [InlineData("assets\\..\\evil.txt")]
    [InlineData("/abs.txt")]
    public async Task UnsafeEntryPathIsRejected(string path)
    {
        using var package = Zip(("extension.json", ValidManifest), (path, "x"));

        var error = await Assert.ThrowsAsync<ExtensionPackageException>(() =>
            ExtensionPackageInspector.InspectAsync(package, HostApi, CancellationToken.None));

        Assert.Equal("extension.pathRejected", error.Code);
    }

    [Theory]
    [InlineData("\"id\": \"example.fishing-timer\"", "\"id\": \"fishing-timer\"", "extension.manifestInvalid")]
    [InlineData("\"example.fishing-timer.timer\"", "\"combat.summary\"", "extension.manifestInvalid")]
    [InlineData("\"capture.selected-window\"", "\"filesystem.all\"", "extension.permissionUnknown")]
    [InlineData("\"maximumExclusive\": \"2.0\"", "\"maximumExclusive\": \"1.0\"", "extension.manifestInvalid")]
    [InlineData("\"minimum\": \"1.0\"", "\"minimum\": \"1.5\"", "extension.hostApiIncompatible")]
    [InlineData("\"permissions\": [\"capture.selected-window\"]", "\"permissions\": [\"process.read.selected-game\"]", "extension.manifestInvalid")]
    [InlineData("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"entry\": \"main.dll\",", "extension.manifestInvalid")]
    public async Task NonConformingManifestIsRejectedWithReason(string original, string replacement, string code)
    {
        Assert.Contains(original, ValidManifest);
        using var package = Zip(("extension.json", ValidManifest.Replace(original, replacement)));

        var error = await Assert.ThrowsAsync<ExtensionPackageException>(() =>
            ExtensionPackageInspector.InspectAsync(package, HostApi, CancellationToken.None));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task MissingManifestIsRejected()
    {
        using var package = Zip(("assets/icon.png", "png-bytes"));

        var error = await Assert.ThrowsAsync<ExtensionPackageException>(() =>
            ExtensionPackageInspector.InspectAsync(package, HostApi, CancellationToken.None));

        Assert.Equal("extension.manifestMissing", error.Code);
    }

    private static MemoryStream Zip(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (path, content) in entries)
            {
                using var writer = archive.CreateEntry(path).Open();
                writer.Write(Encoding.UTF8.GetBytes(content));
            }
        stream.Position = 0;
        return stream;
    }
}
