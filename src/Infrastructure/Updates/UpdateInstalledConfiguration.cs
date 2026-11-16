using System.Text.Json;
using System.Text.Json.Nodes;

namespace MabiTool.Infrastructure.Updates;

internal static class UpdateInstalledConfiguration
{
    internal static string PathFor(string installationRoot) => UpdatePaths.Local(installationRoot, "launcher/install-configuration.json");

    internal static async Task SaveAsync(UpdateInstallIntent intent, string outcome, CancellationToken token = default)
    {
        var json = JsonNode.Parse(intent.InstallationConfiguration)!.AsObject();
        if (outcome == "committed")
        {
            json["installedReleaseId"] = intent.Plan.ReleaseId;
            json["installedReleaseSequence"] = intent.Plan.ReleaseSequence;
            json["installedManifestSha256"] = intent.Plan.ManifestSha256;
            var context = intent.Validation.Context with
            {
                HostVersion = Version.Parse(intent.CandidateTarget.HostVersion), DataSchema = intent.CandidateTarget.DataSchema,
                InstalledComponents = intent.Plan.Components.Select(item => new InstalledUpdateComponent(item.ComponentId,
                    item.Version, item.InstalledBytes, item.Sha256)).ToArray()
            };
            json["plannerContext"] = JsonSerializer.SerializeToNode(context, UpdateHandoffProtocol.Json);
            var updater = intent.Plan.Components.SingleOrDefault(item => item.ComponentId == "updater");
            if (updater is not null) json["runtime"] = JsonSerializer.SerializeToNode(intent.Validation.Runtime with { UpdaterVersion = updater.Version }, UpdateHandoffProtocol.Json);
        }
        var path = PathFor(intent.InstallationRoot); var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await JsonSerializer.SerializeAsync(output, json, UpdateHandoffProtocol.Json, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            if (File.Exists(path)) await UpdatePaths.ReplaceOwnedFileAsync(temporary, path, token).ConfigureAwait(false);
            else File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
