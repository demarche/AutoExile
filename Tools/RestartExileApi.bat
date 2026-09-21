@echo off
rem Kill and restart ExileAPI (Loader.exe), then verify the freshly compiled AutoExile build is loaded.
rem Double-click to run. Add -NoPause when calling from another script.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0RestartExileApi.ps1" %*
