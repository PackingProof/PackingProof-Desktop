[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:5280",
    [Parameter(Mandatory = $true)]
    [string]$AccessKey,
    [string]$SessionId,
    [string]$Weight = "1.25 kg",
    [string]$UpdatedWeight = "2.00 kg",
    [string]$Length = "30 cm",
    [switch]$SkipStoppedCheck
)

$ErrorActionPreference = "Stop"
$base = $BaseUrl.TrimEnd('/')
$headers = @{ "X-EPM-Access-Key" = $AccessKey }

function Invoke-Json {
    param([string]$Method, [string]$Path, $Body)
    $params = @{
        Method = $Method
        Uri = "$base$Path"
        Headers = $headers
        UseBasicParsing = $true
    }
    if ($null -ne $Body) {
        $params.ContentType = "application/json; charset=utf-8"
        $params.Body = ($Body | ConvertTo-Json -Depth 8)
    }
    Invoke-RestMethod @params
}

function Invoke-JsonExpectStatus {
    param([string]$Method, [string]$Path, $Body, [int]$ExpectedStatus)
    try {
        Invoke-Json $Method $Path $Body | Out-Null
        throw "请求成功返回，但预期 HTTP $ExpectedStatus"
    }
    catch [System.Net.WebException] {
        $response = $_.Exception.Response
        if ($null -eq $response -or [int]$response.StatusCode -ne $ExpectedStatus) {
            throw
        }
        Write-Host "通过：录像结束后写入被拒绝（HTTP $ExpectedStatus）" -ForegroundColor Green
    }
}

Write-Host "检查扩展接口能力..."
$capabilities = Invoke-Json GET "/api/extensions/v1/capabilities" $null
if (-not $capabilities.features.watermarkFields) {
    throw "当前主机未声明 watermarkFields 能力"
}

if ([string]::IsNullOrWhiteSpace($SessionId)) {
    Write-Host "查找活跃录像会话..."
    $active = Invoke-Json GET "/api/extensions/v1/recordings/active" $null
    $recording = @($active.recordings) | Select-Object -First 1
    if ($null -eq $recording) {
        throw "没有活跃录像，请先在 PackingProof 中开始录像"
    }
    $SessionId = [string]$recording.recordingSessionId
}

$payload = @{
    namespace = "test.scale"
    providerId = "packingproof-test"
    fields = @{
        weight = $Weight
        length = $Length
    }
}

Write-Host "向会话 $SessionId 推送测试字段..."
$result = Invoke-Json POST "/api/extensions/v1/recordings/$([uri]::EscapeDataString($SessionId))/data" $payload
if (-not $result.success) {
    throw "字段推送失败"
}

$readback = Invoke-Json GET "/api/extensions/v1/recordings/$([uri]::EscapeDataString($SessionId))/data" $null
$fields = @($readback.fields)
$weightField = $fields | Where-Object { $_.namespace -eq "test.scale" -and $_.fieldName -eq "weight" } | Select-Object -First 1
$lengthField = $fields | Where-Object { $_.namespace -eq "test.scale" -and $_.fieldName -eq "length" } | Select-Object -First 1
if ($null -eq $weightField -or $weightField.value -ne $Weight) { throw "读回 weight 与推送值不一致" }
if ($null -eq $lengthField -or $lengthField.value -ne $Length) { throw "读回 length 与推送值不一致" }

Write-Host "通过：初始字段已写入并成功读回" -ForegroundColor Green

$updatedPayload = @{
    namespace = "test.scale"
    providerId = "packingproof-test"
    fields = @{ weight = $UpdatedWeight; length = $Length }
}
Write-Host "提交更新后的重量 $UpdatedWeight..."
$updateResult = Invoke-Json POST "/api/extensions/v1/recordings/$([uri]::EscapeDataString($SessionId))/data" $updatedPayload
if (-not $updateResult.success) { throw "更新字段失败" }
$updated = Invoke-Json GET "/api/extensions/v1/recordings/$([uri]::EscapeDataString($SessionId))/data" $null
$updatedWeightField = @($updated.fields) | Where-Object { $_.namespace -eq "test.scale" -and $_.fieldName -eq "weight" } | Select-Object -First 1
if ($null -eq $updatedWeightField -or $updatedWeightField.value -ne $UpdatedWeight) { throw "更新后的 weight 读回值不一致" }
Write-Host "通过：字段更新后读回了最新值" -ForegroundColor Green

if (-not $SkipStoppedCheck) {
    Read-Host "请现在在 PackingProof 中停止这段录像，完成后按回车继续"
    Invoke-JsonExpectStatus POST "/api/extensions/v1/recordings/$([uri]::EscapeDataString($SessionId))/data" $updatedPayload 409
}

Write-Host "通过：扩展接口生命周期验收完成" -ForegroundColor Green
Write-Host "注意：水印从推送后的后续视频帧开始显示，已经编码的前段不会回写"
