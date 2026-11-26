using System.Text.Json;

namespace MabiTool.Contracts;

public sealed record EndpointDefinition(string Path, string OperationId, Type ResponseType, string MediaType = "application/json",
    string Method = "GET", Type? RequestType = null, string Permission = ApiPermissions.SystemRead, bool Anonymous = false,
    int SuccessStatus = 200, bool Idempotent = false, bool Conditional = false, bool ETag = false,
    bool EventFiltered = false, bool Streaming = false, int MaximumRequestBytes = 64 * 1024, int MaximumResponseBytes = 4 * 1024 * 1024,
    bool BinaryRequest = false, bool BinaryResponse = false);

public static class ApiContract
{
    public const string Version = "1.0";
    public static EndpointDefinition SystemInfo { get; } = new("/api/v1/system/info", "getSystemInfo", typeof(SystemInfo));
    public static EndpointDefinition UpdateState { get; } = new("/api/v1/updates/state", "getUpdateState", typeof(UpdateStateSnapshot));
    public static EndpointDefinition UpdatePolicy { get; } = new("/api/v1/updates/policy", "getUpdatePolicy", typeof(UpdatePolicySnapshot), ETag: true);
    public static EndpointDefinition SaveUpdatePolicy { get; } = new("/api/v1/updates/policy", "saveUpdatePolicy", typeof(UpdatePolicySnapshot), Method: "POST",
        RequestType: typeof(UpdatePolicy), Permission: ApiPermissions.UpdatesManage, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition CreateUpdateCheck { get; } = new("/api/v1/updates/checks", "createUpdateCheck", typeof(OperationSnapshot), Method: "POST",
        RequestType: typeof(UpdateCheckRequest), Permission: ApiPermissions.UpdatesManage, Idempotent: true, SuccessStatus: 202);
    public static EndpointDefinition UpdatePlan { get; } = new("/api/v1/updates/plans/{id}", "getUpdatePlan", typeof(UpdatePlanSnapshot), ETag: true);
    public static EndpointDefinition UpdateNotes { get; } = new("/api/v1/updates/plans/{id}/notes", "getUpdateNotes", typeof(UpdateReleaseNotes));
    public static EndpointDefinition DownloadUpdatePlan { get; } = new("/api/v1/updates/plans/{id}/downloads", "downloadUpdatePlan", typeof(OperationSnapshot), Method: "POST",
        Permission: ApiPermissions.UpdatesManage, Idempotent: true, Conditional: true, SuccessStatus: 202);
    public static EndpointDefinition ApplyUpdatePlan { get; } = new("/api/v1/updates/plans/{id}/apply", "applyUpdatePlan", typeof(OperationSnapshot), Method: "POST",
        Permission: ApiPermissions.UpdatesManage, Idempotent: true, Conditional: true, SuccessStatus: 202);
    public static EndpointDefinition GameWindows { get; } = new("/api/v1/game/windows", "getGameWindows", typeof(ApiList<GameWindowCandidate>));
    public static EndpointDefinition GameObservationRuntime { get; } = new("/api/v1/game/observations/runtime", "getGameObservationRuntime", typeof(GameObservationRuntime));
    public static EndpointDefinition GameObservation { get; } = new("/api/v1/game/observations/current", "getGameObservation", typeof(GameObservationSnapshot), Permission: ApiPermissions.GameObserve, ETag: true);
    public static EndpointDefinition QueryObservationChannels { get; } = new("/api/v1/game/observations/channels/query", "queryObservationChannels", typeof(ApiList<CaptureChannelOption>),
        Method: "POST", Permission: ApiPermissions.GameObserve);
    public static EndpointDefinition StartGameObservation { get; } = new("/api/v1/game/observations", "startGameObservation", typeof(GameObservationSubscription),
        Method: "POST", RequestType: typeof(GameObservationStartRequest), Permission: ApiPermissions.GameObserve, Idempotent: true, Conditional: true, ETag: true, SuccessStatus: 201);
    public static EndpointDefinition ReleaseGameObservation { get; } = new("/api/v1/game/observations/{id}", "releaseGameObservation", typeof(GameObservationRelease),
        Method: "DELETE", Permission: ApiPermissions.GameObserve);
    public static EndpointDefinition GameCaptureSettings { get; } = new("/api/v1/game/observations/settings", "getGameCaptureSettings", typeof(GameCaptureSettings),
        Permission: ApiPermissions.GameObserve, ETag: true);
    public static EndpointDefinition SaveGameCaptureSettings { get; } = new("/api/v1/game/observations/settings", "saveGameCaptureSettings", typeof(GameCaptureSettings),
        Method: "POST", RequestType: typeof(GameCaptureSettings), Permission: ApiPermissions.GameObserve, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition StartChannelDetection { get; } = new("/api/v1/game/observations/channels/detections", "startChannelDetection", typeof(CaptureChannelDetection),
        Method: "POST", RequestType: typeof(CaptureChannelDetectionRequest), Permission: ApiPermissions.GameObserve, Idempotent: true, SuccessStatus: 201);
    public static EndpointDefinition ChannelDetection { get; } = new("/api/v1/game/observations/channels/detections/current", "getChannelDetection", typeof(CaptureChannelDetection),
        Permission: ApiPermissions.GameObserve);
    public static EndpointDefinition CancelChannelDetection { get; } = new("/api/v1/game/observations/channels/detections/current/cancel", "cancelChannelDetection", typeof(CaptureChannelDetection),
        Method: "POST", Permission: ApiPermissions.GameObserve);
    public static EndpointDefinition GameBinding { get; } = new("/api/v1/game/binding", "getGameBinding", typeof(GameBindingSnapshot), ETag: true);
    public static EndpointDefinition SelectGameBinding { get; } = new("/api/v1/game/binding", "selectGameBinding", typeof(GameBindingSnapshot),
        Method: "POST", RequestType: typeof(GameBindingRequest), Permission: ApiPermissions.RoomsCoordinate, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition GameInputOwnership { get; } = new("/api/v1/game/input-ownership", "getGameInputOwnership", typeof(GameInputOwnershipSnapshot));
    public static EndpointDefinition ActivateGameWindow { get; } = new("/api/v1/game/window-activation", "activateGameWindow", typeof(GameWindowActivationReceipt),
        Method: "POST", Permission: ApiPermissions.GameControl);
    public static EndpointDefinition GameEmergencyStop { get; } = new("/api/v1/game/emergency-stop", "emergencyStopGame", typeof(GameEmergencyStopReceipt),
        Method: "POST", RequestType: typeof(GameEmergencyStopRequest), Permission: ApiPermissions.GameControl, Idempotent: true);
    public static EndpointDefinition FileCapabilities { get; } = new("/api/v1/files/capabilities", "getFileCapabilities", typeof(FileTransferCapabilities));
    public static EndpointDefinition StorageUsage { get; } = new("/api/v1/storage/usage", "getStorageUsage", typeof(StorageUsageSnapshot), Permission: ApiPermissions.StorageManage);
    public static EndpointDefinition StorageModuleData { get; } = new("/api/v1/storage/module-data/{id}", "getStorageModuleData", typeof(StorageModuleDataSnapshot), Permission: ApiPermissions.StorageManage);
    public static EndpointDefinition PreviewStorageModuleDeletion { get; } = new("/api/v1/storage/module-deletion-plans", "previewStorageModuleDeletion", typeof(StorageModuleDeletionPlan),
        Method: "POST", RequestType: typeof(StorageModuleDeletionRequest), Permission: ApiPermissions.StorageManage, SuccessStatus: 201, Idempotent: true, ETag: true);
    public static EndpointDefinition StorageModuleDeletionPlan { get; } = new("/api/v1/storage/module-deletion-plans/{id}", "getStorageModuleDeletionPlan", typeof(StorageModuleDeletionPlan), Permission: ApiPermissions.StorageManage, ETag: true);
    public static EndpointDefinition ExecuteStorageModuleDeletion { get; } = new("/api/v1/storage/module-deletion-plans/{id}/execute", "executeStorageModuleDeletion", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.StorageManage, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition PreviewStorageCleanup { get; } = new("/api/v1/storage/cleanup-plans", "previewStorageCleanup", typeof(StorageCleanupPlan),
        Method: "POST", RequestType: typeof(StorageCleanupRequest), Permission: ApiPermissions.StorageManage, SuccessStatus: 201, Idempotent: true, ETag: true);
    public static EndpointDefinition StorageCleanupPlan { get; } = new("/api/v1/storage/cleanup-plans/{id}", "getStorageCleanupPlan", typeof(StorageCleanupPlan), Permission: ApiPermissions.StorageManage, ETag: true);
    public static EndpointDefinition ExecuteStorageCleanup { get; } = new("/api/v1/storage/cleanup-plans/{id}/execute", "executeStorageCleanup", typeof(StorageCleanupResult),
        Method: "POST", Permission: ApiPermissions.StorageManage, Idempotent: true, Conditional: true);
    public static EndpointDefinition StorageCleanupResult { get; } = new("/api/v1/storage/cleanup-plans/{id}/result", "getStorageCleanupResult", typeof(StorageCleanupResult), Permission: ApiPermissions.StorageManage);
    public static EndpointDefinition StorageBackups { get; } = new("/api/v1/storage/backups", "getStorageBackups", typeof(ApiList<StorageBackupSummary>), Permission: ApiPermissions.StorageManage);
    public static EndpointDefinition StorageBackup { get; } = new("/api/v1/storage/backups/{id}", "getStorageBackup", typeof(StorageBackupSummary), Permission: ApiPermissions.StorageManage, ETag: true);
    public static EndpointDefinition CreateStorageBackup { get; } = new("/api/v1/storage/backups", "createStorageBackup", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(StorageBackupRequest), Permission: ApiPermissions.StorageManage, SuccessStatus: 202, Idempotent: true);
    public static EndpointDefinition StorageDirectorySelection { get; } = new("/api/v1/storage/directory-selections/{id}", "getStorageDirectorySelection", typeof(StorageDirectorySelectionSnapshot), Permission: ApiPermissions.StorageManage);
    public static EndpointDefinition PreviewStorageRestore { get; } = new("/api/v1/storage/restore-plans", "previewStorageRestore", typeof(StorageMaintenancePlan),
        Method: "POST", RequestType: typeof(StorageRestoreRequest), Permission: ApiPermissions.StorageManage, SuccessStatus: 201, Idempotent: true, ETag: true);
    public static EndpointDefinition StorageRestorePlan { get; } = new("/api/v1/storage/restore-plans/{id}", "getStorageRestorePlan", typeof(StorageMaintenancePlan), Permission: ApiPermissions.StorageManage, ETag: true);
    public static EndpointDefinition ExecuteStorageRestore { get; } = new("/api/v1/storage/restore-plans/{id}/execute", "executeStorageRestore", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.StorageManage, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition PreviewStorageMigration { get; } = new("/api/v1/storage/migration-plans", "previewStorageMigration", typeof(StorageMaintenancePlan),
        Method: "POST", RequestType: typeof(StorageMigrationRequest), Permission: ApiPermissions.StorageManage, SuccessStatus: 201, Idempotent: true, ETag: true);
    public static EndpointDefinition StorageMigrationPlan { get; } = new("/api/v1/storage/migration-plans/{id}", "getStorageMigrationPlan", typeof(StorageMaintenancePlan), Permission: ApiPermissions.StorageManage, ETag: true);
    public static EndpointDefinition ExecuteStorageMigration { get; } = new("/api/v1/storage/migration-plans/{id}/execute", "executeStorageMigration", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.StorageManage, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition CreateUpload { get; } = new("/api/v1/uploads", "createFileUpload", typeof(FileUploadSnapshot),
        Method: "POST", RequestType: typeof(FileUploadRequest), Permission: ApiPermissions.FilesTransfer, SuccessStatus: 201, Idempotent: true, ETag: true);
    public static EndpointDefinition Upload { get; } = new("/api/v1/uploads/{id}", "getFileUpload", typeof(FileUploadSnapshot), ETag: true);
    public static EndpointDefinition UploadContent { get; } = new("/api/v1/uploads/{id}/content", "uploadFileContent", typeof(FileUploadSnapshot),
        Method: "POST", Permission: ApiPermissions.FilesTransfer, Idempotent: true, Conditional: true, ETag: true,
        MaximumRequestBytes: 256 * 1024 * 1024, BinaryRequest: true);
    public static EndpointDefinition FileSelection { get; } = new("/api/v1/files/selections/{id}", "getFileSelection", typeof(FileSelectionSnapshot));
    public static EndpointDefinition UploadSelection { get; } = new("/api/v1/files/selections/upload", "uploadFileSelection", typeof(FileUploadSnapshot),
        Method: "POST", RequestType: typeof(FileSelectionUploadRequest), Permission: ApiPermissions.FilesTransfer, Idempotent: true, ETag: true);
    public static EndpointDefinition Asset { get; } = new("/api/v1/assets/{id}", "getAsset", typeof(AssetSnapshot), ETag: true);
    public static EndpointDefinition AssetContent { get; } = new("/api/v1/assets/{id}/content", "downloadAsset", typeof(AssetSnapshot),
        MediaType: "application/octet-stream", ETag: true, MaximumResponseBytes: 256 * 1024 * 1024, BinaryResponse: true);
    public static EndpointDefinition CreateFileImport { get; } = new("/api/v1/imports", "previewFileImport", typeof(FileImportPreview),
        Method: "POST", RequestType: typeof(FileImportRequest), Permission: ApiPermissions.FilesTransfer, SuccessStatus: 201, Idempotent: true,
        MaximumResponseBytes: 12 * 1024 * 1024);
    public static EndpointDefinition FileImport { get; } = new("/api/v1/imports/{id}/preview", "getFileImport", typeof(FileImportPreview),
        MaximumResponseBytes: 12 * 1024 * 1024);
    public static EndpointDefinition CommitFileImport { get; } = new("/api/v1/imports/{id}/commit", "commitFileImport", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.FilesTransfer, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition CreateFileExport { get; } = new("/api/v1/exports", "exportFiles", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(FileExportRequest), Permission: ApiPermissions.FilesTransfer, SuccessStatus: 202, Idempotent: true);
    public static EndpointDefinition FileExport { get; } = new("/api/v1/exports/{id}", "getFileExport", typeof(FileExportArtifact));
    public static EndpointDefinition SaveFileExport { get; } = new("/api/v1/exports/{id}/save", "saveFileExport", typeof(FileSaveReceipt),
        Method: "POST", RequestType: typeof(FileExportSaveRequest), Permission: ApiPermissions.FilesTransfer, Idempotent: true);
    public static EndpointDefinition Diagnostics { get; } = new("/api/v1/diagnostics", "getDiagnostics", typeof(DiagnosticSummary));
    public static EndpointDefinition DiagnosticRuntime { get; } = new("/api/v1/diagnostics/runtime", "getDiagnosticRuntime", typeof(DiagnosticRuntimeSnapshot));
    public static EndpointDefinition DiagnosticPolicy { get; } = new("/api/v1/diagnostics/retention", "getDiagnosticPolicy", typeof(DiagnosticPolicySnapshot), ETag: true);
    public static EndpointDefinition ChangeDiagnosticPolicy { get; } = new("/api/v1/diagnostics/retention", "changeDiagnosticPolicy", typeof(DiagnosticPolicySnapshot),
        Method: "POST", RequestType: typeof(DiagnosticRetentionPolicy), Permission: ApiPermissions.ModulesConfigure, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition ExportDiagnostics { get; } = new("/api/v1/diagnostics/exports", "exportDiagnostics", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(DiagnosticExportRequest), Permission: ApiPermissions.DiagnosticsExport, Idempotent: true, SuccessStatus: 202);
    public static EndpointDefinition DiagnosticExport { get; } = new("/api/v1/diagnostics/exports/{id}", "getDiagnosticExport", typeof(DiagnosticExportArtifact),
        Permission: ApiPermissions.DiagnosticsExport);
    public static EndpointDefinition DiagnosticExportContent { get; } = new("/api/v1/diagnostics/exports/{id}/content", "readDiagnosticExport", typeof(AssetContentChunk),
        Method: "POST", RequestType: typeof(AssetContentRequest), Permission: ApiPermissions.DiagnosticsExport);
    public static EndpointDefinition QueryDiagnosticRecords { get; } = new("/api/v1/diagnostics/records/query", "queryDiagnosticRecords", typeof(ApiList<DiagnosticRecordEntry>),
        Method: "POST", RequestType: typeof(DiagnosticRecordQuery));
    public static EndpointDefinition StartDiagnosticCapture { get; } = new("/api/v1/diagnostics/captures", "startDiagnosticCapture", typeof(DiagnosticCaptureSnapshot),
        Method: "POST", RequestType: typeof(DiagnosticCaptureRequest), Permission: ApiPermissions.DiagnosticsDebug, Idempotent: true, SuccessStatus: 201);
    public static EndpointDefinition CurrentDiagnosticCapture { get; } = new("/api/v1/diagnostics/captures/current", "getDiagnosticCapture", typeof(DiagnosticCaptureCurrent),
        Permission: ApiPermissions.DiagnosticsDebug);
    public static EndpointDefinition StopDiagnosticCapture { get; } = new("/api/v1/diagnostics/captures/{id}/stop", "stopDiagnosticCapture", typeof(DiagnosticCaptureSnapshot),
        Method: "POST", Permission: ApiPermissions.DiagnosticsDebug);
    public static EndpointDefinition Shortcuts { get; } = new("/api/v1/system/shortcuts", "getShortcuts", typeof(ApiList<GlobalShortcutSnapshot>));
    public static EndpointDefinition ShortcutSettings { get; } = new("/api/v1/system/shortcut-settings", "getShortcutSettings", typeof(ShortcutSettingsSnapshot), ETag: true);
    public static EndpointDefinition ChangeShortcutSettings { get; } = new("/api/v1/system/shortcut-settings", "changeShortcutSettings", typeof(ShortcutSettingsSnapshot),
        Method: "POST", RequestType: typeof(ShortcutSettingsRequest), Permission: ApiPermissions.DesktopControl, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition Presentation { get; } = new("/api/v1/presentation", "getPresentation", typeof(PresentationSnapshot));
    public static EndpointDefinition QueryPresentation { get; } = new("/api/v1/presentation/query", "queryPresentation", typeof(PresentationSnapshot),
        Method: "POST", RequestType: typeof(PresentationQuery));
    public static EndpointDefinition DesktopBridge { get; } = new("/api/v1/desktop/bridge", "getDesktopBridge", typeof(DesktopBridgeStatus));
    public static EndpointDefinition DesktopSurface { get; } = new("/api/v1/desktop/surfaces/{id}", "getDesktopSurface", typeof(DesktopSurfaceSnapshot), ETag: true);
    public static EndpointDefinition ChangeDesktopSurface { get; } = new("/api/v1/desktop/surfaces/{id}", "changeDesktopSurface", typeof(DesktopSurfaceSnapshot),
        Method: "POST", RequestType: typeof(DesktopSurfaceChange), Permission: ApiPermissions.DesktopControl, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition PreviewRoomImport { get; } = new("/api/v1/rooms/imports", "previewRoomImport", typeof(SettingsImportPreview),
        Method: "POST", RequestType: typeof(SettingsImportRequest), Permission: ApiPermissions.RoomsCoordinate, SuccessStatus: 201, Idempotent: true, MaximumRequestBytes: 4 * 1024 * 1024);
    public static EndpointDefinition RoomImport { get; } = new("/api/v1/rooms/imports/{id}", "getRoomImport", typeof(SettingsImportPreview), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition CommitRoomImport { get; } = new("/api/v1/rooms/imports/{id}/commit", "commitRoomImport", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.RoomsCoordinate, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition RoomPreferences { get; } = new("/api/v1/rooms/preferences", "getRoomPreferences", typeof(RoomPreferences), ETag: true);
    public static EndpointDefinition EditRoomPreferences { get; } = new("/api/v1/rooms/preferences", "editRoomPreferences", typeof(RoomPreferences),
        Method: "POST", RequestType: typeof(RoomPreferences), Permission: ApiPermissions.RoomsCoordinate, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition PreviewColorImport { get; } = new("/api/v1/color/imports", "previewColorImport", typeof(SettingsImportPreview),
        Method: "POST", RequestType: typeof(SettingsImportRequest), Permission: ApiPermissions.ColorEdit, SuccessStatus: 201, Idempotent: true, MaximumRequestBytes: 4 * 1024 * 1024);
    public static EndpointDefinition ColorImport { get; } = new("/api/v1/color/imports/{id}", "getColorImport", typeof(SettingsImportPreview), Permission: ApiPermissions.ColorEdit);
    public static EndpointDefinition CommitColorImport { get; } = new("/api/v1/color/imports/{id}/commit", "commitColorImport", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.ColorEdit, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition ColorPreferences { get; } = new("/api/v1/color/preferences", "getColorPreferences", typeof(ColorStudioPreferences));
    public static EndpointDefinition ImportedColorHistory { get; } = new("/api/v1/color/imported-history/query", "queryImportedColorHistory", typeof(ApiList<ImportedColorHistoryItem>),
        Method: "POST", RequestType: typeof(ImportedColorHistoryQuery));
    public static EndpointDefinition Modules { get; } = new("/api/v1/modules", "getModules", typeof(ApiList<ModuleSnapshot>));
    public static EndpointDefinition ModuleSettingsSchema { get; } = new("/api/v1/modules/{id}/settings-schema",
        "getModuleSettingsSchema", typeof(JsonElement), "application/schema+json");
    public static EndpointDefinition Capabilities { get; } = new("/api/v1/capabilities", "getCapabilities", typeof(ApiList<CapabilitySnapshot>));
    public static EndpointDefinition CaptureColorScreen { get; } = new("/api/v1/color/captures", "captureColorScreen", typeof(ColorScreenCapture), Method: "POST", RequestType: typeof(ColorCaptureRequest), Permission: ApiPermissions.DesktopCapture);
    public static EndpointDefinition PickColorPixel { get; } = new("/api/v1/color/captures/{id}/pixels", "pickColorPixel", typeof(ColorPickedPixel), Method: "POST", RequestType: typeof(ColorPixelQuery), Permission: ApiPermissions.DesktopCapture);
    public static EndpointDefinition ReadColorCapture { get; } = new("/api/v1/color/captures/{id}/content", "readColorCapture", typeof(ColorAssetContent), Method: "POST", RequestType: typeof(ColorAssetRangeRequest), Permission: ApiPermissions.DesktopCapture);
    public static EndpointDefinition ReleaseColorCapture { get; } = new("/api/v1/color/captures/{id}", "releaseColorCapture", typeof(ColorCaptureRelease), Method: "DELETE", Permission: ApiPermissions.DesktopCapture);
    public static EndpointDefinition PreviewColor { get; } = new("/api/v1/color/previews", "previewColor", typeof(ColorPreview), Method: "POST", RequestType: typeof(ColorPreviewRequest), Permission: ApiPermissions.ColorEdit);
    public static EndpointDefinition RenderColorPalette { get; } = new("/api/v1/color/palettes", "renderColorPalette", typeof(ColorPalette), Method: "POST", RequestType: typeof(ColorPaletteRequest), Permission: ApiPermissions.ColorEdit);
    public static EndpointDefinition QueryColorHistory { get; } = new("/api/v1/color/history/query", "queryColorHistory", typeof(ApiList<ColorHistoryItem>),
        Method: "POST", RequestType: typeof(ColorHistoryQuery));
    public static EndpointDefinition ColorHistory { get; } = new("/api/v1/color/history/{id}", "getColorHistory", typeof(ColorRun));
    public static EndpointDefinition ExportColorHistory { get; } = new("/api/v1/color/history/{id}/assets", "exportColorHistory", typeof(AssetSnapshot), Method: "POST");
    public static EndpointDefinition ColorAsset { get; } = new("/api/v1/color/assets/{id}", "getColorAsset", typeof(AssetSnapshot));
    public static EndpointDefinition ColorAssetContent { get; } = new("/api/v1/color/assets/{id}/content", "readColorAsset", typeof(ColorAssetContent),
        Method: "POST", RequestType: typeof(ColorAssetRangeRequest));
    public static EndpointDefinition ColorLibrary { get; } = new("/api/v1/color/library", "getColorLibrary", typeof(ColorLibrary), ETag: true);
    public static EndpointDefinition ColorProfile { get; } = new("/api/v1/color/profiles/{id}", "getColorProfile", typeof(ColorProfile));
    public static EndpointDefinition EditColorLibrary { get; } = new("/api/v1/color/library/edits", "editColorLibrary", typeof(ColorLibraryChanged),
        Method: "POST", RequestType: typeof(ColorLibraryEdit), Permission: ApiPermissions.ColorEdit, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition StartColorRun { get; } = new("/api/v1/color/runs", "startColorRun", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(ColorRunStartRequest), Permission: ApiPermissions.ColorControl, SuccessStatus: 202, Idempotent: true);
    public static EndpointDefinition ColorRun { get; } = new("/api/v1/color/runs/{id}", "getColorRun", typeof(ColorRun), ETag: true);
    public static EndpointDefinition ColorPresence { get; } = new("/api/v1/color/runs/{id}/presence", "renewColorPresence", typeof(ColorInteractionPresence),
        Method: "POST", Permission: ApiPermissions.ColorControl);
    public static EndpointDefinition ColorRuntime { get; } = new("/api/v1/color/runtime", "getColorRuntime", typeof(ColorEngineRuntime));
    public static EndpointDefinition SelectColorCandidate { get; } = new("/api/v1/color/runs/{id}/selections", "selectColorCandidate", typeof(ColorInteractionReceipt),
        Method: "POST", RequestType: typeof(ColorCandidateSelectionRequest), Permission: ApiPermissions.ColorControl);
    public static EndpointDefinition RetryColorRun { get; } = new("/api/v1/color/runs/{id}/retries", "retryColorRun", typeof(ColorInteractionReceipt),
        Method: "POST", RequestType: typeof(ColorRetryRequest), Permission: ApiPermissions.ColorControl);
    public static EndpointDefinition Health { get; } = new("/api/v1/health", "getHealth", typeof(HealthInfo));
    public static EndpointDefinition Ready { get; } = new("/api/v1/ready", "getReady", typeof(HealthInfo));
    /// <summary>退出宿主（含后台服务）：只接受本机桌面所有者会话，回应后宿主按正常停止流程退出。</summary>
    public static EndpointDefinition ShutdownHost { get; } = new("/api/v1/system/shutdown", "shutdownHost", typeof(HealthInfo),
        Method: "POST", Permission: ApiPermissions.AuthManage, SuccessStatus: 202);
    public static EndpointDefinition OpenApi { get; } = new("/api/v1/openapi.json", "getOpenApi", typeof(JsonElement));
    public static EndpointDefinition AuthSession { get; } = new("/api/v1/auth/session", "getAuthSession", typeof(AuthSession));
    public static EndpointDefinition AuthClients { get; } = new("/api/v1/auth/clients", "getAuthClients", typeof(ApiList<AuthSession>), Permission: ApiPermissions.AuthManage);
    public static EndpointDefinition PairingChallenges { get; } = new("/api/v1/auth/pairing-challenges", "createPairingChallenge", typeof(PairingChallenge),
        Method: "POST", RequestType: typeof(PairingChallengeRequest), Permission: ApiPermissions.AuthManage);
    public static EndpointDefinition Pairings { get; } = new("/api/v1/auth/pairings", "redeemPairing", typeof(AccessGrant),
        Method: "POST", RequestType: typeof(PairingRequest), Anonymous: true);
    public static EndpointDefinition RevokeClient { get; } = new("/api/v1/auth/clients/{id}", "revokeClient", typeof(ClientRevocation),
        Method: "DELETE", Permission: ApiPermissions.AuthManage);
    public static EndpointDefinition ModuleConfiguration { get; } = new("/api/v1/modules/{id}/configuration", "getModuleConfiguration", typeof(ModuleConfiguration), ETag: true);
    public static EndpointDefinition ConfigureModule { get; } = new("/api/v1/modules/{id}/configuration", "configureModule", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(ModuleConfigurationRequest), Permission: ApiPermissions.ModulesConfigure,
        SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition Operation { get; } = new("/api/v1/operations/{id}", "getOperation", typeof(OperationSnapshot), ETag: true);
    public static EndpointDefinition CancelOperation { get; } = new("/api/v1/operations/{id}/cancel", "cancelOperation", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(CancelOperationRequest));
    public static EndpointDefinition EventSnapshot { get; } = new("/api/v1/event-snapshot", "getEventSnapshot", typeof(ProjectionSnapshot), EventFiltered: true);
    public static EndpointDefinition Events { get; } = new("/api/v1/events", "streamEvents", typeof(StreamMessage), "text/event-stream", EventFiltered: true, Streaming: true);
    public static EndpointDefinition ReplayCatalog { get; } = new("/api/v1/replay", "getReplayCatalog", typeof(ReplayCatalog));
    public static EndpointDefinition MusicInstruments { get; } = new("/api/v1/music/instruments", "getMusicInstruments", typeof(ApiList<InstrumentDefinition>));
    public static EndpointDefinition QueryOwnedMusicInstruments { get; } = new("/api/v1/music/instruments/owned/query", "queryOwnedMusicInstruments", typeof(MusicOwnedInstrumentPage),
        Method: "POST", RequestType: typeof(MusicOwnedInstrumentQuery));
    public static EndpointDefinition QueryOwnedMusicScores { get; } = new("/api/v1/music/scores/owned/query", "queryOwnedMusicScores", typeof(MusicOwnedScorePage),
        Method: "POST", RequestType: typeof(MusicOwnedScoreQuery));
    public static EndpointDefinition StartReplay { get; } = new("/api/v1/replay/runs", "startReplay", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(ReplayRequest), Permission: ApiPermissions.ReplayControl, SuccessStatus: 202, Idempotent: true);
    public static EndpointDefinition CombatCapabilities { get; } = new("/api/v1/combat/capabilities", "getCombatCapabilities", typeof(CombatCapabilities));
    public static EndpointDefinition StartCombatObservation { get; } = new("/api/v1/combat/observations", "startCombatObservation", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(CombatObservationRequest), Permission: ApiPermissions.CombatControl, SuccessStatus: 202, Idempotent: true);
    public static EndpointDefinition CombatObservation { get; } = new("/api/v1/combat/observations/{id}", "getCombatObservation", typeof(OperationSnapshot), ETag: true);
    public static EndpointDefinition StopCombatObservation { get; } = new("/api/v1/combat/observations/{id}/stop", "stopCombatObservation", typeof(OperationSnapshot), Method: "POST", Permission: ApiPermissions.CombatControl);
    public static EndpointDefinition CombatSnapshot { get; } = new("/api/v1/combat/snapshot", "getCombatSnapshot", typeof(CombatSnapshot), ETag: true);
    public static EndpointDefinition CombatEncounters { get; } = new("/api/v1/combat/encounters", "getCombatEncounters", typeof(CombatEncounterPage));
    public static EndpointDefinition QueryCombatEncounters { get; } = new("/api/v1/combat/encounters/query", "queryCombatEncounters", typeof(CombatEncounterPage), Method: "POST", RequestType: typeof(CombatEncounterQuery));
    public static EndpointDefinition CombatEncounter { get; } = new("/api/v1/combat/encounters/{id}", "getCombatEncounter", typeof(CombatEncounter), ETag: true);
    public static EndpointDefinition StartCombatEncounter { get; } = new("/api/v1/combat/encounters/start", "startCombatEncounter", typeof(CombatEncounter), Method: "POST", RequestType: typeof(CombatBoundaryRequest),
        Permission: ApiPermissions.CombatControl, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition CloseCombatEncounter { get; } = new("/api/v1/combat/encounters/{id}/close", "closeCombatEncounter", typeof(CombatEncounter), Method: "POST", RequestType: typeof(CombatBoundaryRequest),
        Permission: ApiPermissions.CombatControl, Idempotent: true, Conditional: true, ETag: true);
    /// <summary>手动拆段：以 manual-split 结束进行中的分段并返回该段；下一笔有效本机伤害开始新分段。</summary>
    public static EndpointDefinition SplitCombatEncounter { get; } = new("/api/v1/combat/encounters/{id}/split", "splitCombatEncounter", typeof(CombatEncounter), Method: "POST", RequestType: typeof(CombatBoundaryRequest),
        Permission: ApiPermissions.CombatControl, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition ResetCombatEncounter { get; } = new("/api/v1/combat/encounters/{id}/reset", "resetCombatEncounter", typeof(CombatEncounter), Method: "POST", RequestType: typeof(CombatBoundaryRequest),
        Permission: ApiPermissions.CombatControl, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition FavoriteCombatEncounter { get; } = new("/api/v1/combat/encounters/{id}/favorite", "favoriteCombatEncounter", typeof(CombatEncounter), Method: "POST", RequestType: typeof(CombatFavoriteRequest),
        Permission: ApiPermissions.CombatControl, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition CombatEntityEffects { get; } = new("/api/v1/combat/entities/{id}/effects", "getCombatEntityEffects", typeof(CombatEntityEffects), ETag: true);
    /// <summary>当前客户端 Buff 表里全部可显示的效果（编号、名称、极性、描述）；名称表不可用时返回状态与原因。</summary>
    public static EndpointDefinition CombatClientBuffs { get; } = new("/api/v1/combat/client-buffs", "getCombatClientBuffs", typeof(CombatClientBuffCatalog), ETag: true);
    public static EndpointDefinition CombatTimeline { get; } = new("/api/v1/combat/encounters/{id}/timeline", "getCombatTimeline", typeof(CombatTimelinePage));
    public static EndpointDefinition QueryCombatTimeline { get; } = new("/api/v1/combat/encounters/{id}/timeline/query", "queryCombatTimeline", typeof(CombatTimelinePage), Method: "POST", RequestType: typeof(CombatTimelineQuery));
    public static EndpointDefinition QueryCombatRange { get; } = new("/api/v1/combat/ranges/query", "queryCombatRange", typeof(CombatRangeSummary), Method: "POST", RequestType: typeof(CombatRangeQuery));
    public static EndpointDefinition SaveCombatRange { get; } = new("/api/v1/combat/ranges/save", "saveCombatRange", typeof(CombatEncounter), Method: "POST", RequestType: typeof(CombatRangeSaveRequest),
        Permission: ApiPermissions.CombatControl, Idempotent: true);
    public static EndpointDefinition CombatMeasurement { get; } = new("/api/v1/combat/measurement", "getCombatMeasurement", typeof(CombatMeasurementState), ETag: true);
    public static EndpointDefinition StartCombatMeasurement { get; } = new("/api/v1/combat/measurement/start", "startCombatMeasurement", typeof(CombatMeasurementState), Method: "POST",
        RequestType: typeof(CombatMeasurementStartRequest), Permission: ApiPermissions.CombatControl, Idempotent: true, ETag: true);
    public static EndpointDefinition CancelCombatMeasurement { get; } = new("/api/v1/combat/measurement/cancel", "cancelCombatMeasurement", typeof(CombatMeasurementState), Method: "POST",
        RequestType: typeof(CombatMeasurementCancelRequest), Permission: ApiPermissions.CombatControl, Idempotent: true, ETag: true);
    public static EndpointDefinition SaveCombatMeasurementSettings { get; } = new("/api/v1/combat/measurement/settings", "saveCombatMeasurementSettings", typeof(CombatMeasurementState), Method: "POST",
        RequestType: typeof(CombatMeasurementSettings), Permission: ApiPermissions.CombatControl, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition CombatAutoObservation { get; } = new("/api/v1/combat/auto-observation", "getCombatAutoObservation", typeof(CombatAutoObservationState), ETag: true);
    public static EndpointDefinition SetCombatAutoObservation { get; } = new("/api/v1/combat/auto-observation", "setCombatAutoObservation", typeof(CombatAutoObservationState), Method: "POST",
        RequestType: typeof(CombatAutoObservationRequest), Permission: ApiPermissions.CombatControl, Idempotent: true, Conditional: true, ETag: true);
    /// <summary>前端报告战斗数据的显示延迟（最近窗口的中位数、p95 与最大值），宿主写入诊断事件 combat.display.latency。</summary>
    public static EndpointDefinition ReportCombatDisplayLatency { get; } = new("/api/v1/combat/display-latency", "reportCombatDisplayLatency", typeof(CombatDisplayLatencyReceipt),
        Method: "POST", RequestType: typeof(CombatDisplayLatencyReport), Permission: ApiPermissions.CombatControl, MaximumRequestBytes: 1024);
    public static EndpointDefinition CleanupCombatHistory { get; } = new("/api/v1/combat/history/cleanup", "cleanupCombatHistory", typeof(CombatStorageSummary), Method: "POST", Permission: ApiPermissions.CombatControl, Idempotent: true);
    public static EndpointDefinition MusicLibrary { get; } = new("/api/v1/music/library", "getMusicLibrary", typeof(MusicLibraryInfo), ETag: true);
    public static EndpointDefinition QueryMusicLibrary { get; } = new("/api/v1/music/library/query", "queryMusicLibrary", typeof(MusicLibraryPage),
        Method: "POST", RequestType: typeof(MusicQuery));
    public static EndpointDefinition EditMusicLibrary { get; } = new("/api/v1/music/library/edits", "editMusicLibrary", typeof(MusicEditResult),
        Method: "POST", RequestType: typeof(MusicEditBatch), Permission: ApiPermissions.MusicEdit, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition MusicPlaybackHistory { get; } = new("/api/v1/music/history", "getMusicPlaybackHistory", typeof(ApiList<MusicPlaybackHistoryItem>));
    public static EndpointDefinition ImportMusicPlaybackHistory { get; } = new("/api/v1/music/history/imports", "importMusicPlaybackHistory", typeof(MusicPlaybackHistoryItem),
        Method: "POST", RequestType: typeof(MusicPlaybackHistoryInput), Permission: ApiPermissions.MusicEdit, Idempotent: true, SuccessStatus: 201);
    public static EndpointDefinition MusicTimeline { get; } = new("/api/v1/music/timelines/query", "queryMusicTimeline", typeof(ScoreTimeline),
        Method: "POST", RequestType: typeof(MusicTimelineQuery));
    public static EndpointDefinition PreviewMusicImport { get; } = new("/api/v1/music/imports", "previewMusicImport", typeof(MusicImportPreview),
        Method: "POST", RequestType: typeof(MusicImportRequest), Permission: ApiPermissions.MusicEdit, SuccessStatus: 201, Idempotent: true,
        MaximumRequestBytes: 12 * 1024 * 1024, MaximumResponseBytes: 16 * 1024 * 1024);
    public static EndpointDefinition MusicImport { get; } = new("/api/v1/music/imports/{id}", "getMusicImport", typeof(MusicImportPreview),
        Permission: ApiPermissions.MusicEdit, MaximumResponseBytes: 16 * 1024 * 1024);
    public static EndpointDefinition CommitMusicImport { get; } = new("/api/v1/music/imports/{id}/commit", "commitMusicImport", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.MusicEdit, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition MusicPreferences { get; } = new("/api/v1/music/preferences", "getMusicPreferences", typeof(MusicPreferences));
    public static EndpointDefinition PerformanceSessions { get; } = new("/api/v1/music/performance/sessions", "listPerformanceSessions", typeof(ApiList<PerformanceQueueState>));
    public static EndpointDefinition CreatePerformanceSession { get; } = new("/api/v1/music/performance/sessions", "createPerformanceSession", typeof(PerformanceQueueState),
        Method: "POST", RequestType: typeof(PerformanceSessionCreateRequest), Permission: ApiPermissions.MusicEdit, SuccessStatus: 201, Idempotent: true);
    public static EndpointDefinition PerformanceSession { get; } = new("/api/v1/music/performance/sessions/{id}", "getPerformanceSession", typeof(PerformanceQueueState));
    public static EndpointDefinition CommandPerformanceSession { get; } = new("/api/v1/music/performance/sessions/{id}/commands", "commandPerformanceSession", typeof(PerformanceQueueState),
        Method: "POST", RequestType: typeof(PerformanceSessionCommand), Permission: ApiPermissions.MusicEdit, Conditional: true, ETag: true);
    public static EndpointDefinition RoomSession { get; } = new("/api/v1/rooms/session", "getRoomSession", typeof(RoomLifecycleSnapshot));
    public static EndpointDefinition CurrentRoomRound { get; } = new("/api/v1/rooms/rounds/current", "getCurrentRoomRound", typeof(RoomRoundSnapshot), ETag: true);
    public static EndpointDefinition PrepareRoomRound { get; } = new("/api/v1/rooms/rounds", "prepareRoomRound", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(RoomRoundRequest), Permission: ApiPermissions.RoomsCoordinate,
        SuccessStatus: 202, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition ControlRoomRound { get; } = new("/api/v1/rooms/rounds/{id}/commands", "controlRoomRound", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(RoomRoundControlRequest), Permission: ApiPermissions.RoomsCoordinate,
        SuccessStatus: 202, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition RoomServer { get; } = new("/api/v1/rooms/server", "getRoomServer", typeof(RoomServerSnapshot), ETag: true);
    public static EndpointDefinition StartRoomServer { get; } = new("/api/v1/rooms/server/starts", "startRoomServer", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(RoomServerStartRequest), Permission: ApiPermissions.RoomsCoordinate,
        SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition StopRoomServer { get; } = new("/api/v1/rooms/server/stops", "stopRoomServer", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(RoomServerStopRequest), Permission: ApiPermissions.RoomsCoordinate,
        SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition CreateRoom { get; } = new("/api/v1/rooms", "createRoom", typeof(RoomLifecycleSnapshot),
        Method: "POST", RequestType: typeof(RoomCreateRequest), Permission: ApiPermissions.RoomsCoordinate, SuccessStatus: 201, Idempotent: true);
    public static EndpointDefinition JoinRoom { get; } = new("/api/v1/room-joins", "joinRoom", typeof(RoomLifecycleSnapshot),
        Method: "POST", RequestType: typeof(RoomJoinRequest), Permission: ApiPermissions.RoomsCoordinate, SuccessStatus: 201, Idempotent: true);
    public static EndpointDefinition ReconnectRoom { get; } = new("/api/v1/rooms/reconnect", "reconnectRoom", typeof(RoomLifecycleSnapshot),
        Method: "POST", Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition LeaveRoom { get; } = new("/api/v1/rooms/leave", "leaveRoom", typeof(RoomLifecycleSnapshot),
        Method: "POST", RequestType: typeof(RoomLeaveRequest), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition RoomInvitation { get; } = new("/api/v1/rooms/invitation", "getRoomInvitation", typeof(RoomInvitation), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition CreateRoomInvitation { get; } = new("/api/v1/rooms/invitation", "createRoomInvitation", typeof(RoomInvitation),
        Method: "POST", Permission: ApiPermissions.RoomsCoordinate, Idempotent: true, SuccessStatus: 201);
    public static EndpointDefinition EditRoomRoster { get; } = new("/api/v1/rooms/members/roster", "editRoomRoster", typeof(RoomLifecycleSnapshot),
        Method: "POST", RequestType: typeof(RoomRosterEditRequest), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition SetRoomMode { get; } = new("/api/v1/rooms/mode", "setRoomMode", typeof(RoomLifecycleSnapshot),
        Method: "PUT", RequestType: typeof(RoomModeRequest), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition SetRoomReadiness { get; } = new("/api/v1/rooms/readiness", "setRoomReadiness", typeof(RoomLifecycleSnapshot),
        Method: "PUT", RequestType: typeof(RoomReadinessRequest), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition BindRoomCharacter { get; } = new("/api/v1/rooms/members/character-binding", "bindRoomCharacter", typeof(RoomLifecycleSnapshot),
        Method: "PUT", RequestType: typeof(RoomCharacterBindingRequest), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition SelectRoomLeader { get; } = new("/api/v1/rooms/leader", "selectRoomLeader", typeof(RoomLifecycleSnapshot),
        Method: "PUT", RequestType: typeof(RoomLeaderRequest), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition KickRoomMember { get; } = new("/api/v1/rooms/members/kick", "kickRoomMember", typeof(RoomLifecycleSnapshot),
        Method: "POST", RequestType: typeof(RoomMemberKickRequest), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition SetRoomGameOperation { get; } = new("/api/v1/rooms/game-operation", "setRoomGameOperation", typeof(RoomLifecycleSnapshot),
        Method: "PUT", RequestType: typeof(RoomGameOperationRequest), Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition StopRoomLocally { get; } = new("/api/v1/rooms/local-stop", "stopRoomLocally", typeof(RoomLocalStopResult),
        Method: "POST", Permission: ApiPermissions.RoomsCoordinate);
    public static EndpointDefinition ReleaseRoomInput { get; } = new("/api/v1/rooms/preparations/{id}/release", "releaseRoomInput", typeof(RoomLifecycleSnapshot),
        Method: "POST", Permission: ApiPermissions.RoomsCoordinate, Idempotent: true);
    public static EndpointDefinition RoomScoreDraft { get; } = new("/api/v1/rooms/score-draft", "getRoomScoreDraft", typeof(RoomScoreDraftSnapshot), ETag: true);
    public static EndpointDefinition PublishRoomScoreDraft { get; } = new("/api/v1/rooms/score-draft/publish", "publishRoomScoreDraft", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(RoomScoreDraftRequest), Permission: ApiPermissions.RoomsCoordinate, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition MapRoomScoreDraft { get; } = new("/api/v1/rooms/score-draft/map", "mapRoomScoreDraft", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(RoomScoreMappingRequest), Permission: ApiPermissions.RoomsCoordinate, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition ClearRoomScoreDraft { get; } = new("/api/v1/rooms/score-draft/clear", "clearRoomScoreDraft", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.RoomsCoordinate, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition Extensions { get; } = new("/api/v1/extensions", "getExtensions", typeof(ExtensionCatalog));
    public static EndpointDefinition PreviewExtensionInstall { get; } = new("/api/v1/extensions/previews", "previewExtensionInstall", typeof(ExtensionInstallPreview),
        Method: "POST", RequestType: typeof(ExtensionPreviewRequest), Permission: ApiPermissions.ModulesConfigure, SuccessStatus: 201);
    public static EndpointDefinition InstallExtension { get; } = new("/api/v1/extensions/previews/{id}/install", "installExtension", typeof(InstalledExtension),
        Method: "POST", Permission: ApiPermissions.ModulesConfigure);
    public static EndpointDefinition SetExtensionEnabled { get; } = new("/api/v1/extensions/{id}/enabled", "setExtensionEnabled", typeof(InstalledExtension),
        Method: "POST", RequestType: typeof(ExtensionEnableRequest), Permission: ApiPermissions.ModulesConfigure);
    public static EndpointDefinition UninstallExtension { get; } = new("/api/v1/extensions/{id}/uninstall", "uninstallExtension", typeof(ExtensionUninstallReceipt),
        Method: "POST", Permission: ApiPermissions.ModulesConfigure);
    public static EndpointDefinition SetExtensionPermissions { get; } = new("/api/v1/extensions/{id}/permissions", "setExtensionPermissions", typeof(InstalledExtension),
        Method: "POST", RequestType: typeof(ExtensionGrantRequest), Permission: ApiPermissions.ModulesConfigure);
    public static EndpointDefinition RestartExtension { get; } = new("/api/v1/extensions/{id}/restart", "restartExtension", typeof(InstalledExtension),
        Method: "POST", Permission: ApiPermissions.ModulesConfigure);
    public static EndpointDefinition ExtensionLogs { get; } = new("/api/v1/extensions/{id}/logs", "getExtensionLogs", typeof(ExtensionLogPage));
    public static EndpointDefinition SetExtensionDevelopmentMode { get; } = new("/api/v1/extensions/development", "setExtensionDevelopmentMode", typeof(ExtensionCatalog),
        Method: "POST", RequestType: typeof(ExtensionDevelopmentModeRequest), Permission: ApiPermissions.ModulesConfigure);
    public static EndpointDefinition LoadDevelopmentExtension { get; } = new("/api/v1/extensions/development/load", "loadDevelopmentExtension", typeof(InstalledExtension),
        Method: "POST", RequestType: typeof(ExtensionDevelopmentLoadRequest), Permission: ApiPermissions.ModulesConfigure);
    public static EndpointDefinition ExtensionSurfaces { get; } = new("/api/v1/extensions/surfaces", "getExtensionSurfaces", typeof(ExtensionSurfaceCatalog), ETag: true);
    public static EndpointDefinition SendExtensionUiEvent { get; } = new("/api/v1/extensions/{id}/ui-events", "sendExtensionUiEvent", typeof(ExtensionSurfaceCatalog),
        Method: "POST", RequestType: typeof(ExtensionUiEvent), Permission: ApiPermissions.DesktopControl);
    public static EndpointDefinition SaveExtensionSettings { get; } = new("/api/v1/extensions/{id}/settings", "saveExtensionSettings", typeof(ExtensionSettingsForm),
        Method: "POST", RequestType: typeof(ExtensionSettingsUpdate), Permission: ApiPermissions.ModulesConfigure);
    public static EndpointDefinition AnswerExtensionFileRequest { get; } = new("/api/v1/extensions/file-requests/{id}", "answerExtensionFileRequest", typeof(ExtensionSurfaceCatalog),
        Method: "POST", RequestType: typeof(ExtensionFileResponse), Permission: ApiPermissions.FilesTransfer);
    public static IReadOnlyList<EndpointDefinition> Endpoints { get; } = Array.AsReadOnly([
        SystemInfo, GameWindows, GameBinding, SelectGameBinding, GameObservationRuntime, GameObservation, QueryObservationChannels, StartGameObservation, ReleaseGameObservation,
        GameCaptureSettings, SaveGameCaptureSettings, StartChannelDetection, ChannelDetection, CancelChannelDetection,
        GameInputOwnership, ActivateGameWindow, GameEmergencyStop, Shortcuts, ShortcutSettings, ChangeShortcutSettings, Modules, ModuleSettingsSchema, Capabilities, Health, Ready, ShutdownHost, OpenApi,
        FileCapabilities, CreateUpload, Upload, UploadContent, FileSelection, UploadSelection, Asset, AssetContent,
        UpdateState, UpdatePolicy, SaveUpdatePolicy, CreateUpdateCheck, UpdatePlan, UpdateNotes, DownloadUpdatePlan, ApplyUpdatePlan,
        StorageUsage, PreviewStorageCleanup, StorageCleanupPlan, ExecuteStorageCleanup, StorageCleanupResult,
        StorageModuleData, PreviewStorageModuleDeletion, StorageModuleDeletionPlan, ExecuteStorageModuleDeletion,
        StorageBackups, StorageBackup, CreateStorageBackup, StorageDirectorySelection,
        PreviewStorageRestore, StorageRestorePlan, ExecuteStorageRestore, PreviewStorageMigration, StorageMigrationPlan, ExecuteStorageMigration,
        CreateFileImport, FileImport, CommitFileImport, CreateFileExport, FileExport, SaveFileExport,
        Diagnostics, DiagnosticRuntime, DiagnosticPolicy, ChangeDiagnosticPolicy, ExportDiagnostics, DiagnosticExport, DiagnosticExportContent,
        QueryDiagnosticRecords, StartDiagnosticCapture, CurrentDiagnosticCapture, StopDiagnosticCapture,
        Presentation, QueryPresentation, DesktopBridge, DesktopSurface, ChangeDesktopSurface,
        AuthSession, AuthClients, PairingChallenges, Pairings, RevokeClient,
        ModuleConfiguration, ConfigureModule, Extensions, PreviewExtensionInstall, InstallExtension, SetExtensionEnabled, UninstallExtension,
        SetExtensionPermissions, RestartExtension, ExtensionLogs, SetExtensionDevelopmentMode, LoadDevelopmentExtension, ExtensionSurfaces, SendExtensionUiEvent, SaveExtensionSettings, AnswerExtensionFileRequest, Operation, CancelOperation, EventSnapshot, Events, ReplayCatalog, MusicInstruments, QueryOwnedMusicInstruments, QueryOwnedMusicScores, StartReplay,
        CombatCapabilities, StartCombatObservation, CombatObservation, StopCombatObservation, CombatSnapshot, CombatEncounters, QueryCombatEncounters, CombatEncounter,
        StartCombatEncounter, CloseCombatEncounter, SplitCombatEncounter, ResetCombatEncounter, FavoriteCombatEncounter, CombatEntityEffects, CombatClientBuffs, CombatTimeline, QueryCombatTimeline, CleanupCombatHistory,
        QueryCombatRange, SaveCombatRange, CombatMeasurement, StartCombatMeasurement, CancelCombatMeasurement, SaveCombatMeasurementSettings, CombatAutoObservation, SetCombatAutoObservation, ReportCombatDisplayLatency,
        MusicLibrary, QueryMusicLibrary, EditMusicLibrary, MusicPlaybackHistory, ImportMusicPlaybackHistory, MusicTimeline,
        PreviewMusicImport, MusicImport, CommitMusicImport, MusicPreferences,
        PreviewRoomImport, RoomImport, CommitRoomImport, RoomPreferences, EditRoomPreferences,
        PreviewColorImport, ColorImport, CommitColorImport, ColorPreferences, ImportedColorHistory,
        PerformanceSessions, CreatePerformanceSession, PerformanceSession, CommandPerformanceSession,
        RoomSession, CurrentRoomRound, PrepareRoomRound, ControlRoomRound, RoomScoreDraft, PublishRoomScoreDraft, MapRoomScoreDraft, ClearRoomScoreDraft,
        RoomServer, StartRoomServer, StopRoomServer, CreateRoom, JoinRoom, ReconnectRoom, LeaveRoom, RoomInvitation, CreateRoomInvitation,
        EditRoomRoster, SetRoomMode, SetRoomReadiness, BindRoomCharacter, SelectRoomLeader, KickRoomMember, SetRoomGameOperation, StopRoomLocally, ReleaseRoomInput,
        StartColorRun, ColorRun, SelectColorCandidate, RetryColorRun, ColorLibrary, ColorProfile, EditColorLibrary, QueryColorHistory, ColorHistory, ExportColorHistory, ColorAsset, ColorAssetContent,
        CaptureColorScreen, PickColorPixel, ReadColorCapture, ReleaseColorCapture, PreviewColor, RenderColorPalette, ColorPresence, ColorRuntime,
        .. InventoryApi.Endpoints, .. ClientIconApi.Endpoints, .. GameConnectorApi.Endpoints, .. MusicLibraryApi.Endpoints, .. LifeApi.Endpoints, .. GameDataApi.Endpoints
    ]);

    public static IReadOnlyList<Type> DomainTypes { get; } = Array.AsReadOnly<Type>([
        typeof(ApiProblem), typeof(OperationSnapshot), typeof(GameSession), typeof(CharacterIdentity),
        typeof(GameObservationRuntime), typeof(GameObservationSnapshot), typeof(GameObservationStartRequest), typeof(GameObservationSubscription),
        typeof(GameCaptureSettings), typeof(GameCaptureAutomation), typeof(CaptureChannelDetectionRequest), typeof(CaptureChannelDetection),
        typeof(GameObservationRelease), typeof(GameCaptureReport), typeof(GameObservationValue),
        typeof(ObservationCaptureInterests), typeof(ObservationCombatInterest), typeof(CombatCaptureReport),
        typeof(ObservationInventoryInterest), typeof(ObservationInventoryProfile), typeof(InventoryCaptureReport),
        typeof(InventoryClientRequest), typeof(InventoryClientPrepared), typeof(InventoryCatalogArtifact), typeof(InventoryCaptureArtifact), typeof(InventoryFrame),
        typeof(InventoryCatalogData), typeof(InventoryCatalogSnapshot), typeof(InventoryImportReceipt),
        typeof(DiagnosticRecord), typeof(DiagnosticSessionSnapshot), typeof(DiagnosticExportDocument),
        typeof(FileExportManifest),
        typeof(Observation<CharacterIdentity>), typeof(EnvironmentSnapshot), typeof(ScoreEntry),
        typeof(ScoreTimeline), typeof(InstrumentDefinition), typeof(Playlist), typeof(PerformanceSnapshot),
        typeof(PerformanceQueueItem), typeof(PerformanceQueueState),
        typeof(PerformanceSessionCreateRequest), typeof(PerformanceSessionCommand),
        typeof(RoomCreateRequest), typeof(RoomJoinRequest), typeof(RoomLeaveRequest),
        typeof(RoomRosterEditRequest), typeof(RoomModeRequest), typeof(RoomReadinessRequest), typeof(RoomCharacterBindingRequest),
        typeof(RoomLeaderRequest), typeof(RoomMemberKickRequest), typeof(RoomGameOperationRequest), typeof(RoomLocalStopResult),
        typeof(RoomInvitation), typeof(RoomLifecycleSnapshot), typeof(RoomServerStartRequest), typeof(RoomServerStopRequest), typeof(RoomServerSnapshot), typeof(RoomTunnelSnapshot),
        typeof(RoomRoundRequest), typeof(RoomRoundControlRequest), typeof(RoomRoundSnapshot), typeof(RoomRoundTiming), typeof(RoomRoundCommandResult),
        typeof(RoomScoreDraft), typeof(RoomScoreDraftSnapshot), typeof(RoomScoreDraftCommandResult), typeof(RoomLocalScoreMapping), typeof(MusicResolvedScore), typeof(MusicScoreSelection),
        typeof(RoomSnapshot), typeof(RoomClockSample), typeof(ColorProfile), typeof(ColorRun),
        typeof(ColorCandidateSelectionRequest), typeof(ColorRetryRequest),
        typeof(CombatSummary), typeof(EffectsSnapshot), typeof(AssetSnapshot),
        typeof(CombatFrame),
        typeof(UpdateProgress), typeof(UpdateInstallResult), typeof(UpdateDownloadStatus), typeof(UpdatePolicy), typeof(UpdatePolicySnapshot),
        typeof(ResourceSnapshot<CombatSummary>), typeof(EventStreamOptions), typeof(StreamControl)
    ]);
}
