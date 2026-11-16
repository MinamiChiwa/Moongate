// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Modules.MusicLibrary.Application;

public sealed class OwnedScoreProvider(IInventoryReadModel? inventory, string backendEpoch, bool simulation = false)
{
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    public Task<MusicOwnedScorePage> QueryAsync(MusicOwnedScoreQuery query, GameSession? binding, CancellationToken ct = default)
        => QueryAsync(query, () => binding, ct);

    public async Task<MusicOwnedScorePage> QueryAsync(MusicOwnedScoreQuery query, Func<GameSession?> bindingSource, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (query is null || query.PageSize is < 1 or > 1000 || query.Container is { IsKnown: false })
            throw new OperationException("request.invalid", "持有乐谱查询条件无效");
        var snapshot = inventory is null ? new InventoryReadModelSnapshot(new(0), null, new(new(0), null)) : await inventory.ReadAsync(ct);
        ct.ThrowIfCancellationRequested();
        var binding = bindingSource(); var workspace = snapshot.Workspace;
        if (workspace?.Items.Count > 10000 || workspace?.Coverage.Count > 1000)
            throw new OperationException("resource.capacityExceeded", "库存超过持有乐谱查询预算");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            backendEpoch, snapshot.Revision, WorkspaceId = workspace?.Id, workspace?.Identity,
            workspace?.GameSessionId, workspace?.BindingRevision, workspace?.Generation,
            WorkspaceRevision = workspace?.Revision, workspace?.Current, workspace?.Quality, workspace?.Source, binding,
            Query = query with { Cursor = null }
        }, ContractJson.Options)));
        var offset = query.Cursor is null ? 0 : Decode(query.Cursor, fingerprint);
        var matches = workspace is not null && binding is not null && workspace.GameSessionId == binding.Id &&
            workspace.BindingRevision == binding.BindingRevision && workspace.Source is { } source &&
            source.Region == binding.Region && source.GameBuild == binding.GameBuild;
        var identityValid = workspace?.Identity is { } identity && ValidIdentity(identity.AccountId) && ValidIdentity(identity.CharacterId) &&
            !string.IsNullOrWhiteSpace(identity.RawName) && identity.RawName.Length <= 128 && !identity.RawName.Any(char.IsControl);
        var current = workspace?.Current == true && identityValid && matches && Usable(workspace.Quality);
        var reasons = workspace?.Reasons.ToList() ?? [];
        if (inventory is null) reasons.Add("storage.unavailable");
        else if (workspace is null || workspace.Source is null) reasons.Add("inventory.sourceUnavailable");
        else if (!matches) reasons.Add("inventory.bindingChanged");
        if (workspace is not null && !identityValid) reasons.Add("inventory.identityInvalid");
        if (workspace is not null && !workspace.Current && reasons.Count == 0) reasons.Add("inventory.observationStopped");
        if (workspace is not null && !Usable(workspace.Quality)) reasons.Add("inventory.qualityUnavailable");
        var selectedContainers = query.Container is { } selected ? new[] { selected.KnownValue!.Value } : Enum.GetValues<InventoryContainer>();
        var coverage = new List<InventoryCoverage>();
        foreach (var container in selectedContainers)
        {
            var declarations = (workspace?.Coverage ?? []).Where(c => c.Category == "MusicNoteItem" && c.Container.KnownValue == container).ToArray();
            coverage.Add(declarations.Length == 1 ? declarations[0] : new("MusicNoteItem", container, false,
                declarations.Length == 0 ? "inventory.containerNotDeclared" : "inventory.coverageConflict"));
        }
        if (coverage.Any(c => !c.Complete)) reasons.Add("inventory.coverageIncomplete");
        if (query.Container is null)
            coverage.AddRange((workspace?.Coverage ?? []).Where(c => c.Category == "MusicNoteItem" && !c.Container.IsKnown)
                .Select(c => c with { Complete = false, Reason = "inventory.containerUnknown" }));
        var rows = new List<MusicOwnedScore>();
        var ownershipComplete = true;
        foreach (var view in workspace?.Items ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var item = view.Item;
            if (item.Category != "MusicNoteItem" || item.Quantity?.Value == 0 || query.Container is { } filter && item.Container != filter) continue;
            var itemReasons = new List<string>();
            var data = Field(item.Attributes, "data");
            var id = Unsigned(Field(data, "Id"), "id", itemReasons);
            var sheet = Unsigned(Field(data, "SheetId"), "sheetId", itemReasons);
            var title = Text(Field(data, "Title"), "title", itemReasons, requireTitle: true);
            var composer = Text(Field(data, "Composer"), "composer", itemReasons);
            var composerRealm = Realm(Field(data, "ComposerRealmId"), "composerRealmId", itemReasons);
            var arranger = Text(Field(data, "Arranger"), "arranger", itemReasons);
            var arrangerRealm = Realm(Field(data, "ArrangerRealmId"), "arrangerRealmId", itemReasons);
            var acquired = Signed(Field(data, "AcquiredTime"), "acquiredTime", itemReasons);
            var copying = Flag(Field(data, "IsCopyingAllowed"), "isCopyingAllowed", itemReasons);
            var favorite = Flag(Field(data, "IsFavorite"), "isFavorite", itemReasons);
            var locked = Flag(Field(data, "IsLocked"), "isLocked", itemReasons);
            var junked = Flag(Field(data, "IsJunked"), "isJunked", itemReasons);
            var metadataComplete = itemReasons.Count == 0;
            var identityConflict = id is not null && id != item.InstanceId || sheet is not null && sheet.Value.Value != item.TypeId.Value;
            if (id is not null && id != item.InstanceId) itemReasons.Add("music.scoreMetadata.id.conflict");
            if (sheet is not null && sheet.Value.Value != item.TypeId.Value) itemReasons.Add("music.scoreMetadata.sheetId.conflict");
            if (item.InstanceId is null || item.InstanceId.Value.Value == 0) itemReasons.Add("inventory.instanceIdentityMissing");
            if (item.Quantity is null) itemReasons.Add("inventory.quantityUnknown");
            if (!item.Container.IsKnown) itemReasons.Add("inventory.containerUnknown");
            var sourceMatches = view.Source is null || view.Source == workspace?.Source;
            if (!sourceMatches) itemReasons.Add("inventory.bindingChanged");
            if (!view.Current) itemReasons.Add("inventory.observationStopped");
            if (!Usable(view.Quality)) itemReasons.Add("inventory.qualityUnavailable");
            var itemCurrent = current && view.Current && Usable(view.Quality) && sourceMatches && !identityConflict &&
                item.InstanceId?.Value > 0 && item.Quantity?.Value > 0 && item.Container.IsKnown;
            ownershipComplete &= itemCurrent;
            OpenEnum<ObservationQuality> quality = !matches || workspace?.Current != true ? ObservationQuality.Stale : view.Quality;
            if (query.CurrentOnly && !itemCurrent) continue;
            rows.Add(new(item.Type, item.Container, item.Key, item.InstanceId, item.Quantity, sheet, title, composer, composerRealm,
                arranger, arrangerRealm, acquired, copying, favorite, locked, junked,
                item.Attributes.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement<object?>(null) : item.Attributes,
                metadataComplete && !identityConflict, itemReasons, itemCurrent, quality, view.LastObservedAt, view.Source ?? workspace?.Source));
        }
        var ordered = rows.OrderBy(row => row.Container.RawValue, StringComparer.Ordinal).ThenBy(row => row.InstanceId?.Value)
            .ThenBy(row => row.Key, StringComparer.Ordinal).ThenBy(row => row.Type.TypeId.Value).ToArray();
        if (offset < 0 || offset > ordered.Length) throw Stale();
        var items = ordered.Skip(offset).Take(query.PageSize).ToArray();
        // 完整性核对原始查询范围，不能由 currentOnly 隐去的不确定实例推导完整。
        var complete = current && coverage.All(c => c.Complete) && ownershipComplete;
        var page = new MusicOwnedScorePage(backendEpoch, snapshot.Revision, workspace?.Id, workspace?.Revision, workspace?.GameSessionId,
            workspace?.BindingRevision, workspace?.Generation, workspace?.Identity, workspace?.Source, workspace?.LastObservedAt,
            simulation, current, complete, workspace is null ? ObservationQuality.Unknown : !matches || !workspace.Current ? ObservationQuality.Stale : workspace.Quality,
            coverage, reasons.Distinct().ToArray(), items, offset + items.Length < ordered.Length ? Encode(offset + items.Length, fingerprint) : null);
        ct.ThrowIfCancellationRequested();
        if (JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options).Length > 4 * 1024 * 1024)
            throw new OperationException("resource.capacityExceeded", "持有乐谱页超过 4 MiB");
        return page;
    }

    private static JsonElement Field(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static JsonElement Scalar(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return element;
        if (element.EnumerateObject().Count() != 1) return element;
        if (element.TryGetProperty("rawValue", out var raw)) return raw;
        return element.TryGetProperty("Id", out var id) ? id : element;
    }
    private static string? Number(JsonElement element) => element.ValueKind switch {
        JsonValueKind.String => element.GetString(), JsonValueKind.Number => element.GetRawText(), _ => null };
    private static void MissingOrInvalid(JsonElement element, string field, List<string> reasons)
        => reasons.Add("music.scoreMetadata." + field + (element.ValueKind == JsonValueKind.Undefined ? ".missing" : ".invalid"));
    private static UInt64String? Unsigned(JsonElement element, string name, List<string> reasons)
    {
        if (ulong.TryParse(Number(Scalar(element)), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0) return new(value);
        MissingOrInvalid(element, name, reasons); return null;
    }
    private static string? Signed(JsonElement element, string name, List<string> reasons)
    {
        var raw = Number(Scalar(element));
        if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return raw;
        MissingOrInvalid(element, name, reasons); return null;
    }
    private static int? Realm(JsonElement element, string name, List<string> reasons)
    {
        if (int.TryParse(Number(Scalar(element)), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return value;
        MissingOrInvalid(element, name, reasons); return null;
    }
    private static string? Text(JsonElement element, string name, List<string> reasons, bool requireTitle = false)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString()!;
            if (value.Length > 4096 || value.Any(char.IsControl) || requireTitle && string.IsNullOrWhiteSpace(value)) MissingOrInvalid(element, name, reasons);
            return value;
        }
        MissingOrInvalid(element, name, reasons); return null;
    }
    private static bool? Flag(JsonElement element, string name, List<string> reasons)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        MissingOrInvalid(element, name, reasons); return null;
    }
    private static bool ValidIdentity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static bool Usable(OpenEnum<ObservationQuality> value) => value.KnownValue is ObservationQuality.Confirmed or ObservationQuality.Estimated or ObservationQuality.Partial;
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
    private static OperationException Stale() => new("inventory.planStale", "分页条件或库存来源已变化");
}
