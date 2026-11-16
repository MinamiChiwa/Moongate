using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

public sealed partial class UpdateTrustStore
{
    private FileStream AcquireWriter()
    {
        try
        {
            UpdatePaths.RejectLinks(statePath);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var path = statePath + ".lock";
            UpdatePaths.RejectLinks(path);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { throw new TrustValidationException("update.trustStateBusy"); }
        catch (IOException) { throw new TrustValidationException("update.trustStateUnavailable"); }
        catch (UnauthorizedAccessException) { throw new TrustValidationException("update.trustStateUnavailable"); }
    }

    private TrustStateDocument ReadState()
    {
        try
        {
            UpdatePaths.RejectLinks(statePath);
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 1_048_576) throw new TrustValidationException("update.trustStateInvalid");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            using var document = StrictJson.Parse(bytes);
            var loaded = document.RootElement.Deserialize<TrustStateDocument>(JsonOptions)
                ?? throw new TrustValidationException("update.trustStateInvalid");
            ValidateState(loaded);
            return Freeze(loaded);
        }
        catch (JsonException) { throw new TrustValidationException("update.trustStateInvalid"); }
        catch (IOException) { throw new TrustValidationException("update.trustStateUnavailable"); }
        catch (UnauthorizedAccessException) { throw new TrustValidationException("update.trustStateUnavailable"); }
    }

    private static void ValidateState(TrustStateDocument value)
    {
        ValidateRoot(value.Root);
        if (value.Channels is null || value.Channels.Count > 32 || value.LastObservedUtc == default ||
            value.ProductId is not null && !SafeId(value.ProductId) || value.Releases?.Count > 32)
            throw new TrustValidationException("update.trustStateInvalid");
        foreach (var (channel, item) in value.Channels)
        {
            if (!SafeId(channel) || channel.Length > 32 || item is null || item.IndexSequence < 0 || !StateHash(item.PayloadSha256) ||
                item.AcceptedAt == default || item.AcceptedAt > value.LastObservedUtc ||
                item.TargetReleaseSequence is < 0 || item.TargetManifestLength is < 1 or > ReleaseManifestParser.MaximumEnvelopeBytes)
                throw new TrustValidationException("update.trustStateInvalid");
            var target = item.TargetReleaseSequence.HasValue;
            if (target != (item.TargetManifestSha256 is not null) || target != item.TargetManifestLength.HasValue ||
                target && !StateHash(item.TargetManifestSha256!)) throw new TrustValidationException("update.trustStateInvalid");
        }
        foreach (var (channel, release) in value.Releases ?? new Dictionary<string, ReleaseTrustState>())
        {
            if (release is null || release.ReleaseSequence < 0 || !StateHash(release.ManifestSha256) ||
                !value.Channels.TryGetValue(channel, out var index) || index.TargetReleaseSequence is not { } sequence ||
                release.ReleaseSequence > sequence || release.ReleaseSequence == sequence && release.ManifestSha256 != index.TargetManifestSha256)
                throw new TrustValidationException("update.trustStateInvalid");
        }
        if (value.Channels.Values.Any(item => item.TargetReleaseSequence.HasValue) && value.ProductId is null)
            throw new TrustValidationException("update.trustStateInvalid");
    }

    private static bool StateHash(string? value) => value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void Commit(TrustStateDocument candidate)
    {
        var frozen = Freeze(candidate);
        ValidateState(frozen);
        Persist(frozen);
        state = frozen;
    }

    private void Persist(TrustStateDocument candidate)
    {
        var temporary = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            UpdatePaths.RejectLinks(statePath);
            UpdatePaths.RejectLinks(temporary);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, candidate, JsonOptions);
                output.Flush(true);
            }
            UpdatePaths.RejectLinks(statePath);
            File.Move(temporary, statePath, true);
        }
        catch (IOException) { throw new TrustValidationException("update.trustStateUnavailable"); }
        catch (UnauthorizedAccessException) { throw new TrustValidationException("update.trustStateUnavailable"); }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
