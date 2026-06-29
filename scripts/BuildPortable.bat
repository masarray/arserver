@echo off
cd /d "C:\Git\ARServer\scripts"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\publish-windows-portable.ps1"
pause
