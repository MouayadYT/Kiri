<#
.SYNOPSIS
    Adds, replaces or removes the models and voices in an Assistant assets folder, and writes the manifest the Assistant checks them against.

.DESCRIPTION
    The assets folder is <install folder>\assets, or the assets folder of a package being built. It holds
        models\manifest.json   models\<profile id>\model.gguf, mmproj.gguf
        voices\manifest.json   voices\<engine id>\...
    The manifest lists every file with its size and SHA-256. The Assistant verifies those before it loads a packaged model (and in the background when it
    starts), so a missing, truncated or changed file is found and the model is not loaded from it. Nothing here is compiled into the program: models and
    voices can be put in, replaced or taken out of an installed copy without rebuilding or reinstalling it. Close the Assistant first when replacing a
    model: while it is loaded, its engine has the file open.

.PARAMETER AssetsDirectory
    The assets folder. It is created if it does not exist.

.PARAMETER Model
    A model profile to add or replace: "id=<model.gguf>[;<mmproj.gguf>]". The id is one of the app's profiles (chat-4b, chat-9b). The files must be GGUF files.
    Example: -Model "chat-4b=C:\models\qwen3-4b-q4_k_m.gguf;C:\models\qwen3-4b-mmproj-f16.gguf"

.PARAMETER Voice
    A text-to-speech engine to add or replace: "id=<folder>". The id is one of the app's engines (kitten-tts-mini, kokoro-82m-onnx, piper); every file under
    the folder (at most 1000) belongs to the engine. Example: -Voice "piper=C:\voices\piper"

.PARAMETER Remove
    A model or voice to take out: "model:<id>" or "voice:<id>".

.PARAMETER Verify
    Checks every file of every group against its manifest (reading all of them) and says what is wrong. Exit code 1 when anything is.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AssetsDirectory,
    [string[]]$Model = @(),
    [string[]]$Voice = @(),
    [string[]]$Remove = @(),
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageCommon.ps1')

$assets = [System.IO.Path]::GetFullPath($AssetsDirectory)
$kinds = @{ models = (Join-Path $assets 'models'); voices = (Join-Path $assets 'voices') }
$changed = @{ models = $false; voices = $false }
$manifests = @{}

function Read-KindManifest {
    param([string]$Kind)
    $path = Join-Path $kinds[$Kind] 'manifest.json'
    try { return (Read-AssetManifest -Path $path) }
    catch {
        Write-Warning "The $Kind manifest could not be read ($($_.Exception.Message)); it is started again with what is added now."
        return ([ordered]@{})
    }
}

foreach ($kind in @('models', 'voices')) { $manifests[$kind] = Read-KindManifest -Kind $kind }

function Split-Spec {
    param([string]$Spec, [string]$What)
    $index = $Spec.IndexOf('=')
    if ($index -lt 1) { throw "$What '$Spec' must look like id=path." }
    $id = $Spec.Substring(0, $index).Trim()
    if (-not (Test-AssetGroupId $id)) { throw "'$id' is not a valid id: use lower case letters, digits and hyphens." }
    return [pscustomobject]@{ Id = $id; Value = $Spec.Substring($index + 1).Trim().Trim('"') }
}

function Test-Gguf {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "The file '$Path' does not exist." }
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $magic = New-Object byte[] 4
        $read = $stream.Read($magic, 0, 4)
        if ($read -ne 4 -or [System.Text.Encoding]::ASCII.GetString($magic) -ne 'GGUF') { throw "The file '$Path' is not a GGUF model file." }
    } finally { $stream.Dispose() }
}

# Builds the group in a folder beside its final place, checks it, then swaps it in, so a failed copy never replaces a good group.
function Install-Group {
    param([string]$Kind, [string]$Id, [scriptblock]$Fill)
    $kindDirectory = $kinds[$Kind]
    $target = Join-Path $kindDirectory $Id
    $stage = $target + '.new'
    New-Item -ItemType Directory -Force -Path $kindDirectory | Out-Null
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        & $Fill $stage
        $files = Get-AssetGroupFiles -GroupDirectory $stage
    } catch {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Rename-Item -LiteralPath $stage -NewName $Id
    $manifests[$Kind][$Id] = $files
    $changed[$Kind] = $true
    $bytes = ($files | Measure-Object -Property Size -Sum).Sum
    Write-Host ("  {0}/{1}: {2} file(s), {3:N1} MB" -f $Kind, $Id, $files.Count, ($bytes / 1MB))
}

foreach ($spec in $Model) {
    $parsed = Split-Spec -Spec $spec -What 'Model'
    $parts = @($parsed.Value.Split(';') | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
    if ($parts.Count -lt 1 -or $parts.Count -gt 2) { throw "Model '$spec' must name a model file and optionally a projector file, separated by a semicolon." }
    foreach ($part in $parts) { Test-Gguf -Path $part }
    $fill = {
        param($stage)
        Copy-Item -LiteralPath $parts[0] -Destination (Join-Path $stage 'model.gguf')
        if ($parts.Count -eq 2) { Copy-Item -LiteralPath $parts[1] -Destination (Join-Path $stage 'mmproj.gguf') }
    }.GetNewClosure()
    Install-Group -Kind 'models' -Id $parsed.Id -Fill $fill
}

foreach ($spec in $Voice) {
    $parsed = Split-Spec -Spec $spec -What 'Voice'
    $source = $parsed.Value
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "The voice folder '$source' does not exist." }
    $fill = {
        param($stage)
        Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse
    }.GetNewClosure()
    Install-Group -Kind 'voices' -Id $parsed.Id -Fill $fill
}

foreach ($spec in $Remove) {
    $match = [regex]::Match($spec, '^(model|voice):(.+)$')
    if (-not $match.Success) { throw "Remove '$spec' must look like model:<id> or voice:<id>." }
    $kind = if ($match.Groups[1].Value -eq 'model') { 'models' } else { 'voices' }
    $id = $match.Groups[2].Value.Trim()
    if (-not (Test-AssetGroupId $id)) { throw "'$id' is not a valid id." }
    $target = Join-Path $kinds[$kind] $id
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    if ($manifests[$kind].Contains($id)) { $manifests[$kind].Remove($id); $changed[$kind] = $true }
    Write-Host "  removed $kind/$id"
}

foreach ($kind in @('models', 'voices')) {
    if (-not $changed[$kind]) { continue }
    New-Item -ItemType Directory -Force -Path $kinds[$kind] | Out-Null
    Write-AssetManifest -Path (Join-Path $kinds[$kind] 'manifest.json') -Groups $manifests[$kind]
    Write-Host ("  wrote {0}\manifest.json ({1} group(s))" -f $kind, $manifests[$kind].Count)
}

if ($Verify) {
    $problems = 0
    foreach ($kind in @('models', 'voices')) {
        $groups = Read-KindManifest -Kind $kind
        foreach ($id in $groups.Keys) {
            $found = Test-AssetGroup -GroupDirectory (Join-Path $kinds[$kind] $id) -Files $groups[$id]
            if ($found.Count -eq 0) { Write-Host "  ok       $kind/$id" }
            else { $problems += $found.Count; Write-Host "  PROBLEM  $kind/$id : $($found -join '; ')" }
        }
    }
    if ($problems -gt 0) { exit 1 }
}
