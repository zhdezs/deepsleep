@echo off
rem deepsleep installer launcher (double-click me)
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0install.ps1" %*
exit /b 0
