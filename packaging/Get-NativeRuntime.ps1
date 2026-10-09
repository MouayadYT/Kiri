# Official, signed Microsoft Visual C++ x64 prerequisite. Pin bytes rather than the mutable aka.ms redirect.
function Get-AssistantNativeRuntime {
    param([Parameter(Mandatory = $true)][string]$Repository)
    $runtimeDirectory = Join-Path $Repository 'artifacts\native-runtime'
    New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
    $runtimeFile = Join-Path $runtimeDirectory 'vc_redist.x64.exe'
    $expectedHash = 'CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B'
    if (-not (Test-Path -LiteralPath $runtimeFile) -or (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash -ne $expectedHash) {
        $runtimeUrl = 'https://download.visualstudio.microsoft.com/download/pr/bd1c8d9d-ba95-4eee-bc6e-df1fcc876373/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe'
        Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeFile -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The native runtime prerequisite failed checksum verification.' }
    return $runtimeFile
}
