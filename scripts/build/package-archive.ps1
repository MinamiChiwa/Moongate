param(
    # publish-backend.ps1 生成的目录包（根下有 Moongate.exe、components/、清单与第三方声明）。
    [Parameter(Mandatory)][string]$PackageDirectory,
    # 解压版 zip 的绝对路径；同目录写出同名 .sha256。
    [Parameter(Mandatory)][string]$ArchivePath
)
# 把目录包装成解压即用的 zip：解压后是单个 Moongate 文件夹，根下只有 Moongate.exe、components 与 licenses。
# 构建记录 package-build.json 只留在构建目录，不进入 zip；发行身份 components/release.json 随组件进入 zip。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$package = [IO.Path]::GetFullPath($PackageDirectory)
$archive = [IO.Path]::GetFullPath($ArchivePath)
$checksum = $archive + '.sha256'
$partial = $archive + '.partial'
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $checksum) -or (Test-Path -LiteralPath $partial)) { throw "解压版 zip 已存在：$archive" }
if ($archive.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'zip 不能写在目录包内' }

$rootName = 'Moongate'
$licenseFiles = @('THIRD_PARTY_NOTICES.txt', 'sbom.spdx.json')
$allowed = @('Moongate.exe', 'components', 'package-build.json') + $licenseFiles
$required = @('Moongate.exe', 'components') + $licenseFiles
$present = @(Get-ChildItem -LiteralPath $package -Force | ForEach-Object Name)
$unexpected = @($present | Where-Object { $_ -notin $allowed })
$missing = @($required | Where-Object { $_ -notin $present })
if ($unexpected.Count -or $missing.Count) {
    throw "目录包根布局不符：多出 [$($unexpected -join ', ')]，缺少 [$($missing -join ', ')]"
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'Moongate.exe') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $package 'components') -PathType Container)) { throw '目录包的 Moongate.exe 或 components 类型不符' }
if (-not (Test-Path -LiteralPath (Join-Path $package 'components/release.json') -PathType Leaf)) { throw '目录包缺少发行身份 components/release.json' }

# 包内路径 → 源文件；组件树不跟随链接。
$entries = [ordered]@{ "$rootName/Moongate.exe" = (Join-Path $package 'Moongate.exe') }
$componentsRoot = Join-Path $package 'components'
foreach ($item in Get-ChildItem -LiteralPath $componentsRoot -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "组件包含链接：$($item.FullName)" }
    if ($item.PSIsContainer) { continue }
    $relative = $item.FullName.Substring($componentsRoot.Length + 1).Replace('\', '/')
    $entries["$rootName/components/$relative"] = $item.FullName
}
foreach ($name in $licenseFiles) { $entries["$rootName/licenses/$name"] = Join-Path $package $name }

try {
    $stream = [IO.File]::Open($partial, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($entry in $entries.GetEnumerator()) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entry.Value, $entry.Key, [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $zip.Dispose() }
        $stream.Flush($true)
    } finally { $stream.Dispose() }

    # 重新打开核对：顶层只有 Moongate/，其下只有 Moongate.exe、components、licenses，条目数与源文件一致。
    $check = [IO.Compression.ZipFile]::OpenRead($partial)
    try {
        $names = @($check.Entries | ForEach-Object FullName)
        if ($names.Count -ne $entries.Count) { throw '解压版 zip 条目数与目录包文件数不一致' }
        $tops = @($names | ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique)
        $children = @($names | ForEach-Object { $_.Split('/')[1] } | Sort-Object -Unique)
        if (($tops -join '|') -ne $rootName -or ($children -join '|') -ne 'components|licenses|Moongate.exe') {
            throw "解压版 zip 根布局不符：$($tops -join ',') / $($children -join ',')"
        }
    } finally { $check.Dispose() }

    $hash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    $bytes = (Get-Item -LiteralPath $partial).Length
    [IO.File]::Move($partial, $archive)
    [IO.File]::WriteAllText($checksum, "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.Encoding]::ASCII)
} catch {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    throw
}
[pscustomobject]@{ name = [IO.Path]::GetFileName($archive); sha256 = $hash; bytes = $bytes; files = $entries.Count }
