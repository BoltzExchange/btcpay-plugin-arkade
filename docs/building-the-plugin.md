# Building the Plugin

How to build and package the Boltz.Arkade BTCPay plugin from source for development
or manual upload to a BTCPay Server instance.

## Prerequisites

- .NET 10 SDK
- Node.js 20–24 with npm 10+ (for the embedded wallet bundle)
- Rust via rustup (the version and targets are pinned in `rust-toolchain.toml`)
- A GNU cross toolchain and libc sysroot for the non-host Linux target
- Docker with Buildx (for canonical releases, binding validation, and regtest)
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
including the `.btcpay` file and `SHA256SUMS`. Upload the `.btcpay` file to a
BTCPay Server via `Plugins` → `Upload Plugin`.

The release package contains the native Boltz client for both `linux-x64` and
`linux-arm64`. `make release` cross-compiles both libraries and runs the
complete publish and packaging flow on the current host. On an x64 Linux host,
install a complete `aarch64-linux-gnu` GCC toolchain and libc sysroot; an ARM64
host similarly needs the complete `x86_64-linux-gnu` toolchain.

Canonical releases run the same target in Docker with .NET 10, Node 22, and
the pinned Rust toolchain:

```bash
make release-docker
```

The release container is amd64; Docker uses emulation when this target runs on
an ARM host. It still cross-compiles and packages both Linux runtime assets.

To run the generated C# binding contract checks for both architectures:

```bash
make validate-bindings
```
