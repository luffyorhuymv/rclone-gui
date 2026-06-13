@echo off
setlocal
set DRIVE=%~1
set SUBPATH=%~2
if "%DRIVE%"=="" set DRIVE=X:
if "%SUBPATH%"=="" set SUBPATH=public_html
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fix-opencode-session.ps1" -Drive "%DRIVE%" -SubPath "%SUBPATH%"
endlocal
