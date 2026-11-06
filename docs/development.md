# Development Guide

This guide is for building, testing, and changing the Floresta plugin.

## Clone

Clone with submodules, or initialize them after cloning:

```bash
git clone --recurse-submodules <repo-url>
git submodule update --init --recursive
```

## Build

Build only the plugin:

```bash
dotnet build BTCPayServer.Plugins.Floresta.csproj --no-dependencies
```

## Tests

Integration tests and the trial deployment share the node build in
`docker-compose.floresta.yml`. It pins Floresta to source commit
`5698862813a6a810cc927227b1124547e9a0ad57` (2026-11-06), including the Electrum
block-freshness and compact-filter rescan fixes. This is a development revision
after v0.9.1, not a new upstream release. The previous integration image's build
attestation identifies v0.9.1 commit `bc2db8d07e72651f9981ce589c5688f4d575dc7a`.

The new revision has no upstream-published image, so the default builds from its
full Git commit instead of using a registry digest. The image is tagged locally
as `btcpay-floresta:5698862813a6`. CI explicitly builds it before running tests.
The first build needs Rust compilation time and several GB of free disk space.
`FLORESTA_REPO`, `FLORESTA_DOCKERFILE`, and `FLORESTA_IMAGE` still allow explicit
overrides; an override is a different node target from the default tested here.

Run unit tests:

```bash
dotnet test BTCPayServer.Plugins.Floresta.Tests/BTCPayServer.Plugins.Floresta.Tests.csproj --filter "Integration!=Integration&Playwright!=Playwright"
```

Run integration tests against a real `florestad` in Docker:

```bash
docker compose -f docker-compose.integration.yml up -d floresta

dotnet test BTCPayServer.Plugins.Floresta.Tests/BTCPayServer.Plugins.Floresta.Tests.csproj \
  --filter "Integration=Integration"

docker compose -f docker-compose.integration.yml down -v
```

Run the Playwright E2E tests against BTCPay Server, Floresta, bitcoind, and utreexod:

```bash
docker compose -f docker-compose.integration.yml --profile full up -d floresta postgres bitcoind utreexod

dotnet test BTCPayServer.Plugins.Floresta.Tests/BTCPayServer.Plugins.Floresta.Tests.csproj \
  --filter "Playwright=Playwright"

docker compose -f docker-compose.integration.yml --profile full down -v
```

The payment E2E also checks confirmed payments while Electrum clients keep
polling, historical payment discovery after descriptor import and rescan, and
descriptor persistence across a node restart. Floresta uses a disposable named
volume so its data survives that restart; the cleanup command removes it.

## Local Floresta Development

Use `docker-compose.local-mainnet.yml` with a Floresta checkout next to this repository.

Current Floresta `master` uses `contrib/docker/Dockerfile`. Older Floresta releases may still use `Dockerfile` at the repository root.

## Backend Replacement

Installing the plugin alone does not replace an existing BTCPay Bitcoin backend. Backend replacement must be enabled before BTCPay starts. The Docker trial setup already does this.

The startup gate is separate from the saved setting in BTCPay. The saved setting controls the plugin after backend replacement is already enabled; it cannot replace the backend after BTCPay has started.

## Architecture Notes

Floresta validates the chain and tracks wallet descriptors. The plugin keeps the BTCPay-facing wallet cache for addresses, transactions, UTXOs, balances, metadata, and PSBT inputs.

When BTCPay tracks a wallet, the plugin registers wallet descriptors with Floresta, follows wallet activity through Floresta's local Electrum API, and exposes the data through the BTCPay on-chain wallet flow.

## Environment Defaults

The settings page can be prefilled from environment variables. Values saved in BTCPay take precedence after settings are saved.

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
