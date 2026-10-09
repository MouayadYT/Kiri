@echo off
rem Installs the Assistant for the current user. Extra arguments go to Install-Assistant.ps1 (see its help). Needs no administrator rights.
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Assistant.ps1" %*
set RESULT=%ERRORLEVEL%
echo.
if not "%ASSISTANT_SETUP_NO_PAUSE%"=="1" pause
exit /b %RESULT%
