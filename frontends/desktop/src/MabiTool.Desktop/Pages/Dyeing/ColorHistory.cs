using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 一条寻色记录（原工具 QuickPanel 的“寻色记录”行）：时间、结果，以及各区域“目标色 → 实测色”、实测 HEX、色差与后端的分区判定。
/// <see cref="Accepted"/> 是该次实测的整体达标标记，没有实测时为空。
/// </summary>
public sealed record ColorHistoryRow(string Id, string Time, string Result, bool? Accepted, IReadOnlyList<ColorHistoryRegion> Regions)
{
    // 区域列表按内容比较：重读后内容没变的记录行容器保留（StableItems）。
    public bool Equals(ColorHistoryRow? other) => other is not null && Id == other.Id && Time == other.Time && Result == other.Result &&
        Accepted == other.Accepted && Regions.SequenceEqual(other.Regions);

    public override int GetHashCode() => HashCode.Combine(Id, Time, Result, Accepted, Regions.Count);

    public bool ResultGood => Accepted == true;
    public bool ResultWarn => Accepted == false;
    public int Columns => Math.Max(1, Regions.Count);
}

/// <summary>记录里的一个区域：目标色、实测色（没有实测时为空）、色差文字（后端没有给出时为空）与分区判定。</summary>
public sealed record ColorHistoryRegion(string Name, string TargetHex, string? MeasuredHex, string Delta, ColorMark Mark, bool Enabled)
{
    public IBrush TargetBrush => ColorSwatches.Brush(TargetHex);
    public IBrush MeasuredBrush => ColorSwatches.Brush(MeasuredHex);
    public bool HasMeasured => MeasuredHex is not null;
    public bool NoMeasured => MeasuredHex is null;
    public string MeasuredText => MeasuredHex ?? "—";
    public bool HasDelta => Delta.Length > 0;
    public double Opacity => Enabled ? 1 : 0.55;
}

/// <summary>一页寻色记录；<see cref="NextCursor"/> 为空表示没有更多。读取失败时 <see cref="Ok"/> 为假，<see cref="Message"/> 是错误码。</summary>
public sealed record ColorHistoryReply(bool Ok, string Message, IReadOnlyList<ColorHistoryRow> Rows, string? NextCursor);

/// <summary>把后端的寻色记录排成界面行。</summary>
public static class ColorHistoryMapper
{
    /// <summary>每页条数取后端上限 50；后端只保留最新 30 条，一页即是全部记录。</summary>
    public const int PageSize = 50;

    /// <summary>
    /// 实测取运行结束时复核过的游戏颜色；没有时依次取最佳实测、最后实测。色差与分区判定都取同一次实测，
    /// 后端没有列出的区域按“未开始”（规则里停用的为“未启用”）。
    /// </summary>
    public static ColorHistoryRow Map(ColorHistoryItem item)
    {
        var measured = item.Result?.CurrentVerified ?? item.BestMeasured ?? item.Result?.BestMeasured
            ?? item.LastMeasured ?? item.Result?.LastMeasured;
        var marks = measured?.Regions.Select(judgement => (judgement.RegionId, judgement.State.RawValue)).ToList() ?? [];
        var regions = item.RulesSnapshot.Regions.Select((region, index) => new ColorHistoryRegion(
            UiText.Pick($"区域 {index + 1}", $"區域 {index + 1}"), region.TargetHex,
            measured?.HexByRegion.GetValueOrDefault(region.Id),
            measured?.DeltaByRegion.GetValueOrDefault(region.Id) is { } delta ? ColorStages.Delta(delta) : string.Empty,
            ColorMark.Of(marks, region.Id, region.Enabled), region.Enabled)).ToList();
        var outcome = item.Result?.Outcome is { Length: > 0 } value ? ColorStages.Outcome(value) : State(item.State);
        var time = item.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return new(item.OperationId, time, outcome, measured?.Accepted, regions);
    }

    /// <summary>没有寻色结果时的操作状态：已知的终态映射成短句，其余原样显示。</summary>
    public static string State(OpenEnum<OperationState> state) => state.KnownValue switch
    {
        OperationState.Succeeded => UiText.Pick("完成", "完成"),
        OperationState.Cancelled => UiText.Pick("已取消", "已取消"),
        OperationState.Failed => UiText.Pick("失败", "失敗"),
        OperationState.Interrupted => UiText.Pick("中断", "中斷"),
        _ => state.RawValue
    };
}

/// <summary>
/// 寻色记录层：打开时读取后端保留的记录（最新 30 条）并原样显示，不随页面刷新；关闭后迟到的结果丢弃。
/// </summary>
public sealed class ColorHistoryModel : ObservableObject
{
    private readonly IDesktopBackend backend;
    private bool loading;
    private string message = string.Empty;
    private int generation;

    public ColorHistoryModel(IDesktopBackend backend) => this.backend = backend;

    public ObservableCollection<ColorHistoryRow> Rows { get; } = [];

    public bool IsLoading { get => loading; private set { Set(ref loading, value); Raise(nameof(IsEmpty)); } }
    public string Message { get => message; private set { Set(ref message, value); Raise(nameof(HasMessage)); Raise(nameof(IsEmpty)); } }
    public bool HasMessage => message.Length > 0;
    public bool IsEmpty => Rows.Count == 0 && !loading && message.Length == 0;
    public bool HasRows => Rows.Count > 0;

    /// <summary>重新读取最新的记录。</summary>
    public async Task OpenAsync()
    {
        var ticket = ++generation;
        Rows.Clear();
        Message = string.Empty;
        IsLoading = true;
        var reply = await backend.QueryColorHistoryAsync(null, CancellationToken.None);
        if (ticket != generation) return;
        if (reply.Ok)
            foreach (var row in reply.Rows) Rows.Add(row);
        else Message = reply.Message;
        IsLoading = false;
        Raise(nameof(HasRows));
    }

    /// <summary>面板关闭：之后到达的读取结果不再写入。</summary>
    public void Close() => generation++;
}
