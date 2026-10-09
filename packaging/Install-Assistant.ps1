<#
.SYNOPSIS
    Installs (or upgrades, or repairs) the Assistant for the current user. No administrator rights are needed and nothing outside the user's own folders and
    registry keys is touched.

.DESCRIPTION
    Run it from the package folder (or double-click Install-Assistant.cmd). It
      1. checks every program file of the package against package-manifest.json before changing anything;
      2. closes a copy of the Assistant that runs from the install folder (a copy run from anywhere else is never touched);
      3. makes the install folder match the package (program files; the models and voices are handled on their own);
      4. copies the packaged models and voices (the package's app\assets) into <install folder>\assets, skipping files that are already there, verifies every one against its
         manifest (SHA-256) and merges the manifest, so models and voices the user added themselves are kept;
      5. points the things the user has switched on at the new copy: the File Explorer entry and the browser bridge (only if they are on in the
         Assistant's settings, as the Assistant itself would at its next start) and the "start with Windows" entry (only if it exists);
      6. adds a Start menu shortcut and an entry in Settings > Apps > Installed apps, which runs Uninstall-Assistant.ps1.
    The Assistant's own data (settings, history, installed connected apps and their runtimes, models the user added in their own models folder) lives
    in %LOCALAPPDATA%\Assistant and is never changed by installing, upgrading or uninstalling, unless Uninstall-Assistant.ps1 is told to remove it.

.PARAMETER InstallDirectory
    Where the program is installed. Defaults to %LOCALAPPDATA%\Programs\Assistant. The folder must be empty, not exist, or hold an earlier install of
    the Assistant: its contents are replaced by the package's (except its models and voices, which are merged).

.PARAMETER DataDirectory
    The Assistant's data folder, which is only read here (its settings say which integrations are on). Defaults to %LOCALAPPDATA%\Assistant.

.PARAMETER EnableExplorerContextMenu  Register Ask Assistant in File Explorer's right-click menu, even if it is currently off in the Assistant's settings.
.PARAMETER SkipIntegrations  Do not register the File Explorer entry or the browser bridge, even if the settings have them on.
.PARAMETER SkipShortcut      Do not add the Start menu shortcut.
.PARAMETER SkipAssets        Do not copy the packaged models and voices.
.PARAMETER SkipUninstallEntry  Do not add the entry in Settings > Apps.
.PARAMETER StartMenuDirectory  Where the shortcut goes. Defaults to the user's Start menu Programs folder.
.PARAMETER Force             Ends a running copy that does not close when asked.
.PARAMETER Launch            Starts the Assistant when the installation is done.
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Assistant'),
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'Assistant'),
    [switch]$EnableExplorerContextMenu,
    [switch]$SkipIntegrations,
    [switch]$SkipShortcut,
    [switch]$SkipAssets,
    [switch]$SkipUninstallEntry,
    [string]$StartMenuDirectory = [Environment]::GetFolderPath('Programs'),
    [switch]$Force,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageCommon.ps1')

function Write-Step { param([string]$Text) Write-Host ''; Write-Host $Text }

try {
    $package = $PSScriptRoot
    $install = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $data = [System.IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')

    # ---- The package must be whole --------------------------------------------------------------------------------------------------
    $manifestPath = Join-Path $package 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'package-manifest.json is not beside this script. Run the installer from the package folder, with all of its files.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.product -ne 'Assistant' -or $manifest.schemaVersion -ne 1) { throw 'This is not an Assistant package this installer understands.' }
    Write-Host ("Assistant {0} ({1})" -f $manifest.version, $manifest.runtime)

    # ---- The install folder must be one the installer may replace the contents of -------------------------------------------------------
    $root = [System.IO.Path]::GetPathRoot($install).TrimEnd('\')
    $never = @($root, $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:SystemRoot,
        [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('MyDocuments'), $data) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') }
    if ($never -contains $install) { throw "The folder '$install' is not a place to install into. Choose a folder of its own, such as the default." }
    if ($install.StartsWith($package.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase) -or $package.StartsWith($install + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The install folder and the package folder may not contain one another.'
    }
    $previous = $null
    $staleNativeUninstallerOnly = $false
    $installedManifest = Join-Path $install 'package-manifest.json'
    if (Test-Path -LiteralPath $install) {
        $existing = @(Get-ChildItem -LiteralPath $install -Force)
        if (Test-Path -LiteralPath $installedManifest -PathType Leaf) {
            try { $previous = Get-Content -LiteralPath $installedManifest -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $previous = $null }
        }
        if ($existing.Count -gt 0 -and ($null -eq $previous -or $previous.product -ne 'Assistant')) {
            $staleNativeUninstallerOnly = @($existing | Where-Object {
                -not $_.PSIsContainer -and $_.Name -match '^unins\d+\.(exe|dat|msg)$'
            }).Count -eq $existing.Count
            if ($staleNativeUninstallerOnly) {
                Write-Host '  Recovering an incomplete earlier Inno Setup attempt (only its native uninstaller files were left behind).'
            }
            else {
                throw "The folder '$install' is not empty and is not an Assistant installation. Choose an empty folder, or remove what is there."
            }
        }
    }

    # ---- 1. Check the package ---------------------------------------------------------------------------------------------------------
    Write-Step ("Checking the package ({0} files)..." -f @($manifest.files).Count)
    $problems = @()
    foreach ($entry in $manifest.files) {
        $path = Join-Path $package (([string]$entry.path) -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $problems += ($entry.path + ': missing'); continue }
        if ((Get-Item -LiteralPath $path).Length -ne [int64]$entry.size) { $problems += ($entry.path + ': wrong size'); continue }
        if ((Get-FileSha256 $path) -ne ([string]$entry.sha256).ToLowerInvariant()) { $problems += ($entry.path + ': checksum differs') }
    }
    if ($problems.Count -gt 0) {
        throw ("The package is damaged or incomplete ({0} file(s)); nothing was installed. Copy it again. First problems: {1}" -f $problems.Count, (($problems | Select-Object -First 8) -join '; '))
    }

    # ---- 2. Close a running copy --------------------------------------------------------------------------------------------------------
    if (-not (Stop-AssistantProcesses -InstallDirectory $install -Force:$Force)) {
        throw 'The Assistant is running from the install folder and did not close. Choose Exit from its icon in the notification area and run the installer again, or use -Force.'
    }

    # ---- 3. Program files ---------------------------------------------------------------------------------------------------------------
    if ($null -ne $previous) { Write-Step ("Upgrading from version {0}..." -f $previous.version) } else { Write-Step 'Installing the program files...' }
    New-Item -ItemType Directory -Force -Path $install | Out-Null

    # The models and voices are not mirrored with the program files: they are merged in step 4, which keeps those the user added. They sit in the
    # package's app\assets (beside the program, where the program looks for them) and go to <install folder>\assets\models and \voices.
    $packageAssets = Join-Path $package 'app\assets'
    $skipFolders = @()
    foreach ($kind in @('models', 'voices')) {
        $skipFolders += (Join-Path $packageAssets $kind)
        $skipFolders += (Join-Path (Join-Path $install 'assets') $kind)
    }
    # Inno Setup owns unins*.exe/.dat in the install root. Preserve those files so its native uninstaller remains available after
    # the verified program-file mirror (folder-based installs do not have them).
    & robocopy (Join-Path $package 'app') $install /MIR /XD @skipFolders /XF 'unins*.exe' 'unins*.dat' 'unins*.msg' /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the program files failed (robocopy exit code $LASTEXITCODE). Is a file in the install folder open in another program?" }
    Copy-Item -LiteralPath $manifestPath -Destination $installedManifest -Force

    # ---- 4. Models and voices ----------------------------------------------------------------------------------------------------------
    if (-not $SkipAssets -and (Test-Path -LiteralPath $packageAssets -PathType Container)) {
        Write-Step 'Installing the models and voices...'
        $installAssets = Join-Path $install 'assets'
        foreach ($kind in @('models', 'voices')) {
            $sourceKind = Join-Path $packageAssets $kind
            $sourceManifest = Join-Path $sourceKind 'manifest.json'
            if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf)) { continue }
            $groups = Read-AssetManifest -Path $sourceManifest
            $targetKind = Join-Path $installAssets $kind
            New-Item -ItemType Directory -Force -Path $targetKind | Out-Null
            foreach ($id in $groups.Keys) {
                $files = $groups[$id]
                $sourceGroup = Join-Path $sourceKind $id
                $targetGroup = Join-Path $targetKind $id
                $copied = 0
                foreach ($file in $files) {
                    $relative = $file.Path -replace '/', '\'
                    $from = Join-Path $sourceGroup $relative
                    $to = Join-Path $targetGroup $relative
                    if (Test-Path -LiteralPath $to -PathType Leaf) {
                        $have = Get-Item -LiteralPath $to
                        if ($have.Length -eq $file.Size -and $have.LastWriteTimeUtc -eq (Get-Item -LiteralPath $from).LastWriteTimeUtc) { continue }
                        if ($have.Length -eq $file.Size -and (Get-FileSha256 $to) -eq $file.Sha256) { continue }
                    }
                    New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null
                    $partial = $to + '.partial'
                    Copy-Item -LiteralPath $from -Destination $partial -Force
                    Move-Item -LiteralPath $partial -Destination $to -Force
                    $copied++
                }

                # Files an older version of the group had and this one does not are not part of it any more.
                if (Test-Path -LiteralPath $targetGroup -PathType Container) {
                    $wanted = @{}
                    foreach ($file in $files) { $wanted[((Join-Path $targetGroup ($file.Path -replace '/', '\')).ToLowerInvariant())] = $true }
                    foreach ($extra in (Get-ChildItem -LiteralPath $targetGroup -Recurse -File)) {
                        if (-not $wanted.ContainsKey($extra.FullName.ToLowerInvariant())) { Remove-Item -LiteralPath $extra.FullName -Force }
                    }
                }

                $found = Test-AssetGroup -GroupDirectory $targetGroup -Files $files
                if ($found.Count -gt 0) { throw ("The installed files of $kind/$id do not match their manifest: " + (($found | Select-Object -First 5) -join '; ')) }
                Write-Host ("  {0}/{1}: {2} file(s), {3} copied, all verified" -f $kind, $id, $files.Count, $copied)
            }

            # The packaged groups replace those of the same id; groups the user added are kept.
            $targetManifest = Join-Path $targetKind 'manifest.json'
            $merged = [ordered]@{}
            try { $merged = Read-AssetManifest -Path $targetManifest } catch { $merged = [ordered]@{} }
            foreach ($id in $groups.Keys) { $merged[$id] = $groups[$id] }
            Write-AssetManifest -Path $targetManifest -Groups $merged
        }
    }

    # ---- 5. What the user has switched on follows the new copy ------------------------------------------------------------------------
    $explorerExe = Join-Path $install 'Assistant.ExplorerExtension.exe'
    $bridgeExe = Join-Path $install 'Assistant.BrowserBridge.exe'
    $appExe = Join-Path $install 'Assistant.UI.exe'
    if (-not $SkipIntegrations) {
        Write-Step 'Pointing the integrations at this copy...'
        $choices = Get-IntegrationChoices -DataDirectory $data
        if ($EnableExplorerContextMenu) {
            $choices.Explorer = $true
        }
        if ($choices.Explorer) {
            if (Invoke-EntryPoint -Executable $explorerExe -Command 'register') {
                Write-Host '  File Explorer: Ask Assistant registered.'
                if ($EnableExplorerContextMenu) {
                    try { Set-ExplorerIntegrationChoice -DataDirectory $data -Enabled $true }
                    catch { Write-Warning ('The Explorer menu was registered, but its setting could not be saved: ' + $_.Exception.Message) }
                }
            } else { Write-Warning 'The File Explorer entry could not be registered; the Assistant tries again when it starts.' }
        } else { Write-Host '  File Explorer: off in the Assistant''s settings, left alone (turn it on in Settings > Integrations).' }
        if ($choices.Browser) {
            if (Invoke-EntryPoint -Executable $bridgeExe -Command 'register') { Write-Host '  Browsers: native-messaging host registered.' } else { Write-Warning 'The browser bridge could not be registered; the Assistant tries again when it starts.' }
        } else { Write-Host '  Browsers: off in the Assistant''s settings, left alone (turn it on in Settings > Integrations).' }
    }
    if (Test-Path -LiteralPath $script:RunKey) {
        if ($null -ne (Get-Item -LiteralPath $script:RunKey).GetValue($script:ValueName, $null)) {
            Set-ItemProperty -LiteralPath $script:RunKey -Name $script:ValueName -Value ('"' + $appExe + '" --background')
            Write-Host '  Start with Windows: now starts this copy.'
        }
    }

    # ---- 6. Start menu and Settings > Apps --------------------------------------------------------------------------------------------
    if (-not $SkipShortcut) {
        New-Item -ItemType Directory -Force -Path $StartMenuDirectory | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut((Join-Path $StartMenuDirectory 'Assistant.lnk'))
        $link.TargetPath = $appExe
        $link.WorkingDirectory = $install
        $link.Description = 'Assistant'
        $link.IconLocation = $appExe + ',0'
        $link.Save()
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        Write-Host '  Start menu: Assistant'
    }
    $hasNativeUninstaller = $null -ne $previous -and @(Get-ChildItem -LiteralPath $install -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue).Count -gt 0
    if ($SkipUninstallEntry -or $hasNativeUninstaller) {
        # An Inno Setup install has its own Add/Remove Programs entry. Remove the legacy script entry if this is an upgrade
        # from a folder-based package so the user sees only the native uninstaller.
        if (Test-Path -LiteralPath $script:UninstallKey) {
            $legacyLocation = [string](Get-ItemProperty -LiteralPath $script:UninstallKey).InstallLocation
            if ([string]::Equals($legacyLocation.TrimEnd('\'), $install, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $script:UninstallKey -Recurse -Force
            }
        }
    }
    else {
        $bytes = (Get-ChildItem -LiteralPath $install -Recurse -File | Measure-Object -Property Length -Sum).Sum
        New-Item -Path $script:UninstallKey -Force | Out-Null
        $uninstaller = Join-Path $install 'tools\Uninstall-Assistant.ps1'
        Set-ItemProperty -LiteralPath $script:UninstallKey -Name DisplayName -Value 'Assistant'
        Set-ItemProperty -LiteralPath $script:UninstallKey -Name DisplayVersion -Value ([string]$manifest.version)
        Set-ItemProperty -LiteralPath $script:UninstallKey -Name InstallLocation -Value $install
        Set-ItemProperty -LiteralPath $script:UninstallKey -Name DisplayIcon -Value ($appExe + ',0')
        Set-ItemProperty -LiteralPath $script:UninstallKey -Name UninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $uninstaller + '"')
        Set-ItemProperty -LiteralPath $script:UninstallKey -Name InstallDate -Value (Get-Date -Format 'yyyyMMdd')
        New-ItemProperty -LiteralPath $script:UninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -LiteralPath $script:UninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -LiteralPath $script:UninstallKey -Name EstimatedSize -Value ([int][math]::Min([int]::MaxValue, [math]::Round($bytes / 1KB))) -PropertyType DWord -Force | Out-Null
        Write-Host '  Settings > Apps: Assistant (with Uninstall)'
    }

    Write-Host ''
    Write-Host ("Installed: {0}" -f $install) -ForegroundColor Green
    Write-Host 'Start it from the Start menu. Its data (settings, history, connected apps) is kept in'
    Write-Host ("  {0}" -f $data)
    Write-Host 'and is not touched when you upgrade or uninstall unless you ask for that.'
    if ($Launch) { Start-Process -FilePath $appExe -WorkingDirectory $install }
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('Installation failed: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
