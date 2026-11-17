using System.IO.Compression;
using System.Text;
using MabiTool.Host.Extensions;
using MabiTool.Infrastructure;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>纯数据扩展的安装、拒绝、中断收尾、启停持久、卸载与损坏项识别。</summary>
public sealed class ExtensionInstallerTests : IDisposable
{
    private static readonly Version HostApi = new(1, 0);
    private readonly string root = Path.Combine(Path.GetTempPath(), "moongate-extension-tests-" + Guid.NewGuid().ToString("N"));

    private static string Manifest(string id = "example.pack", string version = "1.0.0", string extra = "") => $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "version": "{{version}}",
          "hostApi": { "minimum": "1.0", "maximumExclusive": "2.0" },
          "displayNames": { "zh-Hans": "示例素材" },
          "assetPack": "assets/pack.json"{{extra}}
        }
        """;

    [Fact]
    public async Task InstallsDataOnlyPackageDisabledAndKeepsEnabledStateAcrossRestart()
    {
        using var installer = Started();
        var preview = await Preview(installer, Package(("extension.json", Manifest()), ("assets/pack.json", "{}"), ("assets/icon.png", "png")));
        Assert.Equal("install", preview.Action);
        Assert.Equal(("example.pack", "1.0.0", "example", 3, "unsigned"),
            (preview.Package.Id, preview.Package.Version, preview.Package.Publisher, preview.Package.FileCount, preview.Package.Signature));

        var installed = await installer.InstallAsync(preview.Id, includePaths: true, CancellationToken.None);
        Assert.Equal(("ready", false, (string?)null), (installed.State, installed.Enabled, installed.AssetPackDirectory));
        Assert.True(File.Exists(Path.Combine(root, "example.pack", "1.0.0", "assets", "icon.png")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ExtensionInstaller.StagingName)));

        await installer.SetEnabledAsync("example.pack", true, includePaths: true, CancellationToken.None);
        using var restarted = Started();
        var listed = Assert.Single(restarted.Catalog(includePaths: true).Installed);
        Assert.True(listed.Enabled);
        Assert.Equal(Path.Combine(root, "example.pack", "1.0.0", "assets"), listed.AssetPackDirectory);
        Assert.Null(Assert.Single(restarted.Catalog(includePaths: false).Installed).AssetPackDirectory);
    }

    [Theory]
    [InlineData("bin/plugin.dll", "")]
    [InlineData("assets/readme.txt", ",\n  \"permissions\": [\"capture.selected-window\"]")]
    public async Task RejectsPackagesThatCarryCode(string file, string extra)
    {
        using var installer = Started();
        var error = await Assert.ThrowsAsync<OperationException>(() =>
            Preview(installer, Package(("extension.json", Manifest(extra: extra)), ("assets/pack.json", "{}"), (file, "content"))));
        Assert.Equal("extension.codeNotSupported", error.Code);
        Assert.Equal(ExtensionInstaller.CodeNotSupportedMessage, error.Message);
        Assert.Empty(installer.Catalog(includePaths: false).Installed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ExtensionInstaller.StagingName)));
    }

    [Fact]
    public async Task CodeExtensionUpgradeMarksAddedPermissionsAndKeepsHighRiskOff()
    {
        static string Code(string version, string permissions) => $$"""
            {
              "schemaVersion": 1, "id": "example.code", "version": "{{version}}",
              "hostApi": { "minimum": "1.0", "maximumExclusive": "2.0" },
              "displayNames": { "zh-Hans": "示例程序" },
              "runtime": { "protocol": 1, "kind": "executable", "entry": "bin/run.exe" },
              "permissions": [{{permissions}}]
            }
            """;
        using var installer = Started();
        var first = await Preview(installer, Package(("extension.json", Code("1.0.0", "\"combat.read\"")), ("bin/run.exe", "MZ")));
        Assert.Equal(("executable", "bin/run.exe"), (first.Package.Runtime!.Kind, first.Package.Runtime.Entry));
        await installer.InstallAsync(first.Id, includePaths: false, CancellationToken.None);
        Assert.True(Assert.Single(installer.Get("example.code", false).Permissions!).Granted);

        var upgrade = await Preview(installer, Package(("extension.json", Code("1.1.0", "\"combat.read\", \"ui.page\", \"capture.selected-window\"")), ("bin/run.exe", "MZ")));
        Assert.Equal([("combat.read", false), ("ui.page", true), ("capture.selected-window", true)],
            upgrade.Package.Permissions!.Select(x => (x.Id, x.Added)));
        await installer.InstallAsync(upgrade.Id, includePaths: false, CancellationToken.None);
        Assert.Equal([("combat.read", true), ("ui.page", true), ("capture.selected-window", false)],
            installer.Get("example.code", false).Permissions!.Select(x => (x.Id, x.Granted)));

        var memory = await Assert.ThrowsAsync<OperationException>(() => Preview(installer, Package(("extension.json",
            Code("1.2.0", "\"process.read.selected-game\"").Replace("\"permissions\"", "\"highRiskBehaviors\": [\"memoryRead\"], \"permissions\"")),
            ("bin/run.exe", "MZ"))));
        Assert.Equal("extension.permissionUnavailable", memory.Code);
    }

    [Fact]
    public async Task RejectsPathTraversal()
    {
        using var installer = Started();
        var error = await Assert.ThrowsAsync<OperationException>(() =>
            Preview(installer, Package(("extension.json", Manifest()), ("assets/pack.json", "{}"), ("../evil.txt", "x"))));
        Assert.Equal("extension.pathRejected", error.Code);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.txt")));
    }

    [Fact]
    public async Task CancelledOrInterruptedInstallLeavesNoPartialContent()
    {
        using (var installer = Started())
        {
            var preview = await Preview(installer, Package(("extension.json", Manifest()), ("assets/pack.json", "{}")));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                installer.InstallAsync(preview.Id, includePaths: false, new CancellationToken(canceled: true)));
            Assert.Empty(installer.Catalog(includePaths: false).Installed);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ExtensionInstaller.StagingName)));
        }

        // 模拟进程在发布途中被终止：暂存里留着解出目录，扩展目录里有发布标记和未登记的版本目录。
        Directory.CreateDirectory(Path.Combine(root, ExtensionInstaller.StagingName, "leftover.work", "assets"));
        Directory.CreateDirectory(Path.Combine(root, "example.pack", "1.0.0"));
        File.WriteAllText(Path.Combine(root, "example.pack", "installing.json"), """{"schemaVersion":1,"version":"1.0.0"}""");
        using var restarted = Started();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ExtensionInstaller.StagingName)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "example.pack")));
    }

    [Fact]
    public async Task UninstallRemovesTheExtensionDirectory()
    {
        using var installer = Started();
        var preview = await Preview(installer, Package(("extension.json", Manifest()), ("assets/pack.json", "{}")));
        await installer.InstallAsync(preview.Id, includePaths: false, CancellationToken.None);

        var receipt = await installer.UninstallAsync("example.pack", CancellationToken.None);

        Assert.Equal(("example.pack", (string?)"1.0.0"), (receipt.Id, receipt.Version));
        Assert.False(Directory.Exists(Path.Combine(root, "example.pack")));
        Assert.Empty(installer.Catalog(includePaths: false).Installed);
    }

    [Fact]
    public async Task DamagedInstallsAreListedWithReasonsWithoutStoppingStartup()
    {
        using (var installer = Started())
            foreach (var id in new[] { "example.broken", "example.missing" })
            {
                var preview = await Preview(installer, Package(("extension.json", Manifest(id)), ("assets/pack.json", "{}")));
                await installer.InstallAsync(preview.Id, includePaths: false, CancellationToken.None);
            }
        File.WriteAllText(Path.Combine(root, "example.broken", "state.json"), "{ not json");
        Directory.Delete(Path.Combine(root, "example.missing", "1.0.0"), recursive: true);

        using var restarted = Started();
        var listed = restarted.Catalog(includePaths: true).Installed.ToDictionary(x => x.Id);
        Assert.Equal(("damaged", (string?)"extension.installDamaged", (string?)"安装记录缺失或损坏"),
            (listed["example.broken"].State, listed["example.broken"].ProblemCode, listed["example.broken"].Problem));
        Assert.Equal(("damaged", (string?)"安装目录缺失"), (listed["example.missing"].State, listed["example.missing"].Problem));
        Assert.True(Directory.Exists(Path.Combine(root, "example.broken", "1.0.0")));
        var error = await Assert.ThrowsAsync<OperationException>(() =>
            restarted.SetEnabledAsync("example.missing", true, includePaths: false, CancellationToken.None));
        Assert.Equal("extension.installDamaged", error.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private ExtensionInstaller Started()
    {
        var installer = new ExtensionInstaller(root, HostApi);
        installer.Initialize();
        return installer;
    }

    private static async Task<MabiTool.Contracts.ExtensionInstallPreview> Preview(ExtensionInstaller installer, MemoryStream package)
    {
        using (package) return await installer.PreviewAsync(package, "upload", null, CancellationToken.None);
    }

    private static MemoryStream Package(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var writer = archive.CreateEntry(name).Open();
                writer.Write(Encoding.UTF8.GetBytes(content));
            }
        stream.Position = 0;
        return stream;
    }
}
