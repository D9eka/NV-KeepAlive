@echo off
setlocal
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4 csc.exe not found at %CSC%
    exit /b 1
)

echo [1/2] Building NvKeepAlive.exe...
"%CSC%" /nologo /utf8output /codepage:65001 /optimize+ /platform:x64 /out:"%~dp0..\NvKeepAlive.exe" "%~dp0NvKeepAlive.cs"
if errorlevel 1 exit /b 1

echo [2/2] Building NvKeepAliveUI.exe...
"%CSC%" /nologo /utf8output /codepage:65001 /optimize+ /platform:x64 /target:winexe ^
    /win32icon:"%~dp0nvkeepalive.ico" ^
    /r:"%WPF%\PresentationFramework.dll" ^
    /r:"%WPF%\PresentationCore.dll" ^
    /r:"%WPF%\WindowsBase.dll" ^
    /r:System.Xaml.dll ^
    /r:System.Core.dll ^
    /out:"%~dp0..\NvKeepAliveUI.exe" "%~dp0NvKeepAliveUI.cs"
if errorlevel 1 exit /b 1

echo [OK] Built windows\NvKeepAlive.exe and windows\NvKeepAliveUI.exe
endlocal
