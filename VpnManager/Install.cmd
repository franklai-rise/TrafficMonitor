@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Local.ps1" -InstallPlugin -InstallStartup -Restart
pause
