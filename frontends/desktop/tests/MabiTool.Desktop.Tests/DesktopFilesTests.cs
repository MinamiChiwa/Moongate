using System.Text.Json.Nodes;
using MabiTool.Desktop.Infrastructure;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>前端文件位于工具根：旧位置的前端文件只在工具根还没有 desktop 目录时迁入一次，旧目录保持原样。</summary>
public sealed class DesktopFilesTests
{
    [Fact]
    public void LegacyFolderIsCopiedOnceAndLeftInPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), "moongate-desktop-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var legacy = Path.Combine(root, "legacy");
            var target = Path.Combine(root, "tool", "desktop");
            Directory.CreateDirectory(Path.Combine(legacy, "assets", "pack"));
            File.WriteAllText(Path.Combine(legacy, "assets", "pack", "pack.json"), "{}");
            File.WriteAllText(Path.Combine(legacy, "inventory-notes.json"), """{ "a": { "k": "备注" } }""");
            File.WriteAllText(Path.Combine(legacy, "preferences.json"),
                new JsonObject { ["Language"] = "TraditionalChinese", ["AssetPack"] = Path.Combine(legacy, "assets", "pack") }.ToJsonString());
            var before = Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);

            Assert.Equal(3, DesktopFiles.ImportLegacy(legacy, target));

            Assert.Equal("""{ "a": { "k": "备注" } }""", File.ReadAllText(Path.Combine(target, "inventory-notes.json")));
            Assert.True(File.Exists(Path.Combine(target, "assets", "pack", "pack.json")));
            var preferences = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "preferences.json")))!;
            Assert.Equal(Path.Combine(target, "assets", "pack"), (string?)preferences["AssetPack"]);
            Assert.Equal("TraditionalChinese", (string?)preferences["Language"]);
            Assert.False(Directory.Exists(target + ".import"));
            Assert.Equal(before, Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText));

            // 工具根已有 desktop 目录：不再读取旧位置。
            File.WriteAllText(Path.Combine(legacy, "inventory-notes.json"), "{}");
            Assert.Equal(0, DesktopFiles.ImportLegacy(legacy, target));
            Assert.Equal("""{ "a": { "k": "备注" } }""", File.ReadAllText(Path.Combine(target, "inventory-notes.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
