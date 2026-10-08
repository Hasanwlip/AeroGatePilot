@echo off
rem Double-click: uninstalls Clash Verge and Proxifier, disables Cisco AnyConnect and Hotspot Shield (asks for administrator rights).
powershell.exe -NoProfile -Command "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0remove-vpn-conflicts.ps1\"'"
