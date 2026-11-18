using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure.Workers;

/// <summary>映射原算法的颜色与读取证据；候选顺序和匹配判定由引擎提供。</summary>
public static class ColorWorkerResults
{
    public static ColorRunResult ReadResult(WorkerFrame frame, ColorProfile profile,
        ObservationSource source, UInt64String generation)
    {
        try
        {
            if (frame.Kind != "result" || frame.RunId is null) throw Invalid();
            var raw = frame.Payload;
            var count = raw.TryGetProperty("region_count", out var size) ? size.GetInt32() : 0;
            if (count != 0 && (count is not (2 or 3) || profile.Regions.Count < count)) throw Invalid();
            ColorMeasurement? Read(string key)
            {
                if (!raw.TryGetProperty("measurements", out var evidence) || !evidence.TryGetProperty(key, out var row) ||
                    row.ValueKind == JsonValueKind.Null) return null;
                var colors = Colors(row.GetProperty("colors"), profile, count);
                for (var i = 0; i < count; i++)
                    if (profile.Regions[i].Enabled && colors[profile.Regions[i].Id] is null) throw Invalid();
                var present = colors.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!);
                if (present.Count == 0) throw Invalid();
                var at = row.GetProperty("observedAt").GetDateTimeOffset();
                return new(present, at, source, generation,
                    present.Count == count ? ObservationQuality.Confirmed : ObservationQuality.Partial)
                {
                    DeltaByRegion = Deltas(row.GetProperty("deltas"), profile, count),
                    Accepted = row.GetProperty("accepted").GetBoolean(),
                    TargetExact = row.GetProperty("targetExact").GetBoolean(),
                    Regions = States(row, "regionStates", profile, count)
                };
            }
            var best = Read("bestMeasured");
            var last = Read("lastMeasured");
            var current = Read("currentVerified");
            var outcome = raw.TryGetProperty("outcome", out var value) ? value.GetString() ?? "unverified" : "unverified";
            if (current is null && outcome is "matched" or "compromise") outcome = "unverified";
            // 细分原因与准备步骤只作原因码使用：引擎的错误原文可能含本机路径，不进入运行记录。
            return new(frame.RunId, count, outcome, Code(raw, "stop_reason"), best, last, current)
            {
                // 截图核验未通过与观察失败的细分原因（stop_detail）排在准备失败与中断原因之后。
                StopDetail = Code(raw, "unavailable_reason") ?? Code(raw, "interruption_reason") ?? Code(raw, "stop_detail"),
                FailedStage = Code(raw, "preparation_stage"),
                BestCurrent = raw.TryGetProperty("best_current", out var kept) && kept.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? kept.GetBoolean() : null
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        { throw Invalid(error); }
    }

    public static ColorCandidateBatch ReadCandidates(WorkerFrame frame, ColorProfile profile, DateTimeOffset receivedAt)
    {
        try
        {
            if (frame.Kind != "event" || frame.Payload.GetProperty("kind").GetString() != "native_candidate_ready") throw Invalid();
            var data = frame.Payload.GetProperty("data");
            var count = data.GetProperty("region_count").GetInt32();
            if (count is not (2 or 3) || profile.Regions.Count < count) throw Invalid();
            var batch = data.GetProperty("batch_id").GetString() ?? throw Invalid();
            var interaction = ReadInteraction(data.GetProperty("interaction"));
            if (interaction.BatchId != batch) throw Invalid();
            var deadline = data.GetProperty("effective_deadline").GetDouble();
            if (data.TryGetProperty("selection_deadline", out var selectionDeadline)) deadline = Math.Min(deadline, selectionDeadline.GetDouble());
            var remaining = deadline - frame.Payload.GetProperty("at_monotonic").GetDouble();
            if (!double.IsFinite(remaining) || remaining > 300) throw Invalid();
            var rows = data.GetProperty("candidates");
            if (rows.GetArrayLength() > 12) throw Invalid();
            var candidates = rows.EnumerateArray().Select((row, index) => new ColorCandidate(
                row.GetProperty("id").GetString() ?? throw Invalid(), index,
                Colors(row.GetProperty("colors"), profile, count), Deltas(row.GetProperty("deltas"), profile, count),
                row.GetProperty("predicted").GetBoolean(),
                remaining > 0 && interaction.SelectionEnabled && row.GetProperty("available").GetBoolean(),
                row.GetProperty("current").GetBoolean(), row.GetProperty("accepted").GetBoolean(), row.GetProperty("target_exact").GetBoolean())
            {
                Regions = States(row, "region_states", profile, count),
                MaximumDelta = OptionalDelta(row, "maximum"), AverageDelta = OptionalDelta(row, "average"),
                ExactMatches = OptionalCount(row, "exact_matches"), ExactTotal = OptionalCount(row, "exact_total"),
                FamilyConsistent = row.TryGetProperty("family_consistent", out var family) && family.ValueKind != JsonValueKind.Null ? family.GetBoolean() : null
            }).ToArray();
            if (candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != candidates.Length) throw Invalid();
            return new(batch, candidates, OptionalString(data, "current_candidate_id"),
                receivedAt.AddSeconds(Math.Max(0, remaining)), interaction with { SelectionEnabled = remaining > 0 && interaction.SelectionEnabled });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        { throw Invalid(error); }
    }

    public static ColorInteractionState ReadInteraction(JsonElement row)
    {
        var revision = row.GetProperty("revision").GetString();
        if (!WorkerProtocol.IsUInt64(revision)) throw Invalid();
        return new(new(ulong.Parse(revision!, CultureInfo.InvariantCulture)), OptionalString(row, "batchId"),
            row.GetProperty("selectionEnabled").GetBoolean(), row.GetProperty("retryEnabled").GetBoolean());
    }

    private static Dictionary<string, string?> Colors(JsonElement row, ColorProfile profile, int count)
    {
        if (row.GetArrayLength() != count) throw Invalid();
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var hex = row[i].ValueKind == JsonValueKind.Null ? null : row[i].GetString();
            if (hex is not null && !Regex.IsMatch(hex, @"\A#[0-9A-F]{6}\z", RegexOptions.CultureInvariant)) throw Invalid();
            result.Add(profile.Regions[i].Id, hex);
        }
        return result;
    }

    private static Dictionary<string, double?> Deltas(JsonElement row, ColorProfile profile, int count)
    {
        if (row.GetArrayLength() != count) throw Invalid();
        var result = new Dictionary<string, double?>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            double? delta = row[i].ValueKind == JsonValueKind.Null ? null : row[i].GetDouble();
            if (delta is { } value && (!double.IsFinite(value) || value < 0)) throw Invalid();
            result.Add(profile.Regions[i].Id, delta);
        }
        return result;
    }

    /// <summary>Worker 按原区域卡徽标规则给出的判定；旧 Worker 未提供时返回空列表。</summary>
    private static ColorRegionJudgement[] States(JsonElement row, string key, ColorProfile profile, int count)
    {
        if (!row.TryGetProperty(key, out var states) || states.ValueKind == JsonValueKind.Null) return [];
        if (states.GetArrayLength() != count) throw Invalid();
        var result = new ColorRegionJudgement[count];
        for (var i = 0; i < count; i++)
        {
            OpenEnum<ColorRegionState> state = new(states[i].GetString() ?? throw Invalid());
            if (!state.IsKnown) throw Invalid();
            result[i] = new(profile.Regions[i].Id, state);
        }
        return result;
    }

    private static double? OptionalDelta(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var delta = value.GetDouble();
        return double.IsFinite(delta) && delta >= 0 ? delta : throw Invalid();
    }

    private static int? OptionalCount(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var number = value.GetInt32();
        return number is >= 0 and <= 3 ? number : throw Invalid();
    }

    private static string? OptionalString(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    /// <summary>原因码：1–64 位字母、数字、点、下划线或连字符；其他形状不当作原因码，返回空。</summary>
    private static string? Code(JsonElement row, string key) =>
        OptionalString(row, key) is { Length: >= 1 and <= 64 } value &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? value : null;

    private static WorkerException Invalid(Exception? error = null) => new("worker.resultInvalid", error);
}
