using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Infrastructure.Updates;
using MabiTool.ReleaseBuilder;

try
{
    if (args.Length < 1) throw new ReleaseBuildException("release.commandRequired");
    var command = args[0];
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Length; index += 2)
        if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[index][2..], args[index + 1]))
            throw new ReleaseBuildException("release.argumentsInvalid");
    string Required(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ReleaseBuildException("release.argumentRequired:" + name);
    var trustedRoot = ReleaseJson.Read<TrustRoot>(Required("trust-root"));
    var signers = new List<SigningKey>();
    try
    {
        if (command is "build" or "index")
        {
            var role = command == "build" ? "release" : "index";
            var entries = options.TryGetValue(role + "-signers", out var signersPath)
                ? ReleaseJson.Read<SignerInput[]>(signersPath)
                : [new SignerInput(Required(role + "-key-id"), Required(role + "-key"))];
            foreach (var entry in entries)
            {
                ReleaseFiles.RequireSafePath(entry.PemPath);
                if (new FileInfo(entry.PemPath).Length > 64 * 1024) throw new ReleaseBuildException("release.signersInvalid");
                var signer = ECDsa.Create();
                try { signer.ImportFromPem(File.ReadAllText(entry.PemPath)); }
                catch { signer.Dispose(); throw; }
                signers.Add(new(entry.KeyId, signer, Path.GetFullPath(entry.PemPath)));
            }
        }
        object result = command switch
        {
            "build" => await new ReleaseBuilderService().BuildAsync(ReleaseJson.Read<ReleaseBuildInput>(Required("input")),
                Path.GetDirectoryName(Path.GetFullPath(Required("input")))!, Required("output"), signers, trustedRoot),
            "verify" => (await new ReleaseBuilderService().VerifyAsync(Required("release"), trustedRoot)).Receipt,
            "index" => await new ChannelPublisher().PublishAsync(Required("release"), Required("channel-directory"), trustedRoot,
                signers, long.Parse(Required("index-sequence"), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(Required("issued-at"), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(Required("expires-at"), System.Globalization.CultureInfo.InvariantCulture)),
            _ => throw new ReleaseBuildException("release.commandUnsupported")
        };
        Console.WriteLine(JsonSerializer.Serialize(result, ReleaseJson.Options));
    }
    finally { foreach (var signer in signers) signer.Key.Dispose(); }
    return 0;
}
catch (Exception error)
{
    // 私钥路径、输入正文与异常堆栈不进入发行输出。
    Console.Error.WriteLine(error switch
    {
        ReleaseBuildException build => build.Code,
        TrustValidationException trust => trust.Code,
        FileTreeException tree => tree.Code,
        OperationCanceledException => "release.cancelled",
        _ => "release.failed:" + error.GetType().Name
    });
    return 1;
}

internal sealed record SignerInput(string KeyId, string PemPath);
