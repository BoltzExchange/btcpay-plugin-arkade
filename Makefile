PLUGIN := BTCPayServer.Plugins.Boltz.Arkade
VERSION := $(shell sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' $(PLUGIN)/$(PLUGIN).csproj)
# PluginPacker outputs under the 4-part assembly version (e.g. 2.4.2.0)
RELEASE_PATH := ./release/$(PLUGIN)/$(VERSION).0

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

release: clean
	git submodule update --init
	dotnet publish $(PLUGIN) -c Release -o ./publish
	dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker ./publish $(PLUGIN) ./release

# Commits ALL pending tracked changes as the version-bump commit.
gh-release: release
	@! git rev-parse -q --verify refs/tags/v$(VERSION) >/dev/null || { echo "tag v$(VERSION) already exists"; exit 1; }
	git commit -a -m "chore: bump version to v$(VERSION)"
	git tag -s v$(VERSION) -m "v$(VERSION)"
	git push
	git push --tags
	cd $(RELEASE_PATH) && gpg --detach-sig SHA256SUMS
	gh release create v$(VERSION) --title v$(VERSION) --draft --notes-file release-notes-template.md $(RELEASE_PATH)/*

clean:
	rm -rf ./publish ./release

.PHONY: setup appsettings build run dev regtest regtest-stop regtest-clean test migration release gh-release clean
