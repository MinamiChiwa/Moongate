using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Modules.MusicLibrary.Application;

public sealed class OwnedInstrumentProvider(IInventoryReadModel? inventory, string backendEpoch, bool simulation = false)
{
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private readonly object catalogGate = new();
    private InventoryCatalogSnapshot? cachedCatalog;
    private Dictionary<InventoryTypeKey, InventoryCatalogEntry> catalogIndex = [];

    public Task<MusicOwnedInstrumentPage> QueryAsync(MusicOwnedInstrumentQuery query, GameSession? binding, CancellationToken ct = default)
        => QueryAsync(query, () => binding, ct);

    public async Task<MusicOwnedInstrumentPage> QueryAsync(MusicOwnedInstrumentQuery query, Func<GameSession?> bindingSource, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (query is null || query.PageSize is < 1 or > 1000 || query.Container is { IsKnown: false })
            throw new OperationException("request.invalid", "持有乐器查询条件无效");
        var snapshot = inventory is null ? new InventoryReadModelSnapshot(new(0), null, new(new(0), null)) : await inventory.ReadAsync(ct);
        var binding = bindingSource(); var workspace = snapshot.Workspace;
        if (workspace?.Items.Count > 10000 || snapshot.Catalog.Data?.Entries.Count > 100000)
            throw new OperationException("resource.capacityExceeded", "库存或目录超过查询预算");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            backendEpoch, snapshot.Revision, CatalogRevision = snapshot.Catalog.Revision, CatalogId = snapshot.Catalog.Data?.Id,
            WorkspaceId = workspace?.Id, workspace?.Identity, workspace?.GameSessionId, workspace?.BindingRevision,
            workspace?.Generation, WorkspaceRevision = workspace?.Revision, workspace?.Current, workspace?.Source, binding,
            Query = query with { Cursor = null }
        }, ContractJson.Options)));
        var offset = query.Cursor is null ? 0 : Decode(query.Cursor, fingerprint);
        var matches = workspace is not null && binding is not null && workspace.GameSessionId == binding.Id &&
            workspace.BindingRevision == binding.BindingRevision && workspace.Source is { } source &&
            source.Region == binding.Region && source.GameBuild == binding.GameBuild;
        var current = workspace?.Current == true && workspace.Identity is not null && matches && Usable(workspace.Quality);
        var reasons = workspace?.Reasons.ToList() ?? [];
        if (inventory is null) reasons.Add("storage.unavailable");
        else if (workspace is null || workspace.Source is null) reasons.Add("inventory.sourceUnavailable");
        else if (!matches) reasons.Add("inventory.bindingChanged");
        else if (!workspace.Current && reasons.Count == 0) reasons.Add("inventory.observationStopped");
        var rows = new List<MusicOwnedInstrument>();
        lock (catalogGate)
        {
            if (!ReferenceEquals(cachedCatalog, snapshot.Catalog))
            {
                var next = new Dictionary<InventoryTypeKey, InventoryCatalogEntry>();
                foreach (var entry in snapshot.Catalog.Data?.Entries ?? [])
                { ct.ThrowIfCancellationRequested(); next.TryAdd(entry.Type, entry); }
                catalogIndex = next; cachedCatalog = snapshot.Catalog;
            }
            foreach (var view in workspace?.Items ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var item = view.Item;
                if (item.Category != "EquipmentTool" || item.Quantity?.Value == 0) continue;
                catalogIndex.TryGetValue(item.Type, out var entry);
                var validEntry = entry?.Table == "EquipmentTool" && entry.Type.Category == "EquipmentTool" && !string.IsNullOrEmpty(snapshot.Catalog.Data?.Region) && snapshot.Catalog.Data.Region == workspace!.Source?.Region;
                if (validEntry && entry!.IsInstrument == false) continue;
                var mapped = validEntry && entry!.IsInstrument == true && entry.InstrumentTypeId?.Value > 0 && entry.EquipmentToolTypeId?.Value > 0 && !string.IsNullOrEmpty(snapshot.Catalog.Data?.Region) && snapshot.Catalog.Data.Region == workspace!.Source?.Region;
                var itemCurrent = current && view.Current && Usable(view.Quality) && (view.Source is null || view.Source.Region == binding!.Region && view.Source.GameBuild == binding.GameBuild) && item.Quantity?.Value > 0;
                if (query.CurrentOnly && !itemCurrent || query.Container is { } container && item.Container != container) continue;
                OpenEnum<ObservationQuality> quality = !matches || workspace?.Current != true ? ObservationQuality.Stale : item.Quantity is null && Usable(view.Quality) ? ObservationQuality.Partial : view.Quality;
                rows.Add(new(item.Type, item.Container, item.Key, item.InstanceId, item.Quantity, item.Equipped, view.Name,
                    mapped ? $"client:{snapshot.Catalog.Data!.Region}:Instrument:{entry!.InstrumentTypeId}" : null,
                    validEntry ? entry?.InstrumentTypeId : null, validEntry ? entry?.EquipmentToolTypeId : null, mapped ? MusicInstrumentMappingState.ClientCatalog : MusicInstrumentMappingState.Unknown,
                    itemCurrent, quality, view.LastObservedAt, view.Source ?? workspace!.Source));
            }
        }
        var ordered = rows.OrderBy(x => x.Container.RawValue, StringComparer.Ordinal).ThenBy(x => x.Type.TypeId.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Type.Category, StringComparer.Ordinal)
            .ThenBy(x => x.Type.Sheet, StringComparer.Ordinal).ToArray();
        if (offset < 0 || offset > ordered.Length) throw Stale();
        var items = ordered.Skip(offset).Take(query.PageSize).ToArray();
        var page = new MusicOwnedInstrumentPage(backendEpoch, snapshot.Revision, snapshot.Catalog.Revision, snapshot.Catalog.Data?.Id,
            workspace?.GameSessionId, workspace?.BindingRevision, workspace?.Generation, workspace?.Identity, workspace?.Source,
            simulation, current, workspace is null ? ObservationQuality.Unknown : !matches || !workspace.Current ? ObservationQuality.Stale : workspace.Quality,
            workspace?.Coverage ?? [], reasons.Distinct().ToArray(), items,
            offset + items.Length < ordered.Length ? Encode(offset + items.Length, fingerprint) : null);
        ct.ThrowIfCancellationRequested();
        if (JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options).Length > 4 * 1024 * 1024)
            throw new OperationException("resource.capacityExceeded", "持有乐器页超过 4 MiB");
        return page;
    }

    private string Encode(int offset, string fingerprint)
    {
        var body = Encoding.UTF8.GetBytes(fingerprint + ":" + offset);
        return Convert.ToBase64String(body) + "." + Convert.ToBase64String(HMACSHA256.HashData(cursorKey, body));
    }
    private int Decode(string cursor, string fingerprint)
    {
        try
        {
            if (cursor.Length > 512) throw Stale();
            var parts = cursor.Split('.'); if (parts.Length != 2) throw Stale();
            var body = Convert.FromBase64String(parts[0]); var signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(cursorKey, body))) throw Stale();
            var text = Encoding.UTF8.GetString(body).Split(':');
            if (text.Length != 2 || text[0] != fingerprint || !int.TryParse(text[1], out var offset)) throw Stale();
            return offset;
        }
        catch (FormatException) { throw Stale(); }
    }
    private static bool Usable(OpenEnum<ObservationQuality> quality) => quality.KnownValue is ObservationQuality.Confirmed or ObservationQuality.Estimated or ObservationQuality.Partial;
    private static OperationException Stale() => new("inventory.planStale", "分页条件或库存来源已变化");
}
