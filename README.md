# BTCPayServer.Plugins.Floresta

Experimental BTCPay Server plugin for using local `florestad` as the Bitcoin on-chain backend without Bitcoin Core, without storing the full blockchain, and without requiring NBXplorer at runtime.

This plugin is alpha software. It is intended for technical evaluation and controlled mainnet PoCs, not unattended production stores.

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

Installing the plugin alone does not replace an existing BTCPay Bitcoin Core/NBXplorer backend. Backend replacement is opt-in at process startup with `FLORESTA_REPLACE_BTCPAY_BACKEND=true`.

## Current Alpha Status

Implemented:

- standalone plugin project outside the BTCPay Server repository;
- safe-by-default Plugin Builder install path: the plugin does not replace BTCPay's Bitcoin backend unless `FLORESTA_REPLACE_BTCPAY_BACKEND=true` is set before startup;
- Floresta server settings page;
- settings defaults from `FLORESTA_*` environment variables;
- watch-only wallet setup guard that hides or blocks generated, hot-wallet, and seed-import setup paths for BTC while Floresta is active;
- settings health panel for Electrum status/version, RPC reachability, height, best block, IBD, validated height, and Utreexo root count;
- connection test output for Electrum version plus Floresta RPC height, best block, IBD, validation, and Utreexo root state when available;
- configurable fee-estimation fallback through `FLORESTA_FALLBACK_FEE_SAT_PER_VB`;
- local Floresta Electrum client with JSON-RPC line protocol, subscriptions, reconnect, and resubscribe;
- local Floresta HTTP JSON-RPC client;
- descriptor conversion for single-sig `wpkh`, `sh(wpkh)`, and `pkh`;
- descriptor registration through `loaddescriptor`;
- optional `rescanblockchain`;
- wallet tracking shim for addresses, scripthashes, transactions, UTXOs, confirmations, fees, and broadcast;
- NBXplorer-compatible adapter/shim surface for the BTCPay on-chain pipeline;
- Floresta status monitor and sync summary;
- focused unit tests for Electrum scripthash, descriptor generation, chain-info parsing, fee fallback, and settings defaults;
- Docker integration tests against real `florestad`;
- Playwright E2E smoke test for BTCPay Server + plugin settings UI + Floresta connection.

MVP limits:

- BTC only;
- single-sig only;
- native SegWit/P2WPKH is the priority path;
- multisig/miniscript/private key import are explicit non-goals for the MVP;
- BTCPay generated wallet, hot wallet, and seed-import setup paths are not supported by this plugin; configure an xpub or descriptor and use an external wallet or PSBT workflow for spending;
- no public Electrum fallback;
- a standard BTCPay Bitcoin Core/NBXplorer installation remains unchanged unless backend replacement is explicitly enabled before startup.

## Build

Clone with submodules, or initialize them after cloning:

```bash
git clone --recurse-submodules <repo-url>
git submodule update --init --recursive
```

The plugin follows the BTCPay plugin template layout: BTCPay Server is checked out under `submodules/btcpayserver`, and the plugin project references `submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj`.

Build only the plugin:

```bash
dotnet build BTCPayServer.Plugins.Floresta.csproj --no-dependencies
```

Run focused tests:

```bash
dotnet test BTCPayServer.Plugins.Floresta.Tests/BTCPayServer.Plugins.Floresta.Tests.csproj --no-build --no-restore
```

Run a mainnet PoC stack without Bitcoin Core, NBXplorer, or a local debug plugin mount:

```bash
docker compose -f docker-compose.release.example.yml up -d
```

This starts BTCPay Server, Postgres, and Floresta only, with `FLORESTA_REPLACE_BTCPAY_BACKEND=true` because the compose is explicitly a Floresta backend PoC. Open BTCPay, create the first admin, install the Floresta plugin from Server Settings > Plugins > Available Plugins, restart BTCPay, then configure Server Settings > Floresta. By default BTCPay binds to `127.0.0.1:23000`; change `BTCPAY_HOST_BIND` only when placing it behind a trusted reverse proxy or tunnel.

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

The E2E profile adds temporary Postgres, starts a real BTCPay Server process with `BTCPAY_DEBUG_PLUGINS` pointing to this plugin, opens Chromium via Playwright, registers admin users, saves Floresta settings, checks the health panel, verifies generated/hot/seed wallet setup paths are hidden or blocked, imports a BTC native SegWit xpub, creates an invoice, pays it, mines one regtest block, and checks that the invoice settles and the wallet transaction appears.

The browser E2E topology is `florestad + bitcoind + utreexod`, matching Floresta's own confirmed-wallet regtest fixtures. The `utreexod` image is test-only and is built from `https://github.com/utreexo/utreexod.git` at `UTREEXOD_REF`, defaulting to `main`; override `UTREEXOD_REPO` or `UTREEXOD_REF` if a pinned fork or commit is needed. `bitcoind` remains only a transient regtest miner/payer for this browser test. Neither `bitcoind` nor `utreexod` is part of the runtime deployment, and NBXplorer is still not started.

The integration compose expects a local Floresta checkout next to this repository and the BTCPay Server submodule initialized. If your checkouts are elsewhere, set `FLORESTA_REPO` and `BTCPAYSERVER_REPO` when running Docker Compose.

Razor views compile on build so external plugin loading and Playwright E2E can exercise the actual BTCPay UI.

## Backend Replacement Gate

`FLORESTA_REPLACE_BTCPAY_BACKEND=true` must be present before BTCPay Server starts for the plugin to remove BTCPay's NBXplorer services and register the Floresta backend shim. Without it, the settings page is available, but BTCPay keeps using its normal configured Bitcoin backend.

This startup gate is intentionally separate from the saved `UseFlorestaAsBitcoinBackend` setting. The saved setting controls the plugin once the Floresta backend mode is already enabled; it cannot turn backend replacement on after startup.

## Environment Defaults

The settings page can be prefilled from environment variables. Values stored in the BTCPay database still take precedence after settings are saved. `FLORESTA_REPLACE_BTCPAY_BACKEND` is not a saved setting; it is a startup-only safety gate.

```text
FLORESTA_REPLACE_BTCPAY_BACKEND=false
FLORESTA_ENABLED=true
FLORESTA_CRYPTO_CODE=BTC
FLORESTA_NETWORK=mainnet
FLORESTA_ELECTRUM_HOST=floresta
FLORESTA_ELECTRUM_PORT=50001
FLORESTA_ELECTRUM_TLS=false
FLORESTA_RPC_URL=http://floresta:8332
FLORESTA_RPC_USER=
FLORESTA_RPC_PASSWORD=
FLORESTA_GAP_LIMIT=100
FLORESTA_FALLBACK_FEE_SAT_PER_VB=1
FLORESTA_FILTERS_START_HEIGHT=0
FLORESTA_AUTO_REGISTER_DESCRIPTORS=true
FLORESTA_AUTO_RESCAN_ON_NEW_DESCRIPTOR=false
FLORESTA_USE_AS_BITCOIN_BACKEND=true
```

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
