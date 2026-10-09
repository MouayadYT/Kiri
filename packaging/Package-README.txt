ASSISTANT - PACKAGE
===================

This folder is a complete copy of the Assistant for 64-bit Windows 11. The .NET runtime, the
local model engine (llama.cpp) and everything else it runs on are in it. Models run on this PC; nothing needs LM Studio, Ollama or a speech server.
Native speech models also need Microsoft's signed Visual C++ x64 runtime. The Inno setup carries it and installs it only when the runtime is missing; that
one prerequisite can show a Windows permission prompt. For a portable copy, run app\tools\vc_redist.x64.exe once if Windows does not already have it.

Contents
  Install-Assistant.cmd / .ps1     installs or upgrades the Assistant for you
  Uninstall-Assistant.cmd / .ps1   removes it
  app\                             the program files (copied to the install folder). You can also run app\Assistant.UI.exe from here as it is.
  app\assets\                      the models and voices, if this package has any, in the same folder as Assistant.UI.exe so the program finds them
                                   at once (the installer copies them to <install folder>\assets)
  package-manifest.json            the size and SHA-256 of every program file; the installer checks it first
  README.txt                       this file


INSTALL
-------
1. Copy this whole folder to the PC (any folder; not the install folder itself).
2. Double-click Install-Assistant.cmd. (Or, in PowerShell: .\Install-Assistant.ps1 )
   It installs to  %LOCALAPPDATA%\Programs\Assistant  unless you give another folder:
       Install-Assistant.cmd -InstallDirectory "D:\Apps\Assistant"
   It checks every file of the package first, and changes nothing if one is missing or damaged.
3. Start the Assistant from the Start menu. It lives in the notification area (the icon may be under the ^ arrow until you pin it) and opens with
   the shortcut you set in Settings > Hotkeys (Alt+A by default).

What the installer does, and nothing more
  - copies the program files to the install folder, and the models and voices to <install folder>\assets, checking each against its SHA-256;
  - adds a Start menu shortcut and an entry in Settings > Apps > Installed apps (with Uninstall);
  - if you had switched them on in the Assistant's settings: points the File Explorer entry and the browser bridge at the new copy, and the
    "start with Windows" entry. Nothing is switched on for you: they are off until you turn them on in Settings > Integrations and Settings > General.
  Useful switches: -EnableExplorerContextMenu (registers Ask Assistant in File Explorer's right-click menu)  -SkipIntegrations  -SkipShortcut  -SkipAssets  -SkipUninstallEntry  -Force (ends a running copy that will not close)  -Launch

UPGRADE
-------
Run the installer of the newer package. The program files are replaced, a running copy is closed first, and the packaged models are only copied again
if they changed. Your data is not touched: settings, history, the connected apps you installed (Integrations) with their runtimes, and the models you
added to your own models folder all stay where they are, in  %LOCALAPPDATA%\Assistant .

MODELS AND VOICES
-----------------
Packaged models and voices are in  <install folder>\assets\models  and  <install folder>\assets\voices , each in a folder named for its profile or engine
(chat-4b, chat-9b; kitten-tts-mini, kokoro-82m-onnx, piper, speech-recognition, wake-word) with a manifest.json that lists every file's size and SHA-256. The Assistant checks them in the
background when it starts and again before it loads a model; a model whose files do not match is not loaded, and Settings > Model says which file is wrong.
Settings > Voice shows which voices are installed and whether the chosen one is ready. Voice input needs "speech-recognition", the wake word needs
"wake-word", and spoken answers need the group of the engine chosen in Settings > Voice; without one, that feature says so and nothing else changes.
A voice of your own can also go in  %LOCALAPPDATA%\Assistant\voices\<group id> : that is used first and is not checked against anything.

To add, replace or remove one in an installed copy (close the Assistant first), from the install folder:
    powershell -ExecutionPolicy Bypass -File tools\Add-PackagedAssets.ps1 -AssetsDirectory assets ^
        -Model "chat-4b=C:\models\model-Q4_K_M.gguf;C:\models\mmproj-F16.gguf"
    powershell -ExecutionPolicy Bypass -File tools\Add-PackagedAssets.ps1 -AssetsDirectory assets -Voice "piper=C:\voices\piper"
    powershell -ExecutionPolicy Bypass -File tools\Add-PackagedAssets.ps1 -AssetsDirectory assets -Remove model:chat-9b
    powershell -ExecutionPolicy Bypass -File tools\Add-PackagedAssets.ps1 -AssetsDirectory assets -Verify
You can also put a model of your own in  %LOCALAPPDATA%\Assistant\models\<profile id>\model.gguf  : that is used first and is not checked against anything.

BROWSER EXTENSION
-----------------
The extension is in  <install folder>\browser-extension . Turn on "Edge and Chrome" in Settings > Integrations, then in edge://extensions, chrome://extensions
or brave://extensions turn on Developer mode, choose "Load unpacked" and pick that folder.

FILE EXPLORER
-------------
Turn on "File Explorer" in Settings > Integrations. "Ask Assistant" then appears when you right-click a document or picture (on Windows 11, under
"Show more options").

CONNECTED APPS (MCP INTEGRATIONS)
---------------------------------
The Assistant includes the manager for connected apps, but no connected app. The ones you approve and install are kept in
%LOCALAPPDATA%\Assistant\Integrations (with their runtimes in ...\Runtimes) and are never part of the program files, so upgrading does not remove them;
only removing them in Settings > Integrations, or uninstalling with -RemoveUserData, does.

UNINSTALL
---------
Settings > Apps > Installed apps > Assistant > Uninstall, or double-click Uninstall-Assistant.cmd (here or in <install folder>\tools). It asks whether to
delete your data too; the answer defaults to no. Options: -RemoveUserData  -KeepAssets  -Quiet (asks nothing)  -Force.
It removes the File Explorer entry, the browser bridge and the "start with Windows" entry only if they point at the copy being removed.

NOT INCLUDED
------------
No code signing (Windows SmartScreen may warn about the exes), no automatic updates, no MSIX package, no Windows 11 top-level context-menu entry.

