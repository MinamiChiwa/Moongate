param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+\z')]
    [string]$Version = '0.1.0',
    [string]$ReleaseId,
    # 发行身份里登记的 GitHub 仓库，宿主从这里的 Releases 检查更新。
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z')]
    [string]$Repository = 'MinamiChiwa/Moongate',
    [ValidateSet('stable', 'preview')]
    [string]$Channel = 'preview',
    [string]$OutputDirectory,
    [string]$RuntimeVersion = '10.0.13',
    [switch]$IncludeObservation,
    [switch]$IncludeColor,
    [string]$PythonExecutable = 'python',
    [string]$TesseractDirectory,
    [string]$TesseractLicenseDirectory,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($IncludeColor -and (-not $TesseractDirectory -or -not $TesseractLicenseDirectory)) {
    throw '染色组件随包 Tesseract：须指定 -TesseractDirectory 与 -TesseractLicenseDirectory'
}
if ($IncludeColor) {
    # 构建前核对输入齐全，缺失时列出文件名，不等到发布完 .NET 组件后才失败。长度与哈希仍由 package-color-runtime.py 核对。
    foreach ($directory in @($TesseractDirectory, $TesseractLicenseDirectory)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Tesseract 输入目录不存在：$directory" }
    }
    $tesseractLock = Get-Content -LiteralPath (Join-Path $repositoryRoot 'workers/color-engine/tesseract-lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $missingInputs = @($tesseractLock.files | ForEach-Object { $_.path } | Where-Object { -not (Test-Path -LiteralPath (Join-Path $TesseractDirectory $_) -PathType Leaf) })
    $missingLicenses = @($tesseractLock.packages | Where-Object { $_.id -ne 'tesseract' } | ForEach-Object { Split-Path $_.licenseInput -Leaf } |
        Where-Object { -not (Test-Path -LiteralPath (Join-Path $TesseractLicenseDirectory $_) -PathType Leaf) })
    if ($missingInputs.Count) { throw "-TesseractDirectory 缺少 $($missingInputs.Count) 个锁定文件：$($missingInputs -join '、')" }
    if ($missingLicenses.Count) { throw "-TesseractLicenseDirectory 缺少 $($missingLicenses.Count) 份许可文字：$($missingLicenses -join '、')" }
}
if (-not $ReleaseId) { $ReleaseId = "mabitool-$Version-win-x64" }
if ($ReleaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
    throw '发行目录名无效'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot "artifacts/packages/$ReleaseId" }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packagesRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/packages'))
if (-not $outputRoot.StartsWith($packagesRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '发行输出必须位于 artifacts/packages 的子目录'
}
if (Test-Path -LiteralPath $outputRoot) { throw '发行目录已存在，请指定新的输出目录' }
# 解压版 zip 与目录包并列放在 artifacts/packages；预览渠道在包名与程序内版本处标出。
$displayVersion = if ($Channel -eq 'preview') { "$Version-preview" } else { $Version }
$archivePath = Join-Path $packagesRoot "Moongate-v$displayVersion-Windows-x64.zip"
if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath "$archivePath.sha256")) { throw "解压版 zip 已存在：$archivePath" }
for ($checkedPath = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($outputRoot)); $checkedPath; $checkedPath = $checkedPath.Parent) {
    if ($checkedPath.Exists -and ($checkedPath.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '发行输出包含链接目录' }
}
$workRoot = Join-Path $repositoryRoot ('artifacts/tmp/publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
function Move-PublishOutputToPendingCleanup([string]$Directory) {
    $absoluteDirectory = [IO.Path]::GetFullPath($Directory)
    if (-not $absoluteDirectory.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '打包清理目录越界'
    }
    $target = $absoluteDirectory.Substring($repositoryRoot.Length + 1).Replace('\', '/')
    # 维护脚本引用同目录模块；忽略 Python 环境和站点配置，保留脚本导入目录。
    & $PythonExecutable -E -S -B -X utf8 (Join-Path $repositoryRoot 'scripts/maintenance/clean-artifacts.py') --target $target --apply
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $absoluteDirectory)) {
        throw "打包目录移交待清理失败：$target"
    }
}
$startedAt = [DateTimeOffset]::UtcNow
Push-Location $repositoryRoot
try {
    $dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source
    $runtimeLines = & $dotnetCommand --list-runtimes
    if ($LASTEXITCODE -ne 0) { throw '.NET 运行时枚举失败' }
    $runtimePaths = @{}
    foreach ($runtimeLine in $runtimeLines) {
        if ($runtimeLine -match '^([^ ]+) ([^ ]+) \[(.+)\]$' -and $Matches[2] -eq $RuntimeVersion) {
            $runtimePaths[$Matches[1]] = Join-Path $Matches[3] $RuntimeVersion
        }
    }
    if (-not $runtimePaths.ContainsKey('Microsoft.NETCore.App') -or -not $runtimePaths.ContainsKey('Microsoft.AspNetCore.App')) {
        throw "需要同版本的 Microsoft.NETCore.App 和 Microsoft.AspNetCore.App $RuntimeVersion"
    }
    $dotnetRoot = Split-Path (Split-Path (Split-Path $runtimePaths['Microsoft.NETCore.App'] -Parent) -Parent) -Parent
    $runtimeTarget = Join-Path $workRoot 'components/dotnet-runtime'
    New-Item -ItemType Directory -Path $runtimeTarget -Force | Out-Null
    foreach ($name in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $source = Join-Path $dotnetRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "运行时缺少 $name" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $runtimeTarget $name)
    }
    $fxrSource = Join-Path $dotnetRoot "host/fxr/$RuntimeVersion"
    if (-not (Test-Path -LiteralPath $fxrSource -PathType Container)) { throw '运行时缺少匹配 hostfxr' }
    New-Item -ItemType Directory -Path (Join-Path $runtimeTarget 'host/fxr') -Force | Out-Null
    Copy-Item -LiteralPath $fxrSource -Destination (Join-Path $runtimeTarget "host/fxr/$RuntimeVersion") -Recurse
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
        New-Item -ItemType Directory -Path (Join-Path $runtimeTarget "shared/$framework") -Force | Out-Null
        Copy-Item -LiteralPath $runtimePaths[$framework] -Destination (Join-Path $runtimeTarget "shared/$framework/$RuntimeVersion") -Recurse
    }
    $projects = [ordered]@{
        'core' = 'src/Host/MabiTool.Host.csproj'
        'room-server' = 'src/RoomServer/MabiTool.RoomServer.csproj'
        'desktop' = 'frontends/desktop/src/MabiTool.Desktop/MabiTool.Desktop.csproj'
    }
    if ($IncludeObservation) { $projects['observation-worker'] = 'workers/game-observation/MabiTool.Observation.Worker.csproj' }
    foreach ($component in $projects.Keys) {
        $componentOutput = Join-Path $workRoot "components/$component"
        # 桌面前端保留 apphost（窗口程序，入口以随包运行时的 DOTNET_ROOT 启动它）。
        $appHost = if ($component -in @('observation-worker', 'desktop')) { 'true' } else { 'false' }
        $arguments = @('publish', $projects[$component], '-c', 'Release', '--nologo', '--self-contained', 'false',
            "-p:UseAppHost=$appHost", '-p:RestoreLockedMode=true', "-p:Version=$Version", "-p:RuntimeFrameworkVersion=$RuntimeVersion",
            "-p:ArtifactsPath=$(Join-Path $workRoot 'build')",
            '-maxcpucount:1', '-nodeReuse:false', '-o', $componentOutput)
        if ($component -eq 'core') {
            $arguments += @('-r', 'win-x64', '-p:PublishReadyToRun=true',
                '-p:PublishReadyToRunComposite=false', '-p:PublishTrimmed=false')
        }
        if ($component -eq 'desktop') {
            # RuntimeIdentifier=win-x64 由前端目录的 Directory.Build.props 固定；命令行 -r 会传给引用项目并破坏其锁定还原。
            # InformationalVersion 带渠道标识，设置页的版本据此标注预览版。
            $arguments += @('-p:PublishReadyToRun=false', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false',
                "-p:InformationalVersion=$displayVersion", '-p:IncludeSourceRevisionInInformationalVersion=false')
        }
        if ($NoRestore) { $arguments += '--no-restore' }
        & $dotnetCommand @arguments
        if ($LASTEXITCODE -ne 0) { throw "组件 $component 发布失败" }
        # 发行物不带调试符号：本项目与依赖包复制出的 .pdb 都在这里移除，package-inventory.py 再次拒绝残留。
        Get-ChildItem -LiteralPath $componentOutput -Filter '*.pdb' -File -Recurse | Remove-Item -Force
        $nativeRuntimes = Join-Path $componentOutput 'runtimes'
        if (Test-Path -LiteralPath $nativeRuntimes -PathType Container) {
            foreach ($runtimeDirectory in Get-ChildItem -LiteralPath $nativeRuntimes -Directory) {
                if ($runtimeDirectory.Name -eq 'win-x64') { continue }
                $nativeOutput = [IO.Path]::GetFullPath($runtimeDirectory.FullName)
                if (-not $nativeOutput.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                    ($runtimeDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '原生依赖输出越界或包含链接' }
                Move-PublishOutputToPendingCleanup $nativeOutput
            }
        }
    }
    if ($IncludeColor) {
        & $PythonExecutable -I -S -B -X utf8 scripts/build/package-color-runtime.py --python $PythonExecutable --output (Join-Path $workRoot 'components/color-engine') --tesseract-directory ([IO.Path]::GetFullPath($TesseractDirectory)) --tesseract-licenses ([IO.Path]::GetFullPath($TesseractLicenseDirectory))
        if ($LASTEXITCODE -ne 0) { throw '染色运行时构建失败' }
    }
    # 包根入口 Moongate.exe：.NET Framework 4.8 窗口程序，只取单个 exe，构建中间输出随 build 目录回收。
    $launcherOutput = Join-Path $workRoot 'build/portable-launcher'
    $launcherArguments = @('publish', 'src/PortableLauncher/MabiTool.PortableLauncher.csproj', '-c', 'Release', '--nologo',
        '-p:RestoreLockedMode=true', "-p:Version=$Version", "-p:InformationalVersion=$displayVersion",
        '-p:IncludeSourceRevisionInInformationalVersion=false', "-p:ArtifactsPath=$(Join-Path $workRoot 'build')",
        '-maxcpucount:1', '-nodeReuse:false', '-o', $launcherOutput)
    if ($NoRestore) { $launcherArguments += '--no-restore' }
    & $dotnetCommand @launcherArguments
    if ($LASTEXITCODE -ne 0) { throw '包根入口 Moongate.exe 构建失败' }
    Copy-Item -LiteralPath (Join-Path $launcherOutput 'Moongate.exe') -Destination (Join-Path $workRoot 'Moongate.exe')
    $buildOutput = [IO.Path]::GetFullPath((Join-Path $workRoot 'build'))
    if (-not $buildOutput.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '构建中间目录越界'
    }
    if (Test-Path -LiteralPath $buildOutput) { Move-PublishOutputToPendingCleanup $buildOutput }
    # 只启动后端的诊断入口随 core 组件发行，包根只留 Moongate.exe、components 与生成的清单。
    foreach ($entry in @('launch-backend.ps1', 'launch-backend.cmd')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot ('packaging/' + $entry)) -Destination (Join-Path $workRoot ('components/core/' + $entry))
    }
    & $PythonExecutable -I -S -B -X utf8 scripts/build/package-inventory.py --repository $repositoryRoot --package $workRoot --version $Version --runtime-version $RuntimeVersion --release-id $ReleaseId --channel $Channel --repository-name $Repository
    if ($LASTEXITCODE -ne 0) { throw '发行身份、来源、许可或 SBOM 构建失败' }
    $buildRecord = [ordered]@{
        schemaVersion = 1
        evidenceKind = 'local-directory-package-build'
        startedAtUtc = $startedAt.ToString('O')
        finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sdkVersion = (& $dotnetCommand --version)
        runtimeVersion = $RuntimeVersion
        runtimeMode = 'framework-dependent'
        bundledRuntime = $true
        platform = 'win-x64'
        nativeRuntimeIds = @('win-x64')
        corePublication = [ordered]@{
            runtimeIdentifier = 'win-x64'
            readyToRun = $true
            composite = $false
            trimmed = $false
            useAppHost = $false
        }
        desktopPublication = [ordered]@{
            runtimeIdentifier = 'win-x64'
            runtimeMode = 'framework-dependent'
            useAppHost = $true
            readyToRun = $false
            trimmed = $false
        }
        releaseId = $ReleaseId
        version = $Version
        displayVersion = $displayVersion
        distribution = 'portable-zip'
        repository = $Repository
        components = @($projects.Keys) + @('dotnet-runtime') + $(if ($IncludeColor) { @('color-engine') } else { @() })
        cleanWindowsVerification = 'pending'
    }
    New-Item -ItemType Directory -Path $packagesRoot -Force | Out-Null
    [IO.Directory]::Move($workRoot, $outputRoot)
    $archive = & (Join-Path $repositoryRoot 'scripts/build/package-archive.ps1') -PackageDirectory $outputRoot -ArchivePath $archivePath
    $buildRecord.archive = [ordered]@{ name = $archive.name; sha256 = $archive.sha256; bytes = $archive.bytes; files = $archive.files }
    [IO.File]::WriteAllText((Join-Path $outputRoot 'package-build.json'), ($buildRecord | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Output "已构建目录包：$outputRoot"
    Write-Output "已生成解压版：$archivePath（SHA-256 $($archive.sha256)）"
} finally { Pop-Location }
