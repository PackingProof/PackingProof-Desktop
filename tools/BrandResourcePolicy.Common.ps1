function Resolve-BrandOptionalResource {
    param(
        [Parameter(Mandatory = $true)][string]$Requested,
        [Parameter(Mandatory = $true)][bool]$Enabled,
        [Parameter(Mandatory = $true)][string]$ResourceName
    )

    switch ($Requested.ToLowerInvariant()) {
        "auto" { return $Enabled }
        "include" {
            if (-not $Enabled) {
                throw "软件配置未启用 $ResourceName，不能显式要求打包该资源"
            }
            return $true
        }
        "exclude" { return $false }
        default {
            throw "未知资源选择 '$Requested'：$ResourceName"
        }
    }
}

function Get-BrandResourcePolicy {
    param([Parameter(Mandatory = $true)]$Brand)

    return [pscustomobject]@{
        VisionObb = [bool]$Brand.Features.inputs.visionObb
        Tts = [bool]$Brand.Features.capabilities.tts
        DefaultScripts = [bool]$Brand.Features.capabilities.userscript
    }
}
