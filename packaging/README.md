# Packaging the Assistant

## Brand assets

`docs/images/kiri-logo.svg` is the canonical Kiri artwork. To regenerate the Windows ICOs, browser icons, capture mark,
README PNG, and installer wizard images, run `node packaging/Generate-BrandAssets.cjs` with the `sharp` package available
to Node.js (installed locally or via `NODE_PATH`). Generated assets are checked in; ordinary builds do not need Node.js.
Both Explorer theme variants retain the SVG's white tile. The installer, shortcuts, and tray use the same `Assistant.ico`.

## Building

Steps 122-123 (PROJECT_SPEC §5.7, §3.5). This folder makes a **package**: one folder (and an optional zip) that installs the Assistant on a Windows 11 PC for
one user, with its models and voices, without a .NET install, LM Studio, Ollama or any speech server.

```powershell
# build a package (about 30 s without models); the result is artifacts\package\Assistant-<version>-win-x64\
.\packaging\Build-Package.ps1

# with models and voices (any GGUF files; the ids are the app's profiles and engines), and a zip.
# The release package carries Qwen3.5 4B and 9B (unsloth Q4_K_M and the F16 projector, Apache-2.0) and all five voice groups
.\packaging\Build-Package.ps1 -CreateZip `
    -Model "chat-4b=C:\models\qwen-4b-q4_k_m.gguf;C:\models\qwen-4b-mmproj-f16.gguf" `
    -Model "chat-9b=C:\models\qwen-9b-q4_k_m.gguf;C:\models\qwen-9b-mmproj-f16.gguf" `
    -Voice "piper=C:\voices\piper"
```

To make a single setup executable that carries the package and runs the same per-user installer:

```powershell
.\packaging\Build-Installer.ps1
```

The setup build requires Inno Setup 6 (`ISCC.exe`); the script finds the standard installation locations or accepts `-IsccPath`.
With **Launch Assistant when setup is complete** ticked, the installer starts the Assistant when it is done (`ssDone`), which is after **Finish** is pressed: the Assistant, and the first-run setup it opens, never come up over the installer's last page. The install script's own `-Launch` is for an install from the package folder, and the installer does not pass it.
This writes `artifacts\installer\Assistant-<version>-win-x64-Setup.exe` and the matching package zip. The setup wizard is per-user. It asks whether to add **Ask Assistant** to File Explorer's right-click menu and whether to launch the app when setup finishes. Selecting the Explorer entry also saves that choice in Settings → Integrations, preserving other settings. If the machine lacks the Microsoft Visual C++ x64 runtime required by native speech recognition, setup installs the bundled signed prerequisite and Windows may ask for permission for that one step. For automation, Inno Setup accepts `/DIR=<folder>`, `/TASKS=explorercontextmenu`, `/SKIPINTEGRATIONS`, `/SKIPSHORTCUT`, and `/SKIPUNINSTALLENTRY`.
The GitHub workflow in `.github\workflows\release.yml` runs this command on `v<major>.<minor>.<patch>` tags and attaches both files to the release.

The version defaults to the spec version in `PROJECT_SPEC.md` and is stamped into the program (Settings > About).

## Why a folder and scripts, not MSIX

The Assistant registers its File Explorer entry (`HKCU\Software\Classes\SystemFileAssociations\…\shell`), its browser native-messaging host
(`HKCU\Software\<browser>\NativeMessagingHosts`) and its start-with-Windows entry (`HKCU\…\Run`) **itself, in the user's own registry**, when the user
switches them on (and keeps them pointing at the running copy). Inside an MSIX those writes go to a private copy of the registry that Explorer and the
browsers never see, so the integrations would silently stop working; the first Windows 11 menu uses a sparse package that the entry point registers itself (an out-of-process COM server; Windows accepts it unsigned only while
Developer Mode is on, and the classic "Show more options" verb stays either way; PROJECT_SPEC §4.4, D3). An unpackaged, per-user install keeps every integration working with no administrator rights.

## What is in a package

```
Assistant-<version>-win-x64\
  Install-Assistant.cmd / .ps1      install, upgrade or repair
  Uninstall-Assistant.cmd / .ps1
  PackageCommon.ps1                 helpers shared by the scripts
  README.txt                        the same steps for the person who installs (plain text)
  package-manifest.json             size + SHA-256 of every program file and of the two asset manifests
  app\                              everything that lands in the install folder; it also runs as it is, straight from the package
      Assistant.UI.exe, Assistant.ModelHost.exe, Assistant.ExplorerExtension.exe, Assistant.BrowserBridge.exe, Assistant.MicrosoftTodo.exe, Assistant.ico
      llama.cpp\                    the local engine (Vulkan build, with its licences)
      browser-extension\            the Manifest V3 extension ("Load unpacked" in Edge/Chrome/Brave)
      tools\                        Uninstall-Assistant.ps1, Add-PackagedAssets.ps1, PackageCommon.ps1
      (the self-contained .NET 8 + WPF runtime)
      assets\                       optional, BESIDE Assistant.UI.exe (where the program looks); merged into <install>\assets
          models\manifest.json, models\<profile id>\model.gguf, mmproj.gguf
          voices\manifest.json, voices\<engine id>\...
```

- **Self-contained**, win-x64, no PDBs. All four programs share one copy of the runtime.
- **One copy of each library for every program.** The app and its helper programs (model host, voice host, File Explorer entry, browser bridge,
  Microsoft To Do server) are published into the same folder, so each program's dependency manifest (`<name>.deps.json`) has to name the version of a
  library that is really there. A program built for an older version of a library than the folder holds starts and then fails when the newer one
  needs something its manifest never listed; that is how packages 0.1.133 and 0.1.134 shipped a model host that could load no model (System.Text.Json
  8 against 10, which Whisper.net brings in). `Directory.Build.props` gives every project the same `System.Text.Json`, and `Build-Package.ps1` now
  compares every manifest with the folder and refuses to make a package in which they disagree. The demos' sample server is the exception: it is
  copied out into a bundle of its own and uses the runtime's own libraries.
- **Generic MCP manager and integration registry** are part of the program (`Assistant.Tools`). **No connected app is bundled**: integrations a user installs
  live in `%LOCALAPPDATA%\Assistant\Integrations` (and their runtimes in `...\Runtimes`), outside the install folder, so installing, upgrading and
  uninstalling never touch them. The demos' made-up sample server is left out unless `-IncludeSamples`.
- Nothing is signed, and there is no updater.

## Install, upgrade, uninstall

`Install-Assistant.cmd` (per user, no administrator; default folder `%LOCALAPPDATA%\Programs\Assistant`) checks every file of the package first, closes a
copy that runs from the install folder (WM_CLOSE, so the app flushes its history; `-Force` ends one that does not close), mirrors `app\` into the
install folder (`robocopy /MIR`, which is why it refuses a folder that is not empty and not an Assistant install, a drive root, or the user's own
folders), copies the packaged models and voices into `<install>\assets` (skipping files already there, verifying every file's SHA-256, merging the
manifest so groups the user added are kept), points what the user had switched on at the new copy (it reads `integrations` in `settings.json`; nothing
is switched on for them), repoints an existing start-with-Windows entry, and adds a Start menu shortcut and a Settings > Apps entry whose Uninstall
runs `<install>\tools\Uninstall-Assistant.ps1`. Running it again from a newer package is the upgrade; from the same package, a repair.

The Inno setup's native uninstaller is listed in Settings > Apps > Installed apps as **Assistant**. It stops the app, removes the File Explorer entry, the browser bridge and the Run entry **only if
they point into the install folder**, the shortcut and the install folder. The standalone `Uninstall-Assistant.cmd` / `.ps1` remains available for folder-based installs and supports `-KeepAssets`. The data folder
The native uninstaller asks a **Yes/No question** before uninstalling: whether to delete all downloaded AI and voice models and user configuration. **No** keeps the data; **Yes** also removes history, connected apps, caches and saved API keys. Silent uninstalls keep data unless `/REMOVEUSERDATA` is explicitly supplied.

`%LOCALAPPDATA%\Assistant` is kept unless `-RemoveUserData` (or answering yes at the native prompt): it is deleted only if the folder is named `Assistant`, and the
secrets under `Assistant/` in Credential Manager go with it.

Removing the data folder does not depend on nothing holding it (`Remove-FolderCompletely` in `PackageCommon.ps1`). Windows' Restart Manager says which
programs still have a file in it open; the Assistant's own (run from the install folder, or from the data folder itself, such as a connected app's runtime)
are closed, and any other is named in the message and never ended. The folder is then renamed (`Assistant.removing-xxxxxxxx`) so that a new installation
starts clean, and deleted, waiting out a file that is held for a moment (a virus scanner reading a model). What still cannot be deleted in the renamed copy
is deleted at the next sign-in (a `RunOnce` entry; the uninstaller exits with 3 and says so). The folder under its own name is never left to a later
deletion, since a new installation may be using it by then: when another program holds it so that it cannot even be renamed, everything else in it is
deleted, the script names the program, and the uninstaller exits with 4 and says where the rest is.

**What the native uninstaller's run is like, and what once broke in it.** Through 0.1.138 an uninstall from Settings > Apps removed the program and
then said "Some integrations or user data could not be removed", with the data folder, the shortcut and the stored secrets all left. Two things in the
install folder caused it, neither of which a made-up install folder that only holds a manifest has:

- `unins000.exe` lives in the install folder and waits there for the copy of itself that does the work, so it is running for as long as the script is.
  `Get-AssistantProcesses` took it for the Assistant still running. It now leaves out the uninstaller (`unins<digits>`) and anything that started the
  script (`Get-OwnProcessChain`).
- The script was started with the install folder as its working folder, and the helper types it compiles with `Add-Type` were then built beside the
  app's own .NET assemblies. The C# compiler looks in the working folder first, found the app's `System.Core.dll` (which only passes types on), and
  failed with "The type 'System.Collections.Generic.ISet<T0>' is defined in an assembly that is not referenced". With `$ErrorActionPreference = 'Stop'`
  that ended the script before anything was removed. Now the uninstaller starts the script in the Windows folder, the script moves there by itself
  when an older uninstaller starts it in the install folder, the helpers are compiled from the Windows folder with nothing referenced by name
  (`Add-SetupType`), and a helper that cannot be built is done without.

The script writes what it did and what went wrong to `%TEMP%\Assistant-uninstall.log`, and the uninstaller's message names that file, since the script
runs with no window. `PackagingScriptsTests.TheUninstallRemovesTheDataFromInsideARealInstallFolder_WithTheUninstallerItselfStillRunningInIt` runs the
script as the uninstaller does, in a made-up install folder that holds .NET's own `System.dll` and `System.Core.dll`, a waiting `unins000.exe` and a
helper that has not closed.

Switches: `Install-Assistant.ps1 -InstallDirectory -DataDirectory -EnableExplorerContextMenu -SkipIntegrations -SkipShortcut -SkipAssets -SkipUninstallEntry -StartMenuDirectory -Force -Launch`;
`Uninstall-Assistant.ps1 -InstallDirectory -DataDirectory -RemoveUserData -KeepSecrets -KeepAssets -Quiet -Force`.

## Models and voices (step 123)

`assets\` holds a **manifest** for each kind listing every file's relative path, size and SHA-256 (`schemaVersion` 1; the format is
`AssetManifest`/`AssetManifestReader` in `Assistant.Core/Assets`, which is strict about names). `Add-PackagedAssets.ps1` writes them:

```powershell
.\packaging\Add-PackagedAssets.ps1 -AssetsDirectory <assets folder> -Model "chat-4b=<model.gguf>[;<mmproj.gguf>]" -Voice "piper=<folder>"
.\packaging\Add-PackagedAssets.ps1 -AssetsDirectory <assets folder> -Remove model:chat-9b
.\packaging\Add-PackagedAssets.ps1 -AssetsDirectory <assets folder> -Verify
```

It works on a package's `app\assets` folder or on an installed copy's (`<install>\tools\Add-PackagedAssets.ps1 -AssetsDirectory <install>\assets`; close the
Assistant first, as its engine has a loaded model open), so models and voices can be replaced **without rebuilding or reinstalling anything**. A group is
built beside its final place and swapped in only when whole.

What the app does with them: it verifies every packaged file (size and SHA-256) in the background about five seconds after it starts (hashes are
remembered in `%LOCALAPPDATA%\Assistant\cache\asset-checks.json` by path, size and time stamp, so a multi-gigabyte model is read in full once), and again
before a packaged model is loaded: a model whose files are missing or do not match is **not loaded**. Settings > Model shows each profile's state with a
**Check files** button (a full re-read), and Settings > Voice lists the voice groups with whether each is installed and whole. A model in
the user's own models folder is used first and is not checked against anything; a model picked by path likewise.

The check detects damage and partial copies; it does not defend against someone who can already write to the install folder (they could change the
manifest too).

### Voice groups (step 125)

The voice runtime (sherpa-onnx, with its native libraries) is part of the program files; the **voice files** are not, and no model is in the
repository. Five groups are looked for, each as `assets\voices\<group id>\` (packaged, verified) or `%LOCALAPPDATA%\Assistant\voices\<group id>\`
(the user's own, used first and not checked against anything):

| Group id | What it is |
|---|---|
| `kitten-tts-mini` | KittenTTS Mini 0.8 (80M) text-to-speech (`model.onnx`, `voices.bin`, `tokens.txt`, `espeak-ng-data`) |
| `kokoro-82m-onnx` | Kokoro-82M ONNX (int8 `model.int8.onnx`, `voices.bin`, `tokens.txt`, lexicons, `espeak-ng-data`) |
| `piper` | A Piper voice (`en_US-lessac-medium.onnx`, `tokens.txt`, `espeak-ng-data`) |
| `speech-recognition` | Streaming speech recognition (NeMo FastConformer CTC, `model.int8.onnx`, `tokens.txt`) |
| `wake-word` | The keyword spotter for "Kiri" (zipformer `encoder`, `decoder` and `joiner` int8 files, `tokens.txt`, `bpe.model`) |

```powershell
.\packaging\Add-PackagedAssets.ps1 -AssetsDirectory <assets folder> -Voice "kitten-tts-mini=<folder>","speech-recognition=<folder>"
```

A group that is not installed is reported in Settings > Voice as Not installed and the feature that needs it says so when it is used (voice input
without `speech-recognition`, the wake word without `wake-word`, spoken answers without the chosen engine's group); nothing else is affected.
The self-contained package carries the runtime's `sherpa-onnx-c-api.dll` and `onnxruntime.dll` for `win-x64` only. The terms of the runtime and of each
group are listed in `third_party\voice\README.md`; note in particular that espeak-ng, inside the runtime and the voices, is GPL-3.0.

## Verified by hand (and what the tests cover)

A real package was built with `Build-Package.ps1`, installed to a scratch folder, started (`--background`: the hardware profile read and every packaged
file verified in the background), upgraded over a copy with a stale file and a user-added model group (stale file removed, group and files kept, nothing
re-copied), refused when a package file was changed by one bit, refused for unsafe install folders, installed over a running copy (closed gracefully),
installed with the integrations on and then uninstalled from inside its own folder (registry entries, shortcut, Apps entry and folder removed, other
hosts' keys untouched, data kept), and uninstalled with `-RemoveUserData -KeepAssets`. `PackagingScriptsTests` guards what breaks the scripts quietly (ASCII
only, batch line endings, parse in Windows PowerShell 5.1, the shared manifest limits), and `ScriptMadeAssetsTests` reads and verifies a manifest the script
wrote. Not covered: a real log-off/log-on, a signed or SmartScreen-checked install, a package of real multi-gigabyte models.

Since step 127 the release smoke tests (`tests\Assistant.SmokeTests\PackagingSmokeTests`, run by `tests\Run-SmokeTests.ps1`, listed in `RELEASE_CHECKLIST.md`)
repeat the safe part of that by machine every time: a package is built (or the one in `ASSISTANT_SMOKE_PACKAGE_DIR` is used) and checked against its manifest,
installed into the temp folder with its own data folder and Start menu, removed again by the installed copy's own uninstaller, and refused when it is not whole
or the folder is not one it may replace. They never register File Explorer's entry or the browser host (`-SkipIntegrations`), put back the start-with-Windows
entry the installer repoints, and never use `-RemoveUserData` (it also deletes the secrets stored under `Assistant/` in Credential Manager) or start the
installed copy (it would open the user's own data folder); those parts are in the by-hand list.
