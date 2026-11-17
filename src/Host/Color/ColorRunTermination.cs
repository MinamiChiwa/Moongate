using MabiTool.Contracts;

namespace MabiTool.Host.Color;

/// <summary>
/// 引擎一局结果到运行终态的映射。引擎在准备失败、色板未出现、窗口失焦等情形下也按“结果”返回，
/// 只有真正完成搜索的一局记为成功；其余记为失败或中断，并给出公开错误码，停止原因与细分原因保留在结果里供界面说明。
/// </summary>
public static class ColorRunTermination
{
    /// <summary>完成搜索并给出结论的一局：达标、妥协、预算内未找到、时间用完。</summary>
    private static readonly HashSet<string> Completed = new(StringComparer.Ordinal)
    {
        "time_used", "target_observed", "target_matched", "compromise_observed", "user_candidate_observed", "not_found_in_budget"
    };

    /// <summary>没有开始搜索或引擎出错：准备失败、本局没有可用区域、处理异常。</summary>
    private static readonly HashSet<string> Failures = new(StringComparer.Ordinal)
    {
        "unavailable", "not_started", "no_available_regions", "internal_error", "processing_error", "verification_error"
    };

    /// <summary>搜索中途被外部条件打断：失焦、窗口变化、染色界面关闭、本局色板变化、操作结果无法确认。</summary>
    private static readonly HashSet<string> Interruptions = new(StringComparer.Ordinal)
    {
        "interrupted", "palette_ended", "binding_changed", "session_binding_changed", "input_outcome_unknown", "model_response_mismatch"
    };

    /// <summary>用户自己停止（按钮、停止键）的中断原因。</summary>
    private static readonly HashSet<string> UserStops = new(StringComparer.Ordinal)
    {
        "user_stop", "stop_key_detected", "stop_hotkey", "window_closed", "stop_signal_unknown"
    };

    /// <summary>
    /// 按停止原因、细分原因与是否有已复核的实测决定终态与错误码；成功与取消没有错误码。
    /// 未登记的停止原因：有已复核实测时视为完成的一局，否则视为失败。
    /// </summary>
    public static (OperationState State, string? Problem) Classify(string? stopReason, string? detail, bool verified)
    {
        if (stopReason is null || Completed.Contains(stopReason)) return (OperationState.Succeeded, null);
        if (stopReason == "interrupted" && detail is not null && UserStops.Contains(detail)) return (OperationState.Cancelled, null);
        if (Failures.Contains(stopReason)) return (OperationState.Failed, FailureCode(detail));
        if (Interruptions.Contains(stopReason)) return (OperationState.Interrupted, InterruptionCode(stopReason, detail));
        return verified ? (OperationState.Succeeded, null) : (OperationState.Failed, "operation.executionFailed");
    }

    private static string FailureCode(string? detail) => detail switch
    {
        "process_access" or "permission.missing" => "permission.missing",
        "game_build" or "module_resolution" or "gameBuild.incompatible" => "gameBuild.incompatible",
        "gameBinding.invalid" or "input.bindingChanged" => "input.bindingChanged",
        _ => "operation.executionFailed"
    };

    private static string InterruptionCode(string stopReason, string? detail) => (stopReason, detail) switch
    {
        ("interrupted", "focus_lost" or "activation_failed") => "input.focusChanged",
        ("interrupted", "window_changed") or ("binding_changed", _) or ("session_binding_changed", _) => "input.bindingChanged",
        _ => "operation.executionFailed"
    };
}
