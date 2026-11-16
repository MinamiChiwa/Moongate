using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

internal sealed partial class InstallTransaction
{
    internal static byte[] SerializeInitialPointer(InstallVersion installed) =>
        JsonSerializer.SerializeToUtf8Bytes(Pointer(FreezeVersion(installed), null), JsonOptions);

    internal static byte[] SerializeInitialInstalledRecord(InstallVersion installed)
    {
        var version = FreezeVersion(installed);
        return JsonSerializer.SerializeToUtf8Bytes(new InstalledVersionRecord(1, version, Pointer(version, null)), JsonOptions);
    }

    internal Task<ActiveVersionPointer> InitializeOrResumeInitialPointerAsync(InstallVersion installed,
        Func<InitialInstallPersistencePoint, Task>? observed = null, CancellationToken token = default) => RunAsync(async () =>
    {
        var version = FreezeVersion(installed);
        if (Exists(journalPath)) throw new UpdatePlanException("update.installationAlreadyInitialized");
        await VerifyVersionAsync(version, token).ConfigureAwait(false);
        var pointer = Pointer(version, null); var expected = new InstalledVersionRecord(1, version, pointer);
        var installedPath = UpdatePaths.Local(stateRoot, "installed-version.json");
        if (Exists(installedPath) && !Same(await ReadDocumentAsync<InstalledVersionRecord>(installedPath, MaximumDocumentBytes, token).ConfigureAwait(false), expected) ||
            Exists(pointerPath) && !Same(await ReadPointerAsync(token).ConfigureAwait(false), pointer))
            throw new UpdatePlanException("update.initialInstallInputMismatch");
        if (!Exists(installedPath)) await PersistAsync(installedPath, expected, false, token).ConfigureAwait(false);
        if (observed is not null) await observed(InitialInstallPersistencePoint.InstalledRecordPublished).ConfigureAwait(false);
        if (!Exists(pointerPath)) await PersistAsync(pointerPath, pointer, false, token).ConfigureAwait(false);
        if (observed is not null) await observed(InitialInstallPersistencePoint.PointerPublished).ConfigureAwait(false);
        return pointer;
    }, token);
}
