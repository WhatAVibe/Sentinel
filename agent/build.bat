@echo off
setlocal
cd /d "%~dp0"
if /i "%1"=="Debug" (set CONFIG=Debug) else (set CONFIG=Release)
dotnet build PCMonitor.Agent.sln -c %CONFIG%
if errorlevel 1 exit /b 1
echo.
echo Output: PCMonitor.Agent\bin\%CONFIG%\net10.0-windows\PCMonitor.Agent.exe
endlocal
