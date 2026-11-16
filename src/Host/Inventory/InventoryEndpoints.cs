using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Inventory.Adapters;

namespace MabiTool.Host.Inventory;

public static class InventoryEndpoints
{
    private static readonly string[] Topics = ["inventory.snapshot.changed", "inventory.catalog.changed", "inventory.observation.degraded"];
    private static T Required<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置库存持久存储");
    private static string? Header(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1) throw new OperationException("request.invalid", "请求头不能重复：" + name);
        return values.Count == 0 ? null : values[0];
    }
    private static string Key(HttpContext context) => Header(context, "Idempotency-Key")
        ?? throw new OperationException("idempotency.required", "库存编辑需要 Idempotency-Key");
    private static string Match(HttpContext context) => Header(context, "If-Match")
        ?? throw new OperationException("revision.required", "库存编辑需要 If-Match");
    private static string Epoch(HttpContext context) => Required<SystemInfo>(context).InstanceId;
    private static IResult Accepted(HttpContext context, OperationSnapshot operation)
    {
        context.Response.Headers.ETag = OperationStore.OperationETag(operation);
        return Results.Accepted(ApiContract.Operation.Path.Replace("{id}", operation.Id), operation);
    }
    public static void Map(WebApplication app)
    {
        app.MapGet(InventoryApi.Capabilities.Path, (HttpContext context) => Results.Ok(Required<InventoryOperations>(context).Capabilities()))
            .WithMetadata(InventoryApi.Capabilities);
        app.MapPost(InventoryApi.StartObservation.Path, async (InventoryObservationRequest request, HttpContext context, CancellationToken ct) =>
            Accepted(context, await Required<InventoryOperations>(context).SubmitObservationAsync(LocalApiMiddleware.Session(context), request, Header(context, "Idempotency-Key"), ct)))
            .WithMetadata(InventoryApi.StartObservation);
        app.MapGet(InventoryApi.Observation.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = Required<OperationStore>(context).Get(id, session.ClientId, session.Owner);
            if (value.Kind != "inventory.observe") throw new OperationException("resource.notFound", "库存观察不存在");
            context.Response.Headers.ETag = OperationStore.OperationETag(value); return Results.Ok(value);
        }).WithMetadata(InventoryApi.Observation);
        app.MapPost(InventoryApi.StopObservation.Path, async (string id, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = Required<OperationStore>(context).Get(id, session.ClientId, session.Owner);
            if (value.Kind != "inventory.observe") throw new OperationException("resource.notFound", "库存观察不存在");
            return Results.Ok(await Required<InventoryOperations>(context).CancelAsync(id, session, "inventory.observationStopped", ct));
        }).WithMetadata(InventoryApi.StopObservation);
        app.MapGet(InventoryApi.Snapshot.Path, async (HttpContext context, CancellationToken ct) =>
        {
            var store = Required<InventoryHistoryStore>(context);
            var value = await Required<StateEventHub>(context).CaptureAsync(LocalApiMiddleware.Session(context), EventFilter.Create(Topics),
                (epoch, cursor) => new InventorySnapshot(epoch, cursor, store.Revision, store.Workspace(), store.CatalogSummary()), ct);
            context.Response.Headers.ETag = InventoryOperations.ETag(value.BackendEpoch, value.ResourceRevision); return Results.Ok(value);
        }).WithMetadata(InventoryApi.Snapshot);
        app.MapGet(InventoryApi.Resources.Path, (HttpContext context) => Results.Ok(Required<InventoryHistoryStore>(context).Resources()))
            .WithMetadata(InventoryApi.Resources);
        app.MapGet(InventoryApi.AccountResources.Path, (string id, HttpContext context) => Results.Ok(Required<InventoryHistoryStore>(context).Resources(id)))
            .WithMetadata(InventoryApi.AccountResources);
        app.MapGet(InventoryApi.Accounts.Path, (HttpContext context) => Results.Ok(Required<InventoryHistoryStore>(context).Accounts()))
            .WithMetadata(InventoryApi.Accounts);
        app.MapPost(InventoryApi.QueryAccounts.Path, (InventoryAccountQuery query, HttpContext context) => Results.Ok(Required<InventoryHistoryStore>(context).Accounts(query)))
            .WithMetadata(InventoryApi.QueryAccounts);
        app.MapPost(InventoryApi.Items.Path, (InventoryItemQuery query, HttpContext context) => Results.Ok(Required<InventoryHistoryStore>(context).Items(query)))
            .WithMetadata(InventoryApi.Items);
        app.MapGet(InventoryApi.Associations.Path, (string id, HttpContext context) =>
        {
            var value = Required<InventoryHistoryStore>(context).Associations(id);
            context.Response.Headers.ETag = InventoryOperations.ETag(Epoch(context), value.Revision); return Results.Ok(value);
        }).WithMetadata(InventoryApi.Associations);
        app.MapPost(InventoryApi.EditAssociations.Path, async (InventoryAssociationEditRequest request, HttpContext context, CancellationToken ct) =>
        {
            var value = await Required<InventoryHistoryStore>(context).EditAssociationsAsync(request, Match(context),
                LocalApiMiddleware.Session(context).ClientId, Key(context), ct);
            context.Response.Headers.ETag = InventoryOperations.ETag(Epoch(context), value.Revision); return Results.Ok(value);
        }).WithMetadata(InventoryApi.EditAssociations);
        app.MapPost(InventoryApi.SetCharacterSlots.Path, async (InventoryCharacterSlotsRequest request, HttpContext context, CancellationToken ct) =>
        {
            var value = await Required<InventoryHistoryStore>(context).SetCharacterSlotsAsync(request, Match(context),
                LocalApiMiddleware.Session(context).ClientId, Key(context), ct);
            context.Response.Headers.ETag = InventoryOperations.ETag(Epoch(context), value.Revision); return Results.Ok(value);
        }).WithMetadata(InventoryApi.SetCharacterSlots);
        app.MapGet(InventoryApi.Catalog.Path, (HttpContext context) =>
        {
            var value = Required<InventoryHistoryStore>(context).CatalogSummary();
            context.Response.Headers.ETag = InventoryOperations.CatalogETag(Epoch(context), value.Revision); return Results.Ok(value);
        }).WithMetadata(InventoryApi.Catalog);
        app.MapPost(InventoryApi.QueryCatalog.Path, (InventoryCatalogQuery query, HttpContext context) => Results.Ok(Required<InventoryHistoryStore>(context).CatalogQuery(query)))
            .WithMetadata(InventoryApi.QueryCatalog);
        app.MapPost(InventoryApi.RefreshCatalog.Path, async (InventoryCatalogRefreshRequest request, HttpContext context, CancellationToken ct) =>
            Accepted(context, await Required<InventoryOperations>(context).SubmitCatalogRefreshAsync(LocalApiMiddleware.Session(context), request,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), ct))).WithMetadata(InventoryApi.RefreshCatalog);
        app.MapPost(InventoryApi.ImportCatalog.Path, async (InventoryCatalogImportRequest request, HttpContext context, CancellationToken ct) =>
            Accepted(context, await Required<InventoryOperations>(context).SubmitCatalogImportAsync(LocalApiMiddleware.Session(context), request,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), ct))).WithMetadata(InventoryApi.ImportCatalog);
        app.MapPost(InventoryApi.PreviewImport.Path, async (InventoryImportRequest request, HttpContext context, CancellationToken ct) =>
        {
            var value = await Required<InventoryOperations>(context).PreviewImportAsync(LocalApiMiddleware.Session(context), request, Header(context, "Idempotency-Key"), ct);
            context.Response.Headers.ETag = InventoryOperations.ETag(Epoch(context), value.Revision);
            return Results.Created(InventoryApi.ImportPreview.Path.Replace("{id}", value.Id), value);
        }).WithMetadata(InventoryApi.PreviewImport);
        app.MapGet(InventoryApi.ImportPreview.Path, (string id, HttpContext context) =>
        {
            var value = Required<InventoryOperations>(context).ImportPreview(id, LocalApiMiddleware.Session(context));
            context.Response.Headers.ETag = InventoryOperations.ETag(Epoch(context), value.Revision); return Results.Ok(value);
        }).WithMetadata(InventoryApi.ImportPreview);
        app.MapPost(InventoryApi.CommitImport.Path, async (string id, InventoryImportCommitRequest request, HttpContext context, CancellationToken ct) =>
            Accepted(context, await Required<InventoryOperations>(context).SubmitImportAsync(id, LocalApiMiddleware.Session(context), request,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), ct))).WithMetadata(InventoryApi.CommitImport);
        app.MapPost(InventoryApi.Export.Path, async (InventoryExportRequest request, HttpContext context, CancellationToken ct) =>
            Accepted(context, await Required<InventoryOperations>(context).SubmitExportAsync(LocalApiMiddleware.Session(context), request, Header(context, "Idempotency-Key"), ct)))
            .WithMetadata(InventoryApi.Export);
    }
}
