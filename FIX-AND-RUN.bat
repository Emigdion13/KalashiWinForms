@echo off
cd /d "%~dp0"
echo.
echo === Kalashi LINE fix ===
echo Folder: %CD%
echo Deleting old bin\ and obj\ so Windows cannot run the broken build...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
echo.
echo Building fresh...
dotnet build
if errorlevel 1 (
  echo BUILD FAILED — paste the red errors.
  pause
  exit /b 1
)
echo.
echo Window title must say:  Kalashi — 15m + LINE lab
echo If it still says the old title, you are not in this folder.
echo.
dotnet run
pause
