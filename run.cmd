@echo off
rem Starts the Triviador server and opens the game in your default browser.
cd /d "%~dp0"
echo Starting Triviador on http://localhost:5080 ...
echo Close this window (or press Ctrl+C) to stop the server.
echo.
start "" cmd /c "timeout /t 15 /nobreak >/dev/null & start http://localhost:5080"
dotnet run --project src\Triviador.Server --launch-profile http
pause
