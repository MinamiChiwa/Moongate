@echo off
rem Launch the built executable if present; otherwise build and run.
rem Development runs share one tool root inside the repository (data\review), same as the backend.
setlocal
for %%I in ("%~dp0..\..") do set "TOOLROOT=%%~fI\data\review"
set "EXE=%~dp0..\..\artifacts\build\bin\MabiTool.Desktop\debug_win-x64\MabiTool.Desktop.exe"
if exist "%EXE%" (
  start "" "%EXE%" --portable-root "%TOOLROOT%" %*
) else (
  dotnet run --project "%~dp0src\MabiTool.Desktop" -- --portable-root "%TOOLROOT%" %*
)
