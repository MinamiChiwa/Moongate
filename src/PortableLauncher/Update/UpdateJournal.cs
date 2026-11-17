using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MabiTool.PortableUpdate;

/// <summary>
/// 解压版更新在工具根（目录包为包根下的 userdata）里的固定位置。全部路径由工具根推出，
/// 日志与结果文件只记相对位置，程序文件夹整体搬移后仍能按日志恢复。本文件同时编入 Moongate.exe 与宿主。
/// </summary>
internal static class UpdateLayout
{
    /// <summary>包根下逐项换入换出的三个条目，顺序即换入顺序；回退按相反顺序。</summary>
    internal static readonly string[] Items = { "components", "licenses", ReleaseIdentity.EntryName };

    internal static string Root(string toolRoot) => Path.Combine(toolRoot, "update");
    internal static string Journal(string toolRoot) => Path.Combine(Root(toolRoot), "journal.log");
    internal static string LastResult(string toolRoot) => Path.Combine(Root(toolRoot), "last-result.txt");
    /// <summary>宿主交出控制后存在，替换器在启动新版本（或回退后的旧版本）之前删除；前端见到它就退出、不重启宿主。</summary>
    internal static string Restarting(string toolRoot) => Path.Combine(Root(toolRoot), "restarting");
    /// <summary>替换器运行期间独占打开；包根 Moongate.exe 据此判断更新正在进行。</summary>
    internal static string ReplacerLock(string toolRoot) => Path.Combine(Root(toolRoot), "replacer.lock");
    internal static string Plans(string toolRoot) => Path.Combine(Root(toolRoot), "plans");
    internal static string Downloads(string toolRoot) => Path.Combine(Root(toolRoot), "downloads");
    internal static string Staging(string toolRoot, string transaction) => Path.Combine(Root(toolRoot), "staging", transaction);
    /// <summary>解压出的新包根（与包根同结构）。</summary>
    internal static string StagedPackage(string toolRoot, string transaction) => Path.Combine(Staging(toolRoot, transaction), "Moongate");
    internal static string Previous(string toolRoot, string transaction) => Path.Combine(Root(toolRoot), "previous", transaction);
    internal static string Transaction(string toolRoot, string transaction) => Path.Combine(Root(toolRoot), "transactions", transaction);
    internal static string Replacer(string toolRoot, string transaction) =>
        Path.Combine(Transaction(toolRoot, transaction), "replacer", ReleaseIdentity.EntryName);
    internal static string DatabaseBackup(string toolRoot, string transaction) => Path.Combine(Transaction(toolRoot, transaction), "database-backup.json");
    /// <summary>某个版本的宿主完成数据迁移并就绪后写出的健康标记。</summary>
    internal static string Healthy(string toolRoot, string transaction, string displayVersion) =>
        Path.Combine(Transaction(toolRoot, transaction), "healthy-" + displayVersion);

    /// <summary>与 ModuleSdk 的 ToolRoot 相同：包根已有旧布局的运行目录时用包根，否则用包根下的 userdata。</summary>
    internal static string ToolRootOfPackage(string packageRoot)
    {
        var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new[] { "bootstrap", "data", "runtime", "desktop" }.Any(name => Directory.Exists(Path.Combine(root, name)))
            ? root : Path.Combine(root, "userdata");
    }

    /// <summary>替换器正在运行（独占持有锁文件）。</summary>
    internal static bool ReplacerActive(string toolRoot)
    {
        var path = ReplacerLock(toolRoot);
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static bool IsTransactionId(string? value) =>
        value is { Length: 32 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// 一次更新的事务日志：每行一个以制表符分隔的步骤，追加后立即刷盘。首行 <c>begin</c> 记录事务、新旧版本、发起安装的操作与数据库备份，
/// 其后是等待退出的进程（进程号与启动时间）和各阶段。断电留下的半行在读取时丢弃。
/// </summary>
internal sealed class UpdateJournal
{
    internal const string Begin = "begin", Process = "process", Swap = "swap", Swapped = "swapped", Launch = "launch",
        Committed = "committed", Rollback = "rollback", RestoreDatabase = "restore-database", DatabaseRestored = "database-restored",
        RolledBack = "rolled-back", RollbackFailed = "rollback-failed", Aborted = "aborted";

    private readonly string path;
    private readonly List<string[]> entries;

    private UpdateJournal(string path, List<string[]> entries)
    {
        this.path = path;
        this.entries = entries;
    }

    internal string TransactionId => entries[0][1];
    internal string FromVersion => entries[0][2];
    internal string ToVersion => entries[0][3];
    internal string OperationId => entries[0][4];
    internal string BackupId => entries[0][5];
    internal IReadOnlyList<string[]> Entries => entries;
    internal string Phase => entries[entries.Count - 1][0];
    internal bool IsFinal => Phase is Committed or RolledBack or RollbackFailed or Aborted;

    internal IEnumerable<KeyValuePair<int, long>> Processes => entries.Where(entry => entry[0] == Process && entry.Length == 3)
        .Select(entry => new KeyValuePair<int, long>(int.Parse(entry[1], System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(entry[2], System.Globalization.CultureInfo.InvariantCulture)));

    internal bool Has(string step) => entries.Any(entry => entry[0] == step);
    internal bool Launched(string version) => entries.Any(entry => entry[0] == Launch && entry.Length > 1 && entry[1] == version);

    /// <summary>旧宿主须先恢复数据库：已登记恢复请求且尚未完成。</summary>
    internal bool DatabaseRestorePending
    {
        get
        {
            var request = entries.FindLastIndex(entry => entry[0] == RestoreDatabase);
            return request >= 0 && entries.FindLastIndex(entry => entry[0] == DatabaseRestored) < request;
        }
    }

    /// <summary>写出新事务的首行；已有未结束的事务时拒绝。</summary>
    internal static UpdateJournal Create(string toolRoot, string transactionId, string fromVersion, string toVersion,
        string operationId, string backupId, IEnumerable<KeyValuePair<int, long>> processes)
    {
        if (!UpdateLayout.IsTransactionId(transactionId)) throw new ArgumentException("事务编号无效", nameof(transactionId));
        foreach (var field in new[] { fromVersion, toVersion, operationId, backupId })
            if (field.Length == 0 || field.Any(ch => ch is '\t' or '\r' or '\n')) throw new ArgumentException("事务字段无效");
        var file = UpdateLayout.Journal(toolRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (Read(toolRoot) is { IsFinal: false }) throw new InvalidOperationException("已有未完成的更新事务");
        var lines = new StringBuilder();
        lines.Append(string.Join("\t", Begin, transactionId, fromVersion, toVersion, operationId, backupId)).Append('\n');
        foreach (var process in processes)
            lines.Append(Process).Append('\t').Append(process.Key.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('\t').Append(process.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        var temporary = file + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(lines.ToString());
            output.Write(bytes, 0, bytes.Length);
            output.Flush(true);
        }
        if (File.Exists(file)) File.Delete(file);
        File.Move(temporary, file);
        return Read(toolRoot)!;
    }

    /// <summary>读取工具根内的事务日志；不存在或首行无效时返回空。</summary>
    internal static UpdateJournal? Read(string toolRoot)
    {
        var file = UpdateLayout.Journal(toolRoot);
        if (!File.Exists(file)) return null;
        string text;
        using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(input, new UTF8Encoding(false)))
            text = reader.ReadToEnd();
        var lines = text.Split('\n').ToList();
        // 最后一段没有换行结尾：追加时断电留下的半行，不作数。
        lines.RemoveAt(lines.Count - 1);
        var entries = lines.Where(line => line.Length > 0).Select(line => line.Split('\t')).ToList();
        if (entries.Count == 0 || entries[0].Length != 6 || entries[0][0] != Begin || !UpdateLayout.IsTransactionId(entries[0][1])) return null;
        return new UpdateJournal(file, entries);
    }

    internal void Append(params string[] fields)
    {
        if (fields.Length == 0 || fields.Any(field => field.Any(ch => ch is '\t' or '\r' or '\n'))) throw new ArgumentException("日志字段无效");
        var bytes = new UTF8Encoding(false).GetBytes(string.Join("\t", fields) + "\n");
        using (var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            output.Write(bytes, 0, bytes.Length);
            output.Flush(true);
        }
        entries.Add(fields);
    }
}

/// <summary>最近一次安装的结果，由替换器在事务结束时写出，宿主据此在设置页显示“已更新到的版本”或回退原因。</summary>
internal sealed class UpdateResult
{
    internal const string Committed = "committed", RolledBack = "rolledBack", RollbackFailed = "rollbackFailed", Aborted = "aborted";

    internal UpdateResult(string transactionId, string operationId, string outcome, string fromVersion, string toVersion, string? reason, DateTime at)
    {
        TransactionId = transactionId; OperationId = operationId; Outcome = outcome; FromVersion = fromVersion; ToVersion = toVersion;
        Reason = reason; At = at;
    }

    internal string TransactionId { get; }
    internal string OperationId { get; }
    internal string Outcome { get; }
    internal string FromVersion { get; }
    internal string ToVersion { get; }
    internal string? Reason { get; }
    internal DateTime At { get; }

    internal void Write(string toolRoot)
    {
        var file = UpdateLayout.LastResult(toolRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var text = $"transaction={TransactionId}\noperation={OperationId}\noutcome={Outcome}\nfrom={FromVersion}\nto={ToVersion}\n" +
                   $"reason={Reason ?? ""}\nat={At.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)}\n";
        var temporary = file + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            output.Write(bytes, 0, bytes.Length);
            output.Flush(true);
        }
        if (File.Exists(file)) File.Replace(temporary, file, null);
        else File.Move(temporary, file);
    }

    internal static UpdateResult? Read(string toolRoot)
    {
        var file = UpdateLayout.LastResult(toolRoot);
        if (!File.Exists(file) || new FileInfo(file).Length > 4096) return null;
        var values = File.ReadAllLines(file, new UTF8Encoding(false)).Select(line => line.Split(new[] { '=' }, 2))
            .Where(pair => pair.Length == 2).GroupBy(pair => pair[0]).ToDictionary(group => group.Key, group => group.Last()[1]);
        string Value(string key) => values.TryGetValue(key, out var value) ? value : "";
        if (!UpdateLayout.IsTransactionId(Value("transaction")) ||
            Value("outcome") is not (Committed or RolledBack or RollbackFailed or Aborted) ||
            !DateTime.TryParse(Value("at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return null;
        return new UpdateResult(Value("transaction"), Value("operation"), Value("outcome"), Value("from"), Value("to"),
            Value("reason").Length == 0 ? null : Value("reason"), at);
    }
}
