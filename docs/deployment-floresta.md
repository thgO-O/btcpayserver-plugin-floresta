# Deployment With Floresta

This deployment shape is intentionally different from the normal BTCPay Bitcoin Core/NBXplorer stack.

Installing the public plugin package is safe by default on a normal BTCPay installation. The plugin only replaces BTCPay's Bitcoin backend when `FLORESTA_REPLACE_BTCPAY_BACKEND=true` is present before BTCPay Server starts.

It includes:

- `btcpayserver`
- `postgres`, when the selected BTCPay deployment uses Postgres
- `floresta`

It does not include:

- `bitcoind`
- `nbxplorer`
- public Electrum servers

## Verified Floresta Flags

The current Floresta `florestad` CLI exposes these relevant flags:

- `--network`
- `--data-dir`
- `--rpc-address`
- `--electrum-address`
- `--wallet-xpub`
- `--wallet-descriptor`
- `--filters-start-height`
- `--no-cfilters`
- `--no-assume-utreexo`
- `--no-backfill`
- `--enable-electrum-tls`
- `--electrum-address-tls`
- `--generate-cert`
- `--tls-key-path`
- `--tls-cert-path`

Network values accepted by `florestad` include:

- `bitcoin` for mainnet
- `testnet`
- `signet`
- `regtest`

## Default Ports

RPC defaults:

| Network | RPC |
| --- | ---: |
| bitcoin | 8332 |
| testnet | 18332 |
| signet | 38332 |
| regtest | 18442 |

Electrum defaults:

| Network | TCP | TLS |
| --- | ---: | ---: |
| bitcoin | 50001 | 50002 |
| testnet | 30001 | 30002 |
| signet | 60001 | 60002 |
| regtest | 20001 | 20002 |

The plugin defaults to `floresta:50001` and `http://floresta:8332`, which matches mainnet/`bitcoin`.

## Mainnet Example

Run `florestad` on the Docker internal network only:

```bash
florestad \
  --network bitcoin \
  --data-dir /data \
  --rpc-address 0.0.0.0:8332 \
  --electrum-address 0.0.0.0:50001 \
  --filters-start-height "${FLORESTA_FILTERS_START_HEIGHT:-0}" \
  --no-backfill
```

Do not publish RPC to the public internet. The Electrum endpoint is also expected to be local/internal.

## Plugin Settings

The plugin can be bootstrapped with `FLORESTA_*` environment variables. These values act as defaults before settings are saved in BTCPay:

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

`FLORESTA_REPLACE_BTCPAY_BACKEND=true` is a startup-only safety gate. It must be set before BTCPay Server starts for the plugin to remove BTCPay's NBXplorer services and register the Floresta backend shim. Without it, Server Settings > Floresta remains available for configuration and connection testing, but BTCPay keeps using its normal configured Bitcoin backend.

The saved `UseFlorestaAsBitcoinBackend` setting is still required in Floresta backend mode, but it cannot activate backend replacement by itself after startup.

For mainnet:

```text
Enabled=true
CryptoCode=BTC
Network=mainnet
ElectrumHost=floresta
ElectrumPort=50001
ElectrumUseTls=false
RpcUrl=http://floresta:8332
GapLimit=100
FallbackFeeRateSatsPerByte=1
AutoRegisterDescriptors=true
AutoRescanOnNewDescriptor=false
UseFlorestaAsBitcoinBackend=true
```

The plugin is watch-only. When Floresta is active for BTC, the BTCPay generated wallet, hot-wallet, generated watch-only wallet, and seed-import setup paths are hidden or redirected to xpub import. Configure an existing xpub or descriptor and spend from an external wallet or PSBT workflow.

The settings page includes a health panel showing the current Electrum status/version, RPC reachability, chain height, best block, IBD state, validated height, and Utreexo root count. Use Test Connection for an immediate live check after changing endpoints.

`FLORESTA_FALLBACK_FEE_SAT_PER_VB` controls the static fee rate used when `blockchain.estimatefee` is unavailable or invalid and no cached estimate exists. Successful estimates are clamped to at least this value. The default is `1 sat/vB`.

For signet, adjust:

```text
Network=signet
ElectrumPort=60001
RpcUrl=http://floresta:38332
```

For regtest, adjust:

```text
Network=regtest
ElectrumPort=20001
RpcUrl=http://floresta:18442
```

## Descriptor And Cache Model

Floresta is responsible for descriptor registration, chain validation, rescan, and the Electrum/RPC surface. The plugin is responsible for BTCPay compatibility: it stores a local cache with tracked wallet metadata, derived addresses, transactions, UTXOs, balances, and metadata in the shape BTCPay normally expects from NBXplorer.

On wallet tracking, the plugin creates receive/change descriptors from the BTCPay derivation strategy. With `AutoRegisterDescriptors=true`, tracking fails closed if Floresta rejects descriptor registration. With `AutoRegisterDescriptors=false`, the plugin still stores descriptor metadata locally, but it does not mark the descriptor as registered.

The UTXO cache is rebuilt from Floresta Electrum data:

- `blockchain.scripthash.get_history` supplies transaction history;
- `blockchain.scripthash.listunspent` supplies the UTXO set used for wallet balance and PSBT inputs;
- `blockchain.scripthash.get_balance` is a consistency check only. If it diverges from `listunspent`, the plugin retries once and then logs a warning while continuing with `listunspent`.

## Operational Recovery

Use this sequence when descriptors or the plugin cache need to be recovered:

1. Register descriptors from Server Settings > Floresta, or let wallet tracking do it automatically when `AutoRegisterDescriptors=true`.
2. Rescan Floresta from a height that covers the wallet history.
3. Use the wallet UTXO cache wipe endpoint/action to clear local `utxos` and `transactions`.
4. Start a wallet scan. The plugin keeps `tracked_wallets`, `tracked_addresses`, gap indexes, and `IsUsed` flags, derives only missing address ranges, and rebuilds the cache from `get_history` and `listunspent`.

The cache wipe is intentionally not a wallet reset. It avoids address reuse by preserving local derivation and usage state.

## Wallet Age Guidance

New or recent wallet:

- set `DefaultRescanStartHeight` near wallet creation;
- set `--filters-start-height` near the same height;
- rescan is faster and storage/bandwidth are lower.

Old wallet or unknown history:

- use an older start height, possibly `0`;
- expect more time, bandwidth, and compact-filter storage;
- this still does not store the full blockchain.

Floresta still stores headers, compact filters unless disabled, Utreexo state, and wallet cache data.

## Docker Compose Examples

For a mainnet package-install PoC, use [docker-compose.release.example.yml](../docker-compose.release.example.yml):

```bash
docker compose -f docker-compose.release.example.yml up -d
```

This compose starts BTCPay Server, Postgres, and Floresta only. It does not mount a local plugin build, and it builds the Floresta image from the upstream Git repository pinned to `v0.9.1` by default instead of requiring a local `../Floresta` checkout or floating on the default branch. Set `FLORESTA_REPO` to a local checkout, fork, or another Git URL/ref when you need to pin a different tree. It sets `FLORESTA_REPLACE_BTCPAY_BACKEND=true` because this stack is explicitly a Floresta backend PoC. After BTCPay starts, create the first admin account, install the Floresta plugin from Server Settings > Plugins > Available Plugins, restart BTCPay, then configure Server Settings > Floresta.

By default the BTCPay HTTP port is bound to `127.0.0.1:23000`. Set `BTCPAY_HOST_BIND=0.0.0.0` only when publishing it behind a trusted reverse proxy, VPN, or tunnel.

[docker-compose.local-mainnet.yml](../docker-compose.local-mainnet.yml) is for local development and mounts `./bin/Debug/net10.0` into BTCPay's plugin directory.

Both examples keep Floresta RPC/Electrum internal to the Docker network. Expose those ports only for local development and only to trusted networks.

## Integration Test Compose

For tests against a real `florestad`, use:

```bash
docker compose -f docker-compose.integration.yml up --build --abort-on-container-exit --exit-code-from tests
docker compose -f docker-compose.integration.yml down -v
```

This starts only:

- `floresta` on regtest;
- `tests`, a .NET SDK runner that executes the `Integration=Integration` tests from `BTCPayServer.Plugins.Floresta.Tests`.

It does not start `bitcoind` or NBXplorer. These tests cover RPC/Electrum readiness, descriptor registration, descriptor listing, basic rescan behavior, Electrum headers/features/fee, empty scripthash wallet queries, and explicit broadcast errors.

For browser-level E2E coverage, use the optional profile:

```bash
docker compose -f docker-compose.integration.yml --profile e2e up --build --abort-on-container-exit --exit-code-from e2e-tests e2e-tests
docker compose -f docker-compose.integration.yml --profile e2e down -v
```

This adds temporary Postgres, starts BTCPay Server with `BTCPAY_DEBUG_PLUGINS` pointing at the Floresta plugin, and runs the `Playwright=Playwright` tests through the settings and invoice payment flow. The browser E2E profile also starts `bitcoind` only as a regtest miner/payer and `utreexod` only as a Utreexo proof peer, matching Floresta's own confirmed-wallet regtest fixtures. `florestad` exposes `sendrawtransaction`, `loaddescriptor` and `rescanblockchain`, but not `generatetoaddress`/`generateblock`, so the test keeps `bitcoind` for funds and blocks. These test-only services are not part of the production deployment and NBXplorer is still not started.
