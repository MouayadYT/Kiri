<#
.SYNOPSIS
    Builds the Assistant package: the program (self-contained, so no .NET install is needed), its model host and engine, the File Explorer entry
    point, the browser bridge and extension, the installer scripts, and optionally the models and voices.

.DESCRIPTION
    The package is an ordinary folder, Assistant-<version>-win-x64, that can be copied anywhere and installed with Install-Assistant.cmd:
        app\                    the program files, which are installed in the install folder
        app\assets\             the models and voices with their manifests (optional), beside Assistant.UI.exe where the program looks for them, so
                                app\Assistant.UI.exe finds them at once even when it is run from the package folder without installing
        package-manifest.json   the size and SHA-256 of every program file, which the installer checks before it installs anything
    It is not an MSIX: the Assistant registers its File Explorer entry and its browser bridge itself, in the user's own registry, and an MSIX would give
    it a private copy of that registry that Explorer and the browsers cannot see (PROJECT_SPEC section 5.7, D3).
    Nothing is signed and no community connected-app (MCP) server is included: integrations a user installs live in %LOCALAPPDATA%\Assistant and are
    never part of the package. The made-up sample server the demos use is left out unless -IncludeSamples is given.

.PARAMETER Version
    The package version. Defaults to the one in version.txt at the root of the repository.

.PARAMETER OutputDirectory
    Where the package folder (and the zip) are made. Defaults to artifacts\package in the repository.

.PARAMETER Model
    A model to include, as for Add-PackagedAssets.ps1: "id=<model.gguf>[;<mmproj.gguf>]". May be given more than once.

.PARAMETER Voice
    A text-to-speech engine to include, as for Add-PackagedAssets.ps1: "id=<folder>". May be given more than once.

.PARAMETER AssetsFrom
    An assets folder that already exists (made with Add-PackagedAssets.ps1, or the assets folder of an installed copy) whose models and voices are included as they are, instead of -Model and -Voice.

.PARAMETER IncludeSamples
    Keeps the demos' made-up MCP server in the package.

.PARAMETER CreateZip
    Also makes Assistant-<version>-win-x64.zip beside the folder (stored without compression: models do not compress).

.PARAMETER Configuration
    The build configuration. Release unless a debug package is wanted.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string[]]$Model = @(),
    [string[]]$Voice = @(),
    [string]$AssetsFrom,
    [switch]$IncludeSamples,
    [switch]$CreateZip,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageCommon.ps1')

$repository = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $versionFile = Join-Path $repository 'version.txt'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) { throw 'The version was not given and version.txt is not in the repository.' }
    $Version = (Get-Content -LiteralPath $versionFile -Raw -Encoding UTF8).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "The version '$Version' must look like 1.2.3." }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'artifacts\package' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($AssetsFrom -and ($Model.Count -gt 0 -or $Voice.Count -gt 0)) { throw 'Give -AssetsFrom or -Model/-Voice, not both.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (dotnet) is needed to build the package.' }

$name = "Assistant-$Version-win-x64"
$package = Join-Path $OutputDirectory $name
$work = Join-Path $OutputDirectory '_work'
$app = Join-Path $package 'app'
foreach ($path in @($package, $work)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $app | Out-Null

# 1. The program: every project of the app, self-contained for 64-bit Windows, in one folder. The UI project references the model host, the Explorer
#    entry point, the browser bridge and the engine (llama.cpp), so publishing it publishes them all with the same runtime.
Write-Host "Publishing the Assistant $Version ($Configuration)..."
$arguments = @(
    'publish', (Join-Path $repository 'src\Assistant.UI\Assistant.UI.csproj'),
    '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
    '-o', $app, '--artifacts-path', (Join-Path $work 'obj'),
    "-p:Version=$Version", "-p:InformationalVersion=$Version",
    '-p:DebugType=none', '-p:DebugSymbols=false', '-nologo', '-v', 'minimal'
)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

foreach ($required in @('Assistant.UI.exe', 'Assistant.ModelHost.exe', 'Assistant.VoiceHost.exe', 'Assistant.ExplorerExtension.exe', 'Assistant.BrowserBridge.exe',
        'Assistant.MicrosoftTodo.exe', 'Assistant.ico', 'llama.cpp\llama-server.exe', 'hostfxr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required))) { throw "The published program is missing $required." }
}

# 1b. Every program in the folder shares one copy of each library, so each program's dependency manifest (*.deps.json) must name the version that is
#     really there. A program built for one version of a library and handed a newer one starts, and then fails when the newer one needs something its
#     manifest never listed (the model host with System.Text.Json: no model could be loaded). A package like that is not made.
$mismatches = @(Get-PackageDependencyMismatches -AppDirectory $app)
if ($mismatches.Count -gt 0) {
    $lines = $mismatches | ForEach-Object { "  $($_.Program) was built for $($_.Assembly) $($_.BuiltFor), but the package ships $($_.Ships)." }
    throw ("Programs in the package disagree about a library they share:`n" + ($lines -join "`n") +
        "`nGive every project the same version of that library's package (see Directory.Build.props), then build again.")
}

# 2. The demos' made-up MCP server is not part of the program: it is left out unless asked for. No connected-app server is bundled either way.
if (-not $IncludeSamples) {
    Get-ChildItem -LiteralPath $app -Filter 'Assistant.SampleMcpServer.*' -File | Remove-Item -Force
}

# 3. The browser extension, which the user adds to Edge, Chrome or Brave from this folder ("Load unpacked"); the bridge that registers for it is a program above.
Copy-Item -LiteralPath (Join-Path $repository 'src\Assistant.BrowserBridge\extension') -Destination (Join-Path $app 'browser-extension') -Recurse

# 4. The scripts that go with an installed copy: the uninstaller, and the tool that adds or replaces models and voices.
$tools = Join-Path $app 'tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
. (Join-Path $PSScriptRoot 'Get-NativeRuntime.ps1')
Copy-Item -LiteralPath (Get-AssistantNativeRuntime -Repository $repository) -Destination (Join-Path $tools 'vc_redist.x64.exe')
foreach ($script in @('PackageCommon.ps1', 'Add-PackagedAssets.ps1', 'Uninstall-Assistant.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $tools
}

# 5. The scripts and notes at the package's root.
foreach ($script in @('Install-Assistant.ps1', 'Install-Assistant.cmd', 'Uninstall-Assistant.ps1', 'Uninstall-Assistant.cmd', 'PackageCommon.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $package
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Package-README.txt') -Destination (Join-Path $package 'README.txt')

# 6. Models and voices, in the program folder beside Assistant.UI.exe (app\assets\models and app\assets\voices): that is where the program looks for
#    them, so the folder works as it is, and they can still be replaced without rebuilding anything. (app\assets also holds the program's own icons.)
$assetsFolder = Join-Path $app 'assets'
if ($AssetsFrom) {
    if (-not (Test-Path -LiteralPath $AssetsFrom -PathType Container)) { throw "The assets folder '$AssetsFrom' does not exist." }
    Write-Host 'Copying the assets...'
    foreach ($kind in @('models', 'voices')) {
        $from = Join-Path $AssetsFrom $kind
        if (Test-Path -LiteralPath $from -PathType Container) { Copy-Item -LiteralPath $from -Destination (Join-Path $assetsFolder $kind) -Recurse }
    }
    & (Join-Path $PSScriptRoot 'Add-PackagedAssets.ps1') -AssetsDirectory $assetsFolder -Verify
    if ($LASTEXITCODE -ne 0) { throw 'The assets do not match their manifests.' }
}
elseif ($Model.Count -gt 0 -or $Voice.Count -gt 0) {
    Write-Host 'Adding the assets...'
    & (Join-Path $PSScriptRoot 'Add-PackagedAssets.ps1') -AssetsDirectory $assetsFolder -Model $Model -Voice $Voice
}

# 7. The manifest of the package: the size and SHA-256 of every program file and of the asset manifests, so the installer can tell a damaged copy
#    before it changes anything. (The files of the models and voices are checked against their own manifests, which this one pins.)
Write-Host 'Writing the package manifest...'
$entries = @()
$assetKinds = @('models', 'voices') | ForEach-Object { (Join-Path $assetsFolder $_) + '\' }
$listed = @(Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object {
        $path = $_.FullName
        $inGroup = $false
        foreach ($kindFolder in $assetKinds) {
            if ($path.StartsWith($kindFolder, [System.StringComparison]::OrdinalIgnoreCase) -and $path -ne ($kindFolder + 'manifest.json')) { $inGroup = $true }
        }
        -not $inGroup
    })
foreach ($file in ($listed | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($package.Length + 1) -replace '\\', '/'
    $entries += [pscustomobject]@{ Path = $relative; Size = [int64]$file.Length; Sha256 = Get-FileSha256 $file.FullName }
}
$text = New-Object System.Text.StringBuilder
[void]$text.AppendLine('{')
[void]$text.AppendLine('  "schemaVersion": 1,')
[void]$text.AppendLine('  "product": "Assistant",')
[void]$text.AppendLine('  "version": ' + (ConvertTo-JsonString $Version) + ',')
[void]$text.AppendLine('  "runtime": "win-x64",')
[void]$text.AppendLine('  "builtAt": ' + (ConvertTo-JsonString ((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))) + ',')
[void]$text.AppendLine('  "hasModels": ' + $(if (Test-Path -LiteralPath (Join-Path $assetsFolder 'models\manifest.json')) { 'true' } else { 'false' }) + ',')
[void]$text.AppendLine('  "hasVoices": ' + $(if (Test-Path -LiteralPath (Join-Path $assetsFolder 'voices\manifest.json')) { 'true' } else { 'false' }) + ',')
[void]$text.AppendLine('  "files": [')
for ($i = 0; $i -lt $entries.Count; $i++) {
    $entry = $entries[$i]
    [void]$text.AppendLine('    { "path": ' + (ConvertTo-JsonString $entry.Path) + ', "size": ' + $entry.Size + ', "sha256": ' + (ConvertTo-JsonString $entry.Sha256) + ' }' + $(if ($i -lt $entries.Count - 1) { ',' } else { '' }))
}
[void]$text.AppendLine('  ]')
[void]$text.AppendLine('}')
Write-Utf8NoBom -Path (Join-Path $package 'package-manifest.json') -Text $text.ToString()

Remove-Item -LiteralPath $work -Recurse -Force

if ($CreateZip) {
    $zip = Join-Path $OutputDirectory "$name.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Write-Host 'Making the zip...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $true)
}

$size = (Get-ChildItem -LiteralPath $package -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host "Package: $package"
Write-Host ("  {0} program file(s), {1:N0} MB in all; models: {2}; voices: {3}" -f $entries.Count, ($size / 1MB),
    $(if (Test-Path -LiteralPath (Join-Path $assetsFolder 'models\manifest.json')) { 'yes' } else { 'no' }),
    $(if (Test-Path -LiteralPath (Join-Path $assetsFolder 'voices\manifest.json')) { 'yes' } else { 'no' }))
Write-Host '  Install it with Install-Assistant.cmd in that folder, or run app\Assistant.UI.exe from it as it is: the models and voices are in app\assets.'
