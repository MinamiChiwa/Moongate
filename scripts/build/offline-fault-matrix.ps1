param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N')
$resultsRoot = Join-Path $repositoryRoot "artifacts/test-results/offline-fault-matrix/$runId"
$reportRoot = Join-Path $repositoryRoot 'artifacts/reports'
[IO.Directory]::CreateDirectory($resultsRoot) | Out-Null
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null

# 每个条件对应本次实际执行的行为测试；TRX 缺少结果、跳过或失败均不能成为通过证据。
$runs = @(
    @{ Id = 'host'; Project = 'tests/Contracts/MabiTool.Host.Tests/MabiTool.Host.Tests.csproj'; Classes = @(
        'OfflineFaultMatrixTests', 'ObservationWorkerTests', 'SharedCombatCaptureTests', 'ColorWorkerTests',
        'PersistenceTests', 'EventStreamTests', 'GameBindingPreparationTests', 'RoomInputApiTests',
        'RoomSessionManagerTests', 'UpdateDownloadExecutionTests', 'InstallTransactionTests', 'DataSnapshotRestoreTests',
        'ColorGamePortTests.CancelOrCrashWhileHeldReleasesMouseAndRejectsOldInput',
        'ColorGamePortTests.LatePortCompletionIsDroppedBeforeNextRequest',
        'ColorRunOperationsTests.WorkerCrashReleasesLeaseAndDoesNotReplayTheRun',
        'ColorRunOperationsTests.CrashRecoveryClearsInteractionAndCurrentEvidenceWithoutStartingWorker') },
    @{ Id = 'runtime'; Project = 'tests/Domain/MabiTool.Runtime.Tests/MabiTool.Runtime.Tests.csproj'; Classes = @(
        'GameSessionBindingTests', 'GameInputBrokerTests', 'ActivityBarrierTests', 'ResourceLeaseTests') },
    @{ Id = 'rooms'; Project = 'tests/Domain/MabiTool.Rooms.Tests/MabiTool.Rooms.Tests.csproj'; Classes = @(
        'ActionSchedulerTests', 'RoomServerObservationTests') },
    @{ Id = 'updates'; Project = 'tests/Providers/MabiTool.Updates.Tests/MabiTool.Updates.Tests.csproj'; Classes = @(
        'ResumableAssetDownloaderTests', 'FileTreeStagerTests', 'ReleaseDeliveryIntegrationTests') }
)
$scenarios = @(
    @{ Id = 'network-loss'; Name = '断网与下载恢复'; Tests = @('RoomSessionManagerTests.', 'ResumableAssetDownloaderTests.', 'UpdateDownloadExecutionTests.'); Spec = 'room-coordination/断线恢复不补发动作；data-delivery/下载续传与取消可恢复' },
    @{ Id = 'worker-crash'; Name = 'Worker 崩溃、迟到消息及资源释放'; Tests = @('OfflineFaultMatrixTests.', 'ColorWorkerTests.', 'ObservationWorkerTests.', 'SharedCombatCaptureTests.', 'ColorGamePortTests.CancelOrCrashWhileHeld', 'ColorGamePortTests.LatePortCompletion', 'ColorRunOperationsTests.WorkerCrashReleasesLease'); Spec = 'module-runtime/Worker 故障恢复；game-access/授权共享被动捕获' },
    @{ Id = 'backend-restart'; Name = '后端重启与持久终态恢复'; Tests = @('PersistenceTests.', 'EventStreamTests.Restart', 'InstallTransactionTests.', 'DataSnapshotRestoreTests.', 'ColorRunOperationsTests.CrashRecoveryClearsInteraction'); Spec = 'backend-contracts/异步操作与效果区分；data-delivery/事务更新与健康确认' },
    @{ Id = 'stale-revision'; Name = '旧修订与活动仲裁'; Tests = @('GameBindingPreparationTests.', 'RoomInputApiTests.', 'ActivityBarrierTests.', 'ResourceLeaseTests.'); Spec = 'backend-contracts/幂等与修订控制；room-coordination/准备提交与去重' },
    @{ Id = 'expired-cursor'; Name = '过期游标、多客户端重同步及背压'; Tests = @('EventStreamTests.'); Spec = 'backend-contracts/快照与事件恢复' },
    @{ Id = 'window-restart'; Name = '合成游戏窗口重启与旧输入隔离'; Tests = @('GameSessionBindingTests.', 'GameInputBrokerTests.', 'RoomInputApiTests.RevocationAndWindowRestart', 'ActionSchedulerTests.'); Spec = 'game-access/绑定身份与失效；room-coordination/单调校时与质量' }
)
$allResults = [Collections.Generic.List[object]]::new()
$execution = [Collections.Generic.List[object]]::new()
$pythonIdentity = (& python -c 'import sys; print(sys.executable); print(sys.version)' | Out-String).Trim()
$startingProcesses = @(Get-Process -Name dotnet,testhost,python,pythonw -ErrorAction SilentlyContinue | ForEach-Object {
    @{ id = $_.Id; name = $_.ProcessName; cpuSeconds = $_.CPU; workingBytes = $_.WorkingSet64; startedAt = $_.StartTime.ToUniversalTime().ToString('O') }
})
Push-Location $repositoryRoot
try {
    foreach ($run in $runs) {
        $filter = ($run.Classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
        $arguments = @('test', $run.Project, '--configuration', $Configuration, '--no-restore', '-maxcpucount:1', '-nodeReuse:false',
            '--filter', $filter, '--logger', "trx;LogFileName=$($run.Id).trx", '--logger', 'console;verbosity=normal',
            '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'none', '--results-directory', $resultsRoot)
        if ($NoBuild) { $arguments += '--no-build' }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        & dotnet @arguments
        $code = $LASTEXITCODE
        $timer.Stop()
        $trx = Join-Path $resultsRoot "$($run.Id).trx"
        $assemblyName = [IO.Path]::GetFileNameWithoutExtension($run.Project)
        $assembly = Join-Path $repositoryRoot "artifacts/build/bin/$assemblyName/$($Configuration.ToLowerInvariant())/$assemblyName.dll"
        $assemblyHash = $(if (Test-Path -LiteralPath $assembly) { (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null })
        $execution.Add(@{ project = $run.Project; exitCode = $code; elapsedMs = $timer.Elapsed.TotalMilliseconds;
            command = 'dotnet ' + ($arguments -join ' '); trx = [IO.Path]::GetRelativePath($repositoryRoot, $trx);
            testAssembly = [IO.Path]::GetRelativePath($repositoryRoot, $assembly); testAssemblySha256 = $assemblyHash })
        if (Test-Path -LiteralPath $trx) {
            [xml]$document = [IO.File]::ReadAllText($trx)
            foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
                $allResults.Add(@{ name = [string]$result.testName; outcome = [string]$result.outcome;
                    duration = [string]$result.duration; project = $run.Project; trx = [IO.Path]::GetRelativePath($repositoryRoot, $trx) })
            }
        }
    }
    $matrix = foreach ($scenario in $scenarios) {
        $matched = @($allResults | Where-Object {
            $testName = $_.name
            @($scenario.Tests | Where-Object { $testName.Contains($_, [StringComparison]::Ordinal) }).Count -gt 0
        })
        $missing = @($scenario.Tests | Where-Object {
            $expected = $_
            @($matched | Where-Object { $_.name.Contains($expected, [StringComparison]::Ordinal) }).Count -eq 0
        })
        $failed = @($matched | Where-Object { $_.outcome -ne 'Passed' })
        @{ id = $scenario.Id; name = $scenario.Name; specification = $scenario.Spec;
            status = $(if ($matched.Count -gt 0 -and $missing.Count -eq 0 -and $failed.Count -eq 0) { 'verified-offline' } else { 'incomplete' });
            passed = @($matched | Where-Object { $_.outcome -eq 'Passed' }).Count;
            missingEvidence = $missing; tests = $matched }
    }
    $successful = @($execution | Where-Object { $_.exitCode -ne 0 }).Count -eq 0 -and
        @($matrix | Where-Object { $_.status -ne 'verified-offline' }).Count -eq 0
    $sdkVersion = (& dotnet --version | Out-String).Trim()
    $report = @{ schemaVersion = 1; evidenceKind = 'offline-fault-matrix'; runId = $runId; checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O');
        configuration = $Configuration; environment = @{ os = [Runtime.InteropServices.RuntimeInformation]::OSDescription;
            architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString(); logicalProcessors = [Environment]::ProcessorCount; dotnetSdk = $sdkVersion;
            python = $pythonIdentity; processesAtStart = $startingProcesses };
        status = $(if ($successful) { 'verified-offline' } else { 'incomplete' });
        uniqueTests = $allResults.Count; passed = @($allResults | Where-Object { $_.outcome -eq 'Passed' }).Count;
        executions = @($execution); scenarios = @($matrix) }
    $path = Join-Path $reportRoot 'offline-fault-matrix.json'
    $json = $report | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText((Join-Path $reportRoot "offline-fault-matrix.$runId.json"), $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
    Write-Output "离线故障矩阵：$($report.status)，$($report.passed)/$($report.uniqueTests)；报告：$path"
    if (-not $successful) { throw '离线故障矩阵存在失败、跳过或缺失证据，详情见报告。' }
} finally { Pop-Location }
