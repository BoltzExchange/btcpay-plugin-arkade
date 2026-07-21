---
name: running-locally
description: Use when asked to run, start, launch, smoke-test, or verify BTCPay Server with the Arkade plugin on a local machine — including standing up the regtest environment (bitcoind, arkd, fulmine, Boltz, NBXplorer) or confirming a change works in the real app.
---

# Running BTCPay + Arkade Plugin Locally

Plain agent-agnostic markdown — usable by any coding agent (it is referenced from AGENTS.md; Claude Code also auto-discovers it as a skill). The app is BTCPay Server (from `submodules/btcpayserver`) with the plugin side-loaded via `DEBUG_PLUGINS`; it serves at **http://localhost:14142**.

## Prerequisites

| Requirement | Verify | Install if missing |
|---|---|---|
| .NET 10 SDK | `dotnet --version` → 10.0.x | `winget install Microsoft.DotNet.SDK.10` on Windows (new shells only pick up PATH after refresh); see dotnet.microsoft.com elsewhere |
| Docker engine running | `docker info` | Start Docker Desktop |

## One-time setup

```sh
make setup    # submodules, workloads, publishes plugin, writes appsettings.dev.json
.\setup.ps1   # Windows equivalent
```

## Start the regtest stack

The stack is [BoltzExchange/regtest](https://github.com/BoltzExchange/regtest)
(`submodules/regtest`) with the stock `ci` profile — EVM forks included —
plus `ark`. Under the `ark` profile the regtest repo itself gives the
backend an ARK/BTC pair backed by the stack's fulmine wallet; no repo-local
overlay or config patch is involved.

```sh
make regtest
# Windows: start-test-env.cmd runs the same flow via Git Bash
# (`start-test-env stop` / `... clean` tears it down)
```

First run pulls images (several minutes). `make regtest` waits until the
Boltz backend serves an ARK pair on http://localhost:9001. The EVM forks
default to a flaky public Arbitrum RPC and a non-forking anvil-eth; for
real forking put API-keyed endpoints in a git-ignored `.env.local` at the
repo root (`ARBITRUM_E2E_RPC_URL=...`, `ETHEREUM_E2E_RPC_URL=...`) —
`make regtest` sources it automatically. Do **not** create
databases manually — BTCPay and NBXplorer auto-create their Postgres DBs (the
stack's Postgres is on host port 5432, superuser `boltz`/`boltz`).

Key endpoints: bitcoind RPC 18443 (cookie auth: `docker exec boltz-bitcoind cat /app/bitcoin/regtest/.cookie`),
NBXplorer 32838, Boltz API 9001 (nginx CORS/WS proxy), arkd 7070, fulmine
7000/7001, esplora REST 3002 (electrs), electrum TCP 19001.

## Launch BTCPay Server

First, point the plugin at this stack's Boltz/esplora/electrum endpoints. The
SDK's built-in Regtest preset still describes the ArkLabs stack, and the
plugin merges the datadir `ark.json` over it (`ArkPlugin.GetNetworkConfig`):

```sh
mkdir -p ~/.btcpayserver/RegTest
cat > ~/.btcpayserver/RegTest/ark.json <<'EOF'
{
    "boltz": "http://localhost:9001/",
    "esplora": "http://localhost:3002",
    "electrum-tcp": "tcp://localhost:19001"
}
EOF
```

Do **not** use the stock `Bitcoin` launch profile — its env vars override the
process environment and point at BTCPay's own dev docker-compose (Postgres on
39372, clightning). Run with `--no-launch-profile` and explicit env instead:

```sh
PG="User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432"
LND1="$PWD/submodules/regtest/data/lnd1"
(cd submodules/btcpayserver/BTCPayServer && nohup env \
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS='http://localhost:14142/' \
  BTCPAY_NETWORK=regtest \
  BTCPAY_CHAINS=btc \
  BTCPAY_BTCEXPLORERURL='http://127.0.0.1:32838/' \
  BTCPAY_POSTGRES="$PG;Database=btcpayserver" \
  BTCPAY_EXPLORERPOSTGRES="$PG;Database=nbxplorer" \
  BTCPAY_BTCLIGHTNING="type=lnd-rest;server=https://127.0.0.1:8081/;macaroonfilepath=$LND1/data/chain/bitcoin/regtest/admin.macaroon;certfilepath=$LND1/tls.cert;allowinsecure=true" \
  'BTCPAY_ALLOW-ADMIN-REGISTRATION=true' \
  dotnet run --no-launch-profile \
  > ../../../btcpay-run.log 2> ../../../btcpay-run.err.log &)
```

First launch builds the whole BTCPay solution — allow ~5 minutes before the
port answers. (The workspace ui-dev helper wraps this same launch as
`ui-dev.sh start-btcpay` for Claude Code users.)

## Verify

- `http://localhost:14142/login` returns 200 ("Sign in")
- `http://localhost:14142/api/v1/health` → `{"synchronized":true}`
- `btcpay-run.log` contains `Running plugin BTCPayServer.Plugins.Boltz.Arkade`
- First registered user becomes admin (`ALLOW-ADMIN-REGISTRATION` is on)

## Drive it

- Mine: `docker exec boltz-bitcoind bitcoin-cli -regtest -datadir=/app/bitcoin -rpcwallet=regtest -generate [n]`
- Fund an address: `docker exec boltz-bitcoind bitcoin-cli -regtest -datadir=/app/bitcoin -rpcwallet=regtest sendtoaddress <address> <btc>`
- Pay a BOLT11 invoice: `docker exec boltz-lnd-1 lncli --network=regtest --lnddir=/app/lnd payinvoice --force <invoice>`
- Ark CLI (inside the arkd container): `docker exec arkd ark <cmd>`; more helpers in `submodules/regtest/README.md` (`source aliases.sh`)

## Stop / iterate

- Stop BTCPay: kill the `dotnet` process, then relaunch as above.
- After plugin code changes: `dotnet publish BTCPayServer.Plugins.Boltz.Arkade -c Debug -o BTCPayServer.Plugins.Boltz.Arkade/bin/Debug/net10.0` (what `make setup` does), then restart BTCPay.
- Stack: `make regtest-stop` / `make regtest-clean` (both tear down containers and volumes; the stack always starts from a clean state).

## Common mistakes

- Using `dotnet run -lp Bitcoin` — launchSettings env wins over your shell env, so BTCPay silently connects to the wrong Postgres (39372) and fails.
- Polling the port too early and concluding startup failed — check `btcpay-run.err.log` for a real error first.
- Manually running `createdb` — unnecessary, BTCPay/NBXplorer self-provision.
- Assuming bitcoind has user/password RPC auth — it is cookie-auth; get the cookie with `docker exec boltz-bitcoind cat /app/bitcoin/regtest/.cookie`.
