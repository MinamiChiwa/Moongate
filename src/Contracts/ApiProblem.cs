using System.Text.Json;

namespace MabiTool.Contracts;

public sealed record ApiProblem(string Type, string Title, int Status, string Code, string MessageKey,
    string TraceId, IReadOnlyDictionary<string, JsonElement> Details, bool Retryable);

public sealed record ErrorDefinition(string Code, int Status, bool Retryable);

public static class ApiErrors
{
    public const string AuthRequired = "auth.required";
    public const string HostRejected = "auth.hostRejected";
    public const string OriginRejected = "auth.originRejected";
    public const string QueryNotSupported = "request.queryNotSupported";
    public const string ModuleNotFound = "module.notFound";
    public const string ResourceNotFound = "resource.notFound";
    public const string UnexpectedError = "system.unexpectedError";

    public static IReadOnlyList<ErrorDefinition> Definitions { get; } = Array.AsReadOnly<ErrorDefinition>([
        new(AuthRequired, 401, false), new(HostRejected, 403, false), new(OriginRejected, 403, false),
        new(QueryNotSupported, 400, false), new(ModuleNotFound, 404, false),
        new(ResourceNotFound, 404, false), new(UnexpectedError, 500, false),
        new("request.invalid", 400, false), new("permission.denied", 403, false),
        new("music.cursorStale", 409, false),
        new("import.invalidInput", 422, false), new("import.previewExpired", 409, false), new("import.conflict", 409, false),
        new("resource.busy", 409, false), new("idempotency.conflict", 409, false),
        new("revision.stale", 412, false), new("capability.unavailable", 422, false),
        new("resource.capacityExceeded", 429, true), new("provider.unavailable", 503, true),
        new("module.inUse", 409, false), new("module.disabled", 422, false), new("module.essential", 409, false),
        new("extension.limitExceeded", 422, false), new("extension.packageInvalid", 422, false), new("extension.pathRejected", 422, false),
        new("extension.manifestMissing", 422, false), new("extension.manifestInvalid", 422, false), new("extension.permissionUnknown", 422, false),
        new("extension.hostApiIncompatible", 422, false), new("extension.codeNotSupported", 422, false), new("extension.fileTypeRejected", 422, false),
        new("extension.versionConflict", 409, false), new("extension.downgradeRejected", 409, false), new("extension.installDamaged", 409, false),
        new("extension.previewExpired", 409, false), new("extension.permissionUnavailable", 422, false),
        new("extension.notRunning", 409, false), new("extension.developmentModeOff", 409, false), new("extension.settingInvalid", 422, false),
        new("module.capacityExceeded", 429, true), new("module.startFailed", 503, true),
        new("module.dependencyFailed", 503, true), new("events.resyncRequired", 409, false),
        new("auth.originMismatch", 403, false), new("auth.pairingInvalid", 401, false),
        new("auth.pairingCapacityExceeded", 429, true), new("auth.pairingRateLimited", 429, true),
        new("auth.ownerCannotRevoke", 409, false), new("auth.clientNotFound", 404, false),
        new("request.bodyTooLarge", 413, false), new("request.unsupportedMediaType", 415, false),
        new("request.methodNotAllowed", 405, false), new("revision.required", 428, false),
        new("idempotency.required", 400, false), new("operation.interrupted", 409, false),
        new("resource.conflict", 409, false), new("queue.empty", 422, false),
        new("room.closeUnconfirmed", 503, true),
        new("room.commandUnconfirmed", 503, true), new("room.commandUnsupported", 422, false),
        new("room.interruptionUnavailable", 409, false), new("room.memberNotReady", 409, false),
        new("room.commandRejected", 422, false),
        new("room.scoreDraftUnsupported", 422, false), new("room.scoreDraftStale", 409, false),
        new("room.scoreMappingRequired", 409, false), new("room.scoreUnassigned", 409, false),
        new("room.scoreMissing", 409, false), new("room.scoreAmbiguous", 409, false),
        new("room.scoreContentUnknown", 409, false), new("room.scoreContentMismatch", 409, false),
        new("room.scorePartMismatch", 409, false), new("room.scorePartUnknown", 409, false),
        new("room.scoreInstrumentMismatch", 409, false), new("room.scoreSourceChanged", 409, false), new("room.scoreSourceStale", 409, false),
        new("observation.workerUnavailable", 422, false), new("observation.profileUnavailable", 422, false),
        new("observation.npcapMissing", 422, false), new("observation.npcapUnavailable", 422, false),
        new("observation.channelUnavailable", 422, false),
        new("observation.captureFailed", 503, true), new("observation.sourceEnded", 409, true),
        new("observation.readyTimeout", 503, true),
        new("observation.channelNotFound", 409, true), new("observation.channelAmbiguous", 409, true),
        new("observation.savedChannelMissing", 409, true), new("observation.channelUndetermined", 409, true),
        new("observation.automaticCaptureDisabled", 409, false), new("observation.channelDetecting", 409, true),
        new("room.serverUnavailable", 503, true), new("room.lanUnavailable", 503, true),
        new("room.tunnelNotConfigured", 422, false), new("room.tunnelComponentInvalid", 422, false),
        new("room.tunnelVersionMismatch", 422, false), new("room.tunnelOutputExceeded", 422, false),
        new("room.tunnelUnavailable", 503, true), new("room.tunnelHealthFailed", 503, true),
        new("room.planStale", 409, false), new("room.simulationUnavailable", 422, false),
        new("room.activityActive", 409, false), new("room.clockUnavailable", 409, true),
        new("room.clockQualityInsufficient", 409, true), new("room.timingOffsetInvalid", 400, false),
        new("room.chatDraftUnconfirmed", 409, false), new("room.preparationCapacityExceeded", 429, true),
        new("input.bindingUnavailable", 409, false), new("input.actionMappingUnavailable", 422, false),
        new("input.bindingGeometryMismatch", 409, false), new("input.shortcutConflict", 409, false),
        new("input.focusChanged", 409, false), new("input.stopEvidenceRequired", 409, false),
        new("input.leaseInvalid", 409, false), new("input.deviceFailure", 503, true),
        new("input.controlOccupied", 409, false), new("input.actionInvalid", 400, false),
        new("resource.leaseExpired", 409, false), new("game.selectionExpired", 410, false),
        new("game.controlEpochChanged", 409, false), new("game.stopRequestExpired", 410, false),
        new("input.releaseFailed", 503, true), new("game.activityCancellationFailed", 503, true),
        new("combat.sourceUnavailable", 503, true), new("combat.buildUnsupported", 422, false),
        new("combat.identityUnresolved", 422, false), new("combat.observationGap", 409, false),
        new("combat.entityOutOfScope", 404, false), new("combat.planStale", 409, false),
        new("combat.segmentInactive", 409, false), new("combat.identityPending", 409, true),
        new("combat.measurementActive", 409, false),
        new("combat.measurementInactive", 409, false), new("combat.observationInactive", 409, false),
        new("diagnostics.captureActive", 409, false),
        new("inventory.sourceUnavailable", 422, false), new("inventory.buildUnsupported", 422, false),
        new("inventory.identityUnresolved", 422, false), new("inventory.observationGap", 409, false),
        new("inventory.cursorStale", 409, false), new("inventory.catalogInvalid", 422, false),
        new("inventory.clientChanged", 409, false), new("inventory.nameAmbiguous", 409, false),
        new("inventory.importInvalid", 422, false),
        new("inventory.protocolInvalid", 422, false), new("inventory.planStale", 409, false),
        new("inventory.identityConflict", 409, false),
        new("color.runInactive", 409, false), new("color.interactionConflict", 409, false),
        new("color.candidateUnavailable", 409, false), new("color.retryUnavailable", 409, false),
        new("capture.frameInvalid", 422, false), new("capture.sourceFailed", 503, true),
        new("color.previewTimeout", 503, true), new("color.paletteTimeout", 503, true),
        new("input.bindingChanged", 409, false), new("permission.missing", 403, false),
        new("gameBuild.incompatible", 422, false), new("worker.commandBusy", 409, true),
        new("update.maintenanceActive", 503, true),
        new("update.maintenanceLeaseInvalid", 409, false),
        new("update.planStale", 409, false),
        new("performance.invalidState", 409, false), new("performance.queueBusy", 409, false),
        new("storage.unavailable", 503, true), new("operation.executionFailed", 500, false),
        new("storage.assetDamaged", 422, false), new("storage.assetInvalid", 422, false),
        new("storage.scopeDenied", 403, false), new("storage.pathRejected", 422, false),
        new("storage.quotaExceeded", 413, false), new("storage.archiveLimitExceeded", 413, false),
        new("storage.diskSpaceInsufficient", 507, true), new("storage.volumeUnavailable", 503, true),
        new("storage.reservationInvalid", 400, false), new("storage.planExpired", 409, false),
        new("storage.selectionExpired", 410, false), new("storage.targetChanged", 412, false),
        new("storage.planStale", 412, false), new("storage.backupIncompatible", 422, false),
        new("storage.maintenanceRequired", 503, true), new("storage.rootRetained", 409, false),
        new("storage.moduleEnabled", 409, false), new("storage.assetInUse", 409, false),
        new("storage.writeQueueFull", 503, true), new("storage.readCapacityExceeded", 503, true),
        new("storage.transactionBudgetExceeded", 503, true),
        .. UpdateDefinitions()
    ]);

    private static IEnumerable<ErrorDefinition> UpdateDefinitions()
    {
        foreach (var name in new[] { "sourceNotConfigured", "platformUnsupported", "versionRollback", "releaseIdentityMismatch",
            "assetHashMismatch", "assetLengthMismatch", "assetMetadataInvalid", "packageInvalid", "pathRejected", "planInvalid",
            "redirectHostRejected", "redirectInvalid", "releaseMetadataInvalid", "repositoryInvalid", "rootOverlap", "sourceAccessDenied",
            "sourceHostRejected", "sourceResponseInvalid", "sourceUriInvalid", "manifestTooLarge", "metadataTooLarge" })
            yield return new("update." + name, 422, false);
        foreach (var name in new[] { "allSourcesFailed", "allSourcesUnavailable", "assetDownloadFailed", "diskSpaceInsufficient",
            "metadataTimeout", "networkUnavailable", "releaseAssetMissing", "sourceUnavailable", "storageUnavailable" })
            yield return new("update." + name, 503, true);
        yield return new("update.rateLimited", 429, true);
        yield return new("update.stageExists", 409, false);
        yield return new("update.stageRequired", 409, false);
        yield return new("update.updaterUnavailable", 503, true);
        yield return new("update.installationFailed", 503, true);
        yield return new("update.installationRolledBack", 409, false);
        yield return new("update.recoveryRequired", 503, false);
        // 替换器写入安装结果的失败原因：旧进程未退出、替换失败、新版本未通过健康检查、安装被中断。
        foreach (var name in new[] { "processTimeout", "replaceFailed", "healthTimeout", "interrupted" })
            yield return new("update." + name, 409, false);
    }

    public static ApiProblem Create(string code, string title, string traceId, IReadOnlyDictionary<string, JsonElement>? details = null)
    {
        var definition = Definitions.Single(x => x.Code == code);
        return new("urn:mabitool:problem:" + code.Replace('.', '-'), title, definition.Status,
            code, code, traceId, details ?? new Dictionary<string, JsonElement>(), definition.Retryable);
    }
}
