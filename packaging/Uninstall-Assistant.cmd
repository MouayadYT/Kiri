@echo off
rem Removes the Assistant for the current user. Extra arguments go to Uninstall-Assistant.ps1 (see its help).
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Assistant.ps1" %*
set RESULT=%ERRORLEVEL%
echo.
if not "%ASSISTANT_SETUP_NO_PAUSE%"=="1" pause
exit /b %RESULT%
