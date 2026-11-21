@echo off
rem Builds and starts Sockless Port on Docker Desktop for Windows.
rem   run.bat          build (if needed) and start, then show the URL and setup token
rem   run.bat stop     stop and remove the container (data in the port-data volume is kept)
rem   run.bat logs     follow the container log
setlocal
cd /d "%~dp0"

rem Docker Desktop's socket inside containers belongs to group 0; the app runs as a
rem non-root user and needs that group to reach the engine.
set "DOCKER_GID=0"
set "COMPOSE=docker compose -f docker\compose.yaml"

if /i "%~1"=="stop" (
    %COMPOSE% down
    exit /b %errorlevel%
)
if /i "%~1"=="logs" (
    docker logs -f sockless-port
    exit /b %errorlevel%
)

docker info >nul 2>&1
if errorlevel 1 (
    echo Docker is not running. Start Docker Desktop and try again.
    exit /b 1
)

%COMPOSE% up -d --build
if errorlevel 1 exit /b 1

echo Waiting for Sockless Port to start...
set /a tries=0
:wait
set "health="
for /f "delims=" %%h in ('docker inspect -f "{{.State.Health.Status}}" sockless-port 2^>nul') do set "health=%%h"
if "%health%"=="healthy" goto ready
set /a tries+=1
if %tries% geq 60 (
    echo Sockless Port did not become healthy. Check the log with: run.bat logs
    exit /b 1
)
ping -n 2 127.0.0.1 >nul
goto wait

:ready
echo.
echo Sockless Port is running at http://localhost:9000
rem /setup answers 200 only while no administrator exists (afterwards it redirects).
set "setup="
for /f %%s in ('curl.exe -s -o nul -w "%%{http_code}" http://localhost:9000/setup') do set "setup=%%s"
if not "%setup%"=="200" goto done

rem The newest token line wins: the log can hold lines from earlier starts.
set "token="
for /f "tokens=2 delims=:" %%t in ('docker logs sockless-port 2^>^&1 ^| findstr /c:"setup token"') do set "token=%%t"
echo.
echo First start: enter this setup token to create the administrator:
echo   %token: =%

:done
endlocal
