using System;
using System.IO;
using System.Linq;

namespace MabiTool.PortableUpdate;

/// <summary>替换器与系统交互的部分：等待进程、启动与结束程序、计时。测试用假的实现替换，不启动真实前端。</summary>
internal interface IReplacerEnvironment
{
    /// <summary>等待进程号与启动时间都相符的进程结束；进程已不存在时立即返回 true，超时返回 false。</summary>
    bool WaitForExit(int processId, long startTimeUtcTicks, TimeSpan timeout);
    /// <summary>启动包根里的程序（桌面前端，前端再拉起宿主）。</summary>
    void Launch(string packageRoot);
    /// <summary>结束可执行文件位于包根 <c>components/</c> 下的全部进程（前端、宿主与其 Worker）。</summary>
    void StopPackageProcesses(string packageRoot);
    void Sleep(TimeSpan duration);
    DateTime UtcNow { get; }
}

internal sealed class ReplacerTimeouts
{
    /// <summary>等待前端、宿主退出并释放 <c>runtime/host.lock</c>。</summary>
    internal TimeSpan ProcessExit { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>启动新版本（或回退后的旧版本）到宿主写出健康标记：含前端启动、数据迁移与宿主就绪。</summary>
    internal TimeSpan Health { get; set; } = TimeSpan.FromSeconds(180);
    /// <summary>文件被短暂占用（杀毒软件扫描、索引）时改名的重试次数，间隔从 200 ms 起倍增、单次不超过 3 秒。</summary>
    internal int MoveAttempts { get; set; } = 12;
    internal TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(500);
}

internal enum ReplacerOutcome { None, Committed, RolledBack, RollbackFailed, Aborted }

/// <summary>
/// 解压版整包替换：宿主已校验并解压新包、做好数据库备份并写下事务日志首行后，由新版 Moongate.exe 的副本执行。
/// 只做改名与哈希复核：等待旧进程退出，按日志把 <see cref="UpdateLayout.Items"/> 逐项移入 <c>previous/&lt;事务&gt;</c>、
/// 从暂存目录换入新项，启动新版本并等待其宿主的健康标记；失败时反向改名、请旧宿主恢复数据库后启动旧版本，每次安装只自动回退一次。
/// 每一步先写日志再动文件，中断后由 <see cref="Recover"/> 按日志与实际目录状态回退或收尾。
/// </summary>
internal sealed class UpdateReplacer
{
    private readonly string packageRoot;
    private readonly string toolRoot;
    private readonly IReplacerEnvironment environment;
    private readonly ReplacerTimeouts timeouts;

    internal UpdateReplacer(string packageRoot, string toolRoot, IReplacerEnvironment environment, ReplacerTimeouts? timeouts = null)
    {
        this.packageRoot = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        this.toolRoot = Path.GetFullPath(toolRoot);
        this.environment = environment;
        this.timeouts = timeouts ?? new ReplacerTimeouts();
    }

    /// <summary>回退失败或安装中止时给用户的原因（写入结果文件的代码之外的中文说明）。</summary>
    internal string? FailureReason { get; private set; }

    internal ReplacerOutcome Apply(string transactionId)
    {
        var journal = UpdateJournal.Read(toolRoot);
        if (journal is null || journal.TransactionId != transactionId || journal.IsFinal) return ReplacerOutcome.None;
        if (journal.Phase is not (UpdateJournal.Begin or UpdateJournal.Process)) return Recover();

        var deadline = environment.UtcNow + timeouts.ProcessExit;
        foreach (var process in journal.Processes)
        {
            var remaining = deadline - environment.UtcNow;
            if (remaining < TimeSpan.Zero || !environment.WaitForExit(process.Key, process.Value, remaining))
                return Abort(journal, "update.processTimeout", "旧版本的程序在限定时间内没有退出，未做任何改动。");
        }
        if (!WaitForHostLock(deadline)) return Abort(journal, "update.processTimeout", "后台服务在限定时间内没有释放运行锁，未做任何改动。");

        var staged = UpdateLayout.StagedPackage(toolRoot, transactionId);
        string? problem;
        try
        {
            var identity = ReleaseIdentity.Read(staged);
            problem = identity is null ? ReleaseIdentity.RelativePath :
                identity.DisplayVersion != journal.ToVersion ? "version" : identity.Verify(staged);
            if (problem is null && Directory.GetFileSystemEntries(staged).Select(Path.GetFileName)
                    .Any(name => !UpdateLayout.Items.Contains(name, StringComparer.OrdinalIgnoreCase))) problem = "layout";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { problem = error.Message; }
        if (problem is not null) return Abort(journal, "update.packageInvalid", "暂存的新版本文件与发行身份不符（" + problem + "），未做任何改动。");

        journal.Append(UpdateJournal.Swap);
        var previous = UpdateLayout.Previous(toolRoot, transactionId);
        try
        {
            Directory.CreateDirectory(previous);
            foreach (var item in UpdateLayout.Items)
            {
                var current = Path.Combine(packageRoot, item);
                if (Exists(current)) Move(current, Path.Combine(previous, item));
                Move(Path.Combine(staged, item), current);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return RollBack(journal, "update.replaceFailed", "替换程序文件失败：" + error.Message);
        }
        journal.Append(UpdateJournal.Swapped);
        return LaunchNew(journal);
    }

    /// <summary>
    /// 包根 Moongate.exe 每次启动先调用：没有未完成的事务时返回 <see cref="ReplacerOutcome.None"/>，由调用方照常启动；
    /// 尚未改动文件的事务记为中止；已换入且新版本写过健康标记的事务补记提交；其余按日志与目录状态回退并启动旧版本。
    /// 返回 <see cref="ReplacerOutcome.RolledBack"/> 或 <see cref="ReplacerOutcome.RollbackFailed"/> 时程序已由本方法启动或判定无法启动。
    /// </summary>
    internal ReplacerOutcome Recover()
    {
        var journal = UpdateJournal.Read(toolRoot);
        if (journal is null || journal.IsFinal)
        {
            DeleteFile(UpdateLayout.Restarting(toolRoot));
            return ReplacerOutcome.None;
        }
        if (!journal.Has(UpdateJournal.Swap))
        {
            Abort(journal, "update.interrupted", "安装在替换程序文件之前中断，未做任何改动。", launch: false);
            return ReplacerOutcome.None;
        }
        if (!journal.Has(UpdateJournal.Rollback) && journal.Launched(journal.ToVersion) &&
            File.Exists(UpdateLayout.Healthy(toolRoot, journal.TransactionId, journal.ToVersion)))
        {
            Commit(journal);
            return ReplacerOutcome.None;
        }
        return RollBack(journal, "update.interrupted", "安装过程被中断（断电或程序被结束），已恢复到更新前的版本。");
    }

    private ReplacerOutcome LaunchNew(UpdateJournal journal)
    {
        DeleteFile(UpdateLayout.Restarting(toolRoot));
        journal.Append(UpdateJournal.Launch, journal.ToVersion);
        if (StartAndWaitHealthy(journal, journal.ToVersion))
        {
            Commit(journal);
            return ReplacerOutcome.Committed;
        }
        environment.StopPackageProcesses(packageRoot);
        return RollBack(journal, "update.healthTimeout", "新版本在限定时间内没有正常启动，已恢复到更新前的版本。");
    }

    private void Commit(UpdateJournal journal)
    {
        journal.Append(UpdateJournal.Committed);
        new UpdateResult(journal.TransactionId, journal.OperationId, UpdateResult.Committed, journal.FromVersion, journal.ToVersion, null,
            environment.UtcNow).Write(toolRoot);
    }

    private ReplacerOutcome Abort(UpdateJournal journal, string code, string reason, bool launch = true)
    {
        journal.Append(UpdateJournal.Aborted, code);
        new UpdateResult(journal.TransactionId, journal.OperationId, UpdateResult.Aborted, journal.FromVersion, journal.ToVersion, code,
            environment.UtcNow).Write(toolRoot);
        FailureReason = reason;
        DeleteFile(UpdateLayout.Restarting(toolRoot));
        if (launch) environment.Launch(packageRoot);
        return ReplacerOutcome.Aborted;
    }

    private ReplacerOutcome RollBack(UpdateJournal journal, string code, string reason)
    {
        if (!journal.Has(UpdateJournal.Rollback)) journal.Append(UpdateJournal.Rollback, code);
        else code = journal.Entries.First(entry => entry[0] == UpdateJournal.Rollback).ElementAtOrDefault(1) ?? code;
        var staged = UpdateLayout.StagedPackage(toolRoot, journal.TransactionId);
        var previous = UpdateLayout.Previous(toolRoot, journal.TransactionId);
        try
        {
            foreach (var item in Enumerable.Reverse(UpdateLayout.Items))
            {
                var current = Path.Combine(packageRoot, item);
                var saved = Path.Combine(previous, item);
                // 暂存目录里没有该项说明新版本已换入包根：先移回暂存，再把旧版本移回包根。
                if (!Exists(Path.Combine(staged, item)) && Exists(current))
                {
                    Directory.CreateDirectory(staged);
                    Move(current, Path.Combine(staged, item));
                }
                if (Exists(saved) && !Exists(current)) Move(saved, current);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return RollbackFailed(journal, code, reason + "\n恢复旧版本文件时出错：" + error.Message);
        }
        // 新版本启动过就可能已迁移数据库：请旧宿主启动时先恢复安装前的备份。
        if (journal.Launched(journal.ToVersion) && !journal.Has(UpdateJournal.DatabaseRestored) && !journal.DatabaseRestorePending)
            journal.Append(UpdateJournal.RestoreDatabase);
        DeleteFile(UpdateLayout.Restarting(toolRoot));
        journal.Append(UpdateJournal.Launch, journal.FromVersion);
        if (!StartAndWaitHealthy(journal, journal.FromVersion))
            return RollbackFailed(journal, code, reason + "\n恢复后的旧版本也没有正常启动。");
        journal.Append(UpdateJournal.RolledBack, code);
        new UpdateResult(journal.TransactionId, journal.OperationId, UpdateResult.RolledBack, journal.FromVersion, journal.ToVersion, code,
            environment.UtcNow).Write(toolRoot);
        FailureReason = reason;
        return ReplacerOutcome.RolledBack;
    }

    private ReplacerOutcome RollbackFailed(UpdateJournal journal, string code, string reason)
    {
        journal.Append(UpdateJournal.RollbackFailed, code);
        new UpdateResult(journal.TransactionId, journal.OperationId, UpdateResult.RollbackFailed, journal.FromVersion, journal.ToVersion, code,
            environment.UtcNow).Write(toolRoot);
        FailureReason = reason;
        return ReplacerOutcome.RollbackFailed;
    }

    private bool StartAndWaitHealthy(UpdateJournal journal, string version)
    {
        var marker = UpdateLayout.Healthy(toolRoot, journal.TransactionId, version);
        environment.Launch(packageRoot);
        var deadline = environment.UtcNow + timeouts.Health;
        while (true)
        {
            if (File.Exists(marker)) return true;
            if (environment.UtcNow >= deadline) return false;
            environment.Sleep(timeouts.Poll);
        }
    }

    private bool WaitForHostLock(DateTime deadline)
    {
        var hostLock = Path.Combine(toolRoot, "runtime", "host.lock");
        while (true)
        {
            try
            {
                if (!File.Exists(hostLock)) return true;
                using (new FileStream(hostLock, FileMode.Open, FileAccess.Read, FileShare.None)) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (environment.UtcNow >= deadline) return false;
            environment.Sleep(timeouts.Poll);
        }
    }

    private void Move(string source, string destination)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
                return;
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < timeouts.MoveAttempts &&
                                          !Exists(destination))
            {
                environment.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 3000));
            }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
