using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Infrastructure.Workers;
using MabiTool.LifeVision.Worker;
using Xunit;

namespace MabiTool.LifeVision.Tests;

/// <summary>模板与夹具随仓库分发，登记文件记录来源、字节数与 SHA-256；登记与内容不符、目录里有未登记的 PNG 即失败。</summary>
public sealed class AssetRegistryTests
{
    private static string Root => TestAssets.RepositoryRoot();

    private static void AssertRegistered(string baseDirectory, string lockFile, string imagesDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(lockFile));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Nexon / devCAT", document.RootElement.GetProperty("vendor").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("copyright").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("source").GetString()));
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString()!;
            Assert.True(registered.Add(relative), "重复登记：" + relative);
            Assert.False(string.IsNullOrWhiteSpace(file.GetProperty("origin").GetString()));
            var bytes = File.ReadAllBytes(Path.Combine(baseDirectory, relative));
            Assert.Equal(file.GetProperty("length").GetInt64(), bytes.LongLength);
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        var present = Directory.GetFiles(imagesDirectory, "*.png", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(baseDirectory, path).Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(registered.Order(), present.Order());
    }

    [Fact]
    public void TemplatesMatchTheirRegistration()
    {
        var assets = Path.Combine(Root, "workers", "life-vision", "Assets");
        AssertRegistered(assets, Path.Combine(assets, "templates.lock.json"), Path.Combine(assets, "templates"));
        // 登记的模板恰是识别用的 8 张。
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(assets, "templates.lock.json")));
        Assert.Equal(FishingProfile.Templates.Select(spec => "templates/" + spec.File).Order(),
            document.RootElement.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("path").GetString()!).Order());
    }

    [Fact]
    public void FixturesMatchTheirRegistration()
    {
        var fixtures = Path.Combine(Root, "tests", "Providers", "MabiTool.LifeVision.Tests", "Fixtures");
        AssertRegistered(fixtures, Path.Combine(fixtures, "fixtures.lock.json"), fixtures);
    }

    [Fact]
    public void BuildOutputCarriesTheRegisteredTemplates()
    {
        // 输出目录的 templates/ 与登记文件同在，TemplateCatalog 能完整加载。
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, TemplateCatalog.FolderName, TemplateCatalog.LockFileName)));
        Assert.NotNull(TestAssets.Catalog.Get(FishingProfile.Hook));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrReplacedTemplateFailsAsComponentMissing(bool replace)
    {
        var copy = Path.Combine(AppContext.BaseDirectory, "catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(copy, TemplateCatalog.FolderName));
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, TemplateCatalog.FolderName)))
                File.Copy(file, Path.Combine(copy, TemplateCatalog.FolderName, Path.GetFileName(file)));
            var target = Path.Combine(copy, TemplateCatalog.FolderName, FishingProfile.SceneMap.File);
            if (replace) File.WriteAllBytes(target, [.. File.ReadAllBytes(target), 0]);
            else File.Delete(target);
            var error = Assert.Throws<WorkerException>(() => TemplateCatalog.Load(copy));
            Assert.Equal("life.fishing.componentMissing", error.Code);
        }
        finally { Directory.Delete(copy, recursive: true); }
    }
}
