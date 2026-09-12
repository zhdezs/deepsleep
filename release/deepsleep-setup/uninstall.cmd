@echo off
rem deepsleep uninstaller launcher
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0uninstall.ps1" %*
exit /b 0
