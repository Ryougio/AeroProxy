@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo [1/2] Detecting Windows .NET Framework csc.exe...
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
set "FRAMEWORK=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found at %CSC%
    pause
    exit /b 1
)

echo [2/2] Compiling AeroProxy.exe (Single-file native executable)...
"%CSC%" /target:winexe /optimize+ /platform:x64 /win32icon:"assets\icon.ico" ^
  /reference:"%WPF%\PresentationCore.dll" ^
  /reference:"%WPF%\PresentationFramework.dll" ^
  /reference:"%WPF%\WindowsBase.dll" ^
  /reference:"%FRAMEWORK%\System.Xaml.dll" ^
  /reference:"System.dll" ^
  /reference:"System.Core.dll" ^
  /reference:"%FRAMEWORK%\System.Web.Extensions.dll" ^
  /reference:"%FRAMEWORK%\Microsoft.CSharp.dll" ^
  /out:"AeroProxy.exe" "src\Program.cs"

if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================================
    echo   BUILD SUCCESSFUL: AeroProxy.exe generated!
    echo ========================================================
) else (
    echo.
    echo [ERROR] Compilation failed.
)

endlocal
