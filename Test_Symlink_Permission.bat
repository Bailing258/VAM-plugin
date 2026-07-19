@echo off
setlocal
set "PLUGIN_DIR=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PLUGIN_DIR%Test_Symlink_Permission.ps1"
pause
