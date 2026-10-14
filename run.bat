@echo off
setlocal DisableDelayedExpansion
rem Thin launcher for the vidsc Release build. All build, staging, verification, promotion and
rem launch logic lives in tools\Build-Release.ps1; this file only resolves its own directory,
rem forwards script options and returns the exact exit code.
where pwsh >nul 2>nul
if errorlevel 1 (
    echo [run.bat] PowerShell 7 ^(pwsh^) was not found on PATH.
    echo [run.bat] Install PowerShell 7 or run: pwsh -NoProfile -File "%~dp0tools\Build-Release.ps1" -Run
    if not defined CI pause
    exit /b 9009
)
pwsh -NoProfile -File "%~dp0tools\Build-Release.ps1" -Run %*
set "buildExitCode=%errorlevel%"
if not "%buildExitCode%"=="0" (
    echo Build or launch failed with exit code %buildExitCode%.
    if not defined CI pause
)
exit /b %buildExitCode%
