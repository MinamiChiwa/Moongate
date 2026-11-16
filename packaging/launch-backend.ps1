param(
    [switch]$InitializeData,
    [switch]$Mock,
    [string]$PortableRoot,
    [switch]$Wait,
    # 输出后台启动的宿主进程，供双击入口等待实例发现文件或读取退出码。
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
# 本脚本随包位于 components/core；包根是其上两级。运行文件位置由宿主按工具根规则确定（包根下的 userdata）。
$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runtime = Join-Path $packageRoot 'components/dotnet-runtime/dotnet.exe'
$hostAssembly = Join-Path $packageRoot 'components/core/MabiTool.Host.dll'
if (-not (Test-Path -LiteralPath $runtime -PathType Leaf) -or -not (Test-Path -LiteralPath $hostAssembly -PathType Leaf)) {
    throw '目录包缺少后端或随包运行时'
}
$hostArguments = @($hostAssembly)
if ($InitializeData) { $hostArguments += '--initialize-data' }
if ($Mock) { $hostArguments += '--mock' }
if ($PortableRoot) { $hostArguments += @('--portable-root', [IO.Path]::GetFullPath($PortableRoot)) }
$previousEnvironment = @{}
try {
    $settings = @{
        DOTNET_ROOT = (Split-Path $runtime -Parent)
        DOTNET_ROOT_X64 = (Split-Path $runtime -Parent)
        DOTNET_MULTILEVEL_LOOKUP = '0'
    }
    $observation = Join-Path $packageRoot 'components/observation-worker/MabiTool.Observation.Worker.exe'
    if (Test-Path -LiteralPath $observation -PathType Leaf) {
        $settings.MABITOOL_OBSERVATION_EXECUTABLE = $observation
    }
    $colorPython = Join-Path $packageRoot 'components/color-engine/python/python.exe'
    if (Test-Path -LiteralPath $colorPython -PathType Leaf) {
        $settings.MABITOOL_COLOR_PYTHON = $colorPython
        $settings.MABITOOL_COLOR_WORKER_DIRECTORY = Join-Path $packageRoot 'components/color-engine'
        # 固定组件内的 Tesseract，进程外残留的同名变量不能把识别改到其他位置。
        $settings.MABITOOL_COLOR_TESSERACT_DIRECTORY = Join-Path $packageRoot 'components/color-engine/tesseract'
    }
    foreach ($name in $settings.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    if ($Wait) {
        & $runtime @hostArguments
        exit $LASTEXITCODE
    }
    $quotedArguments = $hostArguments | ForEach-Object { '"' + (($_ -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
    $process = Start-Process -FilePath $runtime -ArgumentList ($quotedArguments -join ' ') -WorkingDirectory $packageRoot -WindowStyle Hidden -PassThru
    if ($PassThru) { $process }
} finally {
    foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
