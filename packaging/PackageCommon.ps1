# Helpers shared by the packaging scripts (Build-Package, Add-PackagedAssets, Install-Assistant, Uninstall-Assistant).
# Windows PowerShell 5.1. Keep this file ASCII: PowerShell 5.1 reads a file without a byte order mark in the ANSI code page.
Set-StrictMode -Version 3.0

# The version of the asset manifest format (src/Assistant.Core/Assets/AssetManifest.cs, PROJECT_SPEC section 3.5).
$script:AssetManifestSchemaVersion = 1

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    # Get-FileHash is normally available in Windows PowerShell, but a process launched by a
    # self-contained setup host may not have the utility module on PSModulePath. Keep package
    # verification independent of module auto-loading.
    if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {
        return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Write-Utf8NoBom {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

# A string as a JSON string literal: quotes and backslashes escaped, anything outside printable ASCII as \uXXXX.
function ConvertTo-JsonString {
    param([AllowEmptyString()][string]$Text)
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    foreach ($character in $Text.ToCharArray()) {
        $code = [int]$character
        if ($code -eq 34) { [void]$builder.Append('\"') }
        elseif ($code -eq 92) { [void]$builder.Append('\\') }
        elseif ($code -lt 32 -or $code -gt 126) { [void]$builder.AppendFormat('\u{0:x4}', $code) }
        else { [void]$builder.Append($character) }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

# ---- Dependency manifests of the programs in a package -------------------------------------------------------------------------------

# The libraries a program of the package was built for at one version and is shipped at another. Every program in the app folder has a
# dependency manifest (<name>.deps.json) that names the version of each library it expects, and the folder holds one copy of each library
# for all of them. A program handed a newer major version than it was built for starts, and then fails when that version needs a library
# its manifest never listed. Returns objects with Program, Assembly, BuiltFor and Ships; nothing when every manifest agrees with the folder.
function Get-PackageDependencyMismatches {
    param([Parameter(Mandatory)][string]$AppDirectory)
    # The desktop runtime has assemblies of its own under these names, which take the place of the core runtime's forwarding stubs in the
    # folder of every WPF app. That is how the folder always looks; it is not a library that changed under a program.
    $desktopRuntime = @('Microsoft.VisualBasic.dll', 'System.Drawing.dll', 'WindowsBase.dll')
    # The demos' made-up server is never run from this folder: a demo copies its own files into a bundle, and it runs from there with the
    # runtime's own libraries. What the folder holds beside it does not concern it.
    $standalone = @('Assistant.SampleMcpServer')
    $found = @()
    foreach ($manifest in @(Get-ChildItem -LiteralPath $AppDirectory -Filter '*.deps.json' -File)) {
        $program = $manifest.Name -replace '\.deps\.json$', ''
        if ($standalone -contains $program) { continue }
        $json = Get-Content -LiteralPath $manifest.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($target in $json.targets.PSObject.Properties) {
            foreach ($library in $target.Value.PSObject.Properties) {
                $runtime = $library.Value.PSObject.Properties['runtime']
                if (-not $runtime) { continue }
                foreach ($asset in $runtime.Value.PSObject.Properties) {
                    $name = Split-Path $asset.Name -Leaf
                    $builtFor = $asset.Value.PSObject.Properties['assemblyVersion']
                    if (-not $builtFor -or $desktopRuntime -contains $name) { continue }
                    $file = Join-Path $AppDirectory $name
                    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
                    try { $ships = [System.Reflection.AssemblyName]::GetAssemblyName($file).Version }
                    catch { continue }
                    $expected = [System.Version]$builtFor.Value
                    # "1.2.3" and "1.2.3.0" are the same version: a part that is not given counts as zero.
                    $expectedParts = @($expected.Major, $expected.Minor, $expected.Build, $expected.Revision) | ForEach-Object { [Math]::Max($_, 0) }
                    $shipsParts = @($ships.Major, $ships.Minor, $ships.Build, $ships.Revision) | ForEach-Object { [Math]::Max($_, 0) }
                    if (($expectedParts -join '.') -ne ($shipsParts -join '.')) {
                        $found += [pscustomobject]@{ Program = $program; Assembly = $name; BuiltFor = ($expectedParts -join '.'); Ships = ($shipsParts -join '.') }
                    }
                }
            }
        }
    }
    return $found
}

# ---- Asset manifests (the format the app reads: group id -> files with size and SHA-256) ----------------------------------------------

# Reads a manifest into an ordered dictionary: group id -> array of @{ Path; Size; Sha256 }. A missing file is an empty manifest.
function Read-AssetManifest {
    param([Parameter(Mandatory)][string]$Path)
    $groups = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $groups }
    $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($group in @($json.groups)) {
        $files = @()
        foreach ($file in @($group.files)) {
            $files += [pscustomobject]@{ Path = [string]$file.path; Size = [int64]$file.size; Sha256 = ([string]$file.sha256).ToLowerInvariant() }
        }
        $groups[[string]$group.id] = $files
    }
    return $groups
}

function Write-AssetManifest {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Groups)
    $text = New-Object System.Text.StringBuilder
    [void]$text.AppendLine('{')
    [void]$text.AppendLine('  "schemaVersion": ' + $script:AssetManifestSchemaVersion + ',')
    [void]$text.AppendLine('  "groups": [')
    $ids = @($Groups.Keys | Sort-Object)
    for ($g = 0; $g -lt $ids.Count; $g++) {
        $id = $ids[$g]
        [void]$text.AppendLine('    {')
        [void]$text.AppendLine('      "id": ' + (ConvertTo-JsonString $id) + ',')
        [void]$text.AppendLine('      "files": [')
        $files = @($Groups[$id])
        for ($f = 0; $f -lt $files.Count; $f++) {
            $file = $files[$f]
            [void]$text.AppendLine('        {')
            [void]$text.AppendLine('          "path": ' + (ConvertTo-JsonString $file.Path) + ',')
            [void]$text.AppendLine('          "size": ' + $file.Size + ',')
            [void]$text.AppendLine('          "sha256": ' + (ConvertTo-JsonString $file.Sha256))
            [void]$text.AppendLine('        }' + $(if ($f -lt $files.Count - 1) { ',' } else { '' }))
        }
        [void]$text.AppendLine('      ]')
        [void]$text.AppendLine('    }' + $(if ($g -lt $ids.Count - 1) { ',' } else { '' }))
    }
    [void]$text.AppendLine('  ]')
    [void]$text.AppendLine('}')
    Write-Utf8NoBom -Path $Path -Text $text.ToString()
}

# The rules AssetManifestReader applies to a group id and a file path (src/Assistant.Core/Assets/AssetManifestReader.cs).
function Test-AssetGroupId {
    param([string]$Id)
    return ($Id -cmatch '^[a-z0-9]+(-[a-z0-9]+)*$') -and $Id.Length -le 64
}

function Test-AssetRelativePath {
    param([string]$Path)
    if ([string]::IsNullOrEmpty($Path) -or $Path.Length -gt 200) { return $false }
    $devices = @('CON', 'PRN', 'AUX', 'NUL', 'CLOCK$', 'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
        'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9')
    foreach ($segment in ($Path -replace '\\', '/').Split('/')) {
        if ($segment.Length -eq 0 -or $segment -eq '.' -or $segment -eq '..') { return $false }
        if ($segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment.StartsWith(' ')) { return $false }
        if ($segment -match '[\x00-\x1f<>:"|?*]') { return $false }
        if ($devices -contains $segment.Split('.')[0].ToUpperInvariant()) { return $false }
    }
    return $true
}

# Describes the files of one group in a folder: every file under it, relative with forward slashes, with its size and SHA-256.
function Get-AssetGroupFiles {
    param([Parameter(Mandatory)][string]$GroupDirectory)
    $root = (Resolve-Path -LiteralPath $GroupDirectory).ProviderPath.TrimEnd('\') + '\'
    $files = @()
    foreach ($item in (Get-ChildItem -LiteralPath $GroupDirectory -Recurse -File | Sort-Object FullName)) {
        $relative = $item.FullName.Substring($root.Length) -replace '\\', '/'
        if (-not (Test-AssetRelativePath $relative)) { throw "The file name '$relative' cannot be listed in an asset manifest." }
        $files += [pscustomobject]@{ Path = $relative; Size = [int64]$item.Length; Sha256 = Get-FileSha256 $item.FullName }
    }
    if ($files.Count -eq 0) { throw "The folder '$GroupDirectory' holds no files." }
    if ($files.Count -gt 1000) { throw "The folder '$GroupDirectory' holds more than 1000 files, which a manifest group cannot list." }
    return , $files
}

# Checks the files of one group against their manifest entries. Returns the problems as text; an empty array means the group is whole.
function Test-AssetGroup {
    param([Parameter(Mandatory)][string]$GroupDirectory, [Parameter(Mandatory)]$Files)
    $problems = @()
    foreach ($file in $Files) {
        $path = Join-Path $GroupDirectory ($file.Path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $problems += ($file.Path + ': missing'); continue }
        if ((Get-Item -LiteralPath $path).Length -ne $file.Size) { $problems += ($file.Path + ': wrong size'); continue }
        if ((Get-FileSha256 $path) -ne $file.Sha256) { $problems += ($file.Path + ': checksum differs') }
    }
    return , $problems
}

# ---- Processes ---------------------------------------------------------------------------------------------------------------------

$script:OwnProcessChain = $null

# The ids of this process and of the ones that started it: the uninstaller that runs this script, and whatever started that.
function Get-OwnProcessChain {
    # Asked once: it does not change while the script runs, and it is looked at every time the running processes are.
    if ($script:OwnProcessChain) { return , $script:OwnProcessChain }
    $ids = @($PID)
    $id = $PID
    for ($depth = 0; $depth -lt 12; $depth++) {
        $parent = $null
        try { $parent = (Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId=" + $id) -ErrorAction Stop).ParentProcessId } catch { $parent = $null }
        if (-not $parent -or $ids -contains [int]$parent) { break }
        $ids += [int]$parent
        $id = [int]$parent
    }
    $script:OwnProcessChain = $ids
    return , $ids
}

# The Assistant's processes that run from the install folder (and only those: a copy run from somewhere else is never touched). The uninstaller is
# not one of them, though it lives there: Inno's unins000.exe starts a copy of itself to do the work and waits in the install folder until that
# copy has finished, so it is running for as long as the uninstall script is. Nor is anything that started this script.
function Get-AssistantProcesses {
    param([Parameter(Mandatory)][string]$InstallDirectory)
    $prefix = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\') + '\'
    $own = Get-OwnProcessChain
    $found = @()
    foreach ($process in (Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $process.Path } catch { $path = $null }
        if (-not $path -or -not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($own -contains $process.Id -or $process.ProcessName -match '^unins\d+$') { continue }
        $found += $process
    }
    return , $found
}

# Compiles a helper type these scripts need, once. The C# compiler looks for the assemblies a program references in the working directory before
# it looks in Windows' own, and the install folder holds the app's own .NET assemblies under the .NET Framework's names (System.dll,
# System.Core.dll): compiled from there, a helper does not build, and an uninstall once stopped at that. So a helper is compiled from the Windows
# folder and references nothing by name, and a helper that cannot be built is done without: the caller falls back to what needs none.
function Add-SetupType {
    param([Parameter(Mandatory)][string]$TypeName, [Parameter(Mandatory)][string]$Source)
    if ($TypeName -as [type]) { return $true }
    $before = [Environment]::CurrentDirectory
    try {
        [Environment]::CurrentDirectory = [Environment]::SystemDirectory
        Add-Type -Language CSharp -TypeDefinition $Source -ErrorAction Stop
        return [bool]($TypeName -as [type])
    }
    catch { return $false }
    finally { try { [Environment]::CurrentDirectory = $before } catch { } }
}

# Asks every window of the processes to close (WM_CLOSE), the way the Assistant's own shutdown expects: it flushes its history and stops its model host.
function Send-CloseToProcesses {
    param([Parameter(Mandatory)]$Processes)
    $built = Add-SetupType -TypeName 'AssistantSetup.NativeWindows' -Source @'
using System;
using System.Runtime.InteropServices;
namespace AssistantSetup
{
    public static class NativeWindows
    {
        private delegate bool EnumProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        public static int PostClose(uint[] processIds)
        {
            int posted = 0;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint id;
                GetWindowThreadProcessId(window, out id);
                if (Array.IndexOf(processIds, id) >= 0 && PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) { posted++; }
                return true;
            }, IntPtr.Zero);
            return posted;
        }
    }
}
'@
    if ($built) {
        try { return [AssistantSetup.NativeWindows]::PostClose([uint32[]]@($Processes | ForEach-Object { [uint32]$_.Id })) } catch { }
    }

    # Without the helper: each process's main window is asked, which is the one that matters to the Assistant.
    $asked = 0
    foreach ($process in $Processes) { try { if ($process.CloseMainWindow()) { $asked++ } } catch { } }
    return $asked
}

# Closes the Assistant if it runs from the install folder. Returns whether none is left. Without -Force a process that does not close in time is left alone.
function Stop-AssistantProcesses {
    param([Parameter(Mandatory)][string]$InstallDirectory, [switch]$Force)
    $running = Get-AssistantProcesses -InstallDirectory $InstallDirectory
    if ($running.Count -eq 0) { return $true }
    Write-Host ('Closing the Assistant (' + $running.Count + ' process(es))...')
    $asked = Send-CloseToProcesses -Processes $running

    # A window that was asked to close is given the time the Assistant takes to save and stop its model. With no window to ask, what is left is a
    # helper of an Assistant that has already gone, which is waited for only a moment.
    $seconds = if ($asked -gt 0) { 25 } else { 5 }
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $running = Get-AssistantProcesses -InstallDirectory $InstallDirectory
        if ($running.Count -eq 0) { return $true }
    }
    if (-not $Force) { return $false }
    foreach ($process in $running) { try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { } }
    Start-Sleep -Milliseconds 800
    return ((Get-AssistantProcesses -InstallDirectory $InstallDirectory).Count -eq 0)
}

# ---- Removing the data folder ------------------------------------------------------------------------------------------------------

# The ids of the processes that have a file under the folder open, as Windows' Restart Manager reports them (the API installers use to find
# what holds a file). Nothing when none does, or when Restart Manager cannot be asked.
function Get-ProcessesHoldingFolder {
    param([Parameter(Mandatory)][string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return , @() }
    $built = Add-SetupType -TypeName 'AssistantSetup.RestartManager' -Source @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace AssistantSetup
{
    public static class RestartManager
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess { public int ProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME StartTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
        [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint appCount, UniqueProcess[] apps, uint serviceCount, string[] services);
        [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] info, ref uint reasons);

        public static int[] Holders(string[] files)
        {
            uint session;
            if (RmStartSession(out session, 0, new StringBuilder(64)) != 0) { return new int[0]; }
            try
            {
                // Registered a few hundred at a time: one call with very many names can be refused.
                for (int i = 0; i < files.Length; i += 400)
                {
                    string[] batch = new string[Math.Min(400, files.Length - i)];
                    Array.Copy(files, i, batch, 0, batch.Length);
                    if (RmRegisterResources(session, (uint)batch.Length, batch, 0, null, 0, null) != 0) { return new int[0]; }
                }

                uint needed, count = 0, reasons = 0;
                if (RmGetList(session, out needed, ref count, null, ref reasons) != 234 || needed == 0) { return new int[0]; }
                ProcessInfo[] info = new ProcessInfo[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, info, ref reasons) != 0) { return new int[0]; }
                List<int> ids = new List<int>();
                for (int j = 0; j < count; j++) { ids.Add(info[j].Process.ProcessId); }
                return ids.ToArray();
            }
            finally { RmEndSession(session); }
        }
    }
}
'@
    # Without the helper nothing can be said about who holds the folder; it is removed as far as it can be all the same.
    if (-not $built) { return , @() }
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    if ($files.Count -eq 0) { return , @() }
    try { return , @([AssistantSetup.RestartManager]::Holders([string[]]$files)) } catch { return , @() }
}

# Closes the Assistant's own programs that still hold files in its data folder: the ones run from the install folder, and the ones run from the
# data folder itself (a connected app's runtime). A copy of the Assistant run from somewhere else, or any other program, is never ended; their
# names are returned so that the user can be told what kept the folder.
function Stop-ProcessesHoldingFolder {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$InstallDirectory, [switch]$Force)
    $ours = @()
    $others = @()
    foreach ($id in (Get-ProcessesHoldingFolder -Directory $Directory)) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -eq $process -or $id -eq $PID) { continue }
        $path = $null
        try { $path = $process.Path } catch { $path = $null }
        if ($path -and ((Test-PointsInto -Text $path -Directory $InstallDirectory) -or (Test-PointsInto -Text $path -Directory $Directory))) { $ours += $process }
        else { $others += $process.ProcessName }
    }
    if ($ours.Count -gt 0) {
        [void](Send-CloseToProcesses -Processes $ours)
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and @($ours | Where-Object { -not $_.HasExited }).Count -gt 0) {
            Start-Sleep -Milliseconds 250
            foreach ($process in $ours) { $process.Refresh() }
        }
        if ($Force) { foreach ($process in @($ours | Where-Object { -not $_.HasExited })) { try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { } } }
    }
    return , @($others | Sort-Object -Unique)
}

# Makes a folder's own files deletable and takes out the links in it, without ever going through one: a junction or a symbolic link in the folder
# (a runtime links its shared cache this way) leads to something that is not this folder's, which is neither changed nor deleted. The link itself is
# removed here, so that nothing that deletes the folder afterwards can follow it.
function Clear-FolderForRemoval {
    param([Parameter(Mandatory)][string]$Directory)
    foreach ($item in @(Get-ChildItem -LiteralPath $Directory -Force -ErrorAction SilentlyContinue)) {
        $isLink = [bool]($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)
        if ($isLink) {
            try { if ($item.PSIsContainer) { [System.IO.Directory]::Delete($item.FullName, $false) } else { [System.IO.File]::Delete($item.FullName) } } catch { }
            continue
        }
        if ($item.Attributes -band [System.IO.FileAttributes]::ReadOnly) { $item.Attributes = $item.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly) }
        if ($item.PSIsContainer) { Clear-FolderForRemoval -Directory $item.FullName }
    }
}

# Deletes what it can of a folder, waiting out a file that is held for a moment (a virus scanner, a program that is closing). Returns whether it is gone.
function Remove-FolderTree {
    param([Parameter(Mandatory)][string]$Directory)
    for ($attempt = 1; $attempt -le 8 -and (Test-Path -LiteralPath $Directory); $attempt++) {
        try {
            Clear-FolderForRemoval -Directory $Directory
            [System.IO.Directory]::Delete($Directory, $true)
        } catch { Start-Sleep -Milliseconds (250 * $attempt) }
    }
    if (Test-Path -LiteralPath $Directory) {
        # cmd's rd removes what the .NET Framework will not, and whatever it can when something is still held.
        $null = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\cmd.exe') -ArgumentList ('/d /c rd /s /q "' + $Directory + '"') -Wait -WindowStyle Hidden -PassThru
    }
    return -not (Test-Path -LiteralPath $Directory)
}

# Deletes a folder and everything in it. It is renamed first, so that what cannot be deleted at once no longer stands under its name and a new
# installation starts clean; a renamed copy that is still held is deleted the next time the user signs in (a RunOnce entry), as are copies an
# earlier removal could not finish. The folder under its own name is never left to a later deletion, since a new installation may be using it by
# then. Returns 'Removed', 'AtSignIn' (out of the way, the rest goes at the next sign-in) or 'Held' (files that something still holds are left in it).
function Remove-FolderCompletely {
    param([Parameter(Mandatory)][string]$Directory)
    $full = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $parent = Split-Path $full -Parent
    $leaf = Split-Path $full -Leaf
    $aside = @(Get-ChildItem -LiteralPath $parent -Directory -Force -Filter ($leaf + '.removing-*') -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $held = $false
    if (Test-Path -LiteralPath $full) {
        # A file that is open keeps its folder from being renamed: then what can go goes where it is, waiting out what is held for a moment, and
        # the rest is renamed if it has been let go of by then.
        $renamed = $null
        for ($attempt = 1; $attempt -le 2 -and $null -eq $renamed -and (Test-Path -LiteralPath $full); $attempt++) {
            $candidate = Join-Path $parent ($leaf + '.removing-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
            try { [System.IO.Directory]::Move($full, $candidate); $renamed = $candidate }
            catch { if ($attempt -eq 1) { [void](Remove-FolderTree -Directory $full) } }
        }
        if ($null -ne $renamed) { $aside = @($renamed) + $aside }
        $held = Test-Path -LiteralPath $full
    }

    $left = @($aside | Where-Object { -not (Remove-FolderTree -Directory $_) })
    if ($left.Count -gt 0) {
        $cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
        if (-not (Test-Path -LiteralPath $script:RunOnceKey)) { New-Item -Path $script:RunOnceKey -Force | Out-Null }
        foreach ($target in $left) {
            Set-ItemProperty -LiteralPath $script:RunOnceKey -Name ('AssistantRemoveData-' + (Split-Path $target -Leaf)) -Value ('"' + $cmd + '" /d /c rd /s /q "' + $target + '"')
        }
    }
    if ($held) { return 'Held' }
    if ($left.Count -gt 0) { return 'AtSignIn' }
    return 'Removed'
}

# ---- What the Assistant registers for the user, outside its own folders ------------------------------------------------------------

$script:ExplorerVerbCommandKey = 'HKCU:\Software\Classes\SystemFileAssociations\.txt\shell\Assistant.AskAssistant\command'
$script:RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:RunOnceKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
$script:StartupApprovedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$script:UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Assistant'
$script:ValueName = 'Assistant'

function Get-RegistryDefaultValue {
    param([string]$KeyPath)
    if (-not (Test-Path -LiteralPath $KeyPath)) { return $null }
    return [string](Get-Item -LiteralPath $KeyPath).GetValue('')
}

# Whether the text (a command line or a path) names a file inside the folder.
function Test-PointsInto {
    param([string]$Text, [string]$Directory)
    if ([string]::IsNullOrEmpty($Text)) { return $false }
    $prefix = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    return $Text.IndexOf($prefix, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

# The program the browsers' native-messaging host registration points at, or $null when none is registered. Each browser's key names a JSON manifest
# (src/Assistant.BrowserBridge/Registration), and the manifest names the program.
function Get-BrowserHostPath {
    $keys = @(
        'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\assistant.browser_bridge',
        'HKCU:\Software\Google\Chrome\NativeMessagingHosts\assistant.browser_bridge',
        'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\assistant.browser_bridge',
        'HKCU:\Software\Chromium\NativeMessagingHosts\assistant.browser_bridge')
    foreach ($key in $keys) {
        $manifest = Get-RegistryDefaultValue -KeyPath $key
        if ($manifest -and (Test-Path -LiteralPath $manifest -PathType Leaf)) {
            try { return [string]((Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json).path) } catch { }
        }
    }
    return $null
}

# Reads which integrations the user switched on in the Assistant's settings. A missing or unreadable file means none: all are off until the user turns them on.
function Get-IntegrationChoices {
    param([Parameter(Mandatory)][string]$DataDirectory)
    $choices = [pscustomobject]@{ Explorer = $false; Browser = $false }
    $file = Join-Path $DataDirectory 'settings.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $choices }
    try {
        $integrations = (Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json).integrations
        if ($null -ne $integrations) {
            if ($integrations.PSObject.Properties['explorerContextMenuEnabled']) { $choices.Explorer = [bool]$integrations.explorerContextMenuEnabled }
            if ($integrations.PSObject.Properties['browserBridgeEnabled']) { $choices.Browser = [bool]$integrations.browserBridgeEnabled }
        }
    } catch { }
    return $choices
}

# Runs one of the Assistant's entry points with a command (register or unregister). Returns whether it succeeded.
function Set-ExplorerIntegrationChoice {
    param([Parameter(Mandatory)][string]$DataDirectory, [Parameter(Mandatory)][bool]$Enabled)
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    $file = Join-Path $DataDirectory 'settings.json'
    $settings = [pscustomobject]@{ schemaVersion = 2 }
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        if ((Get-Item -LiteralPath $file).Length -gt 1MB) { throw 'The settings file is too large to update safely.' }
        $settings = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $settings -or $settings -isnot [pscustomobject]) { throw 'The settings file must contain a JSON object.' }
    }
    if (-not $settings.PSObject.Properties['integrations'] -or $null -eq $settings.integrations) {
        $settings | Add-Member -NotePropertyName integrations -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    $settings.integrations | Add-Member -NotePropertyName explorerContextMenuEnabled -NotePropertyValue $Enabled -Force
    $temporary = Join-Path $DataDirectory ('settings-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $backup = $temporary + '.previous'
    try {
        [IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $file -PathType Leaf) { [IO.File]::Replace($temporary, $file, $backup) }
        else { [IO.File]::Move($temporary, $file) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    }
}

function Invoke-EntryPoint {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$Command)
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { return $false }
    $process = Start-Process -FilePath $Executable -ArgumentList $Command -Wait -PassThru -WindowStyle Hidden
    return ($process.ExitCode -eq 0)
}

# Where an uninstall says what it did and what went wrong: the script runs with no window when the uninstaller starts it, so without this a
# failure leaves nothing to read. It is in the user's temporary folder, so it outlives both the install folder and the data folder.
function Get-UninstallLogPath {
    return (Join-Path ([System.IO.Path]::GetTempPath()) 'Assistant-uninstall.log')
}

function Write-UninstallLog {
    param([string]$Text)
    try { Add-Content -LiteralPath (Get-UninstallLogPath) -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' ' + $Text) -Encoding UTF8 } catch { }
}

function Test-SafeDirectoryName {
    param([string]$Directory, [string]$LeafName)
    $full = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\')
    return ([System.IO.Path]::GetFileName($full) -ieq $LeafName)
}
