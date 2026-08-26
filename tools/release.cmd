@echo off
rem ---------------------------------------------------------------------------
rem  Storm Machine — обёртка для tools/release.sh
rem  Подробности о выборе .cmd вместо .ps1 — в setup-repo.cmd
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

set "GITBASH="

for /f "delims=" %%G in ('where git 2^>nul') do (
    if not defined GITBASH (
        for %%R in ("%%~dpG..") do (
            if exist "%%~fR\bin\bash.exe" set "GITBASH=%%~fR\bin\bash.exe"
        )
    )
)

if not defined GITBASH if exist "%ProgramFiles%\Git\bin\bash.exe" set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"
if not defined GITBASH if exist "%ProgramFiles(x86)%\Git\bin\bash.exe" set "GITBASH=%ProgramFiles(x86)%\Git\bin\bash.exe"
if not defined GITBASH if exist "%LOCALAPPDATA%\Programs\Git\bin\bash.exe" set "GITBASH=%LOCALAPPDATA%\Programs\Git\bin\bash.exe"

if not defined GITBASH (
    echo.
    echo   Git Bash not found. Install Git for Windows:
    echo   https://git-scm.com/download/win
    echo.
    exit /b 1
)

"%GITBASH%" "%~dp0release.sh" %*
exit /b %ERRORLEVEL%
