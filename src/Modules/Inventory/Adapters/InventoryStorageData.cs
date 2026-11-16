// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Inventory.Domain;

namespace MabiTool.Modules.Inventory.Adapters;

public static class InventoryStorageData
{
    public static StorageDataDefinition Definition { get; } = new("inventory", ["inventory_state", "inventory_catalog"]);
    public static IStorageModuleDataOwner Owner(SqliteStore database) => CreateOwner(database,null);
    public static IStorageModuleDataOwner Owner(SqliteStore database,InventoryHistoryStore store) => CreateOwner(database,store);
    private static IStorageModuleDataOwner CreateOwner(SqliteStore database,InventoryHistoryStore? store)=>new StorageModuleDataOwner(Definition,()=>
    {
        var oldState=UInt64String.Parse(database.ReadScalar("SELECT revision FROM inventory_state WHERE id=1") as string??"0");
        var oldCatalog=UInt64String.Parse(database.ReadScalar("SELECT revision FROM inventory_catalog WHERE id=1") as string??"0");
        var stateRevision=new UInt64String(checked(oldState.Value+1));var catalogRevision=new UInt64String(checked(oldCatalog.Value+1));
        var stateJson=JsonSerializer.Serialize(InventorySavedState.Empty with {Revision=stateRevision},ContractJson.Options);
        var catalogJson=JsonSerializer.Serialize(new InventoryCatalogSnapshot(catalogRevision,null),ContractJson.Options);
        var eventData=JsonSerializer.SerializeToElement(new InventorySnapshotChanged(stateRevision,null,null,false),ContractJson.Options);
        var catalogEvent=JsonSerializer.SerializeToElement(new InventoryCatalogChanged(catalogRevision,"deleted",0,0),ContractJson.Options);
        return new(tx=>
        {
            if(UInt64String.Parse(tx.Scalar("SELECT revision FROM inventory_state WHERE id=1") as string??"0")!=oldState || UInt64String.Parse(tx.Scalar("SELECT revision FROM inventory_catalog WHERE id=1") as string??"0")!=oldCatalog) throw new OperationException("inventory.planStale","删除前库存或目录已更新");
            tx.Execute("DELETE FROM inventory_import_receipts");tx.Execute("DELETE FROM inventory_edit_receipts");
            tx.Execute("INSERT INTO inventory_state VALUES(1,$revision,$document) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,document=excluded.document",("$revision",stateRevision.ToString()),("$document",stateJson));
            tx.Execute("INSERT INTO inventory_catalog VALUES(1,$revision,$document) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,document=excluded.document",("$revision",catalogRevision.ToString()),("$document",catalogJson));
            SqliteStore.AppendEvent(tx,new(Guid.NewGuid().ToString("N"),"inventory","inventory",stateRevision,"inventory.snapshot.changed",eventData));
            SqliteStore.AppendEvent(tx,new(Guid.NewGuid().ToString("N"),"inventory-catalog","inventory-catalog",catalogRevision,"inventory.catalog.changed",catalogEvent));
        },Committed:store is null?null:store.Reload);
    },["inventory.observe","inventory.catalog.refresh","inventory.catalog.import","inventory.import","inventory.export"],["inventory.snapshot.changed","inventory.catalog.changed","inventory.observation.degraded"]);
}
