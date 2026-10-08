@echo off
rem Double-click to repair Windows Mobile Hotspot (asks for administrator rights).
powershell.exe -NoProfile -Command "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0repair-hotspot.ps1\"'"
