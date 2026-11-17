@echo off
rem Builds everything into .\publish
rem   publish\BetterScanner.exe                 Windows 10/11 - single file
rem   publish\Windows7\BetterScanner.exe        Windows 7 SP1+ - needs .NET Framework 4.8 (keep the .config next to it)
rem   publish\release\BetterScannerSetup.exe    the installer staff should use (contains both, picks the right one)
cd /d "%~dp0"

dotnet publish BetterScanner.csproj -f net10.0-windows -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
if errorlevel 1 (echo Windows 10/11 build failed. & pause & exit /b 1)

dotnet build BetterScanner.csproj -f net48 -c Release -p:DebugType=none -o publish\Windows7
if errorlevel 1 (echo Windows 7 build failed. & pause & exit /b 1)

rem Installer (Inno Setup 6: winget install JRSoftware.InnoSetup)
set "ISCC="
for %%P in ("%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe") do if exist %%P set "ISCC=%%~P"
if not defined ISCC (echo Inno Setup 6 not found - skipped the installer. Install it with: winget install JRSoftware.InnoSetup & pause & exit /b 1)
"%ISCC%" /Q installer\BetterScanner.iss
if errorlevel 1 (echo Installer build failed. & pause & exit /b 1)

echo.
echo Done:
echo   Installer:     %~dp0publish\release\BetterScannerSetup.exe
echo   Windows 10/11: %~dp0publish\BetterScanner.exe
echo   Windows 7:     %~dp0publish\Windows7\  (BetterScanner.exe + BetterScanner.exe.config)
