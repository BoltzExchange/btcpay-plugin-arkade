# Summary

Initial public release of **Boltz.Arkade** for BTCPay Server (v0.1.0).

Accept Arkade offchain Bitcoin and Lightning (via Boltz swaps) in BTCPay. Includes chain-swap settlement, batch payout tracking, store activity feed, wallet backup flow, and a Greenfield API under `/api/v1/stores/{storeId}/arkade/*`.

See [CHANGELOG.md](CHANGELOG.md) for details.

# Uploading to BTCPay

You can upload the `BTCPayServer.Plugins.Boltz.Arkade.btcpay` file to your BTCPay Server by navigating to `Plugins` and scrolling all the way down to `Upload Plugin`.

# Verifying the Release

In order to verify the release, you'll need to have `gpg` or `gpg2` installed on your system. You'll first need to import the keys that have signed this release if you haven't done so already:

```
curl https://boltz.exchange/static/boltz.asc | gpg --import
```

Once you have the required PGP keys, you can verify the release (assuming `SHA256SUMS` and `SHA256SUMS.sig` are in the current directory) with:

```
gpg --verify SHA256SUMS.sig SHA256SUMS
```

You should see `Good signature from "Boltz (Boltz signing key) <admin@bol.tz>"` if the verification was successful.

You should also verify that the hashes still match with the archive you've downloaded.

```
sha256sum --ignore-missing -c SHA256SUMS
```

If your archive is valid, you should see the following output:

```
BTCPayServer.Plugins.Boltz.Arkade.btcpay.json: OK
BTCPayServer.Plugins.Boltz.Arkade.btcpay: OK
```
