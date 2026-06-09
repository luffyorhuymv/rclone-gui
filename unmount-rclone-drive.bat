@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set /p DRIVE=Nhap ky tu o can ngat, vi du X: 
if "%DRIVE%"=="" (
  echo Chua nhap ky tu o.
  pause
  exit /b 1
)
if "%DRIVE:~-1%" NEQ ":" set "DRIVE=%DRIVE%:"

echo Dang ngat %DRIVE% ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d=$env:DRIVE; Get-CimInstance Win32_Process -Filter \"Name='rclone.exe'\" | Where-Object { $_.CommandLine -match ' mount ' -and $_.CommandLine -like ('* ' + $d + '*') } | ForEach-Object { Write-Host ('Stop rclone PID ' + $_.ProcessId); Stop-Process -Id $_.ProcessId -Force }"
net use %DRIVE% /delete /y

timeout /t 2 /nobreak >nul
if exist %DRIVE%\ (
  echo Van con thay o %DRIVE%.
  echo Hay chay file nay cung quyen voi luc mount hoac mo Task Manager kill rclone.exe mount.
) else (
  echo Da ngat %DRIVE%.
)
pause
