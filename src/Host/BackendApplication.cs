using System.Net;
using System.Runtime.InteropServices;
using MabiTool.Contracts;
using MabiTool.Contracts.Generation;
using MabiTool.ModuleSdk;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.Providers.Replay;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using MabiTool.Modules.MusicLibrary.Application;
using MabiTool.Host.Extensions;
using MabiTool.Host.Rooms;
using MabiTool.Host.Updates;
using MabiTool.Host.Color;
using MabiTool.Host.Shortcuts;
using MabiTool.Host.DataImports;
using MabiTool.Host.Presentation;
using MabiTool.Host.Diagnostics;
using MabiTool.Host.Files;
using MabiTool.Host.Game;
using MabiTool.Host.Combat;
using MabiTool.Host.Inventory;
using MabiTool.Host.Performance;
using MabiTool.Host.Storage;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Modules.Inventory.Adapters;
using MabiTool.Infrastructure.Workers;
using MabiTool.Platform.Windows;
using MabiTool.Modules.Color.Persistence;
using MabiTool.Modules.Rooms.Persistence;
using MabiTool.Modules.Rooms.Domain;
using MabiTool.Providers.GitHubReleases;
using MabiTool.Providers.Tunnels;
using Microsoft.AspNetCore.Mvc;

namespace MabiTool.Host;

public static class BackendApplication
{
    internal static string HostVersion => typeof(BackendApplication).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public static IReadOnlyList<StorageDataDefinition> StorageData { get; } = Array.AsReadOnly<StorageDataDefinition>([
        MusicStorageData.Definition, ColorStorageData.Definition, RoomStorageData.Definition, CombatStorageData.Definition, InventoryStorageData.Definition,
        new("core", ["platform_assets", "platform_asset_references", "core_module_configuration", "platform_shortcut_settings", "platform_shortcut_receipts"])]);
    public static IReadOnlyList<DatabaseMigration> DatabaseMigrations { get; } = Array.AsReadOnly<DatabaseMigration>(
        [.. PlatformMigrations.All, .. MusicLibraryStore.Migrations, .. AssetStore.Migrations, .. UpdatePolicyStore.Migrations, ColorLibraryStore.Migration,
            RoomPreferenceStore.Migration, RoomPreferenceStore.EditingMigration, RoomPreferenceStore.TailDefaultMigration, GameCaptureSettingsStore.Migration, ShortcutSettingsStore.Migration, ColorPreferenceStore.Migration, SettingsImportCoordinator.Migration, DiagnosticStore.Migration, DiagnosticStore.CaptureMigration, FileTransferStore.Migration, .. CombatHistoryStore.Migrations,
            InventoryHistoryStore.Migration, TemporaryStorageRegistry.Migration, StorageControlStore.Migration, DataBackupStore.Migration, StorageMaintenancePlanStore.Migration,
            StorageModuleDataStore.Migration]);
    public static WebApplication Build(LocalApiOptions options, TimeProvider? clock = null, StorageOptions? storage = null, bool mock = false,
        Func<IServiceProvider, ColorRunWorkerFactory>? colorFactory = null,
        IDesktopCaptureSource? desktopCapture = null, Func<PathService, ColorWorkerOptions?>? colorPreviewOptions = null,
        IGlobalShortcutSource? shortcutSource = null, Func<IServiceProvider, GameControlContext>? gameControls = null,
        IGameWindowSource? gameWindowSource = null, Func<IServiceProvider, ICombatObservationSource>? combatSource = null,
        Func<HttpMessageHandler>? updateHttpHandler = null,
        QuickTunnelFactory? roomTunnels = null, IRoomServerListenerFactory? roomListeners = null,
        Func<PathService?, WorkerProcessOptions?>? observationOptions = null, bool gameAutomation = false,
        PortableInstallation? portableUpdate = null, System.Diagnostics.Process? ownerProcess = null)
    {
        options.Validate();
        if (portableUpdate is not null && (storage is null || mock))
            throw new ArgumentException("解压版更新需要持久化正式宿主");
        if (mock && (storage?.PortableRoot is null || storage.DataRoot is not null ||
            !Path.IsPathFullyQualified(storage.PortableRoot) ||
            Path.GetFullPath(storage.BootstrapDirectory) != Path.GetFullPath(Path.Combine(storage.PortableRoot, "bootstrap")) ||
            options.RuntimeDirectory is not null && Path.GetFullPath(options.RuntimeDirectory) != Path.GetFullPath(Path.Combine(storage.PortableRoot, "runtime"))))
            throw new ArgumentException("模拟模式须使用独立 --portable-root，发现与引导目录须位于该目录内");
        var info = new SystemInfo("Moongate", HostVersion, "1.0",
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription);
        // 独立进程配置只经明确的本机选项进入，避免 ASPNETCORE_URLS 改成公网监听。
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(BackendApplication).Assembly.FullName
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        void AddBackground<T>(Func<IServiceProvider, T>? factory = null) where T : class, IHostedService
        {
            builder.Services.AddHostedService(factory ?? (sp => ActivatorUtilities.CreateInstance<T>(sp)));
        }
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, options.Port);
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.Limits.MaxConcurrentConnections = 64;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddSingleton(info);
        builder.Services.AddSingleton(clock ?? TimeProvider.System);
        builder.Services.AddSingleton(sp => new DiagnosticTrace(sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp => new OwnedInstrumentProvider(sp.GetService<IInventoryReadModel>(), info.InstanceId, mock));
        builder.Services.AddSingleton(sp => new OwnedScoreProvider(sp.GetService<IInventoryReadModel>(), info.InstanceId, mock));
        builder.Services.AddSingleton<ICombatObservationSource>(sp => combatSource?.Invoke(sp) ??
            (mock ? new CombatReplaySource(clock) : new SharedCaptureCombatSource(sp.GetRequiredService<GameObservationService>(),
                sp.GetRequiredService<GameControlContext>(), MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol.CombatProfile,
                sp.GetService<OperationStore>())));
        builder.Services.AddSingleton<ActivityBarrier>();
        builder.Services.AddSingleton(VolumeSpaceReservations.Shared);
        builder.Services.AddSingleton(sp => new ResourceLeaseCoordinator(clock, sp.GetRequiredService<ActivityBarrier>()));
        builder.Services.AddSingleton<GameControlContext>(sp => gameControls?.Invoke(sp) ??
            (colorFactory is not null && sp.GetService<ColorRunWorkerFactory>() is { } color
                ? new(color.Bindings, color.Leases, color.Input, mock)
                : GameControlContext.Create(sp.GetRequiredService<ResourceLeaseCoordinator>(), clock ?? TimeProvider.System, mock)));
        builder.Services.AddSingleton(sp => new GameBindingService(sp.GetRequiredService<GameControlContext>(),
            gameWindowSource ?? (!mock && OperatingSystem.IsWindows() ? new WindowsGameWindowSource() : null), info, clock ?? TimeProvider.System,
            sp.GetRequiredService<LocalAuthService>()));
        builder.Services.AddSingleton(sp =>
        {
            var configured = observationOptions?.Invoke(sp.GetService<PathService>());
            return new GameObservationService(sp.GetRequiredService<GameControlContext>(), sp.GetRequiredService<LocalAuthService>(),
                sp.GetRequiredService<ActivityBarrier>(), info, sp.GetRequiredService<TimeProvider>(), () => configured, sp.GetRequiredService<ModuleRuntime>(),
                sp.GetRequiredService<DiagnosticTrace>(), sp.GetRequiredService<GameCaptureSettingsStore>(), gameAutomation);
        });
        builder.Services.AddSingleton(sp => new GameCaptureSettingsStore(sp.GetService<SqliteStore>(), sp.GetRequiredService<TimeProvider>()));
        if (gameAutomation) AddBackground(sp => new GameWindowAutoBinder(sp.GetRequiredService<GameBindingService>(),
            sp.GetRequiredService<GameCaptureSettingsStore>(), sp.GetRequiredService<GameControlContext>(), sp.GetRequiredService<TimeProvider>()));
        AddBackground(sp => sp.GetRequiredService<GameObservationService>());
        builder.Services.AddSingleton(sp =>
        {
            var controls = sp.GetRequiredService<GameControlContext>();
            var binding = sp.GetRequiredService<GameBindingService>();
            return new RoomSessionManager(clock, activityBarrier: sp.GetRequiredService<ActivityBarrier>(),
                inputPorts: new(controls.Bindings, controls.Leases, controls.Input, binding.TryActivate, Simulation: controls.Simulation),
                preferences: owner => sp.GetService<RoomPreferenceStore>()?.Read(owner) ?? RoomPreferences.Default,
                authorized: owner => sp.GetRequiredService<LocalAuthService>().HasPermission(owner, ApiPermissions.RoomsCoordinate),
                shortcutConflict: value => ShortcutService.Conflicts(value, sp.GetService<IGlobalShortcutSource>()), backendEpoch: info.InstanceId,
                scoreReader: sp.GetService<MusicScoreReader>());
        });
        AddBackground(sp => sp.GetRequiredService<RoomSessionManager>());
        builder.Services.AddSingleton<GameControlService>();
        builder.Services.AddSingleton<ShortcutSettingsService>();
        builder.Services.AddSingleton<DesktopBridgeRegistry>();
        builder.Services.AddSingleton<MaintenanceShutdownContext>();
        builder.Services.AddSingleton<FileSelectionRegistry>();
        AddBackground(sp => sp.GetRequiredService<FileSelectionRegistry>());
        builder.Services.AddSingleton(sp => new StateEventHub(sp.GetService<SqliteStore>(), info.InstanceId, clock));
        builder.Services.AddSingleton<PresentationStateFeed>();
        AddBackground(sp => sp.GetRequiredService<PresentationStateFeed>());
        builder.Services.AddSingleton<GameObservationFeed>();
        AddBackground(sp => sp.GetRequiredService<GameObservationFeed>());
        builder.Services.AddSingleton<PresentationService>();
        builder.Services.AddSingleton(_ => new ColorCaptureService(desktopCapture ??
            (!mock && OperatingSystem.IsWindows() ? new WindowsDesktopCaptureSource() : null), clock ?? TimeProvider.System));
        AddBackground(sp => sp.GetRequiredService<ColorCaptureService>());
        builder.Services.AddSingleton(_ => new ProviderCapabilityRegistry([
            new ProviderCapabilityRequirement("music.inventory", "music-library", "tw-cli",
                new Version(1, 0), new Version(2, 0), "TW", new HashSet<string>(),
                new HashSet<string>(["score.read"]), []),
            new ProviderCapabilityRequirement("environment.observe", "environment", "tw-cli",
                new Version(1, 0), new Version(2, 0), "TW", new HashSet<string>(),
                new HashSet<string>(["environment.read"]), [])], clock));
        if (storage is not null)
        {
            builder.Services.AddSingleton(_ => DataRootLease.Open(storage));
            builder.Services.AddSingleton(sp => sp.GetRequiredService<DataRootLease>().Paths);
            builder.Services.AddSingleton(sp => StorageTransactionTelemetry.Attach(SqliteStore.Open(sp.GetRequiredService<DataRootLease>(),
                DatabaseMigrations, mock: mock,
                activityBarrier: sp.GetRequiredService<ActivityBarrier>(), spaceReservations: sp.GetRequiredService<VolumeSpaceReservations>(),
                clock: sp.GetRequiredService<TimeProvider>()), sp));
            builder.Services.AddSingleton<AssetStore>();
            builder.Services.AddSingleton<DataBackupStore>();
            builder.Services.AddSingleton(sp => new StorageDataCatalog(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<PathService>(), StorageData));
            builder.Services.AddSingleton<StorageBackupOperations>();
            AddBackground(sp => sp.GetRequiredService<StorageBackupOperations>());
            AddBackground<StorageAutomaticBackupLifetime>();
            builder.Services.AddSingleton(sp => new StorageMaintenancePlanStore(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<DataBackupStore>(),
                sp.GetRequiredService<StorageDataCatalog>(), info.InstanceId, DatabaseMigrations, sp.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton(sp => new StorageMaintenanceCoordinator(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<DataRootLease>(),
                sp.GetRequiredService<StorageMaintenancePlanStore>(), sp.GetRequiredService<OperationStore>(), DatabaseMigrations));
            builder.Services.AddSingleton<StorageDirectorySelectionRegistry>();
            AddBackground(sp => sp.GetRequiredService<StorageDirectorySelectionRegistry>());
            builder.Services.AddSingleton<StorageMaintenanceOperations>();
            AddBackground(sp => sp.GetRequiredService<StorageMaintenanceOperations>());
            builder.Services.AddSingleton(sp => new ColorLibraryStore(sp.GetRequiredService<SqliteStore>(), clock));
            builder.Services.AddSingleton<RoomPreferenceStore>();
            builder.Services.AddSingleton<ShortcutSettingsStore>();
            builder.Services.AddSingleton<ColorPreferenceStore>();
            builder.Services.AddSingleton<SettingsImportCoordinator>();
            builder.Services.AddSingleton(sp => new StorageModuleDataStore(sp.GetRequiredService<SqliteStore>(),
                sp.GetRequiredService<AssetStore>(), sp.GetRequiredService<StorageDataCatalog>(), sp.GetRequiredService<OperationStore>(),
                [MusicStorageData.Owner(sp.GetRequiredService<SqliteStore>()), ColorStorageData.Owner(sp.GetRequiredService<SqliteStore>()),
                    RoomStorageData.Owner(sp.GetRequiredService<SqliteStore>()), CombatStorageData.Owner(sp.GetRequiredService<SqliteStore>()),
                    InventoryStorageData.Owner(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<InventoryHistoryStore>())],
                id => sp.GetRequiredService<OperationStore>().Configuration(id,
                    sp.GetRequiredService<ModuleCatalog>().Registrations[id].Definition.EnabledByDefault),
                info.InstanceId, sp.GetRequiredService<TimeProvider>(), SettingsImportCoordinator.ForgetModuleData));
            builder.Services.AddSingleton<StorageModuleDeletionOperations>();
            builder.Services.AddSingleton(sp => new ExtensionInstaller(sp.GetRequiredService<PathService>().Resolve("extensions"),
                Version.Parse(ApiContract.Version), sp.GetRequiredService<TimeProvider>()));
            AddBackground<ExtensionStartup>();
            AddBackground(sp => sp.GetRequiredService<StorageModuleDeletionOperations>());
            builder.Services.AddSingleton<SettingsImportOperations>();
            AddBackground(sp => sp.GetRequiredService<SettingsImportOperations>());
            builder.Services.AddSingleton(sp => new UpdatePolicyStore(sp.GetRequiredService<SqliteStore>(), clock));
            builder.Services.AddSingleton(sp => new ScoreCatalogStore(sp.GetRequiredService<SqliteStore>(), info.InstanceId, clock));
            builder.Services.AddSingleton(sp => new MusicLibraryStore(sp.GetRequiredService<SqliteStore>(), info.InstanceId, clock));
            builder.Services.AddSingleton(sp => new PerformanceSessionStore(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<PerformanceSessionService>();
            builder.Services.AddSingleton(sp => new PlaybackHistoryStore(sp.GetRequiredService<SqliteStore>(), clock));
            builder.Services.AddSingleton(sp => new ScoreTimelineStore(sp.GetRequiredService<SqliteStore>(), clock));
            builder.Services.AddSingleton<MusicScoreReader>();
            builder.Services.AddSingleton(sp => new MusicImportStore(sp.GetRequiredService<SqliteStore>(), info.InstanceId, clock));
            AddBackground<PersistenceLifetime>();
            builder.Services.AddSingleton(sp => new RoomServerOperations(sp.GetRequiredService<OperationStore>(),
                sp.GetRequiredService<ActivityBarrier>(), sp.GetRequiredService<ModuleRuntime>(),
                sp.GetRequiredService<StateEventHub>(), info, sp.GetRequiredService<TimeProvider>(),
                roomTunnels ?? new QuickTunnelFactory(TunnelComponentOptions.FromEnvironment(), time: clock),
                roomListeners));
            AddBackground(sp => sp.GetRequiredService<RoomServerOperations>());
            builder.Services.AddSingleton<RoomRoundOperations>();
            AddBackground(sp => sp.GetRequiredService<RoomRoundOperations>());
            builder.Services.AddSingleton<RoomScoreDraftOperations>();
            AddBackground(sp => sp.GetRequiredService<RoomScoreDraftOperations>());
            builder.Services.AddSingleton<DiagnosticStore>();
            builder.Services.AddSingleton(sp => new StorageControlStore(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<PathService>(),
                [sp.GetRequiredService<AssetStore>(), sp.GetRequiredService<DiagnosticStore>(), sp.GetRequiredService<DataBackupStore>(),
                    new PhysicalStorageMaintenance(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<PathService>())],
                info.InstanceId, sp.GetRequiredService<TimeProvider>(), () => new Dictionary<string, long>
                {
                    ["cache"] = 512L * 1024 * 1024,
                    ["logs"] = (long)sp.GetRequiredService<DiagnosticStore>().Policy().Policy.LogBudgetBytes!.Value.Value,
                    ["diagnostics"] = (long)sp.GetRequiredService<DiagnosticStore>().Policy().Policy.SessionBudgetBytes!.Value.Value
                }));
            AddBackground<StorageRetentionLifetime>();
            builder.Services.AddSingleton<DiagnosticRecorder>();
            builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<DiagnosticRecorder>());
            AddBackground(sp => sp.GetRequiredService<DiagnosticRecorder>());
            builder.Services.AddSingleton(sp => new DiagnosticCaptureService(sp.GetRequiredService<DiagnosticTrace>(), sp.GetRequiredService<DiagnosticStore>(),
                sp.GetRequiredService<DiagnosticRuntimeService>(), sp.GetRequiredService<StateEventHub>(), sp.GetRequiredService<PathService>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetService<MaintenanceShutdownContext>(), portableUpdate?.PackageRoot));
            AddBackground(sp => sp.GetRequiredService<DiagnosticCaptureService>());
            builder.Services.AddSingleton<DiagnosticExportOperations>();
            AddBackground(sp => sp.GetRequiredService<DiagnosticExportOperations>());
            builder.Services.AddSingleton<FileTransferStore>();
            builder.Services.AddSingleton<FileTransferService>();
            AddBackground<FileTransferLifetime>();
            builder.Services.AddSingleton<FileExportOperations>();
            AddBackground(sp => sp.GetRequiredService<FileExportOperations>());
            AddBackground<StateOutboxPublisher>();
            builder.Services.AddSingleton(sp => new OperationStore(sp.GetRequiredService<SqliteStore>(), clock));
            builder.Services.AddSingleton(sp => new CombatHistoryStore(sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<OperationStore>(), info.InstanceId, clock));
            builder.Services.AddSingleton<CombatOperations>();
            builder.Services.AddSingleton(sp => new CombatNameCatalog(sp.GetRequiredService<GameControlContext>(), sp.GetRequiredService<TimeProvider>()));
            AddBackground(sp => sp.GetRequiredService<CombatOperations>());
            builder.Services.AddSingleton(sp => new InventoryHistoryStore(sp.GetRequiredService<SqliteStore>(), info.InstanceId, clock));
            builder.Services.AddSingleton<IInventoryReadModel>(sp => sp.GetRequiredService<InventoryHistoryStore>());
            builder.Services.AddSingleton<InventoryOperations>();
            AddBackground(sp => sp.GetRequiredService<InventoryOperations>());
            builder.Services.AddSingleton<MusicImportOperations>();
            AddBackground(sp => sp.GetRequiredService<MusicImportOperations>());
            if (colorFactory is not null) builder.Services.AddSingleton(colorFactory);
            builder.Services.AddSingleton(sp => sp.GetService<ColorRunWorkerFactory>()?.Workers ??
                new ColorWorkerPool(colorPreviewOptions?.Invoke(sp.GetRequiredService<PathService>()), clock));
            AddBackground<ColorRuntimeLifetime>();
            builder.Services.AddSingleton<ColorRunOperations>();
            builder.Services.AddSingleton<ColorHistoryService>();
            builder.Services.AddSingleton(sp => new ColorPreviewService(sp.GetRequiredService<ColorWorkerPool>().Options,
                sp.GetRequiredService<ActivityBarrier>(), sp.GetRequiredService<ColorWorkerPool>()));
            AddBackground(sp => sp.GetRequiredService<ColorRunOperations>());
            builder.Services.AddSingleton(sp => new ColorPaletteService(sp.GetRequiredService<ColorWorkerPool>().Options,
                sp.GetRequiredService<AssetStore>(), sp.GetRequiredService<ActivityBarrier>(), clock));
            AddBackground(sp => sp.GetRequiredService<ColorPaletteService>());
            if (shortcutSource is not null) builder.Services.AddSingleton(shortcutSource);
            else if (!mock && OperatingSystem.IsWindows())
                builder.Services.AddSingleton<IGlobalShortcutSource>(_ => OperatingSystem.IsWindows()
                    ? new WindowsGlobalShortcutSource() : throw new PlatformNotSupportedException());
            builder.Services.AddSingleton<ShortcutService>();
            AddBackground(sp => sp.GetRequiredService<ShortcutService>());
            builder.Services.AddSingleton<ModuleConfigurationOperations>();
            AddBackground(sp => sp.GetRequiredService<ModuleConfigurationOperations>());
            builder.Services.AddSingleton(_ => new GitHubReleaseSource(updateHttpHandler?.Invoke(), clock, retryCount: updateHttpHandler is null ? 2 : 0));
            builder.Services.AddSingleton(sp => new PortableUpdateService(portableUpdate, sp.GetRequiredService<GitHubReleaseSource>(),
                sp.GetRequiredService<SqliteStore>(), sp.GetRequiredService<OperationStore>(),
                () => sp.GetRequiredService<UpdatePolicyStore>().Get().Policy, sp.GetRequiredService<ActivityBarrier>(),
                sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<MaintenanceShutdownContext>(),
                sp.GetRequiredService<TimeProvider>(), ownerProcess, updateHttpHandler));
            builder.Services.AddSingleton<IUpdateCheckService>(sp => sp.GetRequiredService<PortableUpdateService>());
            builder.Services.AddSingleton<IUpdateDownloadService>(sp => sp.GetRequiredService<PortableUpdateService>());
            builder.Services.AddSingleton<IUpdateInstallService>(sp => sp.GetRequiredService<PortableUpdateService>());
            builder.Services.AddSingleton<UpdateCheckOperations>();
            AddBackground(sp => sp.GetRequiredService<UpdateCheckOperations>());
            builder.Services.AddSingleton(sp => new UpdateApiService(portableUpdate, sp.GetRequiredService<IUpdateDownloadService>(),
                sp.GetRequiredService<OperationStore>(), sp.GetRequiredService<UpdatePolicyStore>(), sp.GetRequiredService<TimeProvider>()));
        }
        if (mock)
        {
            builder.Services.AddSingleton(new ReplayProvider(info.InstanceId, clock));
            builder.Services.AddSingleton<ReplayOperations>();
            AddBackground(sp => sp.GetRequiredService<ReplayOperations>());
        }
        // 运行状态探针按展示顺序登记；依赖持久化宿主的组件仅在其服务存在时登记。
        builder.Services.AddSingleton<IDiagnosticProbe>(sp => new HostDiagnosticProbe(info, sp.GetRequiredService<StateEventHub>()));
        builder.Services.AddSingleton<IDiagnosticProbe>(sp => new GameBindingDiagnosticProbe(sp.GetRequiredService<GameControlContext>(),
            sp.GetRequiredService<ProviderCapabilityRegistry>()));
        builder.Services.AddSingleton<IDiagnosticProbe>(sp => new GameInputDiagnosticProbe(sp.GetRequiredService<GameControlContext>()));
        builder.Services.AddSingleton<IDiagnosticProbe>(sp => new ObservationCaptureDiagnosticProbe(sp.GetRequiredService<GameObservationService>()));
        builder.Services.AddSingleton<IDiagnosticProbe>(sp => new MusicDiagnosticProbe(sp.GetRequiredService<GameObservationService>()));
        if (storage is not null)
        {
            builder.Services.AddSingleton<IDiagnosticProbe>(sp => new CombatDiagnosticProbe(sp.GetRequiredService<CombatOperations>(),
                sp.GetRequiredService<ICombatObservationSource>(), sp.GetRequiredService<GameObservationService>(), sp.GetRequiredService<GameControlContext>()));
            builder.Services.AddSingleton<IDiagnosticProbe>(sp => new InventoryDiagnosticProbe(sp.GetRequiredService<InventoryOperations>(),
                sp.GetRequiredService<InventoryHistoryStore>(), sp.GetRequiredService<GameObservationService>(), sp.GetRequiredService<GameControlContext>()));
            builder.Services.AddSingleton<IDiagnosticProbe>(sp => new ColorDiagnosticProbe(sp.GetRequiredService<ColorRunOperations>(),
                sp.GetRequiredService<ColorWorkerPool>()));
        }
        builder.Services.AddSingleton<IDiagnosticProbe>(sp => new RoomDiagnosticProbe(sp.GetRequiredService<RoomSessionManager>(),
            sp.GetService<RoomServerOperations>()));
        if (storage is not null)
            builder.Services.AddSingleton<IDiagnosticProbe>(sp => new DiagnosticsDiagnosticProbe(sp.GetRequiredService<DiagnosticRecorder>(),
                sp.GetRequiredService<DiagnosticStore>()));
        AddBackground(sp => new DiagnosticLifecycleObserver(sp.GetRequiredService<DiagnosticTrace>(), sp.GetRequiredService<GameControlContext>(),
            sp.GetRequiredService<RoomSessionManager>()));
        builder.Services.AddSingleton(sp => new DiagnosticRuntimeService(sp.GetServices<IDiagnosticProbe>(), info, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(new LocalAuthService(options, info.InstanceId, clock));
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        builder.Services.AddSingleton(BuiltInModules.CreateCatalog(colorFactory is not null));
        builder.Services.AddSingleton<ModuleRuntime>();
        AddBackground<IdleCollector>();
        builder.Services.ConfigureHttpJsonOptions(json => ContractJson.Configure(json.SerializerOptions));
        if (options.RuntimeDirectory is not null)
        {
            builder.Services.AddSingleton(options);
            AddBackground<LocalDiscoveryService>();
        }
        var app = builder.Build();
        app.UseMiddleware<LocalApiMiddleware>(options);
        GameEndpoints.Map(app);
        GameControlEndpoints.Map(app);
        ShortcutSettingsEndpoints.Map(app);
        GameObservationEndpoints.Map(app);
        CombatEndpoints.Map(app);
        InventoryEndpoints.Map(app);
        PerformanceEndpoints.Map(app);
        FileTransferEndpoints.Map(app);
        StorageEndpoints.Map(app);
        UpdateEndpoints.Map(app);
        RoomServerEndpoints.Map(app);
        RoomRoundEndpoints.Map(app);
        RoomScoreDraftEndpoints.Map(app);
        ExtensionEndpoints.Map(app);
        app.MapGet(ApiContract.Diagnostics.Path, (HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context); var store = RequiredService<DiagnosticStore>(context);
            var recorder = RequiredService<DiagnosticRecorder>(context);
            return Results.Ok(new DiagnosticSummary(store.Policy(), store.Sessions().Where(x => session.Owner || x.Owner == session.ClientId).ToArray(),
                new((ulong)store.Logs.Bytes), new(store.Logs.DroppedRecords + recorder.DroppedRecords), recorder.LastError ?? store.Logs.LastError));
        }).WithMetadata(ApiContract.Diagnostics);
        app.MapGet(ApiContract.DiagnosticRuntime.Path, (DiagnosticRuntimeService runtime) => Results.Ok(runtime.Capture()))
            .WithMetadata(ApiContract.DiagnosticRuntime);
        app.MapGet(ApiContract.DiagnosticPolicy.Path, (HttpContext context) =>
        {
            var value = RequiredService<DiagnosticStore>(context).Policy();
            context.Response.Headers.ETag = DiagnosticStore.PolicyETag(value.Revision); return Results.Ok(value);
        }).WithMetadata(ApiContract.DiagnosticPolicy);
        app.MapPost(ApiContract.ChangeDiagnosticPolicy.Path, async (DiagnosticRetentionPolicy policy, HttpContext context, CancellationToken ct) =>
        {
            var value = await RequiredService<DiagnosticStore>(context).ChangePolicyAsync(LocalApiMiddleware.Session(context).ClientId,
                policy, context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, ct);
            context.Response.Headers.ETag = DiagnosticStore.PolicyETag(value.Revision); return Results.Ok(value);
        }).WithMetadata(ApiContract.ChangeDiagnosticPolicy);
        app.MapPost(ApiContract.ExportDiagnostics.Path, async (DiagnosticExportRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var operation = await RequiredService<DiagnosticExportOperations>(context).SubmitAsync(session.ClientId, session.Owner, request,
                context.Request.Headers["Idempotency-Key"], ct);
            context.Response.Headers.Location = ApiContract.Operation.Path.Replace("{id}", operation.Id);
            context.Response.Headers.ETag = OperationStore.OperationETag(operation);
            return Results.Json(operation, statusCode: StatusCodes.Status202Accepted);
        }).WithMetadata(ApiContract.ExportDiagnostics);
        app.MapGet(ApiContract.DiagnosticExport.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(RequiredService<DiagnosticStore>(context).GetExport(id, session.ClientId, session.Owner));
        }).WithMetadata(ApiContract.DiagnosticExport);
        app.MapPost(ApiContract.DiagnosticExportContent.Path, async (string id, AssetContentRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(await RequiredService<DiagnosticStore>(context).ReadExportAsync(id, session.ClientId, session.Owner, request, ct));
        }).WithMetadata(ApiContract.DiagnosticExportContent);
        app.MapPost(ApiContract.QueryDiagnosticRecords.Path, async (DiagnosticRecordQuery query, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(await RequiredService<DiagnosticStore>(context).QueryAsync(session.ClientId, session.Owner,
                session.Permissions.Contains(ApiPermissions.DiagnosticsDebug, StringComparer.Ordinal), query, info.InstanceId + ":diagnostic-records", ct));
        }).WithMetadata(ApiContract.QueryDiagnosticRecords);
        app.MapPost(ApiContract.StartDiagnosticCapture.Path, async (DiagnosticCaptureRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = await RequiredService<DiagnosticCaptureService>(context).StartAsync(session.ClientId, request,
                SingleHeader(context, "Idempotency-Key"), ct);
            return Results.Json(value, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(ApiContract.StartDiagnosticCapture);
        app.MapGet(ApiContract.CurrentDiagnosticCapture.Path, (HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(RequiredService<DiagnosticCaptureService>(context).Current(session.ClientId, session.Owner));
        }).WithMetadata(ApiContract.CurrentDiagnosticCapture);
        app.MapPost(ApiContract.StopDiagnosticCapture.Path, async (string id, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(await RequiredService<DiagnosticCaptureService>(context).StopAsync(id, session.ClientId, session.Owner, ct));
        }).WithMetadata(ApiContract.StopDiagnosticCapture);
        app.MapGet(ApiContract.SystemInfo.Path, async (ModuleRuntime runtime, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("core", "system-info", ct);
            return Results.Ok(info);
        }).WithMetadata(ApiContract.SystemInfo);
        app.MapGet(ApiContract.Shortcuts.Path, (HttpContext context) =>
            Results.Ok(context.RequestServices.GetService<ShortcutService>()?.Snapshot() ??
                new ApiList<GlobalShortcutSnapshot>([], null, "shortcuts:unconfigured"))).WithMetadata(ApiContract.Shortcuts);
        app.MapGet(ApiContract.Presentation.Path, async (HttpContext context, PresentationService presentation, CancellationToken ct) =>
            Results.Ok(await presentation.ReadAsync(LocalApiMiddleware.Session(context), new(), ct))).WithMetadata(ApiContract.Presentation);
        app.MapPost(ApiContract.QueryPresentation.Path, async (PresentationQuery query, HttpContext context, PresentationService presentation, CancellationToken ct) =>
            Results.Ok(await presentation.ReadAsync(LocalApiMiddleware.Session(context), query, ct))).WithMetadata(ApiContract.QueryPresentation);
        app.MapGet(ApiContract.DesktopBridge.Path, async (HttpContext context, DesktopBridgeRegistry desktop, CancellationToken ct) =>
            Results.Ok(await desktop.StatusAsync(LocalApiMiddleware.Session(context).ClientId, ct))).WithMetadata(ApiContract.DesktopBridge);
        app.MapGet(ApiContract.DesktopSurface.Path, async (string id, HttpContext context, DesktopBridgeRegistry desktop, CancellationToken ct) =>
        {
            var result = await desktop.ReadAsync(LocalApiMiddleware.Session(context).ClientId, id, ct);
            context.Response.Headers.ETag = DesktopBridgeRegistry.ETag(result.Revision);
            return Results.Ok(result);
        }).WithMetadata(ApiContract.DesktopSurface);
        app.MapPost(ApiContract.ChangeDesktopSurface.Path, async (string id, DesktopSurfaceChange change, HttpContext context, DesktopBridgeRegistry desktop, CancellationToken ct) =>
        {
            var result = await desktop.ApplyAsync(LocalApiMiddleware.Session(context).ClientId, id, change,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, ct);
            context.Response.Headers.ETag = DesktopBridgeRegistry.ETag(result.Revision);
            return Results.Ok(result);
        }).WithMetadata(ApiContract.ChangeDesktopSurface);
        MapSettingsImports(app, "mabisync", ApiContract.PreviewRoomImport, ApiContract.RoomImport, ApiContract.CommitRoomImport);
        MapSettingsImports(app, "color-studio", ApiContract.PreviewColorImport, ApiContract.ColorImport, ApiContract.CommitColorImport);
        app.MapGet(ApiContract.RoomPreferences.Path, (HttpContext context) =>
        {
            var value = RequiredService<RoomPreferenceStore>(context).Read(LocalApiMiddleware.Session(context).ClientId);
            context.Response.Headers.ETag = RoomPreferenceStore.ETag(value.Revision); return Results.Ok(value);
        }).WithMetadata(ApiContract.RoomPreferences);
        app.MapGet(ApiContract.ColorPreferences.Path, (HttpContext context) => Results.Ok(
            RequiredService<ColorPreferenceStore>(context).Read(LocalApiMiddleware.Session(context).ClientId))).WithMetadata(ApiContract.ColorPreferences);
        app.MapPost(ApiContract.ImportedColorHistory.Path, (ImportedColorHistoryQuery query, HttpContext context) => Results.Ok(
            RequiredService<ColorPreferenceStore>(context).History(LocalApiMiddleware.Session(context).ClientId, query))).WithMetadata(ApiContract.ImportedColorHistory);
        app.MapGet(ApiContract.Modules.Path, async (ModuleRuntime runtime, CancellationToken ct) =>
        {
            var snapshot = await runtime.SnapshotAsync(ct);
            return Results.Ok(new ApiList<ModuleSnapshot>(snapshot.Items, null, info.InstanceId + ":" + snapshot.Revision));
        }).WithMetadata(ApiContract.Modules);
        app.MapGet(ApiContract.ModuleSettingsSchema.Path, async (string id, HttpContext context,
            ModuleRuntime runtime, CancellationToken ct) =>
        {
            var snapshot = await runtime.SnapshotAsync(ct);
            var module = snapshot.Items.FirstOrDefault(x => x.Id == id);
            if (module is null) await ApiProblems.Write(context, ApiErrors.ModuleNotFound, "模块不存在");
            else
            {
                context.Response.ContentType = "application/schema+json";
                await context.Response.WriteAsync(module.SettingsSchema, ct);
            }
        }).WithMetadata(ApiContract.ModuleSettingsSchema);
        app.MapGet(ApiContract.Capabilities.Path, (ProviderCapabilityRegistry capabilityRegistry, ICombatObservationSource combatObservationSource, HttpContext context) =>
        {
            var checkedAt = (clock ?? TimeProvider.System).GetUtcNow();
            var inventory = context.RequestServices.GetService<InventoryOperations>()?.Capabilities()
                ?? new InventoryCapabilities(false, [
                    new("inventory.full", CapabilityStatus.Unavailable, null, "storage.unavailable", []),
                    new("inventory.incremental", CapabilityStatus.Unavailable, null, "storage.unavailable", []),
                    new("inventory.catalog", CapabilityStatus.Unavailable, null, "storage.unavailable", [])]);
            CapabilitySnapshot Snapshot(string id, string moduleId, CapabilityStatus status,
                string? providerId, string? providerVersion, string reason) =>
                new(id, moduleId, status, providerId, providerVersion, null, null, reason, checkedAt, []);
            var combat = combatObservationSource.Capabilities(null);
            CapabilitySnapshot Combat(string id, string sourceId)
            {
                var capability = combat.Capabilities.Single(x => x.Id == sourceId);
                return new(id, "combat-observation", capability.Status, capability.Source?.Provider, capability.Source?.ProviderVersion,
                    capability.Source?.GameBuild, null, capability.Reason ?? "available", checkedAt, combat.Simulation ? ["synthetic-observation"] : []);
            }
            CapabilitySnapshot Inventory(InventoryCapability capability) => new(capability.Id, "inventory", capability.Status,
                capability.Source?.Provider, capability.Source?.ProviderVersion, capability.Source?.GameBuild, null,
                capability.Reason ?? "available", checkedAt, inventory.Simulation ? ["synthetic-observation"] : []);
            return Results.Ok(new ApiList<CapabilitySnapshot>(
                [Snapshot("system.inspect", "core", CapabilityStatus.Available, "host", info.Version, "available"),
                 .. capabilityRegistry.Snapshot(),
                 Snapshot("performance.play", "performance", CapabilityStatus.Unavailable, null, null, "module.notImplemented"),
                 Snapshot("rooms.coordinate", "rooms", CapabilityStatus.Unavailable, null, null, "module.notImplemented"),
                 Snapshot("color.search", "color", CapabilityStatus.Unavailable, null, null,
                     colorFactory is null ? "provider.unconfigured" : "game.validationRequired"),
                 Combat("combat.local-dps", "combat.damage.local"),
                 Combat("combat.self-effects", "combat.effects.self"),
                 Combat("combat.monster-effects", "combat.effects.monster"),
                 .. inventory.Capabilities.Select(Inventory)],
                null, info.InstanceId + ":0"));
        }).WithMetadata(ApiContract.Capabilities);
        app.MapGet(ApiContract.ReplayCatalog.Path, () => Results.Ok(new ReplayCatalog(mock, mock ? "mock" : "live",
            ReplayProvider.Source, mock ? ReplayProvider.Scenarios : []))).WithMetadata(ApiContract.ReplayCatalog);
        app.MapGet(ApiContract.MusicInstruments.Path, async (HttpContext context, ModuleRuntime runtime) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "music-query", context.RequestAborted);
            return Results.Ok(new ApiList<InstrumentDefinition>(MusicLibraryEditor.InstrumentCatalog, null, "music-instruments-1"));
        }).WithMetadata(ApiContract.MusicInstruments);
        app.MapPost(ApiContract.QueryOwnedMusicInstruments.Path, async (MusicOwnedInstrumentQuery request, HttpContext context, ModuleRuntime runtime) =>
        {
            await using var demand = await runtime.AcquireManyAsync(["music-library", "inventory"], "music-owned-query", context.RequestAborted);
            var controls = context.RequestServices.GetRequiredService<GameControlContext>();
            var instruments = context.RequestServices.GetRequiredService<OwnedInstrumentProvider>();
            return Results.Ok(await instruments.QueryAsync(request, () => controls.Bindings.Recent?.Session, context.RequestAborted));
        }).WithMetadata(ApiContract.QueryOwnedMusicInstruments);
        app.MapPost(ApiContract.QueryOwnedMusicScores.Path, async (MusicOwnedScoreQuery request, HttpContext context, ModuleRuntime runtime) =>
        {
            await using var demand = await runtime.AcquireManyAsync(["music-library", "inventory"], "music-owned-score-query", context.RequestAborted);
            var controls = context.RequestServices.GetRequiredService<GameControlContext>();
            var scores = context.RequestServices.GetRequiredService<OwnedScoreProvider>();
            return Results.Ok(await scores.QueryAsync(request, () => controls.Bindings.Recent?.Session, context.RequestAborted));
        }).WithMetadata(ApiContract.QueryOwnedMusicScores);
        app.MapGet(ApiContract.MusicLibrary.Path, async (HttpContext context, ModuleRuntime runtime) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "music-query", context.RequestAborted);
            var info = MusicService(context).Info();
            context.Response.Headers.ETag = MusicLibraryStore.ETag(info.Revision);
            return Results.Ok(info);
        }).WithMetadata(ApiContract.MusicLibrary);
        app.MapPost(ApiContract.QueryMusicLibrary.Path, async (MusicQuery request, HttpContext context, ModuleRuntime runtime) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "music-query", context.RequestAborted);
            return Results.Ok(MusicService(context).Query(request));
        }).WithMetadata(ApiContract.QueryMusicLibrary);
        app.MapPost(ApiContract.EditMusicLibrary.Path, async (MusicEditBatch request, HttpContext context, ModuleRuntime runtime) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "music-edit", context.RequestAborted);
            var result = await MusicService(context).EditAsync(LocalApiMiddleware.Session(context).ClientId, request,
                SingleHeader(context, "Idempotency-Key"), SingleHeader(context, "If-Match"), context.RequestAborted);
            context.Response.Headers.ETag = MusicLibraryStore.ETag(result.Revision);
            return Results.Ok(result);
        }).WithMetadata(ApiContract.EditMusicLibrary);
        app.MapGet(ApiContract.MusicPlaybackHistory.Path, async (HttpContext context,
            ModuleRuntime runtime, [FromServices] PlaybackHistoryStore store) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "music-history-query", context.RequestAborted);
            var items = store.Recent(LocalApiMiddleware.Session(context).ClientId);
            return Results.Ok(new ApiList<MusicPlaybackHistoryItem>(items, null, info.InstanceId + ":music-history"));
        }).WithMetadata(ApiContract.MusicPlaybackHistory);
        app.MapPost(ApiContract.ImportMusicPlaybackHistory.Path, async (MusicPlaybackHistoryInput request,
            HttpContext context, ModuleRuntime runtime, [FromServices] PlaybackHistoryStore store) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "music-history-write", context.RequestAborted);
            var result = await store.ImportAsync(LocalApiMiddleware.Session(context).ClientId, request,
                SingleHeader(context, "Idempotency-Key"), context.RequestAborted);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(ApiContract.ImportMusicPlaybackHistory);
        app.MapPost(ApiContract.MusicTimeline.Path, async (MusicTimelineQuery request, HttpContext context,
            ModuleRuntime runtime, [FromServices] ScoreTimelineStore store) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "music-timeline-query", context.RequestAborted);
            var timeline = store.Resolve(LocalApiMiddleware.Session(context).ClientId, request.ScoreId, request.PartId);
            return Results.Ok(timeline);
        }).WithMetadata(ApiContract.MusicTimeline);
        app.MapPost(ApiContract.PreviewMusicImport.Path, async (MusicImportRequest request, HttpContext context, ModuleRuntime runtime,
            [FromServices] MusicImportStore store) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "music-import-preview", context.RequestAborted);
            var preview = await store.PreviewAsync(LocalApiMiddleware.Session(context).ClientId, request, SingleHeader(context, "Idempotency-Key"), context.RequestAborted);
            return Results.Json(preview, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(ApiContract.PreviewMusicImport);
        app.MapGet(ApiContract.MusicImport.Path, (string id, HttpContext context, [FromServices] MusicImportStore store) =>
            Results.Ok(store.Get(LocalApiMiddleware.Session(context).ClientId, id))).WithMetadata(ApiContract.MusicImport);
        app.MapPost(ApiContract.CommitMusicImport.Path, async (string id, HttpContext context, [FromServices] MusicImportOperations operations) =>
        {
            var operation = await operations.SubmitAsync(LocalApiMiddleware.Session(context).ClientId, id,
                SingleHeader(context, "Idempotency-Key"), SingleHeader(context, "If-Match"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(ApiContract.CommitMusicImport);
        app.MapGet(ApiContract.MusicPreferences.Path, (HttpContext context, [FromServices] MusicImportStore store) =>
            Results.Ok(store.Preferences(LocalApiMiddleware.Session(context).ClientId))).WithMetadata(ApiContract.MusicPreferences);
        app.MapGet(ApiContract.RoomSession.Path, (HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(rooms.Get(LocalApiMiddleware.Session(context).ClientId))).WithMetadata(ApiContract.RoomSession);
        app.MapPost(ApiContract.CreateRoom.Path, async (RoomCreateRequest request, HttpContext context, RoomSessionManager rooms) =>
        {
            var result = await rooms.CreateAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(ApiContract.CreateRoom);
        app.MapPost(ApiContract.JoinRoom.Path, async (RoomJoinRequest request, HttpContext context, RoomSessionManager rooms) =>
        {
            var result = await rooms.JoinAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(ApiContract.JoinRoom);
        app.MapPost(ApiContract.ReconnectRoom.Path, async (HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.ReconnectAsync(LocalApiMiddleware.Session(context).ClientId, context.RequestAborted)))
            .WithMetadata(ApiContract.ReconnectRoom);
        app.MapPost(ApiContract.LeaveRoom.Path, async (RoomLeaveRequest request, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.LeaveAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted)))
            .WithMetadata(ApiContract.LeaveRoom);
        app.MapGet(ApiContract.RoomInvitation.Path, (HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(rooms.GetInvitation(LocalApiMiddleware.Session(context).ClientId))).WithMetadata(ApiContract.RoomInvitation);
        app.MapPost(ApiContract.CreateRoomInvitation.Path, (HttpContext context, RoomSessionManager rooms) =>
            Results.Json(rooms.CreateInvitation(LocalApiMiddleware.Session(context).ClientId), statusCode: StatusCodes.Status201Created))
            .WithMetadata(ApiContract.CreateRoomInvitation);
        app.MapPost(ApiContract.EditRoomRoster.Path, async (RoomRosterEditRequest request, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.EditRosterAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted)))
            .WithMetadata(ApiContract.EditRoomRoster);
        app.MapPut(ApiContract.SetRoomMode.Path, async (RoomModeRequest request, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.SetModeAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted)))
            .WithMetadata(ApiContract.SetRoomMode);
        app.MapPut(ApiContract.SetRoomReadiness.Path, async (RoomReadinessRequest request, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.SetReadinessAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted)))
            .WithMetadata(ApiContract.SetRoomReadiness);
        app.MapPut(ApiContract.BindRoomCharacter.Path, async (RoomCharacterBindingRequest request, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.BindCharacterAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted)))
            .WithMetadata(ApiContract.BindRoomCharacter);
        app.MapPut(ApiContract.SelectRoomLeader.Path, async (RoomLeaderRequest request, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.SelectLeaderAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted)))
            .WithMetadata(ApiContract.SelectRoomLeader);
        app.MapPost(ApiContract.KickRoomMember.Path, async (RoomMemberKickRequest request, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.KickMemberAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted)))
            .WithMetadata(ApiContract.KickRoomMember);
        app.MapPut(ApiContract.SetRoomGameOperation.Path, async (RoomGameOperationRequest request, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.SetGameOperationAsync(LocalApiMiddleware.Session(context).ClientId, request, context.RequestAborted)))
            .WithMetadata(ApiContract.SetRoomGameOperation);
        app.MapPost(ApiContract.StopRoomLocally.Path, async (HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.StopLocallyAsync(LocalApiMiddleware.Session(context).ClientId, context.RequestAborted)))
            .WithMetadata(ApiContract.StopRoomLocally);
        app.MapPost(ApiContract.StartReplay.Path, async (ReplayRequest request, HttpContext context) =>
        {
            var service = context.RequestServices.GetService<ReplayOperations>()
                ?? throw new OperationException("capability.unavailable", "请启动独立模拟后端以运行回放");
            var result = await service.SubmitAsync(LocalApiMiddleware.Session(context).ClientId, request,
                SingleHeader(context, "Idempotency-Key"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + result.Id, result);
        }).WithMetadata(ApiContract.StartReplay);
        app.MapGet(ApiContract.Health.Path, () => new HealthInfo("healthy", info.InstanceId)).WithMetadata(ApiContract.Health);
        app.MapGet(ApiContract.Ready.Path, () => new HealthInfo("ready", info.InstanceId)).WithMetadata(ApiContract.Ready);
        // 退出宿主只接受所有者会话（本机桌面前端经命名管道取得的凭据）；回应写完后再停止，前端能收到受理结果。
        app.MapPost(ApiContract.ShutdownHost.Path, (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            if (!LocalApiMiddleware.Session(context).Owner)
                throw new LocalApiException("permission.denied", "只有本机桌面前端可以退出后端");
            context.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Accepted(value: new HealthInfo("stopping", info.InstanceId));
        }).WithMetadata(ApiContract.ShutdownHost);
        var openApi = new Lazy<string>(static () => OpenApiDocument.Create().ToJsonString());
        app.MapGet(ApiContract.OpenApi.Path, () => Results.Text(openApi.Value, "application/json")).WithMetadata(ApiContract.OpenApi);
        app.MapGet(ApiContract.AuthSession.Path, (HttpContext context) => LocalApiMiddleware.Session(context)).WithMetadata(ApiContract.AuthSession);
        app.MapGet(ApiContract.AuthClients.Path, (LocalAuthService auth) => auth.Snapshot()).WithMetadata(ApiContract.AuthClients);
        app.MapPost(ApiContract.PairingChallenges.Path, (PairingChallengeRequest request, LocalAuthService auth) => auth.CreateChallenge(request)).WithMetadata(ApiContract.PairingChallenges);
        app.MapPost(ApiContract.Pairings.Path, (PairingRequest request, HttpContext context, LocalAuthService auth) =>
            auth.Redeem(request, context.Request.Headers.Origin.Count == 0 ? null : context.Request.Headers.Origin.ToString())).WithMetadata(ApiContract.Pairings);
        app.MapDelete(ApiContract.RevokeClient.Path, (string id, LocalAuthService auth) => auth.Revoke(id)).WithMetadata(ApiContract.RevokeClient);
        app.MapGet(ApiContract.ModuleConfiguration.Path, async (string id, HttpContext context) =>
        {
            var value = await ConfigurationService(context).ConfigurationAsync(id, context.RequestAborted);
            context.Response.Headers.ETag = OperationStore.ConfigurationETag(value);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.ModuleConfiguration);
        app.MapPost(ApiContract.ConfigureModule.Path, async (string id, ModuleConfigurationRequest request, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = await ConfigurationService(context).SubmitAsync(session.ClientId, id, request,
                SingleHeader(context, "Idempotency-Key"), SingleHeader(context, "If-Match"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + value.Id, value);
        }).WithMetadata(ApiContract.ConfigureModule);
        app.MapGet(ApiContract.Operation.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = OperationService(context).Get(id, session.ClientId, session.Owner);
            context.Response.Headers.ETag = OperationStore.OperationETag(value);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.Operation);
        app.MapPost(ApiContract.CaptureColorScreen.Path, async (HttpContext context, ColorCaptureService service) =>
            Results.Ok(await service.CaptureAsync(LocalApiMiddleware.Session(context), context.RequestAborted))).WithMetadata(ApiContract.CaptureColorScreen);
        app.MapPost(ApiContract.PickColorPixel.Path, (string id, ColorPixelQuery request, HttpContext context, ColorCaptureService service) =>
            Results.Ok(service.Pixel(id, request, LocalApiMiddleware.Session(context)))).WithMetadata(ApiContract.PickColorPixel);
        app.MapPost(ApiContract.ReadColorCapture.Path, (string id, ColorAssetRangeRequest request, HttpContext context, ColorCaptureService service) =>
            Results.Ok(service.Content(id, request, LocalApiMiddleware.Session(context)))).WithMetadata(ApiContract.ReadColorCapture);
        app.MapDelete(ApiContract.ReleaseColorCapture.Path, (string id, HttpContext context, ColorCaptureService service) =>
            Results.Ok(service.Release(id, LocalApiMiddleware.Session(context)))).WithMetadata(ApiContract.ReleaseColorCapture);
        app.MapPost(ApiContract.PreviewColor.Path, async (ColorPreviewRequest request, HttpContext context) =>
            Results.Ok(await (context.RequestServices.GetService<ColorPreviewService>() ?? throw new OperationException("capability.unavailable", "当前宿主未配置颜色评价"))
                .EvaluateAsync(request, context.RequestAborted))).WithMetadata(ApiContract.PreviewColor);
        app.MapPost(ApiContract.RenderColorPalette.Path, async (ColorPaletteRequest request, HttpContext context) =>
            Results.Ok(await (context.RequestServices.GetService<ColorPaletteService>() ?? throw new OperationException("capability.unavailable", "当前宿主未配置目标色集合图"))
                .RenderAsync(request, context.RequestAborted))).WithMetadata(ApiContract.RenderColorPalette);
        app.MapPost(ApiContract.QueryColorHistory.Path, (ColorHistoryQuery request, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(OperationService(context).ColorHistory(session.ClientId, session.Owner, request));
        }).WithMetadata(ApiContract.QueryColorHistory);
        app.MapGet(ApiContract.ColorHistory.Path, (string id, HttpContext context) =>
            Results.Ok(ColorHistoryService(context).Get(id, LocalApiMiddleware.Session(context)))).WithMetadata(ApiContract.ColorHistory);
        app.MapPost(ApiContract.ExportColorHistory.Path, async (string id, HttpContext context) =>
            Results.Ok(await ColorHistoryService(context).ExportAsync(id, LocalApiMiddleware.Session(context), context.RequestAborted))).WithMetadata(ApiContract.ExportColorHistory);
        app.MapGet(ApiContract.ColorAsset.Path, (string id, HttpContext context) =>
            Results.Ok(ColorHistoryService(context).Asset(id, LocalApiMiddleware.Session(context)))).WithMetadata(ApiContract.ColorAsset);
        app.MapPost(ApiContract.ColorAssetContent.Path, async (string id, ColorAssetRangeRequest request, HttpContext context) =>
            Results.Ok(await ColorHistoryService(context).ContentAsync(id, request, LocalApiMiddleware.Session(context), context.RequestAborted))).WithMetadata(ApiContract.ColorAssetContent);
        app.MapGet(ApiContract.ColorLibrary.Path, (HttpContext context) =>
        {
            var value = ColorLibraryService(context).Read();
            context.Response.Headers.ETag = ColorLibraryStore.ETag(value.Revision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.ColorLibrary);
        app.MapGet(ApiContract.ColorProfile.Path, (string id, HttpContext context) =>
            Results.Ok(ColorLibraryService(context).Get(id))).WithMetadata(ApiContract.ColorProfile);
        app.MapPost(ApiContract.EditColorLibrary.Path, async (ColorLibraryEdit request, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = await ColorLibraryService(context).EditAsync(session.ClientId, request,
                SingleHeader(context, "Idempotency-Key"), SingleHeader(context, "If-Match"), context.RequestAborted);
            context.Response.Headers.ETag = ColorLibraryStore.ETag(value.Revision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.EditColorLibrary);
        app.MapPost(ApiContract.StartColorRun.Path, async (ColorRunStartRequest request, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var operation = await ColorService(context).SubmitAsync(session.ClientId, request,
                SingleHeader(context, "Idempotency-Key"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(ApiContract.StartColorRun);
        app.MapGet(ApiContract.ColorRun.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var run = ColorService(context).Get(id, session.ClientId, session.Owner);
            context.Response.Headers.ETag = $"\"color-{run.Id}-{run.Revision}\"";
            return Results.Ok(run);
        }).WithMetadata(ApiContract.ColorRun);
        app.MapPost(ApiContract.ColorPresence.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(ColorService(context).Presence(id, session.ClientId, session.Owner));
        }).WithMetadata(ApiContract.ColorPresence);
        app.MapGet(ApiContract.ColorRuntime.Path, (HttpContext context) =>
            Results.Ok(context.RequestServices.GetService<ColorWorkerPool>()?.Snapshot() ??
                new ColorEngineRuntime("unconfigured", 0, 60, 1, new(0), null))).WithMetadata(ApiContract.ColorRuntime);
        app.MapPost(ApiContract.SelectColorCandidate.Path, async (string id, ColorCandidateSelectionRequest request, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(await ColorService(context).SelectAsync(id, session.ClientId, session.Owner, request, context.RequestAborted));
        }).WithMetadata(ApiContract.SelectColorCandidate);
        app.MapPost(ApiContract.RetryColorRun.Path, async (string id, ColorRetryRequest request, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(await ColorService(context).RetryAsync(id, session.ClientId, session.Owner, request, context.RequestAborted));
        }).WithMetadata(ApiContract.RetryColorRun);
        app.MapPost(ApiContract.CancelOperation.Path, async (string id, CancelOperationRequest request, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            if (OperationStore.IsRoomScoreDraftOperation(OperationService(context).Get(id, session.ClientId, session.Owner).Kind))
                return Results.Ok(await RequiredService<RoomScoreDraftOperations>(context).CancelAsync(id, session, request.Reason, context.RequestAborted));
            if (OperationStore.IsRoomRoundOperation(OperationService(context).Get(id, session.ClientId, session.Owner).Kind))
                return Results.Ok(await RequiredService<RoomRoundOperations>(context).CancelAsync(id, session, request.Reason, context.RequestAborted));
            if (OperationStore.IsRoomServerOperation(OperationService(context).Get(id, session.ClientId, session.Owner).Kind))
                return Results.Ok(await RequiredService<RoomServerOperations>(context).CancelAsync(id, session, request.Reason, context.RequestAborted));
            if (OperationService(context).Get(id, session.ClientId, session.Owner).Kind == "storage.backup")
                return Results.Ok(await RequiredService<StorageBackupOperations>(context).CancelAsync(id, session, request.Reason, context.RequestAborted));
            if (OperationService(context).Get(id, session.ClientId, session.Owner).Kind is "storage.restore" or "storage.migrate")
                return Results.Ok(await RequiredService<StorageMaintenanceOperations>(context).CancelAsync(id, session, request.Reason, context.RequestAborted));
            if (OperationService(context).Get(id, session.ClientId, session.Owner).Kind == "storage.moduleDelete")
                return Results.Ok(await RequiredService<StorageModuleDeletionOperations>(context).CancelAsync(id, session, request.Reason, context.RequestAborted));
            if (OperationService(context).Get(id, session.ClientId, session.Owner).Kind == "files.export")
                return Results.Ok(await RequiredService<FileExportOperations>(context).CancelAsync(id, session, request.Reason, context.RequestAborted));
            if (OperationService(context).Get(id, session.ClientId, session.Owner).Kind == "diagnostics.export")
                return Results.Ok(await RequiredService<DiagnosticExportOperations>(context).CancelAsync(id, session.ClientId, session.Owner, request.Reason, context.RequestAborted));
            if (OperationService(context).Get(id, session.ClientId, session.Owner).Kind == "combat.observe")
                return Results.Ok(await RequiredService<CombatOperations>(context).CancelAsync(id, session.ClientId, session.Owner, request.Reason, context.RequestAborted));
            if (OperationService(context).Get(id, session.ClientId, session.Owner).Kind.StartsWith("inventory.", StringComparison.Ordinal))
                return Results.Ok(await RequiredService<InventoryOperations>(context).CancelAsync(id, session, request.Reason, context.RequestAborted));
            return Results.Ok(await (context.RequestServices.GetService<ColorRunOperations>() is { } color
                ? color.CancelAsync(id, session.ClientId, session.Owner, request.Reason, context.RequestAborted)
                : OperationService(context).CancelAsync(id, session.ClientId, session.Owner, request.Reason, context.RequestAborted)));
        }).WithMetadata(ApiContract.CancelOperation);
        app.MapGet(ApiContract.EventSnapshot.Path, (Func<HttpContext, Task<IResult>>)(async context =>
            Results.Ok(await EventStreamEndpoints.SnapshotAsync(context)))).WithMetadata(ApiContract.EventSnapshot);
        app.MapGet(ApiContract.Events.Path, EventStreamEndpoints.StreamAsync).WithMetadata(ApiContract.Events);
        app.MapFallback((HttpContext context) => ApiProblems.Write(context, ApiErrors.ResourceNotFound, "资源不存在"));
        return app;
    }

    private static T RequiredService<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置持久存储");
    private static void MapSettingsImports(WebApplication app, string project, EndpointDefinition preview, EndpointDefinition query, EndpointDefinition commit)
    {
        app.MapPost(preview.Path, async (SettingsImportRequest request, HttpContext context) =>
        {
            var value = await RequiredService<SettingsImportCoordinator>(context).PreviewAsync(project, LocalApiMiddleware.Session(context).ClientId,
                request, SingleHeader(context, "Idempotency-Key"), context.RequestAborted);
            return Results.Json(value, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(preview);
        app.MapGet(query.Path, (string id, HttpContext context) => Results.Ok(
            RequiredService<SettingsImportCoordinator>(context).Get(project, LocalApiMiddleware.Session(context).ClientId, id))).WithMetadata(query);
        app.MapPost(commit.Path, async (string id, HttpContext context) =>
        {
            var operation = await RequiredService<SettingsImportOperations>(context).SubmitAsync(project, LocalApiMiddleware.Session(context).ClientId,
                id, SingleHeader(context, "Idempotency-Key"), SingleHeader(context, "If-Match"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(commit);
    }
    private static ModuleConfigurationOperations ConfigurationService(HttpContext context) => context.RequestServices.GetService<ModuleConfigurationOperations>()
        ?? throw new OperationException("storage.unavailable", "当前嵌入式宿主未配置持久存储");
    private static MusicLibraryStore MusicService(HttpContext context) => context.RequestServices.GetService<MusicLibraryStore>()
        ?? throw new OperationException("storage.unavailable", "当前嵌入式宿主未配置持久存储");
    private static OperationStore OperationService(HttpContext context) => context.RequestServices.GetService<OperationStore>()
        ?? throw new OperationException("storage.unavailable", "当前嵌入式宿主未配置持久存储");
    private static ColorHistoryService ColorHistoryService(HttpContext context) => context.RequestServices.GetService<ColorHistoryService>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置持久存储");
    private static ColorLibraryStore ColorLibraryService(HttpContext context) => context.RequestServices.GetService<ColorLibraryStore>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置持久存储");
    private static ColorRunOperations ColorService(HttpContext context) => context.RequestServices.GetService<ColorRunOperations>()
        ?? throw new OperationException("storage.unavailable", "当前嵌入式宿主未配置持久存储");
    private static string? SingleHeader(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1) throw new OperationException("request.invalid", "请求头不能重复：" + name);
        return values.Count == 0 ? null : values[0];
    }
    private sealed class PersistenceLifetime(SqliteStore store, AssetStore assets, UpdatePolicyStore updatePolicy) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await store.RecoverOperationsAsync(cancellationToken);
            await store.TemporaryFiles.RecoverAsync(cancellationToken);
            await assets.RecoverAsync(cancellationToken);
            await updatePolicy.ScheduleStartupAsync(cancellationToken);
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StateOutboxPublisher(SqliteStore store, StateEventHub hub) : BackgroundService, IStateEventSink
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => store.RunOutboxAsync(this, stoppingToken);
        public Task PublishAsync(PersistedEvent state, CancellationToken cancellationToken) => hub.PublishAsync(state, cancellationToken);
    }

    private sealed class IdleCollector(ModuleRuntime runtime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    await runtime.CollectIdleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
