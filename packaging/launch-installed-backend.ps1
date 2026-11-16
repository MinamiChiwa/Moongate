param(
    [Parameter(Mandatory = $true)][string]$InstallationRoot
)
$ErrorActionPreference = 'Stop'

function Assert-InstalledPath([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw '安装路径包含链接，无法启动'
        }
    }
}

function Read-InstalledJson([string]$Path, [long]$MaximumBytes) {
    Assert-InstalledPath $Path
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read -bor [IO.FileShare]::Delete)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt $MaximumBytes) { throw '安装记录大小无效' }
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true)
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

if ($InstallationRoot -notmatch '^[a-zA-Z]:[\\/]') { throw '安装位置必须使用本地磁盘绝对路径' }
$installation = [IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\', '/')
Assert-InstalledPath $installation
$journal = Join-Path $installation 'update-state/initial-install.json'
if (Test-Path -LiteralPath $journal) {
    $initial = Read-InstalledJson $journal (64 * 1024 * 1024)
    if ($initial.schemaVersion -ne 1 -or $initial.phase -ne 'complete') { throw 'update.initialInstallIncomplete' }
}
$pointer = Read-InstalledJson (Join-Path $installation 'launcher/active-version.json') 4096
$release = $pointer.releaseId
if ($pointer.schemaVersion -ne 1 -or $release -isnot [string] -or
    $release -notmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z' -or $release.EndsWith('.') -or
    ($release.Split('.')[0] -match '\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z')) {
    throw 'update.pointerInvalid'
}
$configuration = Join-Path $installation 'launcher/install-configuration.json'
Assert-InstalledPath $configuration
$runtime = Join-Path $installation ('versions/' + $release + '/components/dotnet-runtime/dotnet.exe')
$launcher = Join-Path $installation 'launcher/stable/components/launcher/MabiTool.Launcher.dll'
foreach ($entry in @($runtime, $launcher)) {
    if (-not (Test-Path -LiteralPath $entry -PathType Leaf)) { throw '安装缺少稳定启动器或随包运行时' }
    Assert-InstalledPath $entry
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
    & $runtime $launcher --launch $installation
    exit $LASTEXITCODE
} finally {
    foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
