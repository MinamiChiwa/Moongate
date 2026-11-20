using System.Security.Cryptography;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class ClientLayoutReaderTests
{
    [ClientBinariesFact]
    public void MappedReaderMatchesTheWholeFileReaderOnTheInstalledClient()
    {
        var directory = ClientBinariesFactAttribute.ClientDirectory;
        var expected = LegacyClientLayoutReader.Read(directory);
        var actual = ClientLayoutReader.Read(directory);
        Assert.Equal(expected.AssemblyHash, actual.AssemblyHash);
        Assert.Equal(expected.MetadataHash, actual.MetadataHash);
        Assert.Equal(expected.Protocol, actual.Protocol);
        Assert.Equal(expected.Opcodes.OrderBy(value => value.Key, StringComparer.Ordinal), actual.Opcodes.OrderBy(value => value.Key, StringComparer.Ordinal));
        Assert.Equal(expected.Methods.Keys.Order(), actual.Methods.Keys.Order());
        foreach (var (opcode, method) in expected.Methods)
        {
            Assert.Equal((method.Name, method.Category), (actual.Methods[opcode].Name, actual.Methods[opcode].Category));
            Assert.Equal(method.Parameters, actual.Methods[opcode].Parameters);
        }
        Assert.Equal(expected.Schema.GetRawText(), actual.Schema.GetRawText());

        var expectedFacts = LegacyClientLayoutReader.ReadBuildFacts(directory, TwBuildVerification.SchemaRoots, TwBuildVerification.Interfaces);
        var actualFacts = ClientLayoutReader.ReadBuildFacts(directory, TwBuildVerification.SchemaRoots, TwBuildVerification.Interfaces);
        Assert.Equal((expectedFacts.AssemblyHash, expectedFacts.MetadataHash), (actualFacts.AssemblyHash, actualFacts.MetadataHash));
        Assert.Equal(expectedFacts.Protocols.OrderBy(value => value.Key, StringComparer.Ordinal), actualFacts.Protocols.OrderBy(value => value.Key, StringComparer.Ordinal));
        Assert.Equal(expectedFacts.Schema.GetRawText(), actualFacts.Schema.GetRawText());
        Assert.Equal(expectedFacts.Interfaces.Keys.Order(StringComparer.Ordinal), actualFacts.Interfaces.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, methods) in expectedFacts.Interfaces)
            Assert.Equal(methods.Select(value => (value.Name, value.Category, string.Join(",", value.Parameters))),
                actualFacts.Interfaces[name].Select(value => (value.Name, value.Category, string.Join(",", value.Parameters))));

        var cache = Path.Combine(Path.GetTempPath(), "mabitool-layout-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            ClientLayoutReader.Read(directory, cacheDirectory: cache);
            var stored = Assert.Single(Directory.GetFiles(cache, "layout-*.json"));
            var restored = ClientLayoutReader.Read(directory, cacheDirectory: cache);
            Assert.Equal(expected.Schema.GetRawText(), restored.Schema.GetRawText());
            Assert.Equal(expected.Protocol, restored.Protocol);
            Assert.Equal(expected.Opcodes.OrderBy(value => value.Key, StringComparer.Ordinal), restored.Opcodes.OrderBy(value => value.Key, StringComparer.Ordinal));
            Assert.Equal(expected.Methods.Select(value => (value.Key, value.Value.Name, value.Value.Category, string.Join(",", value.Value.Parameters))).Order(),
                restored.Methods.Select(value => (value.Key, value.Value.Name, value.Value.Category, string.Join(",", value.Value.Parameters))).Order());
            Assert.Equal(expected.SupportsInventoryMutations, restored.SupportsInventoryMutations);
            // 规则版本或哈希不符的缓存文件不被采用。
            File.WriteAllText(stored, File.ReadAllText(stored).Replace("\"rulesVersion\":" + ClientLayoutReader.LayoutRulesVersion, "\"rulesVersion\":0", StringComparison.Ordinal));
            Assert.Null(ClientLayoutCache.Read(cache, expected.AssemblyHash, expected.MetadataHash));
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task StreamingHashMatchesTheWholeFileHashAndRewrittenContentIsNotReused()
    {
        var root = Path.Combine(Path.GetTempPath(), "mabitool-client-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "GameAssembly.dll");
            var content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
            File.WriteAllBytes(path, content);
            var written = File.GetLastWriteTimeUtc(path);
            string Hash() { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return ClientFileHash.Compute(stream, CancellationToken.None); }
            async Task<string> HashAsync()
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
                return await ClientFileHash.ComputeAsync(stream, CancellationToken.None);
            }
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), Hash());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), await HashAsync());
            // 同长度改写并把修改时间改回原值：不得沿用改写前的哈希。
            content[12345] ^= 0xff;
            File.WriteAllBytes(path, content); File.SetLastWriteTimeUtc(path, written);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), Hash());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), await HashAsync());
            using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
            content[0] ^= 0xff; File.WriteAllBytes(path, content);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Throws<OperationCanceledException>(() => ClientFileHash.Compute(stream, cancelled.Token));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>需要本机台服客户端的 GameAssembly.dll 与 metadata（只读）；缺少时跳过。</summary>
public sealed class ClientBinariesFactAttribute : FactAttribute
{
    public static string ClientDirectory => InstalledTwClientFactAttribute.ClientDirectory;
    public ClientBinariesFactAttribute()
    {
        if (!File.Exists(Path.Combine(ClientDirectory, "GameAssembly.dll")) ||
            !File.Exists(Path.Combine(ClientDirectory, "MabinogiMobile_Data", "il2cpp_data", "Metadata", "global-metadata.dat")))
            Skip = "需要台服客户端 GameAssembly.dll 与 global-metadata.dat；设置 MABITOOL_TEST_TW_CLIENT 指向 appdata";
    }
}
