# Changelog

## [0.1.0] - 2026-07-14

Initial public release of **Boltz.Arkade** — a new BTCPay Server plugin from [Boltz Exchange](https://boltz.exchange).

Plugin identifier: `BTCPayServer.Plugins.Boltz.Arkade` · payment/payout method: `ARKADE`

### Features
- Accept Arkade offchain payments and Lightning via Boltz swaps (submarine, reverse, chain).
- Chain-swap settlement for onchain↔Arkade payouts and sends.
- Batch payout tracking through completion.
- Store overview activity including recent boarding and manual Arkade payments.
- Deferred wallet backup flow and merchant onboarding UI.
- Per-type contract scope (NNark/dotnet-sdk#121).
- Per-wallet diagnostic log download (`Plugins/Boltz.Arkade/wallet-logs/`).

### SDK (NNark)
- **Pinned to `be6ae62`** (`NArk.Abstractions/1.0.342-beta`).
