using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

/// <summary>模块公开登记内容及删除入口；删除回调只操作所属模块表，并在同一同步事务内执行。</summary>
public interface IStorageModuleDataOwner
{
    string ModuleId { get; }
    StorageDataDefinition Definition { get; }
    IReadOnlyList<string> OperationKinds { get; }
    IReadOnlyList<string> EventTypes { get; }
    StorageModuleDeletion PrepareDeletion();
}

public sealed record StorageModuleDeletion(Action<DatabaseTransaction> Apply, Action? Committed = null);

public sealed class StorageModuleDataOwner(StorageDataDefinition definition, Func<StorageModuleDeletion> prepare,
    IReadOnlyList<string> operationKinds, IReadOnlyList<string> eventTypes) : IStorageModuleDataOwner
{
    public string ModuleId => Definition.ModuleId;
    public StorageDataDefinition Definition { get; } = definition;
    public IReadOnlyList<string> OperationKinds { get; } = operationKinds;
    public IReadOnlyList<string> EventTypes { get; } = eventTypes;
    public StorageModuleDeletion PrepareDeletion() => prepare();
}
