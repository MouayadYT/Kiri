<#
.SYNOPSIS
    Runs the v1 smoke tests (tests\Assistant.SmokeTests, RELEASE_CHECKLIST.md part 1) and exits with their result.

.DESCRIPTION
    The tests are built into a folder of their own, so a copy of the Assistant that is running from this repository (which locks its bin folder) is not
    disturbed and does not make the build fail. They open real windows for a few seconds, register a shortcut, start the model engine's stand-in and the
    two helper programs, and install and uninstall a real package into the temp folder: leave the keyboard and mouse alone while they run.
    They never read or write the user's data folder, never start the installed Assistant, and put back the one registry value the installer changes.

.PARAMETER PackageDirectory
    A package folder (Assistant-<version>-win-x64) to check instead of building one, such as the package that is going to be handed over.

.PARAMETER Filter
    A dotnet test filter, to run some of the checks, for example "FullyQualifiedName~LocalModel".

.PARAMETER Configuration
    The build configuration of the tests. Release unless a debug run is wanted.

.PARAMETER ArtifactsPath
    Where the tests are built. Defaults to a folder in the temp folder.
#>
[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$Filter,
    [string]$Configuration = 'Release',
    [string]$ArtifactsPath = (Join-Path $env:TEMP 'assistant-smoke-artifacts')
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent

if ($PackageDirectory) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json'))) { throw "'$PackageDirectory' is not a package folder (it has no package-manifest.json)." }
    $env:ASSISTANT_SMOKE_PACKAGE_DIR = (Resolve-Path -LiteralPath $PackageDirectory).Path
}

$arguments = @('test', (Join-Path $repository 'tests\Assistant.SmokeTests'), '-c', $Configuration, '--artifacts-path', $ArtifactsPath, '--nologo')
if ($Filter) { $arguments += @('--filter', $Filter) }
Write-Host 'Running the smoke tests (windows will open for a few seconds; leave the keyboard and mouse alone)...'
& dotnet @arguments
$code = $LASTEXITCODE
if ($code -eq 0) { Write-Host 'The smoke tests passed. Next: RELEASE_CHECKLIST.md part 2 (checks by hand).' -ForegroundColor Green }
else { Write-Host 'The smoke tests did not pass; RELEASE_CHECKLIST.md part 1 says what each one checks.' -ForegroundColor Red }
exit $code
