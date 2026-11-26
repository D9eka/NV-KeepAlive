@echo off
setlocal
set "UNLOCKER=%~1"
if "%UNLOCKER%"=="" (
    echo Drag and drop 40HXInstaller.exe onto this script, or pass its path as argument 1.
    echo Example: safe-gen2-wrapper.bat "C:\Path\To\40HXInstaller.exe"
    pause
    exit /b 1
)
if not exist "%UNLOCKER%" (
    echo [ERROR] File not found: "%UNLOCKER%"
    pause
    exit /b 1
)
net session >nul 2>&1
if errorlevel 1 (
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '\"%UNLOCKER%\"' -Verb RunAs"
    exit /b 1
)
set "KA_DIR=%ProgramData%\NV-KeepAlive"
set "PAUSE_FILE=%KA_DIR%\keepalive.pause"
echo [1/4] Pausing NV-KeepAlive...
if exist "%KA_DIR%" type nul > "%PAUSE_FILE%"
timeout /t 2 /nobreak >nul
echo [2/4] Running 40HXInstaller.exe -gen2...
"%UNLOCKER%" -gen2
echo [3/4] Running built-in PCIe Gen2 chipset-bridge retrain check...
if exist "%KA_DIR%\NvKeepAlive.exe" "%KA_DIR%\NvKeepAlive.exe" --retrain-gen2 --wait 15
echo [4/4] Resuming NV-KeepAlive...
if exist "%PAUSE_FILE%" del /F /Q "%PAUSE_FILE%" >nul 2>&1
timeout /t 1 /nobreak >nul
echo Done!
pause
