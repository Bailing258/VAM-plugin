@echo off
setlocal
set "PLUGIN_DIR=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PLUGIN_DIR%PreIndex_AllPackagesLinker.ps1"
pause
