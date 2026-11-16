param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$ReleaseManifest,
    [Parameter(Mandatory = $true)][string]$ChannelIndex,
    [Parameter(Mandatory = $true)][string]$TrustRoot,
    [Parameter(Mandatory = $true)][string]$InstallationRoot,
    # 默认放在安装根下的 bootstrap 与 data：工具的全部内容位于同一个文件夹。
    [string]$BootstrapDirectory,
    [string]$DataRoot,
    [Parameter(Mandatory = $true)][string]$GithubRepository,
    [string]$Channel = 'stable',
    [string[]]$OptionalComponents = @(),
    [string]$RequestFile
)
$ErrorActionPreference = 'Stop'

function Get-InitialInstallAbsolutePath([string]$Value) {
    if ($Value -notmatch '^[a-zA-Z]:[\\/]') { throw '初装参数必须使用本地磁盘绝对路径' }
    return [IO.Path]::GetFullPath($Value).TrimEnd('\', '/')
}

$package = Get-InitialInstallAbsolutePath $PackageRoot
$installation = Get-InitialInstallAbsolutePath $InstallationRoot
if (-not $BootstrapDirectory) { $BootstrapDirectory = Join-Path $installation 'bootstrap' }
if (-not $DataRoot) { $DataRoot = Join-Path $installation 'data' }
# 请求文件默认写在安装根的 update-state 下：首次安装的输入、日志与暂存都不落在安装根之外。
if (-not $RequestFile) { $RequestFile = Join-Path $installation 'update-state\initial-install-request.json' }
$requestPath = Get-InitialInstallAbsolutePath $RequestFile
$optional = @($OptionalComponents)
[Array]::Sort($optional, [StringComparer]::Ordinal)
$request = [ordered]@{
    schemaVersion = 1
    packageRoot = $package
    releaseManifestPath = Get-InitialInstallAbsolutePath $ReleaseManifest
    channelIndexPath = Get-InitialInstallAbsolutePath $ChannelIndex
    trustRootPath = Get-InitialInstallAbsolutePath $TrustRoot
    installationRoot = $installation
    bootstrapDirectory = Get-InitialInstallAbsolutePath $BootstrapDirectory
    dataRoot = Get-InitialInstallAbsolutePath $DataRoot
    githubRepository = $GithubRepository
    channel = $Channel
    optionalComponents = $optional
}
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($request | ConvertTo-Json -Depth 16 -Compress))
if (Test-Path -LiteralPath $requestPath) {
    if (-not (Test-Path -LiteralPath $requestPath -PathType Leaf) -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($requestPath)) -ne [Convert]::ToBase64String($bytes)) {
        throw '已存在的初装请求与本次输入不匹配；保留原请求文件'
    }
} else {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($requestPath)) | Out-Null
    $temporary = $requestPath + '.tmp-' + [Guid]::NewGuid().ToString('N')
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temporary, $requestPath)
}
$runtime = Join-Path $package 'components/dotnet-runtime/dotnet.exe'
$updater = Join-Path $package 'components/updater/MabiTool.Updater.dll'
if (-not (Test-Path -LiteralPath $runtime -PathType Leaf) -or -not (Test-Path -LiteralPath $updater -PathType Leaf)) {
    throw '目录包缺少随包运行时或初装更新器'
}
$previousEnvironment = @{}
try {
    $settings = @{
        DOTNET_ROOT = (Split-Path $runtime -Parent)
        DOTNET_ROOT_X64 = (Split-Path $runtime -Parent)
        DOTNET_MULTILEVEL_LOOKUP = '0'
    }
    foreach ($name in $settings.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    & $runtime $updater --initial-install $requestPath
    exit $LASTEXITCODE
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}
