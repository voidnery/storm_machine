@echo off
rem Progon priyomki iteratsii: sborka, stend, shagi podryad, protokol v artifacts.
rem
rem Tekst i logika - v tools/acceptance.sh. Zdes tolko zapusk: cmd.exe razbiraet
rem batnik pobaytno v kodirovke konsoli, i kirillica v ispolnyaemyh strokah rvyot
rem razbor na mnogobaytnyh posledovatelnostyah. Poetomu vse .cmd v proekte -
rem tonkie puskoviki bez kirillicy, kak tools/update-oui.cmd.
setlocal

set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"
if not exist "%GITBASH%" set "GITBASH=%ProgramFiles(x86)%\Git\bin\bash.exe"
if not exist "%GITBASH%" set "GITBASH=%LocalAppData%\Programs\Git\bin\bash.exe"

if not exist "%GITBASH%" (
    echo Git Bash not found. Install Git for Windows.
    exit /b 1
)

"%GITBASH%" -lc "cd \"$(cygpath -u '%~dp0..')\" && sh tools/acceptance.sh %*"
