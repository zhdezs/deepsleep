@echo off
rem deepsleep universal installer - Windows entry
rem
rem   double-click : interactive (desktop / core)
rem   install.cmd -Core     install core edition
rem   install.cmd -Desktop  install desktop edition
rem
rem NOTE: this file is intentionally ASCII-only. cmd.exe reads .cmd files with the
rem OEM code page, so any non-ASCII text here turns into mojibake on some machines.
setlocal
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" (
  echo PowerShell not found. Cannot continue.
  pause
  exit /b 1
)
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
