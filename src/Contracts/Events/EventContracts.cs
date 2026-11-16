namespace MabiTool.Contracts;

public sealed record EventEnvelope<T>(string EventId, string Type, int SchemaVersion, string Module,
    string ResourceId, UInt64String ResourceRevision, DateTimeOffset OccurredAt, string? CorrelationId, T Data) where T : notnull;
public sealed record ResourceSnapshot<T>(string BackendEpoch, string EventCursor, UInt64String ResourceRevision, T Data) where T : notnull;
public sealed record EventDefinition(string Type, string Module, Type DataType)
{
    public Type EnvelopeType => typeof(EventEnvelope<>).MakeGenericType(DataType);
}

public static class ContractEvents
{
    public static IReadOnlyList<EventDefinition> Definitions { get; } = Array.AsReadOnly<EventDefinition>([
        new("replay.observation-changed", "core", typeof(Observation<ReplayState>)),
        new("operation.changed", "core", typeof(OperationSnapshot)),
        new("module.changed", "core", typeof(ModuleSnapshot)),
        new("module.configuration-changed", "core", typeof(ModuleConfiguration)),
        new("music.catalog.changed", "music-library", typeof(MusicCatalogChanged)),
        new("music.library.changed", "music-library", typeof(MusicEditResult)),
        new("music.playback-history.changed", "music-library", typeof(MusicPlaybackHistoryChanged)),
        new("music.import.committed", "music-library", typeof(MusicImportChanged)),
        new("room.import.committed", "rooms", typeof(SettingsImportChanged)),
        new("color.import.committed", "color", typeof(SettingsImportChanged)),
        new("room.preferences.changed", "rooms", typeof(PreferenceRevisionChanged)),
        new("room.session.changed", "rooms", typeof(RoomPresentation)),
        new("room.score-draft.changed", "rooms", typeof(RoomScoreDraftChanged)),
        new("room.server.changed", "rooms", typeof(RoomServerSnapshot)),
        new("desktop.surface.changed", "presentation", typeof(DesktopSurfaceSnapshot)),
        new("shortcut.triggered", "presentation", typeof(ShortcutTriggered)),
        new("diagnostics.record", "core", typeof(DiagnosticRecord)),
        new("game.nearby.changed", "game-access", typeof(Observation<NearbyPlayersSnapshot>)),
        new("game.environment.changed", "environment", typeof(Observation<EnvironmentSnapshot>)),
        new("game.observation.changed", "game-access", typeof(GameObservationSnapshot)),
        new("color.preferences.changed", "color", typeof(PreferenceRevisionChanged)),
        new("performance.state.changed", "performance", typeof(PerformanceSnapshot)),
        new("color.library.changed", "color", typeof(ColorLibraryChanged)),
        new("color.run.changed", "color", typeof(ColorRun)),
        new("combat.summary.changed", "combat-observation", typeof(CombatSummary)),
        new("combat.effects.changed", "combat-observation", typeof(EffectsSnapshot)),
        new("combat.encounter.closed", "combat-observation", typeof(CombatEncounterClosed)),
        new("combat.observation.degraded", "combat-observation", typeof(CombatObservationDegraded)),
        new("combat.measurement.changed", "combat-observation", typeof(CombatMeasurementState)),
        new("inventory.snapshot.changed", "inventory", typeof(InventorySnapshotChanged)),
        new("inventory.observation.degraded", "inventory", typeof(InventoryObservationDegraded)),
        new("inventory.catalog.changed", "inventory", typeof(InventoryCatalogChanged)),
        new("storage.changed", "core", typeof(StorageChanged)),
        new("storage.backup.changed", "core", typeof(StorageBackupChanged)),
        new("storage.maintenance.changed", "core", typeof(StorageMaintenanceChanged)),
        new("storage.module-data.changed", "core", typeof(StorageModuleDataChanged)),
        new("update.progress", "core", typeof(UpdateProgress)),
        new("update.policy-changed", "core", typeof(UpdatePolicySnapshot))
    ]);
}
