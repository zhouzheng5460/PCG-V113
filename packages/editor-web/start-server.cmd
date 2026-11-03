@echo off
REM 可选：用 http 起服务（http 版能热改文件，内联版不能）。
REM 直接双击本文件即可，不用带参数。
REM
REM 注意：这个 node 进程会随启动它的终端一起结束。如果你只是要"看一眼验证器"，
REM 双击同目录的「规则验证器.html」就行 —— 它是自包含单文件，不需要任何服务。
setlocal
set "HERE=%~dp0"
set "NODE=%~1"
if "%NODE%"=="" set "NODE=node"
where "%NODE%" >nul 2>nul
if errorlevel 1 (
  if exist "C:\Users\Administrator\.workbuddy\binaries\node\versions\22.22.2-6\node.exe" (
    set "NODE=C:\Users\Administrator\.workbuddy\binaries\node\versions\22.22.2-6\node.exe"
  )
)
set "PORT=%~2"
if "%PORT%"=="" set "PORT=8765"

echo 规则验证器 → http://127.0.0.1:%PORT%/
echo 服务在另一个最小化的窗口里跑，关掉那个窗口即停止。
echo.

REM 先起服务（独立窗口），等它就绪再开浏览器 —— 反过来会撞 ERR_CONNECTION_REFUSED
start "规则验证器服务" /min "%NODE%" "%HERE%serve.mjs" %PORT%
ping -n 3 127.0.0.1 >nul
start "" "http://127.0.0.1:%PORT%/"
endlocal
