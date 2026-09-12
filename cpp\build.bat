@echo off
REM 编译「以理服人」C++ 客户端（Win32 + MSVC）
setlocal
cd /d "%~dp0"

set VCVARS="C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist %VCVARS% (
    echo [ERROR] 未找到 vcvars64.bat
    pause
    exit /b 1
)

call %VCVARS% >nul 2>&1

cl /nologo /utf-8 /O2 /EHsc /W3 ^
    /DUNICODE /D_UNICODE ^
    main.cpp ^
    /Fe:以理服人.exe ^
    /link /SUBSYSTEM:WINDOWS user32.lib gdi32.lib comctl32.lib > build_log.txt 2>&1

if errorlevel 1 (
    type build_log.txt
    echo [ERROR] 编译失败，详见 build_log.txt
    pause
    exit /b 1
)

echo [OK] 编译成功：以理服人.exe
exit /b 0
