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

Run integration tests against a real `florestad` and PostgreSQL in Docker:

```bash
docker compose -f docker-compose.integration.yml --profile full up -d --wait floresta postgres

dotnet test BTCPayServer.Plugins.Floresta.Tests/BTCPayServer.Plugins.Floresta.Tests.csproj \
  --filter "Integration=Integration"

docker compose -f docker-compose.integration.yml --profile full down -v
```

The payment persistence regressions reuse the listener fixture with synthetic
RPC/Electrum and a real PostgreSQL invoice/payment store. They create and drop a
unique `floresta_payments_*` database per case, apply the pinned host migrations,
and cover duplicate delivery, ordinary block refresh and polling with conflicting
transaction-cache rows in another wallet. `E2E_POSTGRES` overrides the connection;
the default is the disposable Compose PostgreSQL above. The existing database in
that connection string is never migrated or dropped by these regressions. The
database user needs permission to create and drop the isolated test databases.

Run the Playwright E2E tests against BTCPay Server, Floresta, bitcoind, and utreexod:

```bash
docker compose -f docker-compose.integration.yml --profile full up -d floresta postgres bitcoind utreexod

dotnet test BTCPayServer.Plugins.Floresta.Tests/BTCPayServer.Plugins.Floresta.Tests.csproj \
  --filter "Playwright=Playwright"

docker compose -f docker-compose.integration.yml --profile full down -v
```

The payment E2E also checks confirmed payments while Electrum clients keep
polling, historical payment discovery separately through descriptor import and
explicit rescan, and descriptor persistence across a node restart. It mines a
payment at descriptor index120 before another at index80, in separate blocks.
Import finds index80 and expands the node's initial 100-address horizon; the
older index120 payment must remain absent until the explicit rescan recovers it.
An RPC that reports success without scanning therefore cannot satisfy the test.
This exercises the node directly and does not change the plugin's address cap.
Floresta uses a disposable named volume so its data survives that restart; the
cleanup command removes it.
After checking persistence, the E2E recreates only the regtest node with a fresh
datadir inside that disposable volume. It retains the plugin database and peers,
requires automatic descriptor and historical payment recovery before creating
another invoice or starting a manual scan, then checks a new payment and spend.
The external descriptor must disappear after this reset, proving that retained
node history cannot conceal a recovery failure.
An observer in the disposable test database records any removal, deletion or
spent-state transition of the known payments throughout this reset and recovery.
Replay cannot hide a temporary loss by restoring the final cache state.
Wallet cache reconciliation is deferred while the node reports IBD. The listener
retains the previously known wallet height and tracks whether wallet reconciliation
was deferred, even if the node becomes ready during initialization. Successful
preparation is reused during IBD for the same connection and preparation settings;
new wallets, failed preparation and changed settings are checked on later passes.
The existing loop
reconciles again once ready before marking initialization complete. Failed
preparation is retried. An Electrum connection change during
the check or address reads also invalidates that synchronization attempt.
After recovery, the final spend verifies that genuinely spent outputs are still
reconciled normally.
This scenario waits for automatic descriptor registration, restarts BTCPay and
waits for the persisted wallet to load while the node is still empty. It then
reconnects the regtest peers and checks historical recovery during chain replay. The
plugin persists a pending rescan before importing missing descriptors. It waits
for an explicit non-IBD response and retries a raced IBD rejection (RPC -32093)
on later listener initialization passes, even when no descriptors are newly
registered. Once the listener is initialized, retries prepare only pending wallets;
an unsuccessful import does not reinitialize other wallets or poll all invoices.
Readiness-check failures also keep the request pending without bypassing wallet
initialization's deferred-reconciliation guard.
If synchronization after an import or recovery retry is deferred or incomplete,
the tracker retains per-wallet synchronization work in memory. Results reached
after successful preparation remain eligible for synchronization even when
automatic rescan is disabled and the rescan status is `NotRequested`. This is
distinct from work still waiting for preparation. A later failed preparation
invalidates this rescan-independent eligibility, including a rejected rescan
after successful descriptor registration. A process-local failure generation
prevents an older synchronization result from restoring that eligibility.
An old persisted rescan failure does not block a later successful import.
Later listener passes synchronize
only eligible wallets without preparing descriptors again or requesting another
rescan. Restart uses normal full initialization instead.
Transactions found by recovery are retained in memory for the listener's existing
loop to deliver through normal transaction/payment processing, outside the
initialization lock. Failed deliveries stay queued for a later pass. This also
preserves received outputs that are already spent, without polling every invoice
after an unsuccessful or still-deferred retry. Recovery hands newly discovered
receipts to the existing queue immediately after a successful cache commit,
before awaiting subscriptions for additional addresses. Cancellation during
those subscriptions cannot lose a committed receipt. A failed commit publishes
no receipts, and a successful sync queues each receipt only once.
Transactions discovered before a
later failure within the same address are retained when that sync commits. Full
initialization delivers newly discovered results even without a memory retry
marker, including a `Requested` wallet restored after restart. Before delivery,
the listener refreshes confirmation state from that wallet's cache; a receipt
marked removed is recorded as unaccounted rather than settled. The queue is not
durable across process termination: already cached receipts lost before delivery
are not replayed, which is distinct from delivering new results found at startup.
Ordinary payment refresh and polling also read transaction state from the
invoice's account derivation. A missing derivation or cache entry does not fall
back to a different wallet's transaction row.
Pending intent and its start height survive a plugin restart or
cancellation. Disabling automatic registration or rescan clears it. A successful
RPC response records `Requested` and stops rescan retries; other rescan errors record
`Failed` for operator investigation. Existing `Failed` records from older plugin
versions are not automatically reclassified.

`Requested` means the node accepted an asynchronous task, not that history is
complete. At the pinned node revision, compact filters download after IBD and
there is no RPC exposing their height or rescan completion. An accepted rescan
can therefore cover incomplete filters. The controlled E2E above does not prove
recovery when descriptor registration races replay after old blocks were already
processed. Full automatic recovery for that ordering still needs a node API
that reports filter coverage and scan completion; operators may need to rescan
again after filters are available. No fixed delay establishes readiness.
The fixture enables `utreexod --cfilters` and checks that Floresta has a
compact-filter peer, which is required for historical rescans. The historical
payments are mined before the wallet flow and imported later, after the peers
have had time to exchange its filters.

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
