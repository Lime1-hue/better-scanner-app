@echo off
rem Builds both versions into .\publish
rem   publish\BetterScanner.exe           Windows 10/11 - single file, nothing to install
rem   publish\Windows7\BetterScanner.exe  Windows 7 SP1+ - needs .NET Framework 4.8 (keep the .config next to it)
cd /d "%~dp0"

dotnet publish BetterScanner.csproj -f net10.0-windows -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
if errorlevel 1 (echo Windows 10/11 build failed. & pause & exit /b 1)

dotnet build BetterScanner.csproj -f net48 -c Release -p:DebugType=none -o publish\Windows7
if errorlevel 1 (echo Windows 7 build failed. & pause & exit /b 1)

echo.
echo Done:
echo   Windows 10/11: %~dp0publish\BetterScanner.exe
echo   Windows 7:     %~dp0publish\Windows7\  (BetterScanner.exe + BetterScanner.exe.config)
