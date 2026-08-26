@echo off
rem ---------------------------------------------------------------------------
rem  Storm Machine — обёртка для tools/setup-repo.sh
rem
rem  Зачем .cmd, а не .ps1: на машине действует групповая политика
rem  MachinePolicy = AllSigned — неподписанные PowerShell-скрипты не запускаются
rem  даже с -ExecutionPolicy Bypass. На .cmd и на скрипты Git Bash она не действует.
rem
rem  Важно: используем ИМЕННО bash из Git for Windows. Команда `bash` в PATH
rem  на этой машине указывает на WSL (C:\Windows\System32\bash.exe) — это другая
rem  среда, и запускать скрипт в ней нельзя.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

set "GITBASH="

rem 1) Ищем bash рядом с тем git.exe, который реально используется
for /f "delims=" %%G in ('where git 2^>nul') do (
    if not defined GITBASH (
        for %%R in ("%%~dpG..") do (
            if exist "%%~fR\bin\bash.exe" set "GITBASH=%%~fR\bin\bash.exe"
        )
    )
)

rem 2) Типовые места установки
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

"%GITBASH%" "%~dp0setup-repo.sh" %*
exit /b %ERRORLEVEL%
