@echo off
setlocal
start "" "%~dp0CliWatch.App.exe" --config "%~dp0config\default.json" %*
