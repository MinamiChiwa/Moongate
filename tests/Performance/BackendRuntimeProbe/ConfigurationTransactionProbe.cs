using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

internal static class ConfigurationTransactionProbe
{
    public static async Task RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 5)
            throw new ArgumentException("参数：--configuration-transactions repository report environmentJson sampleCount temporaryRoot；需 Windows x64");
        var repository = Path.GetFullPath(args[0]); var report = Path.GetFullPath(args[1]);
        var environmentPath = Path.GetFullPath(args[2]);
        if (!Path.IsPathFullyQualified(args[0]) || !File.Exists(Path.Combine(repository, "MabiTool.slnx")) ||
            !report.StartsWith(Path.Combine(repository, "artifacts", "reports") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(args[3], out var sampleCount) || sampleCount is < 1 or > 1000)
            throw new ArgumentException("路径或配置事务样本数量无效");
        TemporaryWorkspace.EnsureNoLinks(report); TemporaryWorkspace.EnsureNoLinks(environmentPath);
        JsonElement environment;
        using (var environmentDocument = JsonDocument.Parse(await File.ReadAllTextAsync(environmentPath)))
            environment = environmentDocument.RootElement.Clone();
        using var workspace = TemporaryWorkspace.Create(args[4]);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        var portableRoot = Path.Combine(workspace.DirectoryPath, "configuration-host");
        var samples = new List<ConfigurationSample>(); var timingGate = new object();
        List<SqliteWriteTiming>? target = null; var droppedTimings = 0;
        long pendingOperations = 0, pendingConfigurations = 0; int remainingActivities = 0;
        string? setupFailure = null; bool storeReleased = false, leaseReleased = false;
        string sqlitePath = ProbeDataPaths.FromPortableRoot(portableRoot).Sqlite;
        string? journalMode = null; long? synchronous = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            try
            {
                using (var lease = DataRootLease.Open(new(Path.Combine(portableRoot, "bootstrap"), true, PortableRoot: portableRoot)))
                {
                    sqlitePath = lease.Paths.Database;
                    await using (var store = SqliteStore.Open(lease, mock: true))
                    {
                        var operations = new OperationStore(store);
                        store.WriteTimingObserver = timing =>
                        {
                            lock (timingGate)
                            {
                                if (target is null) return;
                                if (target.Count < 4) target.Add(timing); else droppedTimings++;
                            }
                        };
                        var policy = await store.WriteAsync(tx => new SqlitePolicy((string?)tx.Scalar("PRAGMA journal_mode"),
                            Convert.ToInt64(tx.Scalar("PRAGMA synchronous"))), cancellation.Token);
                        journalMode = policy.JournalMode; synchronous = policy.Synchronous;
                        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase) || synchronous != 2)
                            throw new InvalidOperationException("实际 SQLite writer 策略不是 WAL/FULL");
                        Console.WriteLine($"配置事务受理测量：{sampleCount} 次，SQLite FULL，预算 {SqliteStore.TransactionBudget.TotalMilliseconds} ms");
                        try
                        {
                            for (var index = 0; index < sampleCount && !cancellation.IsCancellationRequested; index++)
                            {
                                var admissionTimings = new List<SqliteWriteTiming>(); var releaseTimings = new List<SqliteWriteTiming>();
                                OperationAdmission? admitted = null; string? failure = null, releaseFailure = null;
                                var released = false;
                                lock (timingGate) target = admissionTimings;
                                var started = Stopwatch.GetTimestamp();
                                double admissionMs;
                                try
                                {
                                    admitted = await operations.AdmitConfigurationAsync("configuration-probe", "music-library", true, new(false),
                                        "configuration-probe-" + index, "\"module-music-library-0\"", cancellationToken: cancellation.Token);
                                    admissionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                                }
                                catch (Exception error)
                                {
                                    admissionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; failure = FailureCode(error);
                                }
                                finally { lock (timingGate) target = null; }
                                if (admitted is not null)
                                {
                                    lock (timingGate) target = releaseTimings;
                                    try
                                    {
                                        var cancelled = await operations.CancelAsync(admitted.Snapshot.Id, "configuration-probe", true, "probe-release", CancellationToken.None);
                                        released = cancelled.State.KnownValue == OperationState.Cancelled;
                                    }
                                    catch (Exception error) { releaseFailure = FailureCode(error); }
                                    finally { lock (timingGate) target = null; }
                                }
                                samples.Add(new(index, index == 0, admissionMs, failure is null ? "succeeded" : failure,
                                    failure, admitted?.Snapshot.Id, released, releaseFailure, admissionTimings.ToArray(), releaseTimings.ToArray()));
                                if ((index + 1) % 100 == 0 || index + 1 == sampleCount) Console.WriteLine($"配置事务样本完成 {index + 1}/{sampleCount}");
                                if (cancellation.IsCancellationRequested || admitted is not null && !released) break;
                            }
                        }
                        finally { store.WriteTimingObserver = null; lock (timingGate) target = null; }
                        pendingOperations = store.Read(connection => { using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM core_operations WHERE terminal=0"; return Convert.ToInt64(command.ExecuteScalar()); });
                        pendingConfigurations = store.Read(connection => { using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM core_module_configuration WHERE pending_operation IS NOT NULL"; return Convert.ToInt64(command.ExecuteScalar()); });
                        remainingActivities = store.Activities.Snapshot().Activities.Count;
                    }
                    storeReleased = true;
                }
                leaseReleased = true;
            }
            catch (Exception error) { setupFailure = FailureCode(error); }
            workspace.Dispose();
            var timings = samples.SelectMany(sample => sample.AdmissionTransactions).ToArray();
            var output = new
            {
                schemaVersion = 1, evidenceKind = "configuration-transaction-performance", measuredAtUtc = DateTimeOffset.UtcNow,
                environment, runtime = RuntimeInformation.FrameworkDescription,
                assemblies = new { infrastructure = await IdentityAsync(typeof(SqliteStore).Assembly), host = await EventHostIdentity.CaptureAsync(), probe = await IdentityAsync(typeof(ConfigurationTransactionProbe).Assembly) },
                paths = new { temporaryParent = workspace.ParentPath, temporaryDirectory = workspace.DirectoryPath, portableRoot, sqlitePath },
                protocol = new { fullProtocol = false, requestedSamples = sampleCount, completedSamples = samples.Count,
                    moduleId = "music-library", defaultEnabled = true, requestedEnabled = false, configurationRevision = "0",
                    journalMode, synchronous, synchronousName = synchronous == 2 ? "FULL" : "unexpected",
                    transactionBudgetMs = SqliteStore.TransactionBudget.TotalMilliseconds,
                    timingBoundary = "before AdmitConfigurationAsync invocation through returned result or exception; CancelAsync and cleanup excluded",
                    coldSampleDefinition = "first configuration admission in this process; SQLite writer policy preflight is outside timing and does not encode operation snapshots",
                    sqlitePolicyPreflight = new { connection = "actual SqliteStore writer", includedInAdmissionTiming = false,
                        traceObserverInstalled = true, configurationSampleTarget = "null", operationSnapshotsEncoded = 0 },
                    modulesExecuted = 0, systemInputCalls = 0, cancelled = cancellation.IsCancellationRequested },
                coldSample = samples.FirstOrDefault(), samples,
                admissionMs = Summarize(samples.Select(sample => sample.AdmissionMs)),
                warmAdmissionMs = Summarize(samples.Skip(1).Select(sample => sample.AdmissionMs)),
                succeededAdmissionMs = Summarize(samples.Where(sample => sample.FailureCode is null).Select(sample => sample.AdmissionMs)),
                transactionStages = new { queueWaitMs = Summarize(timings.Select(t => t.QueueWaitMs)), beginMs = Summarize(timings.Select(t => t.BeginMs)),
                    callbackMs = Summarize(timings.Select(t => t.CallbackMs)), sqlPrepareMs = Summarize(timings.Select(t => t.SqlPrepareMs)),
                    sqlExecuteMs = Summarize(timings.Select(t => t.SqlExecuteMs)), operationEncodingMs = Summarize(timings.Select(t => t.OperationEncodingMs)),
                    commitMs = Summarize(timings.Select(t => t.CommitMs)) },
                failures = samples.Where(sample => sample.FailureCode is not null || sample.ReleaseFailureCode is not null).Select(sample => new { sample.Index, sample.FailureCode, sample.ReleaseFailureCode }).ToArray(),
                setupFailure, droppedTimings, elapsedMs = elapsed.Elapsed.TotalMilliseconds,
                cleanup = new { storeReleased, leaseReleased, pendingOperations, pendingConfigurations, remainingActivities,
                    temporaryDirectoryRemoved = !Directory.Exists(workspace.DirectoryPath), spawnedProcesses = 0 }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(output, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
            Console.WriteLine(report);
            if (setupFailure is not null) throw new InvalidOperationException("配置事务探针未完成：" + setupFailure);
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static Distribution? Summarize(IEnumerable<double> samples)
    {
        var values = samples.ToArray(); return values.Length == 0 ? null : Distribution.From(values);
    }
    private static async Task<EventHostIdentity> IdentityAsync(Assembly assembly) => new(assembly.Location,
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location))).ToLowerInvariant());
    private static string FailureCode(Exception error) => error switch
    {
        StorageException storage => storage.Code, OperationException operation => operation.Code,
        OperationCanceledException => "cancelled", _ => error.GetType().Name
    };
    private sealed record ConfigurationSample(int Index, bool Cold, double AdmissionMs, string Outcome, string? FailureCode,
        string? OperationId, bool OperationReleased, string? ReleaseFailureCode, SqliteWriteTiming[] AdmissionTransactions, SqliteWriteTiming[] ReleaseTransactions);
    private sealed record SqlitePolicy(string? JournalMode, long Synchronous);
}
