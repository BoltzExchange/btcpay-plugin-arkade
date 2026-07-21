@echo off
setlocal
rem Wrapper around the BoltzExchange/regtest stack (submodules/regtest,
rem profiles ci,ark). Requires bash (Git for Windows). No arguments starts
rem the stack; `start-test-env stop` or `... clean` tears it down. Fork RPC
rem overrides (ARBITRUM_E2E_RPC_URL / ETHEREUM_E2E_RPC_URL) can be set in
rem the environment before calling.
if "%~1"=="stop" goto down
if "%~1"=="clean" goto down
bash -c "cd '%~dp0submodules/regtest' && COMPOSE_PROFILES=ci,ark ./start.sh"
exit /b %errorlevel%
:down
bash -c "cd '%~dp0submodules/regtest' && COMPOSE_PROFILES=ci,ark ./stop.sh"
exit /b %errorlevel%
