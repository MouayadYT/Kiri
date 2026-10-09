<#
.SYNOPSIS
    Removes the Assistant for the current user: its program files, its Start menu shortcut, its entry in Settings > Apps, and what it registered in the
    user's registry (the File Explorer entry, the browser bridge, start with Windows). The Assistant's own data is kept unless it is asked for.

.DESCRIPTION
    Run by the native Inno Setup uninstaller, by Settings > Apps > Installed apps > Assistant > Uninstall, or by hand from the package folder or from <install folder>\tools.
    Kept by default, in %LOCALAPPDATA%\Assistant: settings, conversation history, connected apps (integrations) with their runtimes, the models folder
    you added models to, caches and logs. Removing that too is what -RemoveUserData does (and what the prompt offers); the secrets the Assistant stored in
    Windows Credential Manager under "Assistant/" go with it.
    The File Explorer entry, the browser bridge and the start-with-Windows entry are removed only when they point into the install folder, so a copy
    of the Assistant run from somewhere else keeps its own.

.PARAMETER InstallDirectory   The install folder. Found from where this script is, or from Settings > Apps, when not given.
.PARAMETER DataDirectory      The Assistant's data folder. Defaults to %LOCALAPPDATA%\Assistant.
.PARAMETER RemoveUserData     Also delete the data folder and the stored secrets.
.PARAMETER KeepAssets         Keep <install folder>\assets (the packaged models and voices) when removing the program.
.PARAMETER Quiet              Ask nothing. Without -RemoveUserData the data is kept.
.PARAMETER Force              Ends a running copy that does not close when asked.
.PARAMETER PreserveInstallDirectory  Leaves program-file deletion to the native Inno Setup uninstaller after registry and integration cleanup.
.PARAMETER KeepSecrets        With -RemoveUserData, leaves what the Assistant stored in Windows Credential Manager where it is.

.NOTES
    What it did, and what went wrong if anything did, is written to %TEMP%\Assistant-uninstall.log: the uninstaller runs it with no window.
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'Assistant'),
    [string]$StartMenuDirectory = [Environment]::GetFolderPath('Programs'),
    [switch]$RemoveUserData,
    [switch]$KeepAssets,
    [switch]$Quiet,
    [switch]$Force,
    [switch]$PreserveInstallDirectory,
    [switch]$KeepSecrets,
    [switch]$FromCopy
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageCommon.ps1')

function Confirm-Choice {
    param([string]$Question, [bool]$Default)
    if ($Quiet) { return $Default }
    $suffix = if ($Default) { ' [Y/n] ' } else { ' [y/N] ' }
    try { $answer = Read-Host ($Question + $suffix) } catch { return $Default }
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim().StartsWith('y', [System.StringComparison]::OrdinalIgnoreCase)
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory)][string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

try {
    # ---- Which installation ----------------------------------------------------------------------------------------------------------
    if (-not $InstallDirectory) {
        $beside = Split-Path $PSScriptRoot -Parent
        if ((Split-Path $PSScriptRoot -Leaf) -ieq 'tools' -and (Test-Path -LiteralPath (Join-Path $beside 'Assistant.UI.exe'))) { $InstallDirectory = $beside }
        elseif (Test-Path -LiteralPath $script:UninstallKey) { $InstallDirectory = [string](Get-ItemProperty -LiteralPath $script:UninstallKey).InstallLocation }
        else { $InstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Assistant' }
    }
    $install = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $data = [System.IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
    if ($StartMenuDirectory) { $StartMenuDirectory = [System.IO.Path]::GetFullPath($StartMenuDirectory) }
    $manifestPath = Join-Path $install 'package-manifest.json'

    # Now that the folders are known by their full names: not from inside the folder that is being removed. The uninstaller starts this script
    # there, and a process keeps its working folder from being deleted; a compiler started from there also finds the app's own assemblies before
    # Windows' (see Add-SetupType in PackageCommon.ps1).
    try {
        [Environment]::CurrentDirectory = [Environment]::SystemDirectory
        Set-Location -LiteralPath ([Environment]::SystemDirectory)
    } catch { }
    if (-not (Test-Path -LiteralPath $install -PathType Container)) { throw "There is no installation at '$install'." }
    $isOurs = $false
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try { $isOurs = ((Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json).product -eq 'Assistant') } catch { $isOurs = $false }
    }
    if (-not $isOurs) { throw "'$install' is not an Assistant installation (it has no package-manifest.json), so nothing was removed." }
    if ($RemoveUserData -and -not (Test-SafeDirectoryName -Directory $data -LeafName 'Assistant')) {
        throw "'$data' is not a folder named Assistant, so it will not be deleted. Nothing was removed."
    }

    # This script may be running from inside the folder it is about to delete: carry on from a copy.
    if (-not $FromCopy -and $PSScriptRoot.StartsWith($install + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        $copy = Join-Path ([System.IO.Path]::GetTempPath()) ('assistant-uninstall-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $copy | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-Assistant.ps1') -Destination $copy
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageCommon.ps1') -Destination $copy
        $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-ProcessArgument (Join-Path $copy 'Uninstall-Assistant.ps1')), '-InstallDirectory', (Quote-ProcessArgument $install),
            '-DataDirectory', (Quote-ProcessArgument $data), '-StartMenuDirectory', (Quote-ProcessArgument $StartMenuDirectory), '-FromCopy')
        foreach ($name in @('RemoveUserData', 'KeepAssets', 'Quiet', 'Force', 'PreserveInstallDirectory', 'KeepSecrets')) { if ((Get-Variable $name).Value) { $forward += "-$name" } }
        $child = Start-Process -FilePath 'powershell.exe' -ArgumentList $forward -Wait -PassThru -NoNewWindow
        Remove-Item -LiteralPath $copy -Recurse -Force -ErrorAction SilentlyContinue
        exit $child.ExitCode
    }

    # ---- Ask ---------------------------------------------------------------------------------------------------------------------------
    if (-not (Confirm-Choice -Question "Remove the Assistant from '$install'?" -Default $true)) { Write-Host 'Nothing was removed.'; exit 0 }
    if (-not $RemoveUserData -and -not $Quiet) {
        $RemoveUserData = Confirm-Choice -Question "Also delete your Assistant data in '$data' (settings, history, connected apps)?" -Default $false
    }

    Write-UninstallLog ("Uninstalling from the install folder; the data is " + $(if ($RemoveUserData) { 'removed too' } else { 'kept' }) + '.')

    # ---- Close it ------------------------------------------------------------------------------------------------------------------------
    if (-not (Stop-AssistantProcesses -InstallDirectory $install -Force:$Force)) {
        throw 'The Assistant is running and did not close. Choose Exit from its icon in the notification area and run the uninstaller again, or use -Force.'
    }

    # ---- What it registered, when it points at this copy -----------------------------------------------------------------------------
    Write-Host 'Removing what the Assistant registered...'
    $explorer = Join-Path $install 'Assistant.ExplorerExtension.exe'
    $bridge = Join-Path $install 'Assistant.BrowserBridge.exe'
    if (Test-PointsInto -Text (Get-RegistryDefaultValue -KeyPath $script:ExplorerVerbCommandKey) -Directory $install) {
        if (Invoke-EntryPoint -Executable $explorer -Command 'unregister') { Write-Host '  File Explorer entry removed.' } else { Write-Warning 'The File Explorer entry could not be removed.' }
    }
    if (Test-PointsInto -Text (Get-BrowserHostPath) -Directory $install) {
        if (Invoke-EntryPoint -Executable $bridge -Command 'unregister') { Write-Host '  Browser bridge removed.' } else { Write-Warning 'The browser bridge could not be removed.' }
    }
    if (Test-Path -LiteralPath $script:RunKey) {
        $run = (Get-Item -LiteralPath $script:RunKey).GetValue($script:ValueName, $null)
        if ($null -ne $run -and (Test-PointsInto -Text ([string]$run) -Directory $install)) {
            Remove-ItemProperty -LiteralPath $script:RunKey -Name $script:ValueName
            if (Test-Path -LiteralPath $script:StartupApprovedKey) { Remove-ItemProperty -LiteralPath $script:StartupApprovedKey -Name $script:ValueName -ErrorAction SilentlyContinue }
            Write-Host '  Start with Windows removed.'
        }
    }
    $shortcut = Join-Path $StartMenuDirectory 'Assistant.lnk'
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force; Write-Host '  Start menu shortcut removed.' }
    if (Test-Path -LiteralPath $script:UninstallKey) {
        $location = [string](Get-ItemProperty -LiteralPath $script:UninstallKey).InstallLocation
        if ([string]::Equals($location.TrimEnd('\'), $install, [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $script:UninstallKey -Recurse -Force
            Write-Host '  Settings > Apps entry removed.'
        }
    }

    # ---- The program files ---------------------------------------------------------------------------------------------------------------
    if ($PreserveInstallDirectory) {
        Write-Host 'Leaving the program files for the native uninstaller to remove...'
    }
    elseif ($KeepAssets) {
        Write-Host 'Removing the program files...'
        foreach ($item in (Get-ChildItem -LiteralPath $install -Force | Where-Object { $_.Name -ine 'assets' })) { Remove-Item -LiteralPath $item.FullName -Recurse -Force }
        Write-Host ("  The packaged models and voices were kept in '{0}'." -f (Join-Path $install 'assets'))
    }
    else {
        Write-Host 'Removing the program files...'
        if (-not (Remove-FolderTree -Directory $install)) { throw "The program files in '$install' could not be removed completely." }
    }

    # ---- The user's data, only when asked ---------------------------------------------------------------------------------------------------
    # The folder is held by whatever still has a file in it open: the model host's leftovers, a connected app's runtime, a virus scanner that is
    # reading a model. What is the Assistant's own is closed; the folder is then renamed and deleted, and what cannot be deleted now is deleted at
    # the next sign-in, so that no "Assistant" folder is left behind either way.
    $finishesAtSignIn = $false
    $heldByOther = $false
    if ($RemoveUserData) {
        if (Test-Path -LiteralPath $data) {
            $holders = Stop-ProcessesHoldingFolder -Directory $data -InstallDirectory $install -Force:$Force
            switch (Remove-FolderCompletely -Directory $data) {
                'Removed' { Write-Host ("  Data folder removed: {0}" -f $data); Write-UninstallLog 'The data folder was removed.' }
                'AtSignIn' {
                    $finishesAtSignIn = $true
                    Write-Warning 'A few files of the data folder were still in use. They are deleted the next time you sign in.'
                    Write-UninstallLog 'The data folder is out of the way; a few files that were in use are deleted at the next sign-in.'
                }
                default {
                    # Everything that could go has gone; what is left is what another program still holds. The rest of the uninstall carries on.
                    $heldByOther = $true
                    $heldBy = if ($holders.Count -gt 0) { ($holders -join ', ') } else { 'another program' }
                    Write-Warning ("The data folder '{0}' could not be removed completely: {1} still has files in it open. Close it and delete the folder." -f $data, $heldBy)
                    Write-UninstallLog ("The data folder could not be removed completely: " + $heldBy + " still has files in it open.")
                }
            }
        }
        else {
            # An earlier removal that could not finish may have left a renamed copy beside where the folder was.
            [void](Remove-FolderCompletely -Directory $data)
        }
        if (-not $KeepSecrets) {
            foreach ($line in @(cmdkey /list 2>$null)) {
                $match = [regex]::Match([string]$line, 'Target:\s*\w+:target=(Assistant/.+)$')
                if ($match.Success) { cmdkey /delete:$($match.Groups[1].Value.Trim()) | Out-Null; Write-Host '  A stored secret was removed from Credential Manager.' }
            }
        }
    }
    else {
        Write-Host ("Your data was kept in '{0}'. Delete that folder to remove it too." -f $data)
    }

    Write-Host ''
    Write-Host 'The Assistant was removed.' -ForegroundColor Green
    Write-UninstallLog 'The Assistant was removed.'
    # 4 tells the native uninstaller that another program still holds some of the data, and 3 that the rest of it goes at the next sign-in, so that
    # it can say which.
    if ($heldByOther) { exit 4 }
    if ($finishesAtSignIn) { exit 3 }
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('Uninstall failed: ' + $_.Exception.Message) -ForegroundColor Red
    Write-UninstallLog ('Uninstall failed: ' + $_.Exception.Message + ' (' + $_.InvocationInfo.ScriptLineNumber + ')')
    exit 1
}
