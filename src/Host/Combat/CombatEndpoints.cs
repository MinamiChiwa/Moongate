using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Application;

namespace MabiTool.Host.Combat;

public static class CombatEndpoints
{
    private static readonly string[] Topics = ["combat.summary.changed", "combat.effects.changed", "combat.encounter.closed", "combat.observation.degraded"];
    private static T Required<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置战斗持久存储");
    private static string? Header(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1) throw new OperationException("request.invalid", "请求头不能重复：" + name);
        return values.Count == 0 ? null : values[0];
    }
    /// <summary>名称解析器；宿主未配置时返回按 ID 给出 unavailable 的解析器，返回内容同样不把 ID 当名称。</summary>
    private static ICombatNameResolver Names(HttpContext context) => context.RequestServices.GetService<CombatNameCatalog>() ?? (ICombatNameResolver)NoNames.Instance;
    private sealed class NoNames : ICombatNameResolver
    {
        public static readonly NoNames Instance = new();
        public CombatResolvedName Resolve(string kind, uint sheetId) => new(kind, new(sheetId), null, "unavailable", "combat.nameCatalogUnavailable");
    }
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.CombatCapabilities.Path, (HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(context.RequestServices.GetService<CombatOperations>()?.Capabilities(session.ClientId, session.Owner)
                ?? Required<ICombatObservationSource>(context).Capabilities(null));
        }).WithMetadata(ApiContract.CombatCapabilities);
        app.MapPost(ApiContract.StartCombatObservation.Path, async (CombatObservationRequest request, HttpContext context, CancellationToken ct) =>
        {
            var value = await Required<CombatOperations>(context).SubmitAsync(LocalApiMiddleware.Session(context).ClientId, request, Header(context, "Idempotency-Key"), ct);
            context.Response.Headers.ETag = OperationStore.OperationETag(value); return Results.Accepted(ApiContract.Operation.Path.Replace("{id}", value.Id), value);
        }).WithMetadata(ApiContract.StartCombatObservation);
        app.MapGet(ApiContract.CombatObservation.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context); var value = Required<OperationStore>(context).Get(id, session.ClientId, session.Owner);
            if (value.Kind != "combat.observe") throw new OperationException("resource.notFound", "战斗观察不存在");
            context.Response.Headers.ETag = OperationStore.OperationETag(value); return Results.Ok(value);
        }).WithMetadata(ApiContract.CombatObservation);
        app.MapPost(ApiContract.StopCombatObservation.Path, async (string id, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(await Required<CombatOperations>(context).CancelAsync(id, session.ClientId, session.Owner, "observation-stopped", ct));
        }).WithMetadata(ApiContract.StopCombatObservation);
        app.MapGet(ApiContract.CombatSnapshot.Path, async (HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context); var store = Required<CombatHistoryStore>(context);
            var result = await Required<StateEventHub>(context).CaptureAsync(session, EventFilter.Create(Topics), (epoch, cursor) =>
            {
                var value = store.Workspace(session.ClientId, session.Owner);
                return new CombatSnapshot(epoch, cursor, value?.Revision ?? new(0), value is null ? null : CombatNameProjection.Project(value, Names(context)),
                    store.Storage(session.ClientId, session.Owner));
            }, ct);
            if (result.Data is { } data) context.Response.Headers.ETag = CombatHistoryStore.WorkspaceETag(data);
            return Results.Ok(result);
        }).WithMetadata(ApiContract.CombatSnapshot);
        app.MapGet(ApiContract.CombatEncounters.Path, (HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(CombatNameProjection.Project(Required<CombatHistoryStore>(context).Query(session.ClientId, session.Owner, new()), Names(context)));
        }).WithMetadata(ApiContract.CombatEncounters);
        app.MapPost(ApiContract.QueryCombatEncounters.Path, (CombatEncounterQuery query, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(CombatNameProjection.Project(Required<CombatHistoryStore>(context).Query(session.ClientId, session.Owner, query), Names(context)));
        }).WithMetadata(ApiContract.QueryCombatEncounters);
        app.MapGet(ApiContract.CombatEncounter.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context); var value = Required<CombatHistoryStore>(context).Encounter(id, session.ClientId, session.Owner);
            context.Response.Headers.ETag = CombatHistoryStore.ETag(value); return Results.Ok(CombatNameProjection.Project(value, Names(context)));
        }).WithMetadata(ApiContract.CombatEncounter);
        app.MapPost(ApiContract.StartCombatEncounter.Path, async (CombatBoundaryRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context); var value = await Required<CombatOperations>(context).BoundaryAsync(session.ClientId, session.Owner, "start", request.ObservationId,
                request, Header(context, "Idempotency-Key"), Header(context, "If-Match"), ct);
            context.Response.Headers.ETag = CombatHistoryStore.ETag(value); return Results.Ok(CombatNameProjection.Project(value, Names(context)));
        }).WithMetadata(ApiContract.StartCombatEncounter);
        MapBoundary(app, ApiContract.CloseCombatEncounter, "close"); MapBoundary(app, ApiContract.ResetCombatEncounter, "reset");
        MapBoundary(app, ApiContract.SplitCombatEncounter, "split");
        app.MapPost(ApiContract.FavoriteCombatEncounter.Path, async (string id, CombatFavoriteRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context); var value = await Required<CombatHistoryStore>(context).FavoriteAsync(session.ClientId, session.Owner, id, request,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), ct);
            context.Response.Headers.ETag = CombatHistoryStore.ETag(value); return Results.Ok(CombatNameProjection.Project(value, Names(context)));
        }).WithMetadata(ApiContract.FavoriteCombatEncounter);
        app.MapGet(ApiContract.CombatEntityEffects.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context); var value = Required<CombatHistoryStore>(context).EntityEffects(id, session.ClientId, session.Owner);
            context.Response.Headers.ETag = "\"combat-effects-" + id + "-" + value.Revision + "\""; return Results.Ok(CombatNameProjection.Project(value, Names(context)));
        }).WithMetadata(ApiContract.CombatEntityEffects);
        app.MapGet(ApiContract.CombatClientBuffs.Path, (HttpContext context) =>
        {
            var catalog = context.RequestServices.GetService<CombatNameCatalog>()?.ClientBuffs()
                ?? new CombatClientBuffCatalog(CombatClientBuffCatalogState.Unavailable, "combat.nameCatalogUnavailable", null, []);
            context.Response.Headers.ETag = CombatNameCatalog.ClientBuffsETag(catalog);
            return Results.Ok(catalog);
        }).WithMetadata(ApiContract.CombatClientBuffs);
        app.MapGet(ApiContract.CombatTimeline.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(CombatNameProjection.Project(Required<CombatHistoryStore>(context).Timeline(session.ClientId, session.Owner, id, new()), Names(context)));
        }).WithMetadata(ApiContract.CombatTimeline);
        app.MapPost(ApiContract.QueryCombatTimeline.Path, (string id, CombatTimelineQuery query, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            try { return Results.Ok(CombatNameProjection.Project(Required<CombatHistoryStore>(context).Timeline(session.ClientId, session.Owner, id, query), Names(context))); }
            catch (ArgumentException error) { throw new OperationException("request.invalid", error.Message); }
        }).WithMetadata(ApiContract.QueryCombatTimeline);
        app.MapPost(ApiContract.QueryCombatRange.Path, (CombatRangeQuery query, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(CombatNameProjection.Project(Required<CombatHistoryStore>(context).QueryRange(session.ClientId, session.Owner, query), Names(context)));
        }).WithMetadata(ApiContract.QueryCombatRange);
        app.MapPost(ApiContract.SaveCombatRange.Path, async (CombatRangeSaveRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = await Required<CombatHistoryStore>(context).SaveRangeAsync(session.ClientId, session.Owner, request, Header(context, "Idempotency-Key"), ct);
            context.Response.Headers.ETag = CombatHistoryStore.ETag(value); return Results.Ok(CombatNameProjection.Project(value, Names(context)));
        }).WithMetadata(ApiContract.SaveCombatRange);
        app.MapGet(ApiContract.CombatMeasurement.Path, (HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = Required<CombatOperations>(context).MeasurementState(session.ClientId, session.Owner);
            context.Response.Headers.ETag = CombatHistoryStore.MeasurementSettingsETag(value.SettingsRevision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.CombatMeasurement);
        app.MapPost(ApiContract.StartCombatMeasurement.Path, async (CombatMeasurementStartRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = await Required<CombatOperations>(context).StartMeasurementAsync(session.ClientId, session.Owner, request, Header(context, "Idempotency-Key"), ct);
            context.Response.Headers.ETag = CombatHistoryStore.MeasurementSettingsETag(value.SettingsRevision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.StartCombatMeasurement);
        app.MapPost(ApiContract.CancelCombatMeasurement.Path, async (CombatMeasurementCancelRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = await Required<CombatOperations>(context).CancelMeasurementAsync(session.ClientId, session.Owner, request, Header(context, "Idempotency-Key"), ct);
            context.Response.Headers.ETag = CombatHistoryStore.MeasurementSettingsETag(value.SettingsRevision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.CancelCombatMeasurement);
        app.MapPost(ApiContract.SaveCombatMeasurementSettings.Path, async (CombatMeasurementSettings request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = await Required<CombatOperations>(context).SaveMeasurementSettingsAsync(session.ClientId, session.Owner, request,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), ct);
            context.Response.Headers.ETag = CombatHistoryStore.MeasurementSettingsETag(value.SettingsRevision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.SaveCombatMeasurementSettings);
        app.MapGet(ApiContract.CombatAutoObservation.Path, (HttpContext context) =>
        {
            var value = Required<CombatOperations>(context).AutoObservation();
            context.Response.Headers.ETag = CombatHistoryStore.AutoObservationETag(value.Revision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.CombatAutoObservation);
        app.MapPost(ApiContract.SetCombatAutoObservation.Path, async (CombatAutoObservationRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var value = await Required<CombatOperations>(context).SetAutoObservationAsync(session.ClientId, request,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), ct);
            context.Response.Headers.ETag = CombatHistoryStore.AutoObservationETag(value.Revision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.SetCombatAutoObservation);
        app.MapPost(ApiContract.CleanupCombatHistory.Path, async (HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            return Results.Ok(await Required<CombatHistoryStore>(context).CleanAsync(session.ClientId, Header(context, "Idempotency-Key"), ct, session.Owner));
        })
            .WithMetadata(ApiContract.CleanupCombatHistory);
        app.MapPost(ApiContract.ReportCombatDisplayLatency.Path, (CombatDisplayLatencyReport report, HttpContext context) =>
            Results.Ok(Required<CombatOperations>(context).ReportDisplayLatency(LocalApiMiddleware.Session(context).ClientId, report)))
            .WithMetadata(ApiContract.ReportCombatDisplayLatency);
    }
    private static void MapBoundary(WebApplication app, EndpointDefinition endpoint, string route) => app.MapPost(endpoint.Path,
        async (string id, CombatBoundaryRequest request, HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context); var value = await Required<CombatOperations>(context).BoundaryAsync(session.ClientId, session.Owner, route, id, request,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), ct);
            context.Response.Headers.ETag = CombatHistoryStore.ETag(value); return Results.Ok(CombatNameProjection.Project(value, Names(context)));
        }).WithMetadata(endpoint);
}
