param(
    [string]$Configuration = "Release",
    [ValidateSet("auto", "include", "exclude")]
    [string]$VisionObb = "auto"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "Branding.Common.ps1")
. (Join-Path $PSScriptRoot "BrandResourcePolicy.Common.ps1")
$brand = Get-BrandProfile -RepoRoot $repoRoot
$resourcePolicy = Get-BrandResourcePolicy -Brand $brand
$includeVisionObb = Resolve-BrandOptionalResource `
    -Requested $VisionObb `
    -Enabled $resourcePolicy.VisionObb `
    -ResourceName "inputs.visionObb"
$solution = Join-Path $repoRoot "ExpressPackingMonitoring.sln"
$testsProject = Join-Path $repoRoot "tests\ExpressPackingMonitoring.Tests\ExpressPackingMonitoring.Tests.csproj"
$encodingTestScript = Join-Path $repoRoot "tools\Test-EncodingCodecs.ps1"
$webPage = Join-Path $repoRoot "src\ExpressPackingMonitoring\Web\index.html"
$artifactsRoot = Join-Path $repoRoot "TestResults\ReleaseBuild\$Configuration"
$visionArguments = @(
    "-p:IncludeVisionObb=$($includeVisionObb.ToString().ToLowerInvariant())"
)

# 真实摄像头属于发布现场人工检查；自动化门禁只验证确定性逻辑。
# 本机/CI 的虚拟摄像头帧率、占用状态和驱动差异不能让正式出包随机失败。
$env:PACKINGPROOF_SKIP_CAMERA_TESTS = "1"

$requiredCoreTests = @(
    "CameraLifecycleTests.PreviewSessionGate_StaleCallbackCannotReleaseAwakenedSession",
    "CameraLifecycleTests.CameraFrameReadySignal_WakeRequiresNewSessionFrame",
    "CameraLifecycleTests.CameraFrameReadySignal_RecordingStartTimesOutWithoutFrame",
    "CameraBarcodeRecognitionTests.StabilityTracker_FirstReappearanceAfterRearmDelayUnlocksSameCode",
    "CameraBarcodeRecognitionTests.StabilityTracker_TwoHitsWithinWindowConfirmOnce",
    "CameraBarcodeRecognitionTests.StabilityTracker_MissedDetectionDoesNotResetConfirmationCount",
    "CameraBarcodeRecognitionTests.StabilityTracker_IntermittentWindowConfirmsOnThirdHit",
    "CameraBarcodeRecognitionTests.StabilityTracker_IntermittentWindowKeepsHitsAcrossMissedDetections",
    "CameraBarcodeRecognitionTests.StabilityTracker_IntermittentWindowRestartsAfterWindowExpires",
    "CameraBarcodeRecognitionTests.StabilityTracker_CustomRearmDelayControlsWhenSameCodeCanReturn",
    "CameraBarcodeRecognitionTests.NormalizeAfterLoad_ClampsCameraSameCodeTimingSettings",
    "CameraBarcodeRecognitionTests.RecordingDecisionPolicy_ScannerStillSwitchesRecordingInContinuousMode",
    "CameraBarcodeRecognitionTests.RuntimeOptions_ShadowModeRequiresExplicitOptIn",
    "CameraBarcodeRecognitionTests.Decoder_RepeatedDecodesAvoidPerFrameLargeManagedAllocations",
    "CameraBarcodeRecognitionTests.RecordingDecisionPolicy_CameraPreviewMatchesRecordingRules",
    "CameraBarcodeRecognitionTests.RecordingDecisionPolicy_ProductEan13NeverStopsSameCodeRecording",
    "ConfigurationAndScannerTests.AppConfig_LegacyJsonEnablesMaximumSpeechVolumeByDefault",
    "ConfigurationAndScannerTests.IsFastSequence_DistinguishesScannerAndManualTyping",
    "ConfigurationAndScannerTests.ShouldAlertPrintedRefund_AlertsEnabledShippingAndReturnScans",
    "ConfigurationAndScannerTests.ShouldAlertPrintedRefund_UsesRefundStatus",
    "ConfigurationAndScannerTests.GetPrintedRefundLookupDelay_RequestsImmediatelyThenThrottlesForFiveSeconds",
    "ConfigurationAndScannerTests.AddMonitorConnectPermission_AddsExactHostWithoutRequiringWildcardPermission",
    "ConfigurationAndScannerTests.AddMonitorConnectPermissions_AddsDistinctPrivateHostsAndRejectsPublicHosts",
    "ConfigurationAndScannerTests.AddMonitorConnectPermission_PreservesCrLfLineEndings",
    "ConnectedClientTests.ConnectedDeviceCountDeduplicatesClientsByRemoteAddress",
    "ConfigurationAndScannerTests.AlertService_CriticalAlertBlocksNormalAlertUntilDisplayEnds",
    "ConfigurationAndScannerTests.AlertService_ForwardsIndustrialAlarmAndRefundSpeechOnce",
    "VideoDatabaseTests.GetRecentCompletedVideos_ReturnsLatestTwentyValidRecordsForDate",
    "VideoDatabaseTests.OrderIdExistsRecent_ChecksThirtyDaysAndIgnoresDeletedOrExcludedRecords",
    "VideoDatabaseTests.GetRecentOrderInfos_UsesDatabaseAsNinetyDaySourceOfTruth",
    "VideoDatabaseTests.UpsertOrderInfos_DoesNotLetOlderSnapshotOverwriteNewerRefundState",
    "VideoDatabaseTests.VideoRecords_DerivesFileNameFromPathAndDoesNotPersistRedundantColumn",
    "MobileConnectionTests.FirstUseDefaultsLeaveMobilePromptPendingUntilQrWasShown"
    "MobileConnectionTests.GeneratedQrDecodesToExactAccessUrl"
    "MobileConnectionTests.ProtectedEndpointRejectsUnauthorizedAndAcceptsQueryThenCookie"
)

$wpfTestClasses = @(
    "BarcodePrintLayoutTests",
    "BarcodeRenderRoundTripTests",
    "CameraChannelChoiceUiTests",
    "CameraVisionGuideRoiTests",
    "ClipboardHelperTests",
    "ExtensionMarketWindowTests",
    "InputClearButtonRenderTests",
    "InputPasteButtonRenderTests",
    "MenuThemeRenderTests",
    "PhotoEvidenceTests",
    "PlaybackWindowRenderTests",
    "PlaybackWindowVideoHostSmokeTests",
    "SettingsCardSpacingTests",
    "SettingsWindowRenderTests",
    "ThemeManagerTests",
    "UiScreenshotTests",
    "WindowConstructionTests",
    "ZoomGuideEditingRenderTests"
)

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $testsProject)) {
    throw "Test project not found: $testsProject"
}
if (-not (Test-Path $webPage)) {
    throw "Web page not found: $webPage"
}
if (-not (Test-Path $encodingTestScript)) {
    throw "Encoder round-trip test script not found: $encodingTestScript"
}

if (Test-Path $artifactsRoot) {
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null

Write-Host "Building complete release solution..."
Invoke-DotNet -Arguments (@(
    "build", $solution, "-c", $Configuration, "--nologo",
    "--artifacts-path", $artifactsRoot
) + $visionArguments)

Write-Host "Discovering required core business and recovery tests..."
$testList = (& dotnet test $testsProject -c $Configuration --nologo --list-tests --no-build --no-restore --artifacts-path $artifactsRoot @visionArguments 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Test discovery failed with exit code $LASTEXITCODE`n$testList"
}
foreach ($requiredTest in $requiredCoreTests) {
    if (-not $testList.Contains($requiredTest, [System.StringComparison]::Ordinal)) {
        throw "Required release test is missing: $requiredTest"
    }
}

Write-Host "Running release business tests..."
$businessTestFilter = ($wpfTestClasses |
    ForEach-Object { "FullyQualifiedName!~.$_." }) -join "&"
Invoke-DotNet -Arguments (@(
    "test", $testsProject, "-c", $Configuration, "--nologo",
    "--no-build", "--no-restore", "--artifacts-path", $artifactsRoot,
    "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
    "--filter", $businessTestFilter
) + $visionArguments)

Write-Host "Running isolated WPF test classes..."
foreach ($testClass in $wpfTestClasses) {
    Write-Host "  $testClass"
    $previousPlaybackSmoke = $env:EPM_PLAYBACK_SMOKE
    try {
        if ($testClass -eq "PlaybackWindowVideoHostSmokeTests") {
            $env:EPM_PLAYBACK_SMOKE = "1"
        }
        # WPF 的 Application、资源字典和窗口生命周期是进程级状态；同一测试进程连续
        # 执行多个 WPF 用例时，先关闭的 Application 会让后续资源 URI 和窗口失效。
        # 因此所有 WPF 用例都按方法分别启进程，保证每个用例都有干净的应用生命周期。
        $methodPrefix = "ExpressPackingMonitoring.Tests.$testClass."
        $windowTests = [regex]::Matches(
            $testList,
            [regex]::Escape($methodPrefix) + "[A-Za-z0-9_]+") |
            ForEach-Object { $_.Value } |
            Sort-Object -Unique
        if ($windowTests.Count -eq 0) {
            throw "$testClass discovery returned no test methods"
        }
        foreach ($windowTest in $windowTests) {
            Write-Host "    $($windowTest.Substring($methodPrefix.Length))"
            Invoke-DotNet -Arguments (@(
                "test", $testsProject, "-c", $Configuration, "--nologo",
                "--no-build", "--no-restore", "--artifacts-path", $artifactsRoot,
                "--blame-hang-timeout", "2m", "--blame-hang-dump-type", "none",
                "--filter", "FullyQualifiedName=$windowTest"
            ) + $visionArguments)
        }
    }
    finally {
        $env:EPM_PLAYBACK_SMOKE = $previousPlaybackSmoke
    }
}

Write-Host "Running hardware-aware FFmpeg encoder round trips..."
& $encodingTestScript -Configuration $Configuration -ArtifactsPath $artifactsRoot -NoBuild

Write-Host "Checking Web JavaScript syntax..."
$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $node) {
    throw "Node.js is required for release Web JavaScript validation."
}
$html = [System.IO.File]::ReadAllText($webPage, [System.Text.Encoding]::UTF8)
$scriptBlocks = [regex]::Matches(
    $html,
    '<script\b(?<attributes>[^>]*)>(?<code>[\s\S]*?)</script>',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$inlineScripts = @($scriptBlocks | Where-Object {
    $_.Groups["attributes"].Value -notmatch '\bsrc\s*='
})
if ($inlineScripts.Count -eq 0) {
    throw "Web page inline script block was not found."
}
$tempScript = Join-Path ([System.IO.Path]::GetTempPath()) "ExpressPackingMonitoring-release-$([Guid]::NewGuid().ToString('N')).js"
try {
    foreach ($script in $inlineScripts) {
        [System.IO.File]::WriteAllText(
            $tempScript,
            $script.Groups["code"].Value,
            [System.Text.UTF8Encoding]::new($false))
        & $node.Source --check $tempScript
        if ($LASTEXITCODE -ne 0) {
            throw "Web JavaScript syntax validation failed with exit code $LASTEXITCODE"
        }
    }
}
finally {
    Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
}

Write-Host "Release automated validation passed."
