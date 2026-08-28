@echo off
setlocal

set "APP_EXE=%~dp0src\AnzExplorer.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AnzExplorer.exe"

if not exist "%APP_EXE%" (
    echo Anz Explorer has not been built yet.
    echo.
    echo Expected executable:
    echo %APP_EXE%
    echo.
    echo Build the project, then run this launcher again.
    pause
    exit /b 1
)

start "Anz Explorer" /D "%~dp0" "%APP_EXE%"
exit /b 0
