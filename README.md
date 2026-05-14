# BTCPayServer.Plugins.Floresta

Standalone BTCPay Server plugin prototype for using local `florestad` as the Bitcoin on-chain backend without Bitcoin Core, without storing the full blockchain, and without requiring NBXplorer at runtime.

Target architecture:

```text
BTCPay Server
  -> BTCPayServer.Plugins.Floresta
  -> florestad
       -> local Electrum server
       -> local JSON-RPC server
       -> Bitcoin P2P network
```

This is a watch-only integration. It never stores private keys, seeds, or mnemonics.

## Current MVP Status

Implemented:

- standalone plugin project outside the BTCPay Server repository;
- Floresta server settings page;
- local Floresta Electrum client with JSON-RPC line protocol, subscriptions, reconnect, and resubscribe;
- local Floresta HTTP JSON-RPC client;
- descriptor conversion for single-sig `wpkh`, `sh(wpkh)`, and `pkh`;
- descriptor registration through `loaddescriptor`;
- optional `rescanblockchain`;
- wallet tracking shim for addresses, scripthashes, transactions, UTXOs, confirmations, fees, and broadcast;
- NBXplorer-compatible adapter/shim surface for the BTCPay on-chain pipeline;
- Floresta status monitor and sync summary;
- focused unit tests for Electrum scripthash and descriptor generation;
- Docker integration tests against real `florestad`;
- Playwright E2E smoke test for BTCPay Server + plugin settings UI + Floresta connection.

MVP limits:

- BTC only;
- single-sig only;
- native SegWit/P2WPKH is the priority path;
- multisig/miniscript/private key import are explicit non-goals for the MVP;
- no public Electrum fallback;
- disabling the plugin at runtime is still coarse-grained: do not load the plugin package when you want normal NBXplorer behavior.

## Build

The plugin expects a local BTCPay Server checkout next to this repository:

```text
/home/thgoyo/Desktop/Dev/btcpayserver
/home/thgoyo/Desktop/Dev/btcpayserver-floresta-plugin
```

Build only the plugin:

```bash
dotnet build BTCPayServer.Plugins.Floresta.csproj --no-dependencies
```

Run focused tests:

```bash
dotnet test BTCPayServer.Plugins.Floresta.Tests/BTCPayServer.Plugins.Floresta.Tests.csproj --no-build --no-restore
```

Run integration tests against a real `florestad` in Docker:

```bash
docker compose -f docker-compose.integration.yml up --build --abort-on-container-exit --exit-code-from tests
docker compose -f docker-compose.integration.yml down -v
```

Run the Playwright E2E tests against BTCPay Server + real `florestad`:

```bash
docker compose -f docker-compose.integration.yml --profile e2e up --build --abort-on-container-exit --exit-code-from e2e-tests e2e-tests
docker compose -f docker-compose.integration.yml --profile e2e down -v
```

The E2E profile adds temporary Postgres, starts a real BTCPay Server process with `BTCPAY_DEBUG_PLUGINS` pointing to this plugin, opens Chromium via Playwright, registers admin users, saves Floresta settings, imports a BTC native SegWit xpub, creates an invoice, pays it, mines one regtest block, and checks that the invoice settles and the wallet transaction appears.

The browser E2E topology is `florestad + bitcoind + utreexod`, matching Floresta's own confirmed-wallet regtest fixtures. The `utreexod` image is test-only and is built from `https://github.com/utreexo/utreexod.git` at `UTREEXOD_REF`, defaulting to `main`; override `UTREEXOD_REPO` or `UTREEXOD_REF` if a pinned fork or commit is needed. `bitcoind` remains only a transient regtest miner/payer for this browser test. Neither `bitcoind` nor `utreexod` is part of the runtime deployment, and NBXplorer is still not started.

The integration compose expects:

```text
/home/thgoyo/Desktop/Dev/Floresta
/home/thgoyo/Desktop/Dev/btcpayserver
/home/thgoyo/Desktop/Dev/btcpayserver-floresta-plugin
```

Override those paths if needed:

```bash
FLORESTA_REPO=/path/to/Floresta \
BTCPAYSERVER_REPO=/path/to/btcpayserver \
docker compose -f docker-compose.integration.yml up --build --abort-on-container-exit --exit-code-from tests
```

Razor views compile on build so external plugin loading and Playwright E2E can exercise the actual BTCPay UI.

## Floresta Methods Used

RPC:

- `getblockchaininfo`
- `ping`
- `loaddescriptor`
- `listdescriptors`
- `rescanblockchain`
- `sendrawtransaction`

Electrum:

- `server.version`
- `server.features`
- `server.ping`
- `blockchain.headers.subscribe`
- `blockchain.scripthash.subscribe`
- `blockchain.scripthash.get_history`
- `blockchain.scripthash.listunspent`
- `blockchain.scripthash.get_balance`
- `blockchain.transaction.get`
- `blockchain.transaction.broadcast`
- `blockchain.estimatefee`

See [docs/deployment-floresta.md](docs/deployment-floresta.md) for a deployment shape without `bitcoind` and without NBXplorer.
