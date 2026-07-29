PLUGIN := BTCPayServer.Plugins.Boltz.Arkade
PLUGIN_PACKER := submodules/btcpayserver/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj
BINDINGS := BoltzClientBindings
RUST_VERSION := $(shell sed -n 's/^channel = "\(.*\)"/\1/p' rust-toolchain.toml)
X64_RUST_TARGET := x86_64-unknown-linux-gnu
ARM64_RUST_TARGET := aarch64-unknown-linux-gnu
VERSION := $(shell sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' $(PLUGIN)/$(PLUGIN).csproj)
RELEASE_PATH := ./release/$(PLUGIN)/$(VERSION)

setup:
	git submodule update --init --recursive
	dotnet workload restore
	rm -rf $(PLUGIN)/bin/Debug/net10.0
	dotnet publish $(PLUGIN) -c Debug -o $(PLUGIN)/bin/Debug/net10.0
	$(MAKE) appsettings

# Requires the plugin to be built (make setup or make build).
appsettings:
	dotnet run --project ConfigBuilder/ConfigBuilder.csproj

build:
	dotnet build $(PLUGIN)

native-runtimes:
	cargo build --locked --release \
		--manifest-path $(BINDINGS)/Cargo.toml \
		--target $(X64_RUST_TARGET)
	install -D \
		$(BINDINGS)/target/$(X64_RUST_TARGET)/release/libboltz_client_bindings.so \
		$(BINDINGS)/artifacts/runtimes/linux-x64/native/libboltz_client_bindings.so
	cargo build --locked --release \
		--manifest-path $(BINDINGS)/Cargo.toml \
		--target $(ARM64_RUST_TARGET)
	install -D \
		$(BINDINGS)/target/$(ARM64_RUST_TARGET)/release/libboltz_client_bindings.so \
		$(BINDINGS)/artifacts/runtimes/linux-arm64/native/libboltz_client_bindings.so

validate-bindings: native-runtimes
	docker buildx build \
		--file $(BINDINGS)/Dockerfile.native \
		--platform linux/amd64 \
		--target validate \
		--build-arg DOTNET_RUNTIME_IDENTIFIER=linux-x64 \
		--progress plain \
		.
	docker buildx build \
		--file $(BINDINGS)/Dockerfile.native \
		--platform linux/arm64 \
		--target validate \
		--build-arg DOTNET_RUNTIME_IDENTIFIER=linux-arm64 \
		--progress plain \
		.

run:
	cd submodules/btcpayserver/BTCPayServer && dotnet run --launch-profile Bitcoin-HTTPS

dev: setup run

# Optional git-ignored .env.local provides API-keyed fork RPC endpoints
# (ARBITRUM_E2E_RPC_URL / ETHEREUM_E2E_RPC_URL) for local runs; without it
# the regtest defaults apply.
-include .env.local
export ARBITRUM_E2E_RPC_URL ETHEREUM_E2E_RPC_URL

# BoltzExchange/regtest, stock ci profile (EVM forks included) plus the ark
# profile for arkd/fulmine and the backend's ARK/BTC pair.
regtest:
	cd submodules/regtest && COMPOSE_PROFILES=ci,ark ./start.sh
	@echo "waiting for the ARK/BTC pair..."
	@timeout 180 sh -c 'until curl -sf http://localhost:9001/v2/swap/submarine 2>/dev/null | grep -q ARK; do sleep 2; done' || \
		{ echo "boltz backend did not expose an ARK pair" >&2; docker logs --tail 50 boltz-backend >&2; exit 1; }

regtest-stop regtest-clean:
	cd submodules/regtest && COMPOSE_PROFILES=ci,ark ./stop.sh

# Requires the regtest stack (make regtest); Postgres and NBXplorer come from
# it. ConfigBuilder must run after the test project is built so it can write
# appsettings.dev.json into the test bin (same ordering as CI).
test:
	dotnet build NArk.E2E.Tests/NArk.E2E.Tests.csproj
	$(MAKE) appsettings
	$(eval BTC_COOKIE := $(shell docker exec boltz-bitcoind cat /app/bitcoin/regtest/.cookie))
	TESTS_BTCRPCCONNECTION="server=http://127.0.0.1:18443;$(BTC_COOKIE)" \
	TESTS_BTCNBXPLORERURL="http://127.0.0.1:32838/" \
	TESTS_POSTGRES="User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=btcpayserver" \
	TESTS_EXPLORER_POSTGRES="User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=nbxplorer" \
	TESTS_HOSTNAME="127.0.0.1" \
	dotnet test NArk.E2E.Tests/NArk.E2E.Tests.csproj --no-build --logger "console;verbosity=normal"

migration:
	@test -n "$(NAME)" || { echo "Usage: make migration NAME=<MigrationName>"; exit 1; }
	dotnet ef migrations add "$(NAME)" \
		--project $(PLUGIN)/$(PLUGIN).csproj \
		--context ArkPluginDbContext \
		--output-dir Data/Migrations

bump-version:
	@test "$(origin VERSION)" = "command line" || { echo "Usage: make bump-version VERSION=<x.y.z>" >&2; exit 1; }
	@./scripts/bump-version.sh "$(VERSION)"

release: clean native-runtimes
	dotnet publish $(PLUGIN) -c Release -o ./publish
	dotnet restore $(PLUGIN_PACKER) --source ./publish
	dotnet run --no-restore --project $(PLUGIN_PACKER) -- ./publish $(PLUGIN) ./release

release-docker: clean
	docker buildx build \
		--file Dockerfile.release \
		--build-arg RUST_VERSION=$(RUST_VERSION) \
		--platform linux/amd64 \
		--target artifact \
		--output type=local,dest=./release \
		.

# Commits ALL pending tracked changes as the version-bump commit.
gh-release: release-docker
	@! git rev-parse -q --verify refs/tags/v$(VERSION) >/dev/null || { echo "tag v$(VERSION) already exists"; exit 1; }
	git commit -a -m "chore: bump version to v$(VERSION)"
	git tag -s v$(VERSION) -m "v$(VERSION)"
	git push
	git push --tags
	cd $(RELEASE_PATH) && gpg --yes --armor --output SHA256SUMS.asc --detach-sign SHA256SUMS
	gh release create v$(VERSION) --title v$(VERSION) --draft --notes-file release-notes-template.md $(RELEASE_PATH)/*

clean:
	rm -rf ./publish ./release

.PHONY: setup appsettings build native-runtimes validate-bindings run dev regtest regtest-stop regtest-clean test migration bump-version release release-docker gh-release clean
