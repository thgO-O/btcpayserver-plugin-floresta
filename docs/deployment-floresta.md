# Deployment With Floresta

This deployment shape is intentionally different from the normal BTCPay Bitcoin Core/NBXplorer stack.

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

## Docker Compose Skeleton

See [docker-compose.floresta.example.yml](../docker-compose.floresta.example.yml).

The example is deliberately internal-only for Floresta RPC/Electrum. Expose ports only for local development and only to trusted networks.

## Integration Test Compose

For tests against a real `florestad`, use:

```bash
docker compose -f docker-compose.integration.yml up --build --abort-on-container-exit --exit-code-from tests
docker compose -f docker-compose.integration.yml down -v
```

This starts only:

- `floresta` on regtest;
- `tests`, a .NET SDK runner that executes `BTCPayServer.Plugins.Floresta.IntegrationTests`.

It does not start `bitcoind` or NBXplorer. These tests cover RPC/Electrum readiness, descriptor registration, descriptor listing, basic rescan behavior, Electrum headers/features/fee, empty scripthash wallet queries, and explicit broadcast errors.

For browser-level E2E coverage, use the optional profile:

```bash
docker compose -f docker-compose.integration.yml --profile e2e up --build --abort-on-container-exit --exit-code-from e2e-tests e2e-tests
docker compose -f docker-compose.integration.yml --profile e2e down -v
```

This adds temporary Postgres, starts BTCPay Server with `BTCPAY_DEBUG_PLUGINS` pointing at the Floresta plugin, and runs Playwright/Chromium through the settings and invoice payment flow. The browser E2E profile also starts `bitcoind` only as a regtest miner/payer and `utreexod` only as a Utreexo proof peer, matching Floresta's own confirmed-wallet regtest fixtures. `florestad` exposes `sendrawtransaction`, `loaddescriptor` and `rescanblockchain`, but not `generatetoaddress`/`generateblock`, so the test keeps `bitcoind` for funds and blocks. These test-only services are not part of the production deployment and NBXplorer is still not started.
