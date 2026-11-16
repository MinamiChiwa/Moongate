using System.Diagnostics;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

/// <summary>调试采集的接收端：只在采集开启并选中记录模块与等级时被调用。</summary>
public interface IDiagnosticCaptureSink
{
    void Accept(DiagnosticCaptureGate gate, DiagnosticRecord record, Exception? exception, string? detail);
}

public sealed record DiagnosticCaptureGate(string Id, IReadOnlySet<string> Modules, DiagnosticLevel Level, bool IncludeDetail, IDiagnosticCaptureSink Sink);

/// <summary>
/// 诊断记录入口：分配会话内序号，info 及以上交默认记录，命中调试采集的记录另交采集。未开启采集时
/// debug/trace 调用点只读取一次采集门限。
/// </summary>
public sealed class DiagnosticTrace(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private volatile DiagnosticCaptureGate? capture;
    private volatile Func<DiagnosticRecord, bool>? persist;
    private long sequence;

    public bool IsEnabled(DiagnosticEventDefinition definition, string? module = null) =>
        definition.Level >= DiagnosticLevel.Info || Captures(definition.Module ?? module ?? "core", definition.Level);
    private bool Captures(string module, DiagnosticLevel level) => capture is { } gate && level >= gate.Level && gate.Modules.Contains(module);

    public void AttachPersistence(Func<DiagnosticRecord, bool> sink) => persist = sink;
    public void BeginCapture(DiagnosticCaptureGate gate) => capture = gate;
    public void EndCapture(string id) { if (capture?.Id == id) capture = null; }

    public void Emit(DiagnosticEventDefinition definition, DiagnosticFields? fields = null, string? module = null, string? code = null,
        string? operationId = null, Exception? exception = null, string? detail = null)
    {
        var target = definition.Module ?? module ?? "core";
        if (definition.Level < DiagnosticLevel.Info && !Captures(target, definition.Level)) return;
        Submit(DiagnosticSanitizer.Create(time.GetUtcNow(), DiagnosticEvents.LevelName(definition.Level), target, code,
            Activity.Current?.TraceId.ToString(), operationId, exceptionType: exception?.GetType().Name, component: definition.Component,
            @event: definition.Code, fields: fields?.Items), exception, detail);
    }

    /// <summary>返回记录是否进入默认记录写入队列。</summary>
    public bool Submit(DiagnosticRecord record, Exception? exception = null, string? detail = null)
    {
        record = record with { Sequence = new((ulong)Interlocked.Increment(ref sequence)), Detail = null };
        var level = DiagnosticEvents.Level(record.Level);
        var persisted = level >= DiagnosticLevel.Info && persist is { } sink && sink(record);
        if (capture is { } gate && level >= gate.Level && gate.Modules.Contains(record.Module))
            gate.Sink.Accept(gate, record, exception, detail);
        return persisted;
    }
}
