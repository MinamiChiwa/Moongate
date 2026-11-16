// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Inventory.Domain;

namespace MabiTool.Modules.Inventory.Adapters;

public sealed partial class InventoryHistoryStore
{
    public async Task<InventoryCatalogSummary> ReplaceCatalogAsync(InventoryCatalogData value, UInt64String? expectedRevision = null, CancellationToken ct = default)
    {
        ValidateCatalog(value); await mutation.WaitAsync(ct);
        try
        {
            var previous = Catalog(); if (expectedRevision is { } expected && previous.Revision != expected) throw new OperationException("inventory.planStale", "名称目录已更新");
            var next = new InventoryCatalogSnapshot(Next(previous.Revision), value);
            var json = JsonSerializer.Serialize(next, ContractJson.Options);
            if (Encoding.UTF8.GetByteCount(json) > 32 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "名称目录超过 32 MiB");
            var eventData=JsonSerializer.SerializeToElement(new InventoryCatalogChanged(next.Revision, value.Id, value.Entries.Count, value.Excluded.Count), ContractJson.Options);
            await database.WriteAsync(tx =>
            {
                var revision = tx.Scalar("SELECT revision FROM inventory_catalog WHERE id=1") as string ?? "0";
                if (revision != previous.Revision.ToString()) throw new OperationException("inventory.planStale", "名称目录已更新");
                tx.Execute("INSERT INTO inventory_catalog VALUES(1,$r,$d) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,document=excluded.document", ("$r", next.Revision.ToString()), ("$d", json));
                SqliteStore.AppendEvent(tx, new(Guid.NewGuid().ToString("N"), "inventory-catalog", "inventory-catalog", next.Revision,
                    "inventory.catalog.changed", eventData)); return true;
            }, ct);
            lock (catalogGate) { catalog = next; }
            return CatalogSummary();
        }
        finally { mutation.Release(); }
    }
    public Task<InventoryAssociationSnapshot> EditAssociationsAsync(InventoryAssociationEditRequest request, UInt64String expectedRevision, CancellationToken ct = default) => EditAssociationsCoreAsync(request, expectedRevision, null, null, ct);
    public Task<InventoryAssociationSnapshot> EditAssociationsAsync(InventoryAssociationEditRequest request, UInt64String expectedRevision, string principal, string key, CancellationToken ct = default) => EditAssociationsCoreAsync(request, expectedRevision, principal, key, ct);
    public Task<InventoryAssociationSnapshot> EditAssociationsAsync(InventoryAssociationEditRequest request, string etag, string principal, string key, CancellationToken ct = default) => EditAssociationsCoreAsync(request, ParseEditTag(etag).Revision, principal, key, ct, etag);
    private async Task<InventoryAssociationSnapshot> EditAssociationsCoreAsync(InventoryAssociationEditRequest request, UInt64String expectedRevision, string? principal, string? key, CancellationToken ct, string? etag = null)
    {
        if (request is null || request.Changes is null || request.Changes.Any(c=>c is null || c.Type is null) || request.Changes.Count is < 1 or > 1000 || request.Changes.Select(c => TypeKey(c.Type)).Distinct(StringComparer.Ordinal).Count() != request.Changes.Count)
            throw new OperationException("request.invalid", "关联编辑为空、重复或超过预算");
        await mutation.WaitAsync(ct);
        try
        {
            var receipt=EditReceipt("associations",principal,key,request,expectedRevision,etag);
            if(ReadEditReceipt<InventoryAssociationSnapshot>(receipt) is { } replay) return replay;
            if(etag is not null && ParseEditTag(etag).Epoch!=epoch) throw new OperationException("inventory.planStale","库存 ETag 属于此前宿主");
            var previous = state; if (previous.Revision != expectedRevision) throw new OperationException("inventory.planStale", "道具关联已更新");
            var account = Account(request.AccountId, previous);
            var observed = account.Characters.Values.SelectMany(c => c.Items).Concat(account.SharedItems).Select(i => TypeKey(i.Type)).ToHashSet(StringComparer.Ordinal);
            var associations = new Dictionary<string, InventoryNameAssociation>(account.Associations, StringComparer.Ordinal);
            foreach (var change in request.Changes)
            {
                if (!observed.Contains(TypeKey(change.Type))) throw new OperationException("request.invalid", "仅可关联已观察道具类型");
                var name = change.Clear ? null : NormalizeName(change.Name ?? throw new OperationException("request.invalid", "确认名称不能为空"));
                associations[TypeKey(change.Type)] = new(change.Type, name, name,
                    change.Clear ? InventoryNameState.UserCleared : InventoryNameState.UserConfirmed, "user");
            }
            var revision = Next(previous.Revision); var accounts = new Dictionary<string, InventorySavedAccount>(previous.Accounts, StringComparer.Ordinal)
            { [request.AccountId] = account with { Associations = associations } };
            var next = previous with { Revision = revision, Accounts = accounts,
                Workspace = previous.Workspace is { } workspace ? workspace with { Revision = revision } : null };
            var savedAccount=accounts[request.AccountId];
            var types=savedAccount.Characters.Values.SelectMany(c=>c.Items).Concat(savedAccount.SharedItems).Select(i=>i.Type).DistinctBy(TypeKey).OrderBy(t=>t.Category,StringComparer.Ordinal).ThenBy(t=>t.TypeId.Value);
            var response=new InventoryAssociationSnapshot(request.AccountId,revision,types.Select(t=>Name(t,savedAccount)).ToArray());
            var document=JsonSerializer.Serialize(response,ContractJson.Options);
            await SaveStateAsync(previous, next, new(revision, request.AccountId, null, next.Workspace?.Current ?? false), ct,tx=>WriteEditReceipt(tx,receipt,document));
            return response;
        }
        finally { mutation.Release(); }
    }
    public Task<InventoryAccountView> SetCharacterSlotsAsync(InventoryCharacterSlotsRequest request, UInt64String expectedRevision, CancellationToken ct = default) => SetCharacterSlotsCoreAsync(request,expectedRevision,null,null,ct);
    public Task<InventoryAccountView> SetCharacterSlotsAsync(InventoryCharacterSlotsRequest request, UInt64String expectedRevision, string principal,string key,CancellationToken ct = default) => SetCharacterSlotsCoreAsync(request,expectedRevision,principal,key,ct);
    public Task<InventoryAccountView> SetCharacterSlotsAsync(InventoryCharacterSlotsRequest request,string etag,string principal,string key,CancellationToken ct=default) => SetCharacterSlotsCoreAsync(request,ParseEditTag(etag).Revision,principal,key,ct,etag);
    private async Task<InventoryAccountView> SetCharacterSlotsCoreAsync(InventoryCharacterSlotsRequest request, UInt64String expectedRevision, string? principal,string? key,CancellationToken ct,string? etag=null)
    {
        if (request is null || request.Slots is null || request.Slots.Any(s=>s is null) || request.Slots.Count > 6 || request.Slots.Select(s => s.CharacterId).Distinct(StringComparer.Ordinal).Count() != request.Slots.Count ||
            request.Slots.Any(s => string.IsNullOrWhiteSpace(s.CharacterId) || string.IsNullOrWhiteSpace(s.RawName) || s.RawName.Length > 128 || s.RawName.Any(char.IsControl)))
            throw new OperationException("request.invalid", "角色槽位身份重复或超出范围");
        await mutation.WaitAsync(ct);
        try
        {
            var receipt=EditReceipt("slots",principal,key,request,expectedRevision,etag);
            if(ReadEditReceipt<InventoryAccountView>(receipt) is { } replay) return replay;
            if(etag is not null && ParseEditTag(etag).Epoch!=epoch) throw new OperationException("inventory.planStale","库存 ETag 属于此前宿主");
            var previous = state; if (previous.Revision != expectedRevision) throw new OperationException("inventory.planStale", "账户角色已更新");
            var account = Account(request.AccountId, previous);
            foreach (var slot in request.Slots)
                if (account.Characters.TryGetValue(slot.CharacterId, out var known) && known.Identity.RawName != slot.RawName)
                    throw new OperationException("inventory.identityConflict", "角色名称与已观察身份冲突");
            var accounts = new Dictionary<string, InventorySavedAccount>(previous.Accounts, StringComparer.Ordinal)
            { [request.AccountId] = account with { Slots = request.Slots.ToArray(), SlotsComplete = request.Complete } };
            var revision = Next(previous.Revision); var next = previous with { Revision = revision, Accounts = accounts, Workspace = previous.Workspace is { } w ? w with { Revision = revision } : null };
            var response=AccountView(accounts[request.AccountId],next);var document=JsonSerializer.Serialize(response,ContractJson.Options);
            await SaveStateAsync(previous, next, new(revision, request.AccountId, null, next.Workspace?.Current ?? false), ct,tx=>WriteEditReceipt(tx,receipt,document));
            return response;
        }
        finally { mutation.Release(); }
    }
    private static (string Epoch,UInt64String Revision) ParseEditTag(string etag)
    {
        if(etag is null)throw new OperationException("revision.required","库存编辑需要 If-Match");
        var match=System.Text.RegularExpressions.Regex.Match(etag,"\\A\"inventory-([0-9a-fA-F]{32})-(0|[1-9][0-9]{0,19})\"\\z");
        if(!match.Success)throw new OperationException("request.invalid","库存编辑需要单个强 ETag");
        try{return(match.Groups[1].Value,UInt64String.Parse(match.Groups[2].Value));}
        catch(Exception error)when(error is FormatException or OverflowException){throw new OperationException("request.invalid","库存修订超过 64 位范围");}
    }
    private sealed record EditReceiptIdentity(string Id,string Hash);
    private static EditReceiptIdentity? EditReceipt<T>(string kind,string? principal,string? key,T request,UInt64String expectedRevision,string? etag=null)
    {
        if(principal is null && key is null) return null;
        if(string.IsNullOrWhiteSpace(principal)||string.IsNullOrWhiteSpace(key)||principal.Length>256||key.Length>256) throw new OperationException("request.invalid","编辑幂等身份无效");
        var id=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(principal+"\0"+kind+"\0"+key)));
        var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(etag is null?JsonSerializer.SerializeToUtf8Bytes(new{request,expectedRevision},ContractJson.Options):JsonSerializer.SerializeToUtf8Bytes(new{request,etag},ContractJson.Options)));
        return new(id,hash);
    }
    private T? ReadEditReceipt<T>(EditReceiptIdentity? receipt) where T:class
    {
        if(receipt is null) return null;
        if(database.ReadScalar("SELECT document FROM inventory_edit_receipts WHERE id=$id",("$id",receipt.Id)) is not string document) return null;
        if(database.ReadScalar("SELECT hash FROM inventory_edit_receipts WHERE id=$id",("$id",receipt.Id)) as string != receipt.Hash) throw new OperationException("idempotency.conflict","编辑幂等键已用于其他内容或修订");
        return JsonSerializer.Deserialize<T>(document,ContractJson.Options)!;
    }
    private static void WriteEditReceipt(DatabaseTransaction tx,EditReceiptIdentity? receipt,string document)
    {
        if(receipt is not null)tx.Execute("INSERT INTO inventory_edit_receipts VALUES($id,$hash,$doc)",("$id",receipt.Id),("$hash",receipt.Hash),("$doc",document));
    }
    private static void ValidateCatalog(InventoryCatalogData value)
    {
        if(value is null || value.Entries is null || value.Excluded is null || value.Categories is null || value.Resources is null || value.IndexSha256 is null || value.Entries.Any(e=>e is null || e.Type is null) || value.Excluded.Any(e=>e is null || e.Type is null) || value.Categories.Any(c=>c is null) || value.Resources.Any(r=>r is null || r.Sha256 is null || r.DecodedSha256 is null))
            throw new OperationException("inventory.catalogInvalid","名称目录及集合不能为空");
        if (string.IsNullOrWhiteSpace(value.Id) || value.Id.Length>256 || value.Region != "TW" || value.IndexSha256.Length != 64 || !value.IndexSha256.All(Uri.IsHexDigit) ||
            value.Entries.Count + value.Excluded.Count > 100000 || value.Categories.Count > 256 || value.Resources.Count > 32 ||
            value.Entries.Select(e => TypeKey(e.Type)).Distinct(StringComparer.Ordinal).Count() != value.Entries.Count)
            throw new OperationException("inventory.catalogInvalid", "名称目录身份、重复键或预算无效");
        foreach (var entry in value.Entries)
            if (entry.Type.TypeId.Value == 0 || string.IsNullOrWhiteSpace(entry.Type.Category) || string.IsNullOrWhiteSpace(entry.Table) ||
                string.IsNullOrWhiteSpace(entry.RawName) || string.IsNullOrWhiteSpace(entry.DisplayName) || entry.DisplayName.Length > 160 || entry.DisplayName.Any(char.IsControl))
                throw new OperationException("inventory.catalogInvalid", "名称目录条目无效");
        if (value.Resources.Any(r => r.Sha256.Length != 64 || !r.Sha256.All(Uri.IsHexDigit) || r.DecodedSha256.Length != 64 || !r.DecodedSha256.All(Uri.IsHexDigit)))
            throw new OperationException("inventory.catalogInvalid", "名称资源指纹无效");
    }
}
