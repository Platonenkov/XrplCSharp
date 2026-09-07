# Integration Tests

Integration tests (`TestI*` filter) require a local `rippled` node in standalone mode with amendments enabled and an automatic ledger acceptor.

> **Do not** run a bare `rippled -a` or a random standalone image: a node without amendment configuration answers `temDISABLED` to AMM/NFT/MPT/Batch and most other modern transactions. Use the Docker Compose environments below — they enable the required amendments at genesis.

## Run

```bash
# release-node environment (rippled 3.2.0, same as CI)
docker compose -f .ci-config/docker-compose.ci.yml up -d
dotnet test Tests/Xrpl.Tests/Xrpl.Tests.csproj --settings test.runsettings --filter "TestI"
docker compose -f .ci-config/docker-compose.ci.yml down
```

Amendment-gated classes (`TestIBatch`, `TestIDelegateSet`, `TestIClosedEndedVault`) are skipped on the release node and need the nightly-develop environment instead:

```bash
docker compose -f .ci-config/docker-compose.batchv11.yml up -d --build
dotnet test Tests/Xrpl.Tests/Xrpl.Tests.csproj --settings test.runsettings --filter "TestIBatch|TestIDelegateSet|TestIClosedEndedVault"
docker compose -f .ci-config/docker-compose.batchv11.yml down
```

Tests connect to `ws://localhost:6006` by default and fund wallets from the standalone genesis account (see `Utils.cs`).

## Node profile

The node under test is chosen through the environment (`IntegrationTestConfig` in `Utils.cs`); every `TestI*` class reads it, none hard-codes a node:

| Variable | Values | Effect |
|----------|--------|--------|
| `XRPL_TEST_NODE` | `standalone` (default), `devnet`, `testnet` | Funding policy (genesis account vs. public faucet) and whether `ledger_accept` is issued |
| `XRPL_TEST_NODE_URL` | any WebSocket URL | Replaces the profile's default URL - a stand on other ports, or a private node |
| `XRPL_TEST_ADMIN_AUTH_URL` | any WebSocket URL | The credential-protected admin port (`[port_ws_admin_auth]`, default `ws://127.0.0.1:6007`) used by `TestIAdminCredentials`; standalone only |

```bash
# a second standalone stand published on other ports
XRPL_TEST_NODE_URL=ws://localhost:7016 dotnet test Tests/Xrpl.Tests/Xrpl.Tests.csproj --settings test.runsettings --filter "TestI"

# devnet: wallets come from the public faucet (about 100 XRP each, rate-limited)
XRPL_TEST_NODE=devnet dotnet test Tests/Xrpl.Tests/Xrpl.Tests.csproj --settings test.runsettings --filter "FullyQualifiedName~TestIBatchInnerTypes|FullyQualifiedName~TestISponsoredTypes"
```

Accounts are generated per run, never derived from a fixed phrase. A phrase is the same account on every network, so on a public one it is shared with everyone who ever ran the same test, and two classes disable a master key on one of theirs. On the standalone stand a derived account also carried state between runs, which let a "create" test pass as a modify.

A public faucet hands out a fixed 100 XRP per call, less than a single account needs in some flows (a lending broker funds a 100 XRP vault and a 50 XRP cover). `IntegrationTestConfig.EnsureBalanceAsync` tops an account up to a stated minimum by calling the faucet again; on the standalone stand the master payment covers any realistic minimum and it returns after the first check.

Amendment-gated classes check the network through `AmendmentGuard` and mark themselves inconclusive when the amendment is not active there, so a broad filter is safe on any profile. The GitHub workflow `devnet-coverage.yml` (manual dispatch) runs the coverage-oriented classes against devnet this way.

## Coverage matrices

Three classes exist to exercise the SDK's transaction surface end to end rather than one feature at a time. They double as traffic for the XRPL Foundation amendment dashboard, which scores devnet by the transaction types, fields and flags that validated transactions have touched:

- `TestIBatchInnerTypes` - every transaction type wrapped as a Batch inner (XLS-56), each inner read back by its computed id. rippled rejects a Batch with fewer than two inners (`temARRAY_EMPTY`), which the SDK validation mirrors.
- `TestISponsoredTypes` - the `Sponsor` field (XLS-68) on every transaction type other than Payment, with the sponsor co-signing.
- `TestIAMMMpt` - every AMM transaction type over an MPT asset (XLS-62, `MPTokensV2`). On the standalone stands the amendment is a `[features]` preset invisible to the on-ledger guard, so the class runs there unconditionally.

Full details — amendment activation, `temDISABLED` troubleshooting, genesis funding, ledger advancement: [Standalone Node Guide](../../../DocFx/StandaloneNode-Guide.md) ([русская версия](../../../DocFx/StandaloneNode-Guide.ru.md)).
