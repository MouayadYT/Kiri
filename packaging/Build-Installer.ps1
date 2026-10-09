<#
.SYNOPSIS
    Builds a self-contained Windows setup executable and the portable package zip.

.DESCRIPTION
    Build-Package.ps1 produces the tested, per-user package used by the existing installer scripts.
    This script compiles a genuine Inno Setup installer around that package. Inno carries the package
    in its compressed payload, unpacks it to a temporary folder, and runs Install-Assistant.ps1 so the
    existing verified per-user install, upgrade and integration behavior is retained.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string[]]$Model = @(),
    [string[]]$Voice = @(),
    [string]$AssetsFrom,
    [switch]$IncludeSamples,
    [string]$Configuration = 'Release',
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'artifacts\installer' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$packageArgs = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Build-Package.ps1'),
    '-OutputDirectory', $OutputDirectory, '-CreateZip', '-Configuration', $Configuration
)
if ($Version) { $packageArgs += @('-Version', $Version) }
foreach ($value in $Model) { $packageArgs += @('-Model', $value) }
foreach ($value in $Voice) { $packageArgs += @('-Voice', $value) }
if ($AssetsFrom) { $packageArgs += @('-AssetsFrom', $AssetsFrom) }
if ($IncludeSamples) { $packageArgs += '-IncludeSamples' }

$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell)) { $windowsPowerShell = (Get-Command powershell.exe -ErrorAction Stop).Source }
& $windowsPowerShell @packageArgs
if ($LASTEXITCODE -ne 0) { throw "Build-Package.ps1 failed with exit code $LASTEXITCODE." }

$package = Get-ChildItem -LiteralPath $OutputDirectory -Directory -Filter 'Assistant-*-win-x64' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $package) { throw "The package folder was not created in '$OutputDirectory'." }
$manifest = Get-Content -LiteralPath (Join-Path $package.FullName 'package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$Version = [string]$manifest.version
$zip = Join-Path $OutputDirectory ($package.Name + '.zip')
if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "The package zip '$zip' was not created." }

$iss = Join-Path $PSScriptRoot 'Assistant.iss'
if (-not $IsccPath) {
    $isccCommand = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($isccCommand) { $IsccPath = $isccCommand.Source }
}
if (-not $IsccPath) {
    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles} 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { $IsccPath = $candidate; break }
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 or pass -IsccPath.'
}

Write-Host "Compiling Assistant $Version with Inno Setup..."
& $IsccPath "/DAppVersion=$Version" "/DPackageDir=$($package.FullName)" "/DOutputDir=$OutputDirectory" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }

$setup = Join-Path $OutputDirectory ("Assistant-{0}-win-x64-Setup.exe" -f $Version)
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Inno Setup did not create '$setup'." }

Write-Host ''
Write-Host "Setup:  $setup"
Write-Host "Package: $zip"
