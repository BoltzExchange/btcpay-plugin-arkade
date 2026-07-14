# Boltz.Arkade for BTCPay Server

> Accept Bitcoin payments through [Arkade](https://arkadeos.com) — a self-custodial, off-chain Bitcoin Layer 2 — directly inside BTCPay Server. Built by [Boltz](https://boltz.exchange).

[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![BTCPay Plugin](https://img.shields.io/badge/BTCPay%20Server-Plugin-orange)](https://btcpayserver.org)

---

## What Is This?

**Boltz.Arkade** is a BTCPay Server plugin published by Boltz Exchange. It integrates [Arkade](https://arkadeos.com) as a payment method so merchants can accept instant, low-fee Bitcoin payments off-chain while retaining full self-custody — no Lightning node required, no custodian involved.

Payments are settled through **Virtual UTXOs (VTXOs)**, Arkade's off-chain Bitcoin outputs that are cryptographically anchored to real Bitcoin and can be unilaterally exited to the base chain at any time.

---

## Payment Flows

### 1. Arkade Native
Direct VTXO-to-VTXO off-chain payments within the Arkade network. Instant settlement, zero routing fees. Payers need an Arkade-compatible wallet.

### 2. Lightning via Boltz
Payers with Lightning wallets pay a BOLT11 invoice. The plugin uses Boltz's trustless submarine swap to convert the Lightning payment into a VTXO in your Arkade wallet. No Lightning node needed on the merchant side.

### 3. On-chain Boarding (Receive page)
Merchants can generate a boarding address from the wallet **Receive** page to accept on-chain Bitcoin entry into Arkade. This is not shown on invoice checkout — checkout is Arkade-native + optional Lightning only unless the store also enables BTCPay's standard on-chain BTC payment method.

The checkout page presents applicable methods in a single BIP-21 QR code when multiple rails are active.

---

## Architecture

```
BTCPay Server
└── Boltz.Arkade Plugin (BTCPayServer.Plugins.Boltz.Arkade)
    ├── ArkController              # HTTP endpoints for store management
    ├── ArkContractInvoiceListener # Monitors contract state → updates invoice status
    ├── BoardingTransactionListener# Watches on-chain boarding UTXOs via NBXplorer
    ├── ArkadeSpendingService      # Sends payments (payouts, refunds)
    └── NNark (submodule)          # .NET Arkade SDK
        ├── NArk.Core              # Wallet, VTXO logic, HD signing
        ├── NArk.Storage.EfCore    # PostgreSQL persistence (EF Core)
        └── NArk.Swaps             # Boltz submarine/reverse swap client
```

The plugin persists all state (VTXOs, contracts, swaps, intents, wallets) in BTCPay's existing PostgreSQL database via EF Core migrations.

---

## Requirements

- **BTCPay Server** (self-hosted, any recent version)
- **PostgreSQL** (bundled with standard BTCPay deployments)
- **Arkade server (arkd)** v0.9.0 or later — accessible over gRPC from your BTCPay host
- **.NET 10** SDK (if building from source)

> ⚠️ **Early release (v0.1.x).** This plugin is under active development. Test thoroughly before production use. Always maintain a backup of your seed phrase.

---

## Installation

### Via BTCPay Plugin Manager (Recommended)

1. Open your BTCPay Server instance
2. Go to **Server Settings → Plugins**
3. Search for **"Boltz.Arkade"** (or upload the `.btcpay` bundle manually)
4. Click **Install** and restart when prompted

### From Source

```bash
git clone https://github.com/BoltzExchange/btcpay-plugin-arkade.git
cd btcpay-plugin-arkade
make setup        # Pulls submodules, restores workloads, publishes plugin
```

On Windows (no `make` required):
```powershell
.\setup.ps1
```

`make setup` will:
- Pull the `submodules/btcpayserver` and `submodules/NNark` submodules
- Restore .NET workloads
- Publish the plugin (bundling its NNark dependencies)
- Register the plugin with the dev server via `appsettings.dev.json`

See [docs/building-the-plugin.md](docs/building-the-plugin.md) for details.

---

## Setup

### 1. Configure Your Store

1. Navigate to your BTCPay store → **Settings → Arkade**
2. Enter your **Arkade server URL** (e.g. `https://arkd.yourdomain.com`)
3. Create or import your wallet:
   - Generate a new hot wallet
   - Paste a BIP-39 mnemonic (12 or 24 words)

### 2. Configure Payment Methods

1. Go to **Store Settings → Payment Methods**
2. Enable **Arkade** as a payment method
3. Optionally enable **Lightning (via Boltz)** if you have a Boltz instance configured

### 3. Store Settings

| Setting | Default | Description |
|---|---|---|
| Sub-dust Payments | Disabled | Accept payments below 330 sats (no dust limit for VTXOs) |
| Auto-sweep Address | — | Forward all received funds to this on-chain address automatically |

---

## Wallet

### HD Wallet (BIP-39 Mnemonic)
- Full hierarchical deterministic key derivation
- Unique address per invoice (BIP-44 style)
- Supports boarding addresses (requires HD derivation)
- Recommended for merchants

---

## Features

### Invoices & Checkout
- Unified BIP-21 QR code covers Arkade native + Lightning in one scan
- NFC-compatible checkout component
- Real-time payment status updates via VTXO subscription
- Sub-dust toggle for micro-payments

### Wallet Management
- Create a new hot wallet or import via BIP-39 mnemonic
- View balance in BTC with show/hide privacy toggle
- Clear wallet configuration without losing on-chain funds
- Contract sync on import

### VTXO Management
- List, filter, and inspect all virtual UTXOs
- Track spend status, expiry, and settlement transaction
- VTXO asset support (Arkade programmable assets)

### Intent & Batch System
- Construct batch payment intents with multiple outputs
- Intent builder UI with fee breakdown
- Unified Send wizard: QR scanning, BIP-21 parsing, multi-output, manual coin selection

### Swaps (Lightning ↔ Arkade)
- Boltz submarine swaps for incoming Lightning payments
- Boltz reverse swaps for outgoing Lightning payments
- Real-time swap lifecycle monitoring
- LNURL-pay destination support

### Payouts
- Process Arkade payouts through BTCPay's native payout system
- Payout tracking integration in the Send wizard

### Dashboard
- "Recent Activity" widget: latest VTXOs, intents, and swaps at a glance
- Balance display on BTCPay store overview

---

## Development

### Prerequisites
- .NET 10 SDK
- Docker (for test environment)
- PostgreSQL

### Running Tests

After running `make setup`, start the local regtest environment (Bitcoin + arkd + Boltz/Fulmine — a cross-platform Node CLI) and run the E2E suite:
```bash
make regtest
make test
```

`make regtest-stop` keeps the stack's data, `make regtest-clean` wipes it; other regtest actions go through the CLI directly, e.g. `node submodules/NNark/regtest/regtest.mjs mine 5`. On Windows use `start-test-env.cmd` (arguments pass through, e.g. `start-test-env stop`).

### Adding EF Core Migrations

```bash
make migration NAME=<MigrationName>
# or on Windows:
.\add-migration.ps1 <MigrationName>
```

### Project Structure

```
btcpay-plugin-arkade/
├── BTCPayServer.Plugins.Boltz.Arkade/   # Main BTCPay plugin
│   ├── Controllers/                     # HTTP controllers
│   ├── Data/                           # EF Core entities & migrations
│   ├── Models/                         # View models
│   ├── Services/                       # Background services
│   ├── Views/                          # Razor views
│   └── PaymentHandler/                 # BTCPay payment method integration
├── NArk.E2E.Tests/                     # End-to-end test suite
├── submodules/
│   ├── NNark/                          # .NET Arkade SDK (NArk.Core / .Swaps / .Storage.EfCore)
│   └── btcpayserver/                   # BTCPay Server source (build dependency)
├── docs/                               # Developer docs & internal design documents
├── Makefile                            # setup / dev / test / release targets
└── setup.ps1 / add-migration.ps1 / start-test-env.cmd   # Windows equivalents
```

### Release Process

Bump `<Version>` in `BTCPayServer.Plugins.Boltz.Arkade.csproj`, update `CHANGELOG.md` (with an otherwise clean working tree), and run `make gh-release` — it packs the plugin with BTCPay's `PluginPacker`, commits the pending changes as the version bump, pushes a signed tag, and creates a draft GitHub release with signed `SHA256SUMS`. See [docs/release-process.md](docs/release-process.md).

---

## Ark Protocol Concepts

### VTXOs (Virtual UTXOs)
Off-chain Bitcoin outputs secured via collaborative (user + operator) and unilateral (timelocked) Taproot spending paths. VTXOs are the atomic unit of value in Arkade. The operator can never steal them — the unilateral exit path is always available.

### Batches & Commitment Transactions
Periodically, the Arkade operator batches pending VTXOs into an on-chain **commitment transaction**, anchoring off-chain state to Bitcoin. This is how off-chain payments get Bitcoin-level finality.

### Contracts
Payment flows are modeled as Taproot contracts derived from a wallet descriptor and derivation index. Each invoice gets a unique contract address.

### Boarding Addresses
Taproot addresses that serve as the entry point into Arkade for on-chain funds. When funded, the operator converts the UTXO into a VTXO in the next batch. Protected by a timelock for unilateral recovery.

### Unilateral Exit
At any time, a user can exit to on-chain Bitcoin without the operator's cooperation by broadcasting the VTXO tree transaction. This is the security guarantee that makes Arkade non-custodial.

---

## Security

- The operator is **trusted for liveness only** — never for custody
- Users can always exit to on-chain Bitcoin unilaterally
- Private keys never leave your server
- The plugin uses POST redirects for private key display (never URL query params)
- All wallet state is stored in your own PostgreSQL database

**Never share your mnemonic or wallet seed with anyone, including Ark Labs.**

---

## Greenfield REST API

The plugin exposes a store-scoped REST API under `/api/v1/stores/{storeId}/arkade/*`. Endpoints are authenticated via BTCPay's standard Greenfield API key scheme and use the same permission policies as the rest of BTCPay (`btcpay.store.cansettings`, `btcpay.store.canmodifystoresettings`).

### Endpoints

- `GET /api/v1/stores/{storeId}/arkade/wallet` — current Arkade wallet config.
- `POST /api/v1/stores/{storeId}/arkade/wallet` — create or import a wallet.
- `PATCH /api/v1/stores/{storeId}/arkade/wallet/settings` — update sub-dust toggle.
- `DELETE /api/v1/stores/{storeId}/arkade/wallet` — unlink wallet from the store.
- `GET /api/v1/stores/{storeId}/arkade/balance` — available / locked / recoverable / boarding sats.
- `GET|POST /api/v1/stores/{storeId}/arkade/address` — read or mint a receive / boarding address.
- `POST /api/v1/stores/{storeId}/arkade/send` — send to an Ark address, BIP21 URI, or BOLT11 invoice.
- `POST /api/v1/stores/{storeId}/arkade/estimate-fees` — estimate fees for a prospective send (Arkade / Batch / Lightning).
- `POST /api/v1/stores/{storeId}/arkade/parse-destination` — classify a destination (Ark / BIP21 / BOLT11 / LNURL).
- `GET /api/v1/stores/{storeId}/arkade/vtxos` — list VTXOs.
- `GET /api/v1/stores/{storeId}/arkade/intents` — list pending batch intents.
- `DELETE /api/v1/stores/{storeId}/arkade/intents/{intentTxId}` — cancel an intent.
- `GET /api/v1/stores/{storeId}/arkade/contracts` — list address derivations.
- `GET /api/v1/stores/{storeId}/arkade/swaps` — list Lightning / chain swaps.
- `GET /api/v1/stores/{storeId}/arkade/server-info` — Ark operator info.
- `GET /api/v1/stores/{storeId}/arkade/status` — overall service status.
- `GET /api/v1/stores/{storeId}/arkade/boltz-limits` — Boltz swap limits and fees.
- `POST /api/v1/stores/{storeId}/arkade/sync` — force a VTXO + boarding sync.

### Send example

```http
POST /api/v1/stores/{storeId}/arkade/send
Authorization: token <api-key>
Content-Type: application/json

{
  "destination": "ark1...",
  "amountSats": 25000,
  "inputOutpoints": ["abc...:0", "def...:1"]
}
```

- `amountSats` is required when the destination is a bare Ark address. For BIP21 URIs it overrides any embedded `amount`. For Lightning destinations it must be omitted (the BOLT11 invoice fixes the amount).
- `inputOutpoints` is optional. When provided, only the listed VTXOs are spent — automatic coin selection is bypassed. Must be omitted for Lightning destinations.

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for release notes.

---

## Contributing

Pull requests are welcome. For significant changes, open an issue first to discuss.

- Branch naming: `your-name/short-description`
- Ensure `dotnet build NArk.sln` passes before submitting
- Add or update tests for new payment flows

---

## Links

- [Boltz Exchange](https://boltz.exchange) — publisher; trustless Lightning ↔ on-chain swaps
- [Arkade](https://arkadeos.com) — the Ark protocol implementation
- [BTCPay Server](https://btcpayserver.org) — the self-hosted payment processor

---

## License

[MIT](LICENSE) © Boltz Exchange
