@echo off
setlocal
cd /d "%~dp0"

set "PROJECT=%~dp0GameServer\GameServer.csproj"
set "OUTPUT=%~dp0Publish\GameServer\Release"

echo [Release] Restoring NuGet packages and project dependencies...
dotnet restore "%PROJECT%" --force -r win-x64 -p:Configuration=Release -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 goto restore_failed

echo.
echo [Release] Publishing GameServer with runtime and NuGet dependencies...
dotnet publish "%PROJECT%" --no-restore -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o "%OUTPUT%"
if errorlevel 1 goto publish_failed

echo.
echo [SUCCESS] Release publish completed.
echo Output: %OUTPUT%
echo Managed NuGet runtime DLLs are bundled inside GameServer.exe.
echo Deploy the entire output folder, including Config and any sidecar files.
if /i not "%~1"=="--no-pause" pause
exit /b 0

:restore_failed
echo.
echo [ERROR] NuGet restore failed. Publish was not run.
if /i not "%~1"=="--no-pause" pause
exit /b 1

:publish_failed
echo.
echo [ERROR] Release publish failed. Do not deploy this output.
if /i not "%~1"=="--no-pause" pause
exit /b 1

