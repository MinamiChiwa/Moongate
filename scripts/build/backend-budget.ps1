param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(1, 3600)][int]$IdleSeconds = 600,
    [ValidateRange(1, 100)][int]$ColdCount = 30,
    [ValidateRange(1, 10000)][int]$SampleCount = 1000,
    [ValidateRange(1, 10000)][int]$EventCount = 1000,
    [string]$ReportName = 'backend-runtime-budget.json',
    [string]$HostAssembly,
    [string]$TemporaryRoot,
    [switch]$ConfigurationTransactions,
    [switch]$CommandAdmissionOnly,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($ConfigurationTransactions) {
    if ($SampleCount -gt 1000) { throw '配置事务模式最多 1000 个样本' }
    if ($PSBoundParameters.ContainsKey('HostAssembly')) { throw '配置事务模式使用探针实际加载程序集，不接受 HostAssembly' }
    if (-not $PSBoundParameters.ContainsKey('ReportName')) { $ReportName = 'backend-configuration-transactions.json' }
}
if ($ReportName -notmatch '^[a-zA-Z0-9._-]+\.json$') { throw '报告名必须是单个 JSON 文件名' }
$project = Join-Path $repositoryRoot 'tests/Performance/BackendRuntimeProbe/MabiTool.BackendRuntimeProbe.csproj'
$reportRoot = Join-Path $repositoryRoot 'artifacts/reports'
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$flavor = $Configuration.ToLowerInvariant()
$probe = Join-Path $repositoryRoot "artifacts/build/bin/MabiTool.BackendRuntimeProbe/$flavor/MabiTool.BackendRuntimeProbe.dll"
if (-not $HostAssembly) { $HostAssembly = Join-Path $repositoryRoot "artifacts/build/bin/MabiTool.Host/$flavor/MabiTool.Host.dll" }
if (-not $TemporaryRoot) { $TemporaryRoot = Join-Path $repositoryRoot 'artifacts/tmp' }
$HostAssembly = [IO.Path]::GetFullPath($HostAssembly)
$TemporaryRoot = [IO.Path]::GetFullPath($TemporaryRoot)
function Assert-NoPathLinks([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            if ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "测量路径包含链接：$candidate" }
        }
        $candidate = [IO.Path]::GetDirectoryName($candidate)
    }
}
Assert-NoPathLinks $HostAssembly
Assert-NoPathLinks $TemporaryRoot
$environmentPath = Join-Path $reportRoot ($ReportName -replace '\.json$', '.environment.json')
$physicalDisks = @(Get-PhysicalDisk)
$driveMappings = foreach ($pathRecord in @(
    @{ purpose = 'repository'; path = $repositoryRoot }, @{ purpose = 'host'; path = $HostAssembly },
    @{ purpose = 'probe'; path = $probe }, @{ purpose = 'temporary-data'; path = $TemporaryRoot }
)) {
    $drive = [IO.Path]::GetPathRoot($pathRecord.path)
    $mapping = [ordered]@{ purpose = $pathRecord.purpose; path = $pathRecord.path; volumeRoot = $drive; status = 'unresolved' }
    try {
        if ($drive -notmatch '^([A-Za-z]):\\$') { throw '路径不是本机盘符卷' }
        $partitions = @(Get-Partition -DriveLetter $Matches[1])
        $mapping.disks = @($partitions | ForEach-Object {
            $disk = Get-Disk -Number $_.DiskNumber
            $physical = @($physicalDisks | Where-Object {
                ($disk.UniqueId -and $_.UniqueId -eq $disk.UniqueId) -or
                ($disk.SerialNumber -and $_.SerialNumber -and $_.SerialNumber.Trim() -eq $disk.SerialNumber.Trim())
            })
            $media = if ($physical.Count -eq 1) { [string]$physical[0].MediaType } else { 'Unknown' }
            $mediaEvidence = if ($physical.Count -eq 1) { 'Get-PhysicalDisk identity matched Get-Disk' } else { 'physical-media identity was not uniquely resolved' }
            [ordered]@{ diskNumber = $disk.Number; model = $disk.FriendlyName; busType = [string]$disk.BusType; mediaType = $media; mediaTypeEvidence = $mediaEvidence; sizeBytes = $disk.Size }
        })
        if ($mapping.disks.Count -eq 0) { throw '未取得卷对应的物理盘' }
        $mapping.status = 'resolved'
    } catch { $mapping.reason = $_.Exception.Message }
    $mapping
}
$environmentRecord = [ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    totalPhysicalMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
    disks = @($physicalDisks | Select-Object FriendlyName, MediaType, BusType, Size)
    driveMappings = @($driveMappings)
    activePowerScheme = (& powercfg /getactivescheme | Out-String).Trim()
    repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
    workingTreeChanges = @(& git -C $repositoryRoot status --short)
}
$environmentRecord | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $environmentPath -Encoding utf8NoBOM
Push-Location $repositoryRoot
try {
    if (-not $SkipBuild) {
        dotnet restore $project --locked-mode --disable-parallel -maxcpucount:1 -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { throw '性能探针锁定还原失败' }
        dotnet build $project --configuration $Configuration --no-restore -maxcpucount:1 -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { throw '性能探针构建失败' }
    }
    if ($CommandAdmissionOnly) {
        dotnet $probe '--command-admission' $repositoryRoot $HostAssembly $SampleCount $TemporaryRoot
    } elseif ($ConfigurationTransactions) {
        dotnet $probe '--configuration-transactions' $repositoryRoot (Join-Path $reportRoot $ReportName) $environmentPath $SampleCount $TemporaryRoot
    } else {
        dotnet $probe $repositoryRoot (Join-Path $reportRoot $ReportName) $HostAssembly $environmentPath $IdleSeconds $ColdCount $SampleCount $EventCount $TemporaryRoot
    }
    if ($LASTEXITCODE -ne 0) { throw '后端性能测量失败' }
} finally { Pop-Location }
