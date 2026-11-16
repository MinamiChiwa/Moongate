param(
    [ValidateSet('Build', 'Test', 'Verify')]
    [string]$Action = 'Build',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if ($Action -ne 'Build') {
    throw '全量 Test/Verify 入口已停用。按改动选择具体测试项目与最小用例，用 dotnet test --filter 运行。'
}
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repositoryRoot
try {
    dotnet restore MabiTool.slnx --locked-mode --disable-parallel -maxcpucount:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw '.NET 依赖还原失败' }
    dotnet build MabiTool.slnx --configuration $Configuration --no-restore -maxcpucount:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw '.NET 构建失败' }
} finally { Pop-Location }
