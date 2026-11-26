using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MabiTool.Providers.TwCli;

public enum GameProcessState { NotRunning, Running, Unverifiable }

/// <summary>游戏进程探测：映像路径等于所选目录的 <c>MabinogiMobile.exe</c> 才算运行中；读不到某个同名进程的映像路径时为 <see cref="GameProcessState.Unverifiable"/>。</summary>
public sealed record GameProcessInfo(GameProcessState State, int ProcessId = 0, long StartTicks = 0)
{
    public string? SessionKey => State == GameProcessState.Running ? ProcessId + ":" + StartTicks : null;
}

public interface IGameProcessProbe
{
    GameProcessInfo Probe(string gameDirectory);
}

public sealed class TwCliPreferencesException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>偏好里两个连接器键的当前值（原始文本）；未设置或已撤销为空。</summary>
public sealed record MmPlayerPrefsValues(string? Enabled, string? LastUsedTicks)
{
    public bool? EnabledFlag => Enabled is null ? null : string.Equals(Enabled, "True", StringComparison.OrdinalIgnoreCase);
    public DateTimeOffset? LastUsedAt => long.TryParse(LastUsedTicks, out var ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
}

/// <summary>
/// 台服偏好文件（<c>BaseMMPlayerPrefs.txt</c> 与 <c>ChangedMMPlayerPrefs.txt</c>）里连接器键的读写，保持已核对的适配器格式与安全步骤：
/// 游戏已退出、首次修改前备份、UTF-8、保留其他内容、临时文件原子替换、写前复核进程与文件、写后读回。
/// </summary>
public sealed class MmPlayerPrefs(string preferencesDirectory, string stateDirectory, IGameProcessProbe probe, TimeProvider? time = null)
{
    public const string EnableKey = "game_use_cli_for_ai", TimeKey = "game_cli_for_ai_last_used_utc_ticks";
    private const string StateFile = "connector-state.json";
    private static readonly Dictionary<string, string> Sectors = new(StringComparer.Ordinal)
    { ["STRING"] = "1", ["INT"] = "2", ["FLOAT"] = "3", ["BOOL"] = "4" };
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal) { [EnableKey] = "4", [TimeKey] = "1" };
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();

    private sealed record FileStamp(string Sha256, long Bytes);
    private sealed record HistoryEntry(string Action, DateTimeOffset At, Dictionary<string, string?> Values);
    private sealed record State(Dictionary<string, string?> OriginalPreferences, string Backup, Dictionary<string, FileStamp> OriginalFiles, List<HistoryEntry> History);

    private string Base => Path.Combine(preferencesDirectory, "BaseMMPlayerPrefs.txt");
    private string Changed => Path.Combine(preferencesDirectory, "ChangedMMPlayerPrefs.txt");

    public bool HasBackup => File.Exists(Path.Combine(stateDirectory, StateFile));
    public bool PreferencesExist => File.Exists(Base);

    /// <summary>读取两个键的当前值；键所在类型段或增量记号与已核对格式不一致时抛出 <c>gameCli.preferencesInvalid</c>。</summary>
    public MmPlayerPrefsValues Read()
    {
        var values = new Dictionary<string, string?> { [EnableKey] = null, [TimeKey] = null };
        string? sector = null;
        if (File.Exists(Base))
            foreach (var line in Lines(Base))
            {
                if (Sectors.TryGetValue(line, out var section)) { sector = section; continue; }
                var parts = line.Split(" : ", 2);
                if (parts.Length == 2 && Keys.TryGetValue(parts[0], out var expected))
                {
                    if (sector != expected) throw Invalid("连接器配置类型与已核对的客户端格式不一致");
                    values[parts[0]] = parts[1];
                }
            }
        if (File.Exists(Changed))
            foreach (var line in Lines(Changed))
            {
                var parts = line.Split(" : ", 3);
                if (parts.Length < 2 || !Keys.TryGetValue(parts[1], out var expected)) continue;
                var token = parts[0];
                if (token.Length != 2 || token[0] is not ('+' or '-') || token[1].ToString() != expected) throw Invalid("连接器增量配置格式不匹配");
                if (token[0] == '+' && parts.Length != 3) throw Invalid("连接器增量配置缺少值");
                values[parts[1]] = token[0] == '+' ? parts[2] : null;
            }
        return new(values[EnableKey], values[TimeKey]);
    }

    public MmPlayerPrefsValues Enable(string gameDirectory)
    {
        lock (gate)
        {
            RequireClosed(gameDirectory);
            var state = EnsureBackup();
            var values = new Dictionary<string, string?>
            { [EnableKey] = "True", [TimeKey] = clock.GetUtcNow().UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            Append(gameDirectory, values);
            Record(state, "enable", values);
            return Read();
        }
    }

    public MmPlayerPrefsValues Disable(string gameDirectory)
    {
        lock (gate)
        {
            RequireClosed(gameDirectory);
            var state = EnsureBackup();
            var values = new Dictionary<string, string?> { [EnableKey] = "False" };
            Append(gameDirectory, values);
            Record(state, "disable", values);
            return Read();
        }
    }

    /// <summary>恢复首次修改前的两项偏好（原先不存在的键追加撤销记录），保留其他设置。</summary>
    public MmPlayerPrefsValues Restore(string gameDirectory)
    {
        lock (gate)
        {
            RequireClosed(gameDirectory);
            var state = LoadState() ?? throw new TwCliPreferencesException("gameCli.noBackup", "没有可恢复的连接器配置记录");
            var values = new Dictionary<string, string?>(state.OriginalPreferences);
            Append(gameDirectory, values);
            Record(state, "restore", values);
            return Read();
        }
    }

    private void RequireClosed(string gameDirectory)
    {
        switch (probe.Probe(gameDirectory).State)
        {
            case GameProcessState.Running:
                throw new TwCliPreferencesException("gameCli.gameRunning", "请先正常退出台服游戏，再启用、关闭或恢复连接器；配置在重新启动时读取");
            case GameProcessState.Unverifiable:
                throw new TwCliPreferencesException("gameCli.processUnverifiable", "无法核对游戏进程路径，请先退出游戏再修改配置");
        }
    }

    private State EnsureBackup()
    {
        if (LoadState() is { } existing) return existing;
        if (!File.Exists(Base)) throw new TwCliPreferencesException("gameCli.preferencesMissing", "未找到台服现有设置，请先正常启动台服一次");
        var current = Read();
        Directory.CreateDirectory(stateDirectory);
        var stamp = clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmssffffff'Z'");
        var backup = Path.Combine(stateDirectory, "backups", stamp);
        Directory.CreateDirectory(backup);
        var files = new Dictionary<string, FileStamp>();
        foreach (var name in new[] { "BaseMMPlayerPrefs.txt", "ChangedMMPlayerPrefs.txt" })
        {
            var path = Path.Combine(preferencesDirectory, name);
            if (!File.Exists(path)) continue;
            var data = File.ReadAllBytes(path);
            File.WriteAllBytes(Path.Combine(backup, name), data);
            files[name] = new(Convert.ToHexStringLower(SHA256.HashData(data)), data.Length);
        }
        var state = new State(new() { [EnableKey] = current.Enabled, [TimeKey] = current.LastUsedTicks }, backup, files, []);
        SaveState(state);
        return state;
    }

    private void Append(string gameDirectory, IReadOnlyDictionary<string, string?> values)
    {
        if (!File.Exists(Base)) throw new TwCliPreferencesException("gameCli.preferencesMissing", "未找到台服现有设置，请先正常启动台服一次");
        var original = File.Exists(Changed) ? File.ReadAllBytes(Changed) : [];
        var records = new StringBuilder();
        foreach (var (key, value) in values)
        {
            if (!Keys.TryGetValue(key, out var sector) || value is not null && value.IndexOfAny(['\r', '\n', ':', ';']) >= 0)
                throw Invalid("无效的连接器配置");
            records.Append(value is null ? '-' : '+').Append(sector).Append(" : ").Append(key).Append(" : ").Append(value ?? string.Empty).Append('\n');
        }
        var suffix = Utf8.GetBytes(records.ToString());
        var separator = original.Length > 0 && original[^1] != (byte)'\n' ? 1 : 0;
        var content = new byte[original.Length + separator + suffix.Length];
        original.CopyTo(content, 0);
        if (separator == 1) content[original.Length] = (byte)'\n';
        suffix.CopyTo(content, original.Length + separator);
        string? temporary = null;
        try
        {
            temporary = Path.Combine(preferencesDirectory, ".mabi-cli-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            RequireClosed(gameDirectory);
            var latest = File.Exists(Changed) ? File.ReadAllBytes(Changed) : [];
            if (!latest.AsSpan().SequenceEqual(original))
                throw new TwCliPreferencesException("gameCli.preferencesChanged", "配置文件在写入前发生变化，已保留原文件，请重试");
            File.Move(temporary, Changed, overwrite: true);
            temporary = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TwCliPreferencesException("gameCli.writeFailed", "写入台服设置失败：" + error.Message);
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        var actual = Read();
        foreach (var (key, value) in values)
            if ((key == EnableKey ? actual.Enabled : actual.LastUsedTicks) != value)
                throw new TwCliPreferencesException("gameCli.verifyFailed", "写入后配置校验失败");
    }

    private State? LoadState()
    {
        var path = Path.Combine(stateDirectory, StateFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(path, Utf8)); }
        catch (JsonException) { return null; }
    }

    private void SaveState(State state)
    {
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, StateFile);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Utf8);
        File.Move(temporary, path, overwrite: true);
    }

    private void Record(State state, string action, Dictionary<string, string?> values)
    {
        state.History.Add(new(action, clock.GetUtcNow(), new(values)));
        if (state.History.Count > 200) state.History.RemoveRange(0, state.History.Count - 200);
        SaveState(state);
    }

    private static string[] Lines(string path) =>
        Utf8.GetString(File.ReadAllBytes(path)).TrimStart('﻿').Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    private static TwCliPreferencesException Invalid(string message) => new("gameCli.preferencesInvalid", message);
}
