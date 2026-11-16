namespace MabiTool.Contracts;

public static class InventoryApi
{
    public static EndpointDefinition Capabilities { get; } = new("/api/v1/inventory/capabilities", "getInventoryCapabilities", typeof(InventoryCapabilities));
    public static EndpointDefinition Snapshot { get; } = new("/api/v1/inventory/snapshot", "getInventorySnapshot", typeof(InventorySnapshot), ETag: true, MaximumResponseBytes: 16 * 1024 * 1024);
    public static EndpointDefinition StartObservation { get; } = new("/api/v1/inventory/observations", "startInventoryObservation", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(InventoryObservationRequest), Permission: ApiPermissions.InventoryControl, SuccessStatus: 202, Idempotent: true);
    public static EndpointDefinition Observation { get; } = new("/api/v1/inventory/observations/{id}", "getInventoryObservation", typeof(OperationSnapshot), ETag: true);
    public static EndpointDefinition StopObservation { get; } = new("/api/v1/inventory/observations/{id}/stop", "stopInventoryObservation", typeof(OperationSnapshot),
        Method: "POST", Permission: ApiPermissions.InventoryControl);
    /// <summary>当前库存身份所属账户的钱包资源（只读）。</summary>
    public static EndpointDefinition Resources { get; } = new("/api/v1/inventory/resources", "getInventoryResources", typeof(InventoryResourcesView));
    /// <summary>指定库存账户的钱包资源（只读）；背包页资源区按账号下拉读取。</summary>
    public static EndpointDefinition AccountResources { get; } = new("/api/v1/inventory/accounts/{id}/resources", "getInventoryAccountResources", typeof(InventoryResourcesView));
    public static EndpointDefinition Accounts { get; } = new("/api/v1/inventory/accounts", "getInventoryAccounts", typeof(InventoryAccountPage));
    public static EndpointDefinition QueryAccounts { get; } = new("/api/v1/inventory/accounts/query", "queryInventoryAccounts", typeof(InventoryAccountPage),
        Method: "POST", RequestType: typeof(InventoryAccountQuery));
    public static EndpointDefinition Items { get; } = new("/api/v1/inventory/items/query", "queryInventoryItems", typeof(InventoryItemPage),
        Method: "POST", RequestType: typeof(InventoryItemQuery));
    public static EndpointDefinition Associations { get; } = new("/api/v1/inventory/accounts/{id}/associations", "getInventoryAssociations", typeof(InventoryAssociationSnapshot), ETag: true);
    public static EndpointDefinition EditAssociations { get; } = new("/api/v1/inventory/associations", "editInventoryAssociations", typeof(InventoryAssociationSnapshot),
        Method: "POST", RequestType: typeof(InventoryAssociationEditRequest), Permission: ApiPermissions.InventoryEdit, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition SetCharacterSlots { get; } = new("/api/v1/inventory/character-slots", "setInventoryCharacterSlots", typeof(InventoryAccountView),
        Method: "POST", RequestType: typeof(InventoryCharacterSlotsRequest), Permission: ApiPermissions.InventoryEdit, Idempotent: true, Conditional: true, ETag: true);
    public static EndpointDefinition Catalog { get; } = new("/api/v1/inventory/catalog", "getInventoryCatalog", typeof(InventoryCatalogSummary), ETag: true);
    public static EndpointDefinition QueryCatalog { get; } = new("/api/v1/inventory/catalog/query", "queryInventoryCatalog", typeof(InventoryCatalogPage),
        Method: "POST", RequestType: typeof(InventoryCatalogQuery));
    public static EndpointDefinition RefreshCatalog { get; } = new("/api/v1/inventory/catalog/refresh", "refreshInventoryCatalog", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(InventoryCatalogRefreshRequest), Permission: ApiPermissions.InventoryEdit, Idempotent: true, Conditional: true, SuccessStatus: 202);
    public static EndpointDefinition ImportCatalog { get; } = new("/api/v1/inventory/catalog/imports", "importInventoryCatalog", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(InventoryCatalogImportRequest), Permission: ApiPermissions.InventoryEdit, Idempotent: true, Conditional: true, SuccessStatus: 202);
    public static EndpointDefinition PreviewImport { get; } = new("/api/v1/inventory/imports", "previewInventoryImport", typeof(InventoryImportPreview),
        Method: "POST", RequestType: typeof(InventoryImportRequest), Permission: ApiPermissions.InventoryEdit, SuccessStatus: 201, Idempotent: true, ETag: true);
    public static EndpointDefinition ImportPreview { get; } = new("/api/v1/inventory/imports/{id}", "getInventoryImport", typeof(InventoryImportPreview), Permission: ApiPermissions.InventoryEdit, ETag: true);
    public static EndpointDefinition CommitImport { get; } = new("/api/v1/inventory/imports/{id}/commit", "commitInventoryImport", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(InventoryImportCommitRequest), Permission: ApiPermissions.InventoryEdit, SuccessStatus: 202, Idempotent: true, Conditional: true);
    public static EndpointDefinition Export { get; } = new("/api/v1/inventory/exports", "exportInventory", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(InventoryExportRequest), Permission: ApiPermissions.InventoryEdit, SuccessStatus: 202, Idempotent: true);

    public static IReadOnlyList<EndpointDefinition> Endpoints { get; } = Array.AsReadOnly<EndpointDefinition>([
        Capabilities, Snapshot, StartObservation, Observation, StopObservation, Resources, AccountResources, Accounts, QueryAccounts, Items,
        Associations, EditAssociations, SetCharacterSlots, Catalog, QueryCatalog, RefreshCatalog, ImportCatalog,
        PreviewImport, ImportPreview, CommitImport, Export
    ]);
}
