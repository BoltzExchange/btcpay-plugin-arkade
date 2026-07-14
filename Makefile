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

regtest:
	node submodules/NNark/regtest/regtest.mjs start --profile boltz,delegate

regtest-stop:
	node submodules/NNark/regtest/regtest.mjs stop

regtest-clean:
	node submodules/NNark/regtest/regtest.mjs clean

# The E2E suite's Postgres lives outside the regtest stack (CI provides it as
# a service container); ensure a matching one locally.
test-db:
	@docker start btcpay-e2e-postgres 2>/dev/null || docker run -d \
		--name btcpay-e2e-postgres \
		-e POSTGRES_USER=postgres \
		-e POSTGRES_PASSWORD=postgres \
		-e POSTGRES_DB=btcpay_e2e_test \
		-p 5432:5432 \
		postgres:16

# Requires the regtest stack (make regtest). ConfigBuilder must run after the
# test project is built so it can write appsettings.dev.json into the test bin
# (same ordering as CI).
test: test-db
	dotnet build NArk.E2E.Tests/NArk.E2E.Tests.csproj
	$(MAKE) appsettings
	TESTS_BTCRPCCONNECTION="server=http://127.0.0.1:18443;admin1:123" \
	TESTS_BTCNBXPLORERURL="http://127.0.0.1:32838/" \
	TESTS_POSTGRES="Host=localhost;Port=5432;Database=btcpay_e2e_test;Username=postgres;Password=postgres" \
	TESTS_HOSTNAME="127.0.0.1" \
	ARKADE_CHEAT_ARK_CONTAINER="$$(docker ps --format '{{.Names}}' | grep -m1 -x -e arkd -e ark)" \
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

.PHONY: setup appsettings build run dev regtest regtest-stop regtest-clean test test-db migration release gh-release clean
