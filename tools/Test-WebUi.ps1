# 网页回放页的真实浏览器 UI 测试。
#
# 起一个隔离的无头监控端（tools/ExpressPackingMonitoring.AutomationHost，真实 WebServer + 临时数据库
# + 临时录像），再用 Playwright 驱动 Chrome/Edge 跑 tests/Automation 下的用例。
# 页面脚本初始化抛异常、列表/搜索不再加载这类问题只有在这里才能拦住。
#
#   pwsh -NoProfile -File tools\Test-WebUi.ps1 [-Configuration Debug] [-SkipBuild]

param(
    [string]$Configuration = "Debug",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot ("TestResults\WebUi\" + [DateTime]::Now.ToString("yyyyMMdd-HHmmss"))
$dataRoot = Join-Path $runRoot "data"
$fixtureVideo = Join-Path $runRoot "fixture.mp4"
$hostStdout = Join-Path $runRoot "host.stdout.log"
$hostStderr = Join-Path $runRoot "host.stderr.log"
$hostProcess = $null

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Wait-ForWebServer {
    param([string]$Url, [int]$TimeoutSeconds = 30)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    $details = if (Test-Path $hostStderr) { [System.IO.File]::ReadAllText($hostStderr, [System.Text.Encoding]::UTF8) } else { "" }
    throw "自动化网页服务未就绪：$Url`n$details"
}

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force -Path $runRoot, $dataRoot | Out-Null

    if (-not $SkipBuild) {
        Write-Host "构建自动化宿主（$Configuration）..."
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "build", "tools\ExpressPackingMonitoring.AutomationHost\ExpressPackingMonitoring.AutomationHost.csproj",
            "-c", $Configuration, "--nologo")
    }

    # 目标框架升级后这里不能再写死 net8.0-*：旧目录还在时会把上一次的产物当成新的跑。
    $hostCandidates = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "tools\ExpressPackingMonitoring.AutomationHost\bin\$Configuration") `
            -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'net*-windows*' } |
        Sort-Object -Property @{ Expression = { [version]([regex]::Match($_.Name, '\d+\.\d+').Value) } } -Descending |
        ForEach-Object { Join-Path $_.FullName 'win-x64\ExpressPackingMonitoring.AutomationHost.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1)
    $hostExecutable = if ($hostCandidates.Count -gt 0) { [string]$hostCandidates[0] } else { "" }
    if (-not $hostExecutable) {
        throw "找不到自动化宿主（先执行 dotnet build）：tools\ExpressPackingMonitoring.AutomationHost\bin\$Configuration\net*-windows*\win-x64\ExpressPackingMonitoring.AutomationHost.exe"
    }

    # 测试录像：优先用仓库钉死的 FFmpeg 缓存，其次用 PATH 里的 ffmpeg
    $ffmpeg = Join-Path $repoRoot "package\dependency-cache\ffmpeg\4.4.1\ffmpeg.exe"
    if (-not (Test-Path $ffmpeg)) { $ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue)?.Source }
    if (-not $ffmpeg -or -not (Test-Path $ffmpeg)) {
        throw "需要 ffmpeg 生成测试录像，可先运行 tools\Prepare-PinnedFFmpeg.ps1"
    }
    Invoke-Checked -FilePath $ffmpeg -Arguments @(
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=black:s=640x360:d=2",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", $fixtureVideo)

    if (-not (Test-Path (Join-Path $repoRoot "node_modules\playwright-core\package.json"))) {
        Write-Host "安装 Playwright 测试依赖..."
        Invoke-Checked -FilePath "npm" -Arguments @("ci", "--ignore-scripts")
    }

    $browserCandidates = @(
        (Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe"),
        (Join-Path $env:LOCALAPPDATA "Google\Chrome\Application\chrome.exe"),
        (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe")
    )
    $browserExecutable = $browserCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $browserExecutable) { throw "需要 Chrome 或 Microsoft Edge 才能跑网页 UI 测试。" }

    $port = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$port/"
    Write-Host "启动隔离监控端：$baseUrl"
    $hostProcess = Start-Process -FilePath $hostExecutable `
        -ArgumentList @("$port", $dataRoot, $fixtureVideo) `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $hostStdout -RedirectStandardError $hostStderr
    Wait-ForWebServer -Url $baseUrl

    $previousBaseUrl = $env:EPM_AUTOMATION_BASE_URL
    $previousBrowserExecutable = $env:EPM_BROWSER_EXECUTABLE
    $previousScreenshotsDir = $env:EPM_AUTOMATION_SCREENSHOTS_DIR
    $env:EPM_AUTOMATION_BASE_URL = $baseUrl
    $env:EPM_BROWSER_EXECUTABLE = $browserExecutable
    $env:EPM_AUTOMATION_SCREENSHOTS_DIR = Join-Path $runRoot "screenshots"
    try {
        Write-Host "运行网页 UI 自动化测试..."
        Invoke-Checked -FilePath "npm" -Arguments @("run", "test:e2e")
    }
    finally {
        $env:EPM_AUTOMATION_BASE_URL = $previousBaseUrl
        $env:EPM_BROWSER_EXECUTABLE = $previousBrowserExecutable
        $env:EPM_AUTOMATION_SCREENSHOTS_DIR = $previousScreenshotsDir
    }

    Write-Host ""
    Write-Host "网页 UI 自动化测试通过。"
    Write-Host "运行日志：$runRoot"
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        $hostProcess.WaitForExit(5000) | Out-Null
    }
    Pop-Location
}
