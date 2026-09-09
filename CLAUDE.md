# XrplCSharp — C# SDK for XRP Ledger

A pure C# implementation for interacting with the XRP Ledger. Published as the `Xrpl` NuGet package.
Provides native C# methods and models for XRPL transactions, serialization, transaction signing, and the rippled WebSocket API.

Repository: [github.com/StaticBit-io/XrplCSharp](https://github.com/StaticBit-io/XrplCSharp)

## Technology Stack

- **.NET 8 / 9 / 10** — multi-target (`net8.0;net9.0;net10.0` for main library)
- **C# latest** — `<LangVersion>latest</LangVersion>` in main Xrpl project
- Base libraries target **netstandard2.0** (AddressCodec, BinaryCodec) and **netstandard2.1** (Keypairs)
- **System.Text.Json** — JSON serialization in client code
- **NBitcoin** 8.0.13 — Bitcoin/crypto primitives
- **Portable.BouncyCastle** 1.9.0 — cryptographic operations
- **Chaos.NaCl.Standard** 1.0.0 — Ed25519 operations
- **MSTest** — test framework
- **Flurl.Http** — HTTP client in tests

## Project Structure

Solution file: `XrplCSharp.sln`

```
XrplCSharp/
├── Base/                              # Low-level codec libraries
│   ├── Xrpl.AddressCodec/            # Base58, XRP address encoding/decoding (netstandard2.0)
│   ├── Xrpl.BinaryCodec/            # XRPL binary serialization format (netstandard2.0)
│   └── Xrpl.Keypairs/               # Ed25519/secp256k1 key generation and signing (netstandard2.1)
│
├── Xrpl/                             # Main SDK library (net8.0;net9.0;net10.0)
│   ├── Client/                       # XrplClient — WebSocket client to rippled
│   │   ├── IXrplClient.cs           # Main interface for consumers
│   │   ├── XrplClient.cs            # WebSocket connection, RPC methods, subscriptions
│   │   └── Json/                    # JSON converters for XRPL types
│   ├── Models/                       # Request/response DTOs for rippled API
│   │   ├── Methods/                 # account_info, account_lines, tx, submit, etc.
│   │   ├── Transactions/           # Payment, TrustSet, OfferCreate, AMMCreate, etc.
│   │   ├── Subscriptions/          # Stream events (ledger, transactions, accounts)
│   │   ├── Ledger/                 # Ledger objects (AccountRoot, RippleState, Offer, etc.)
│   │   ├── Common/                 # Currency, Amount, shared types
│   │   └── Utils/                  # Utility models
│   ├── Sugar/                       # High-level helpers (auto-fill, submit-and-wait, fee estimation)
│   ├── Wallet/                      # XrplWallet — key generation, signing, testnet funding
│   ├── Utils/                       # Hashing, utilities
│   └── Properties/
│
├── Tests/
│   ├── Xrpl.Tests/                  # Unit (TestU) + Integration (TestI) tests for main library
│   ├── Xrpl.AddressCodec.Test/     # AddressCodec unit tests
│   ├── Xrpl.BinaryCodec.Test/     # BinaryCodec unit tests
│   ├── Xrpl.Keypairs.Test/        # Keypairs unit tests
│   └── TestsClients/
│       ├── Test.ClonsoleApp/       # Console demo app
│       └── Blazor-WebAssembly/     # Blazor WASM demo app
│
├── Tools/
│   └── GenerateEnums/              # Code generator for XRPL enums from rippled definitions (net8.0)
│
├── DocFx/                           # Documentation generator config
├── docs/                            # Generated HTML documentation
│
├── .ci-config/
│   ├── docker-compose.ci.yml       # Docker Compose: rippled standalone + ledger-acceptor (release image)
│   ├── rippled.cfg                 # rippled configuration for CI ([features]/[amendments] are generated)
│   ├── generate-amendments.sh      # Regenerates rippled.cfg [features]/[amendments] from a rippled tag's features.macro
│   ├── docker-compose.batchv11.yml # Nightly-develop stand for unreleased amendments (BatchV1_1, ...)
│   ├── Dockerfile.nightly          # xrpld nightly image from repos.ripple.com (version pinned)
│   ├── rippled.batchv11.cfg        # Config with [amendments] genesis up-votes for the nightly stand
│   └── validators.txt              # Validator config for CI
│
├── XrplCSharp.sln                   # Solution file
├── test.runsettings                 # MSTest config: parallel at class level
├── CONTRIBUTING.md                  # Development setup and release process
├── CHANGES.md                       # Changelog
└── README.md                        # Usage examples and documentation links
```

## Architecture

### Package Hierarchy

```
Xrpl (main NuGet package)
├── Xrpl.BinaryCodec (binary serialization)
│   └── Xrpl.AddressCodec (Base58, address codec)
└── Xrpl.Keypairs (Ed25519/secp256k1)
    └── Xrpl.AddressCodec
```

### Key Public API

- **`XrplClient`** (`Xrpl.Client`) — main entry point; WebSocket connection to rippled, RPC methods (AccountInfo, Fee, ServerInfo, Subscribe, etc.), event-driven API (`OnConnected`, etc.)
- **`IXrplClient`** — interface for DI and testing
- **`XrplWallet`** (`Xrpl.Wallet`) — wallet generation, seed import, transaction signing
- **`WalletSugar`** — testnet faucet funding
- **`Xrpl.Sugar`** — auto-fill transaction fields, `SubmitAndWait`, fee helpers
- **`Xrpl.Models.*`** — full set of DTOs for rippled API methods, transactions, ledger objects

### Key Namespaces

| Namespace | Purpose |
|-----------|---------|
| `Xrpl.Client` | WebSocket client and JSON-RPC interface |
| `Xrpl.Client.Json` | JSON converters for XRPL types |
| `Xrpl.Models.Methods` | Request/response types for rippled API commands |
| `Xrpl.Models.Transactions` | Transaction type models (Payment, TrustSet, etc.) |
| `Xrpl.Models.Subscriptions` | Subscription/stream event models |
| `Xrpl.Models.Ledger` | Ledger entry types (AccountRoot, RippleState, etc.) |
| `Xrpl.Models.Common` | Currency, Amount, and shared types |
| `Xrpl.Sugar` | High-level transaction helpers |
| `Xrpl.Wallet` | Key management and signing |
| `Xrpl.Utils` | Hashing and utility functions |
| `Xrpl.BinaryCodec` | XRPL binary serialization |
| `Xrpl.BinaryCodec.Enums` | Generated enums from rippled definitions |
| `Xrpl.BinaryCodec.Types` | Serialization field types |
| `Xrpl.AddressCodec` | Base58 and address encoding |
| `Xrpl.Keypairs` | Ed25519 and secp256k1 key operations |

## Build & Test

### Prerequisites

- .NET 10 SDK (for development)
- Docker + Docker Compose (for integration tests)

### Build

```bash
dotnet restore
dotnet build
```

### Unit Tests

```bash
dotnet test --verbosity normal --settings test.runsettings --filter "TestU"
```

### Integration Tests

Integration tests require a standalone `rippled` node. The Docker Compose setup provides one with automatic ledger acceptance:

```bash
# Start rippled in standalone mode
docker compose -f .ci-config/docker-compose.ci.yml up -d

# Run integration tests
dotnet test Tests/Xrpl.Tests/Xrpl.Tests.csproj --verbosity normal --settings test.runsettings --filter "TestI"

# Stop containers
docker compose -f .ci-config/docker-compose.ci.yml down
```

The Docker setup exposes:
- Port 5005 — JSON-RPC
- Port 5006 — WebSocket
- Port 6006 — Admin WebSocket

The `ledger-acceptor` container calls `ledger_accept` every 4 seconds to advance the standalone ledger.

### Integration Tests for Unreleased Amendments (nightly stand)

Some amendments (e.g. `BatchV1_1`, `PermissionDelegationV1_1`) exist only on the rippled `develop` branch and are absent from the release image used by CI. A second stand runs the nightly `xrpld` build with those amendments enabled at genesis:

```bash
# Uses the same ports as the CI stand — stop it first
docker compose -f .ci-config/docker-compose.ci.yml down

docker compose -f .ci-config/docker-compose.batchv11.yml up -d --build
dotnet test Tests/Xrpl.Tests/Xrpl.Tests.csproj --settings test.runsettings --filter "TestIBatch|TestIDelegateSet|TestIClosedEndedVault"
docker compose -f .ci-config/docker-compose.batchv11.yml down
```

The node under test is selected through the environment, not in code: `XRPL_TEST_NODE` (`standalone` default, `devnet`, `testnet`) picks the funding policy, `XRPL_TEST_NODE_URL` overrides the WebSocket URL (a stand on other ports, a private node). See `Tests/Xrpl.Tests/Integration/README.md`.

Amendment-dependent test classes use `Tests/Xrpl.Tests/Integration/AmendmentGuard.cs`: `ClassInitialize` checks the Amendments ledger object and marks tests inconclusive (skipped, exit 0) when the amendment is not active — so these tests are safe on the CI stand and run for real on the nightly stand. To gate a new test class, add the amendment id constant to `AmendmentGuard` and call `Assert.Inconclusive` from `TestInitialize` when inactive.

Nightly stand specifics (see comments in `Dockerfile.nightly` / `rippled.batchv11.cfg`):
- rippled was renamed to **xrpld** on `develop`; the nightly apt channel publishes the `xrpld` package. The version must be **pinned**: the build-timestamp format shrank from 14 to 12 digits in mid-2026, so Debian version ordering ranks older builds above newer ones. To bump, run `.ci-config/bump-nightly-pin.sh` (`--check` reports without changing anything): it picks the newest `xrpld` build from the `jammy nightly` Packages index at repos.ripple.com, rewrites `ARG XRPLD_VERSION` and regenerates the nightly amendment lists from the develop commit encoded in that version string. The ref must match the pinned build — a newer ref can emit feature names unknown to the binary, which rejects them at startup — which is why the script derives it from the version rather than taking it as an argument. `nightly-pin-watch.yml` runs the same script weekly and opens a PR once the pin is older than its allowance.
- On xrpld (3.2.x and `develop`) the two amendment config sections do different jobs. `[amendments]` (`<hash> <name>`, hash = sha512half of the name) registers genesis up-votes at `--start` — amendments become enabled **on-ledger** (Amendments object, `feature` RPC), which is what `AmendmentGuard`-gated tests check; only Supported::Yes amendments — unsupported ones (e.g. MPTokensV2 on 3.2.0) are skipped at genesis. `[features]` does NOT vote (startup logs say "will be down voted by default") but it DOES feed Rules presets: listed amendments are treated as active during **transaction processing** even when not enabled on-ledger — this is the only way to exercise Supported::No code paths (MPTokensV2 MPT-AMM tests run via this mechanism) and the reason `[features]` may be a superset of `[amendments]`. Without `[amendments]`, introspection reports everything disabled and guard-gated tests skip, even though preset-covered transactors work. Both sections are regenerated by `.ci-config/generate-amendments.sh <tag>` — do not edit the lists by hand.

### Generate Documentation

```bash
dotnet tool install -g docfx
docfx DocFx/docfx.json
```

Output goes to `docs/` directory. Published to GitHub Pages.

## CI/CD

### GitHub Actions (`.github/workflows/`)

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `dotnet.test.yml` | Push/PR to `dev`, `release`; merge queue | Build + unit tests (`TestU`) + integration tests (`TestI` with Docker rippled) |
| `nuget.release.yml` | Push to `release` | Build Release → Pack → Publish to GitHub Packages + NuGet.org |
| `docs.yml` | Push to `release` | DocFx → GitHub Pages |
| `protocol-watch.yml` | Weekly cron (Mon 06:00 UTC); manual | Diffs rippled develop `*.macro` protocol files vs the baseline in the `protocol-watch` tracking issue; comments there on changes |
| `release-watch.yml` | Weekly cron (Mon 06:30 UTC); manual | Checks the CI stand against the latest stable rippled release; when a newer release has a Docker image, regenerates the stand config (`generate-amendments.sh`), smoke-tests it and opens a bump PR (via GitHub App `RELEASE_WATCH_APP_ID`/`RELEASE_WATCH_APP_PRIVATE_KEY` or `RELEASE_WATCH_PAT`; falls back to a `release-watch` issue comment) |
| `definitions-watch.yml` | Weekly cron (Mon 07:00 UTC); manual | Raises the nightly stand and diffs `definitions.json` against its `server_definitions`; red when the node has fields the SDK lacks. Its horizon is the nightly pin, so a stale pin makes it report on the past |
| `devnet-coverage.yml` | Manual only | Runs the coverage-oriented integration classes (`TestIBatchInnerTypes`, `TestISponsoredTypes`, `TestIAMMMpt`, XChain, Sponsorship, Batch) against devnet or testnet with faucet funding; feeds the XRPL Foundation amendment dashboard. Never part of CI |
| `nightly-pin-watch.yml` | Weekly cron (Mon 05:00 UTC); manual | Checks the nightly `xrpld` pin against the nightly apt channel; once it is older than `MAX_PIN_AGE_DAYS` (21), runs `bump-nightly-pin.sh`, starts the stand on the new pin, and opens a bump PR (same credential ladder as release-watch; falls back to a `nightly-pin-watch` issue comment) |

Integration tests do **not** run on PRs into `dev` — `dev` uses a GitHub merge queue, and the `integration` job runs on the `merge_group` event against the merge result before the merge lands ("Merge when ready" button). Promotion PRs into `release` still run the full suite directly. New pushes to a PR cancel its in-flight CI run (`concurrency`, PR events only).

### Release Process

1. Ensure all tests pass on `dev`
2. Bump `<PackageVersion>` **only in the packages that actually changed** — not in all four. The base packages (`Xrpl.AddressCodec`, `Xrpl.BinaryCodec`, `Xrpl.Keypairs`) are consumed via `ProjectReference`, so a `Xrpl` package built at a newer version keeps depending on the already published base packages at their existing version. Leaving an untouched package behind is correct, not an oversight. Check with `git diff --stat origin/release...origin/dev -- Base/` before deciding
3. Choose the bump from the nature of the change: patch for a bugfix with no contract change, minor otherwise
4. Update `CHANGES.md`
5. Merge `dev` → `release`
6. NuGet publish triggers automatically on push to `release`
7. Create GitHub release with tag

### NuGet Packages Published

- `Xrpl` (main package)
- `Xrpl.AddressCodec`
- `Xrpl.BinaryCodec`
- `Xrpl.Keypairs`

## Code Generation

The `Tools/GenerateEnums/` project generates C# enums from rippled's binary codec definitions.
Source definitions are used to keep `Xrpl.BinaryCodec.Enums` in sync with the latest rippled protocol changes.
Generated files have `*.Generated.cs` suffix.

Reference: [xrpl-codec-gen](https://github.com/RichardAH/xrpl-codec-gen)

## Development Notes

- Documentation language: English. Prose, headings, and examples in English; do not mix languages
- Commit messages are English too — subject and body. This repository is public, and its history is part of what a reader sees; 36 of the 562 subjects on `dev` are Russian, and no more are to be added
- No `Directory.Build.props` or `global.json` — versions are managed per `.csproj`
- No centralized package management — each project specifies its own NuGet versions
- `test.runsettings` configures MSTest parallel execution at class level
- `.editorconfig` is minimal (primarily CS8632 suppression)
- The library uses `System.Text.Json` (not Newtonsoft.Json) for serialization
