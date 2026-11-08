# BTCPay Server Floresta Plugin

This plugin lets BTCPay Server use [Floresta](https://github.com/getfloresta/floresta) to follow Bitcoin payments.

Floresta is a lighter Bitcoin node. The goal is to let BTCPay see Bitcoin payments without running the usual full Bitcoin Core and NBXplorer setup.

This is alpha software. It is useful for testing and controlled trials, but it is not ready for unattended production stores.

## What This Means

- You can try a lighter way for BTCPay to follow Bitcoin payments.
- The plugin is watch-only: it can see payments, but it cannot spend your coins.
- It never stores private keys, seeds, or recovery words.
- You still use your own wallet app or hardware wallet to spend funds.
- It is focused on regular single-person BTC wallets.
- Existing BTCPay installations are not changed just by installing the plugin.

## Before You Use It

This plugin is currently best for people who want to test Floresta with BTCPay and understand the current limits.

Do not use it yet if you need:

- a production store you can leave unattended;
- multisig wallets;
- a BTCPay hot wallet;
- wallet seed import inside BTCPay;
- non-BTC coins.

## Try It With Docker

You need Docker Desktop installed.

Start a test BTCPay setup with Floresta:

```bash
docker compose -f docker-compose.release.example.yml up -d
```

This starts everything needed for a basic trial.

The first run builds the pinned Floresta node from source and needs several GB
of free disk space. See the [node build details](docs/development.md#tests).

After it starts:

1. Open `http://127.0.0.1:23000`.
2. Create the first admin user.
3. Install the Floresta plugin from Server Settings > Plugins > Available Plugins.
4. Restart BTCPay.
5. Open Server Settings > Floresta and configure the plugin.

To stop the trial setup:

```bash
docker compose -f docker-compose.release.example.yml down
```

For public hosting, custom Floresta builds, or a more detailed setup guide, see [Deployment with Floresta](docs/deployment-floresta.md).

## What Works Today

- Connect BTCPay Server to Floresta.
- Watch for incoming BTC payments.
- Connect a wallet in watch-only mode.
- Show wallet history and balances in BTCPay.
- Send signed Bitcoin transactions through Floresta.
- Show Floresta connection and sync status in BTCPay.
- Rescan wallet data if needed.

## Current Limits

- BTC only.
- Regular single-person wallets only.
- The best-supported path is a standard modern Bitcoin wallet.
- Spending should be done with an external wallet.
- BTCPay generated wallets, hot wallets, and seed import are not supported while this backend is active.
- Fallback to third-party Electrum servers is not supported.

## More Documentation

- [Deployment with Floresta](docs/deployment-floresta.md) explains custom setups and public hosting.
- [Development Guide](docs/development.md) covers building, testing, local development, settings, and Floresta APIs used by the plugin.
