using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record InventoryReadModelSnapshot(UInt64String Revision, InventoryWorkspace? Workspace,
    InventoryCatalogSnapshot Catalog);

/// <summary>读取同一库存变更水位下的工作区和名称目录；查询不会启动观察。</summary>
public interface IInventoryReadModel
{
    ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}
