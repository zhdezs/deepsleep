@echo off
setlocal
set VCVARS="C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
call %VCVARS% >nul 2>&1
cl /nologo /utf-8 /O2 /EHsc test_engine.cpp /Fe:test_engine.exe
if errorlevel 1 ( exit /b 1 )
test_engine.exe
