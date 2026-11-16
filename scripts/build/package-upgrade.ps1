[CmdletBinding(DefaultParameterSetName = 'ThreeVersions')]
param(
    [Parameter(Mandatory, ParameterSetName = 'ThreeVersions')][string]$Version010,
    [Parameter(Mandatory, ParameterSetName = 'ThreeVersions')][string]$Version020,
    [Parameter(Mandatory, ParameterSetName = 'ThreeVersions')][string]$Version030,
    [Parameter(Mandatory, ParameterSetName = 'PackagePair')][string]$BaselinePackage,
    [Parameter(Mandatory, ParameterSetName = 'PackagePair')][string]$CandidatePackage,
    [Parameter(ParameterSetName = 'PackagePair')][switch]$PublicApply,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw '输出目录必须尚未存在' }
$packages = @{}
$entries = if ($PSCmdlet.ParameterSetName -eq 'PackagePair') {
    @(@('baseline', $BaselinePackage), @('candidate', $CandidatePackage))
} else { @(@('0.1.0', $Version010), @('0.2.0', $Version020), @('0.3.0', $Version030)) }
$metadata = @{}
foreach ($entry in $entries) {
    $package = (Resolve-Path -LiteralPath $entry[1]).Path
    if (!(Test-Path -LiteralPath (Join-Path $package 'release-input.json')) -or
        !(Test-Path -LiteralPath (Join-Path $package 'components/dotnet-runtime/dotnet.exe'))) { throw '需要包含私有运行时及 release-input.json 的实际目录包' }
    $packages[$entry[0]] = $package
    $packageInput = Get-Content -LiteralPath (Join-Path $package 'release-input.json') -Raw | ConvertFrom-Json
    $parsedVersion = $null
    if (-not $packageInput.releaseId -or $packageInput.releaseSequence -lt 1 -or -not [Version]::TryParse([string]$packageInput.version, [ref]$parsedVersion)) { throw '目录包发行身份无效' }
    if ($PSCmdlet.ParameterSetName -eq 'ThreeVersions' -and $packageInput.version -ne $entry[0]) { throw '目录包实际版本与三版本参数不匹配' }
    $metadata[$entry[0]] = $packageInput
}
if ($PSCmdlet.ParameterSetName -eq 'PackagePair') {
    $baseline = $metadata['baseline']; $candidate = $metadata['candidate']
    if ($packages['baseline'] -eq $packages['candidate'] -or $baseline.releaseId -eq $candidate.releaseId -or
        [Version]$candidate.version -le [Version]$baseline.version -or $candidate.releaseSequence -le $baseline.releaseSequence -or
        $candidate.productId -ne $baseline.productId -or $candidate.channel -ne $baseline.channel -or
        $candidate.architecture -ne $baseline.architecture -or $candidate.runtimeMode -ne $baseline.runtimeMode) { throw '基础包和候选包必须为身份一致且版本、序列递增的独立发行' }
    if ($PublicApply) {
        $oldUpdater = @($baseline.components | Where-Object componentId -eq 'updater')
        if ($oldUpdater.Count -ne 1 -or [Version]$candidate.minUpdaterVersion -le [Version]$oldUpdater[0].version) {
            throw '公共引导验收要求候选最低 updater 版本高于冻结基线的实际 updater 版本'
        }
    }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$inputFile = Join-Path $output 'package-upgrade-input.json'
$packages | ConvertTo-Json | Set-Content -LiteralPath $inputFile -Encoding utf8
$selectedSources = Join-Path $output 'package-upgrade-only.targets'
@'
<Project>
  <ItemGroup Condition="'$(MSBuildProjectName)' == 'MabiTool.Host.Tests'">
    <Compile Remove="**/*.cs" />
    <Compile Include="$(MSBuildProjectDirectory)/ManagedInstallFixture.cs" />
    <Compile Include="$(MSBuildProjectDirectory)/ManagedInstallEvidence.cs" />
    <Compile Include="$(MSBuildProjectDirectory)/PackagedReleaseInstallTests.cs" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $selectedSources -Encoding utf8
$oldInput = $env:MABITOOL_PACKAGE_UPGRADE_INPUT
$oldReport = $env:MABITOOL_PACKAGE_UPGRADE_REPORT
$oldPublicReport = $env:MABITOOL_PACKAGE_PUBLIC_APPLY_REPORT
try {
    $env:MABITOOL_PACKAGE_UPGRADE_INPUT = $inputFile
    $env:MABITOOL_PACKAGE_UPGRADE_REPORT = Join-Path $output 'package-upgrade-metrics.json'
    $env:MABITOOL_PACKAGE_PUBLIC_APPLY_REPORT = if ($PublicApply) { Join-Path $output 'package-public-apply-metrics.json' } else { $null }
    $testFilter = if ($PublicApply) { 'FullyQualifiedName~PackagedUpdaterBootstrapTests' } else { 'FullyQualifiedName~PackagedReleaseInstallTests' }
    $testBuildRoot = Join-Path $output 'build'
    & dotnet test (Join-Path $repository 'tests/Contracts/MabiTool.Host.Tests/MabiTool.Host.Tests.csproj') -c Release "-p:ArtifactsPath=$testBuildRoot" "-p:CustomAfterMicrosoftCommonTargets=$selectedSources" --filter $testFilter --logger trx --results-directory $output -maxcpucount:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "实际包升级验证失败，退出码 $LASTEXITCODE" }
    $reportFile = if ($PublicApply) { $env:MABITOOL_PACKAGE_PUBLIC_APPLY_REPORT } else { $env:MABITOOL_PACKAGE_UPGRADE_REPORT }
    if (!(Test-Path -LiteralPath $reportFile)) { throw '实际包升级测试没有生成测量记录' }
    Get-Content -LiteralPath $reportFile
    if (-not $PublicApply) {
        $rollbackReport = Join-Path $output 'package-rollback-metrics.json'
        if (!(Test-Path -LiteralPath $rollbackReport -PathType Leaf)) { throw '实际包健康失败恢复测试没有生成独立测量记录' }
        Get-Content -LiteralPath $rollbackReport
    }
}
finally {
    $env:MABITOOL_PACKAGE_UPGRADE_INPUT = $oldInput
    $env:MABITOOL_PACKAGE_UPGRADE_REPORT = $oldReport
    $env:MABITOOL_PACKAGE_PUBLIC_APPLY_REPORT = $oldPublicReport
}
