# Building the Plugin

How to build and package the Boltz.Arkade BTCPay plugin from source for development
or manual upload to a BTCPay Server instance.

## Prerequisites

- .NET 10 SDK
- Node.js 20–24 with npm 10+ (for the regtest CLI and embedded wallet bundle)
- Docker (for the regtest stack)
- GNU Make

## First-time setup

```bash
git clone https://github.com/BoltzExchange/btcpay-plugin-arkade.git
cd btcpay-plugin-arkade
make setup
```

On Windows, use `.\setup.ps1` instead of `make setup`.

`make setup` initialises the `submodules/btcpayserver` and `submodules/NNark`
submodules, restores .NET workloads, publishes the plugin (bundling its NNark
dependencies) to `BTCPayServer.Plugins.Boltz.Arkade/bin/Debug/net10.0`, and
registers it with the dev server via a `DEBUG_PLUGINS` entry in
`appsettings.dev.json` (written by `ConfigBuilder`).

## Development loop

```bash
make regtest   # start the local Bitcoin + arkd + Boltz/Fulmine regtest stack
make dev       # setup + run BTCPay with the plugin (Bitcoin-HTTPS profile)
```

After plugin code changes, re-run `make setup` (republish) and restart BTCPay.
`make regtest-stop` keeps the stack's data; `make regtest-clean` wipes it. For
other regtest actions call the CLI directly, e.g.
`node submodules/NNark/regtest/regtest.mjs mine 5`.

## Tests

With the regtest stack running:

```bash
make test
```

This builds `NArk.E2E.Tests` and runs the Playwright/ServerTester E2E suite
against the local stack.

## EF Core migrations

```bash
make migration NAME=<MigrationName>
```

Requires the `dotnet-ef` tool (`dotnet tool install --global dotnet-ef`).

## Packaging a .btcpay file

```bash
make release
```

This publishes the plugin in Release configuration and packs it with BTCPay's
`PluginPacker` into `release/BTCPayServer.Plugins.Boltz.Arkade/<version>/`
(the packer uses the 4-part assembly version, e.g. `0.1.0.0`), including the
`.btcpay` file and `SHA256SUMS`. Upload the `.btcpay` file to a BTCPay Server
via `Plugins` → `Upload Plugin`.
