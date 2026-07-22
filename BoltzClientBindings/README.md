# Boltz client native bindings

This crate is the in-process UniFFI facade for the pinned
`breez/boltz-client` revision in `Cargo.toml`. It owns the Tokio runtime that
drives swaps; it holds no storage of its own.

Build the Linux native library:

```sh
cargo build --manifest-path BoltzClientBindings/Cargo.toml
```

Generate the tracked C# source with `uniffi-bindgen-cs`
`v0.11.0+v0.31.0`:

```sh
uniffi-bindgen-cs \
  --library BoltzClientBindings/target/debug/libboltz_client_bindings.so \
  --crate boltz_client_bindings \
  --out-dir BoltzClientBindings/generated \
  --config BoltzClientBindings/uniffi.toml
sed -i 's/[[:space:]]\+$//' BoltzClientBindings/generated/boltz_client_bindings.cs
sed -i '${/^$/d;}' BoltzClientBindings/generated/boltz_client_bindings.cs
```

The BTCPay plugin build invokes `cargo build --locked` and fails closed unless
the native library for the current Linux host is produced and copied beside the
plugin assembly. A release build uses Cargo's release profile. Cross-building
and publishing an ARM64 artifact still requires an ARM64 builder/toolchain; it
must not reuse an x64 `.so`.

The managed host creates one native client per Arkade wallet. It derives the
native 64-byte BIP-39 seed from that store wallet's existing mnemonic. No
environment-variable opt-in or separate seed is required on mainnet.
Non-mainnet deployments report the settlement option as unavailable.

Storage lives entirely in the BTCPay plugin: the constructor takes a
`SwapStorage` implementation (a UniFFI foreign trait — C# implements the
generated interface) alongside `ClientConfig`. The crate is stateless apart
from the runtime. Swap rows cross the FFI as opaque json plus denormalized
`status`/`is_terminal` convenience columns; the derived-key counter is a
plain `next_key_index()` callback whose strict per-wallet monotonicity —
across restarts and processes — is the host's responsibility (a regressed
counter re-derives used preimages, a fund-theft vector). Callbacks are
synchronous, may block on database I/O, and are invoked from the runtime's
blocking-worker threads, never from a host-controlled thread; implementations
must not call back into the same `BoltzClient`. Failures are thrown as the
generated binding exception and surface to swap logic as store errors.

Idempotency also lives on the C# side (ledger write-ahead states, unique
indexes, per-wallet serialization). `create_reverse_swap` is a plain create:
a crash between Boltz accepting the create and the host persisting the
outcome can orphan an unfunded remote swap, which is an accepted harmless
residual. The config's `Debug` output redacts the seed, and the seed is
zeroized as soon as the core has consumed it.

Store adapter tests are pure rust against in-memory `SwapStorage`
implementations — `cargo test` needs no external services.
