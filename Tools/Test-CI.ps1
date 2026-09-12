param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    Write-Host "还原解决方案..."
    dotnet restore ExpressPackingMonitoring.sln --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败，退出码：$LASTEXITCODE" }

    Write-Host "构建解决方案（$Configuration）..."
    dotnet build ExpressPackingMonitoring.sln -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码：$LASTEXITCODE" }

    Write-Host "运行单元测试..."
    dotnet test ExpressPackingMonitoring.Tests\ExpressPackingMonitoring.Tests.csproj -c $Configuration --no-build --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet test 失败，退出码：$LASTEXITCODE" }

    Write-Host "检查扩展 API 示例语法..."
    node --check "docs\examples\extension-v1-minimal.js"
    if ($LASTEXITCODE -ne 0) { throw "扩展 API 最小示例语法检查失败" }
    node --check "docs\examples\extension-v1-serial-scale.js"
    if ($LASTEXITCODE -ne 0) { throw "扩展 API 称重示例语法检查失败" }

    Write-Host "本地 CI 检查通过。"
}
finally {
    Pop-Location
}
