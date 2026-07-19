@echo off
setlocal
set "PLUGIN_DIR=%~dp0"
set "TEMP_TAG=%RANDOM%%RANDOM%"
set "TEMP_BAT=%TEMP%\AllPackagesLinker_uninstall_%TEMP_TAG%.bat"
set "TEMP_PS1=%TEMP%\AllPackagesLinker_uninstall_%TEMP_TAG%.ps1"
copy /Y "%PLUGIN_DIR%Uninstall_AllPackagesLinker.ps1" "%TEMP_PS1%" >nul
> "%TEMP_BAT%" echo @echo off
>> "%TEMP_BAT%" echo timeout /t 1 /nobreak ^>nul
>> "%TEMP_BAT%" echo powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%TEMP_PS1%" -PluginDir "%PLUGIN_DIR%"
>> "%TEMP_BAT%" echo del "%TEMP_PS1%" ^>nul 2^>nul
>> "%TEMP_BAT%" echo pause
>> "%TEMP_BAT%" echo del "%%~f0" ^>nul 2^>nul
start "" "%TEMP_BAT%"
exit /b
