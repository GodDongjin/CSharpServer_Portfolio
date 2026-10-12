@echo off
setlocal
set "PROJECT=%~dp0CSharpServer.slnx"
set "RESULTS=%~dp0TestResults"
set "NO_PAUSE="
set "RESTORE_OPTION="
:parse
if "%~1"=="" goto run
if /i "%~1"=="--no-pause" set "NO_PAUSE=1"
if /i "%~1"=="--no-restore" set "RESTORE_OPTION=--no-restore"
shift
goto parse
:run
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found. Install .NET 10 SDK and try again.
    set "RESULT=1"
    goto finish
)
echo [INFO] Running ServerCore.Tests and GameServer.Tests. No GameServer, MySQL or Redis required.
echo [INFO] Project: "%PROJECT%"
echo.
dotnet test "%PROJECT%" %RESTORE_OPTION% --logger "console;verbosity=normal" --logger "trx" --results-directory "%RESULTS%"
set "RESULT=%ERRORLEVEL%"
echo.
if "%RESULT%"=="0" (
    echo [PASS] Tests completed successfully.
) else (
    echo [FAIL] Test execution failed. Read the errors above.
)
echo [INFO] Results directory: "%RESULTS%"
:finish
if not defined NO_PAUSE pause
exit /b %RESULT%

