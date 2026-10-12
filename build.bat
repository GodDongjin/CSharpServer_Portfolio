@echo off
setlocal

REM Publish GameServer with all NuGet runtime dependencies.
REM Usage: build.bat [configuration] [output-directory]
REM Example: build.bat Debug C:\Builds\GameServer

set "SCRIPT_DIR=%~dp0"
set "PROJECT=%SCRIPT_DIR%Server\GameServer\GameServer.csproj"
set "CONFIGURATION=%~1"
if not defined CONFIGURATION set "CONFIGURATION=Release"

set "OUTPUT_DIR=%~2"
if not defined OUTPUT_DIR set "OUTPUT_DIR=%SCRIPT_DIR%Publish\GameServer-%CONFIGURATION%"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found in PATH.
    echo         Install .NET SDK 10 or later and try again.
    exit /b 1
)

if not exist "%PROJECT%" (
    echo [ERROR] Project file was not found:
    echo         %PROJECT%
    exit /b 1
)

echo [INFO] Project       : %PROJECT%
echo [INFO] Configuration : %CONFIGURATION%
echo [INFO] Output        : %OUTPUT_DIR%
echo.

REM Restore newly added or updated NuGet packages first.
dotnet restore "%PROJECT%" --force-evaluate
if errorlevel 1 goto :failed

REM Publish the executable, project references, config, and NuGet runtime DLLs.
dotnet publish "%PROJECT%" ^
    --configuration "%CONFIGURATION%" ^
    --output "%OUTPUT_DIR%" ^
    --no-restore ^
    --self-contained false
if errorlevel 1 goto :failed

echo.
echo [SUCCESS] Publish completed.
echo [SUCCESS] Executable: %OUTPUT_DIR%\GameServer.exe
exit /b 0

:failed
echo.
echo [ERROR] Publish failed. Check the messages above.
exit /b 1
