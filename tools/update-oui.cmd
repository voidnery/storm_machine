@echo off
rem Обновляет встроенную базу «префикс MAC -> вендор» из реестров IEEE.
rem Ищем именно Git Bash: bash из PATH может оказаться WSL, где нет ни curl
rem с нужными сертификатами, ни того же представления о путях Windows.
setlocal

set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"
if not exist "%GITBASH%" set "GITBASH=%ProgramFiles(x86)%\Git\bin\bash.exe"
if not exist "%GITBASH%" set "GITBASH=%LocalAppData%\Programs\Git\bin\bash.exe"

if not exist "%GITBASH%" (
    echo Git Bash не найден. Установите Git for Windows.
    exit /b 1
)

"%GITBASH%" -lc "cd \"$(cygpath -u '%~dp0..')\" && sh tools/update-oui.sh"
