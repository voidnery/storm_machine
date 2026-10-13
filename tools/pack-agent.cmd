@echo off
rem Sborka agentov pod vse platformy i ukladka ih v postavku klienta.
rem
rem Tekst i logika - v tools/pack-agent.sh. Zdes tolko zapusk: cmd.exe razbiraet
rem batnik pobaytno v kodirovke konsoli, i kirillica v ispolnyaemyh strokah rvyot
rem razbor na mnogobaytnyh posledovatelnostyah.
setlocal

set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"
if not exist "%GITBASH%" set "GITBASH=%ProgramFiles(x86)%\Git\bin\bash.exe"
if not exist "%GITBASH%" set "GITBASH=%LocalAppData%\Programs\Git\bin\bash.exe"

if not exist "%GITBASH%" (
    echo Git Bash not found. Install Git for Windows.
    exit /b 1
)

"%GITBASH%" -lc "cd \"$(cygpath -u '%~dp0..')\" && sh tools/pack-agent.sh %*"
