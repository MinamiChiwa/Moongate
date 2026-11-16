using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class TwClientFingerprintTests
{
    [Fact]
    public void BuildIdentityIsBoundedAndIncludesAllThreePinnedHashes()
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join(":",
            Tw20261110CombatProtocol.AssemblySha256, Tw20261110CombatProtocol.MetadataSha256, TwClientFingerprint.UnitySha256))));
        Assert.Equal("tw-20261110:" + digest, TwClientFingerprint.GameBuild);
        Assert.True(TwClientFingerprint.GameBuild.Length <= 128);
    }

    [Fact]
    public async Task MatchingFilesPassTheInjectedBaselineButNotTheRealClientBaseline()
    {
        using var area = new Area();
        Assert.True(await TwClientFingerprint.MatchesFilesAsync(area.Image, area.Hashes, CancellationToken.None));
        Assert.False(await TwClientFingerprint.MatchesAsync(area.Image, CancellationToken.None));
    }

    [Theory]
    [InlineData("GameAssembly.dll")]
    [InlineData("UnityPlayer.dll")]
    [InlineData("MabinogiMobile_Data/il2cpp_data/Metadata/global-metadata.dat")]
    public async Task MissingOrTamperedFilesFailClosed(string relative)
    {
        using var area = new Area(); var path = Path.Combine(area.Root, relative);
        var before = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, [5, 4, 3, 2]); File.SetLastWriteTimeUtc(path, before);
        Assert.False(await TwClientFingerprint.MatchesFilesAsync(area.Image, area.Hashes, CancellationToken.None));
        File.Delete(path);
        Assert.False(await TwClientFingerprint.MatchesFilesAsync(area.Image, area.Hashes, CancellationToken.None));
        Assert.Null(TwClientFingerprint.ReadFileStamps(area.Image));
    }

    [Fact]
    public async Task BaselineCannotAddOrSubstituteAnUnregisteredPath()
    {
        using var area = new Area();
        var altered = new Dictionary<string, string>(area.Hashes);
        altered.Remove("UnityPlayer.dll"); altered["../UnityPlayer.dll"] = area.Hashes["UnityPlayer.dll"];
        Assert.False(await TwClientFingerprint.MatchesFilesAsync(area.Image, altered, CancellationToken.None));
        Assert.False(await TwClientFingerprint.MatchesFilesAsync(TwClientFingerprint.ImageName, area.Hashes, CancellationToken.None));
    }

    [Fact]
    public async Task FilesBeingWrittenCannotBecomeVerified()
    {
        using var area = new Area();
        await using var writer = new FileStream(Path.Combine(area.Root, "GameAssembly.dll"), FileMode.Open,
            FileAccess.Write, FileShare.ReadWrite);
        Assert.False(await TwClientFingerprint.MatchesFilesAsync(area.Image, area.Hashes, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationPropagatesBeforeReadingClientFiles()
    {
        using var area = new Area(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TwClientFingerprint.MatchesFilesAsync(area.Image, area.Hashes, cancel.Token));
    }

    [Fact]
    public void FileStampChangesWithdrawThePreparedEvidence()
    {
        using var area = new Area(); var before = TwClientFingerprint.ReadFileStamps(area.Image)!;
        File.AppendAllText(Path.Combine(area.Root, "UnityPlayer.dll"), "changed");
        var after = TwClientFingerprint.ReadFileStamps(area.Image)!;
        Assert.False(before.SequenceEqual(after));
    }

    [Fact]
    public async Task LinkedClientDirectoriesAreRejectedBeforeHashing()
    {
        using var area = new Area(); var link = area.Root + "-link";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/d /c mklink /J \"{link}\" \"{area.Root}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
                };
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await error); await output;
            }
            else Directory.CreateSymbolicLink(link, area.Root);
            var image = Path.Combine(link, TwClientFingerprint.ImageName);
            Assert.Null(TwClientFingerprint.ReadFileStamps(image));
            Assert.False(await TwClientFingerprint.MatchesFilesAsync(image, area.Hashes, CancellationToken.None));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    private sealed class Area : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mabitool-client-fingerprint-" + Guid.NewGuid().ToString("N"));
        public string Image => Path.Combine(Root, TwClientFingerprint.ImageName);
        public IReadOnlyDictionary<string, string> Hashes { get; }
        public Area()
        {
            Directory.CreateDirectory(Root); File.WriteAllBytes(Image, [1]);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var relative in new[] { "GameAssembly.dll", "UnityPlayer.dll", "MabinogiMobile_Data/il2cpp_data/Metadata/global-metadata.dat" })
            {
                var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [1, 2, 3, 4]); hashes.Add(relative, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            }
            Hashes = hashes;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
