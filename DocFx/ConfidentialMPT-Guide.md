# Confidential MPT Guide (ConfidentialTransfer)

This guide explains how the XrplCSharp SDK supports Confidential Multi-Purpose Tokens — MPT balances hidden behind ElGamal encryption and zero-knowledge proofs, with optional issuer/auditor visibility.

> **Note:** Requires the `ConfidentialTransfer` amendment. As of mid-2026 it exists only on the rippled `develop` branch — not in any release, not on mainnet/testnet. The feature is in draft and subject to change.
>
> **Scope of the SDK:** XrplCSharp provides the **transport layer** — transaction models, binary serialization, signing and submission. Encrypted amounts, commitments, blinding factors and ZK proofs are **opaque hex blobs** to the SDK; producing them requires an external prover (cryptographic tooling published by the protocol authors). Until such a prover is available, only negative-path testing is possible (see [Testing](#testing)).

## Table of Contents

- [Overview](#overview)
- [Issuance Setup](#issuance-setup)
- [Transaction Types](#transaction-types)
- [Balance Lifecycle](#balance-lifecycle)
- [Ledger Objects](#ledger-objects)
- [Testing](#testing)
- [Common Errors](#common-errors)

---

## Overview

Confidential MPT splits a holder's balance into a **public** part (ordinary `MPTAmount`) and a **confidential** part (encrypted). Third parties see that a transfer happened, but not the amount. The issuer and an optional auditor can decrypt amounts with their own keys — every confidential operation carries the amount encrypted separately under each relevant key.

```text
public balance ──Convert──► confidential balance ──Send──► recipient inbox
      ▲                          │      ▲                        │
      └──────ConvertBack─────────┘      └───────MergeInbox───────┘
```

Incoming confidential transfers land in a holder's **inbox** and must be merged into the spendable confidential balance with `ConfidentialMPTMergeInbox` — this prevents a sender from invalidating a recipient's in-flight proofs.

---

## Issuance Setup

The issuance must be privacy-enabled and carry the issuer's (and optionally an auditor's) ElGamal encryption key:

```csharp
var create = new MPTokenIssuanceCreate
{
    Account = issuer.ClassicAddress,
    // ...
    IssuerEncryptionKey = issuerElGamalPubKeyHex,
    AuditorEncryptionKey = auditorElGamalPubKeyHex,   // optional
};
```

For an existing issuance, privacy is enabled one-way via `MPTokenIssuanceSet`:

```csharp
var set = new MPTokenIssuanceSet
{
    Account = issuer.ClassicAddress,
    MPTokenIssuanceID = issuanceId,
    Flags = MPTokenIssuanceSetFlags.tfMPTSetCanHoldConfidentialBalance,
    IssuerEncryptionKey = issuerElGamalPubKeyHex,
};
```

Rules enforced by rippled preflight (mirrored by SDK validation):

- a non-zero `TransferFee` **cannot** be combined with enabling confidential balances (`temBAD_TRANSFER_FEE`);
- `tifMPTCanHoldConfidentialBalance` in `ImmutableFlags` — on the create or on any later set — permanently forbids enabling privacy;
- an `AuditorEncryptionKey` requires an `IssuerEncryptionKey`.
- a key is registered once. With the ConfidentialMPTKeyRotation amendment active, an `MPTokenIssuanceSet` carrying a different key rotates it and increments `IssuerKeyEpoch` / `AuditorKeyEpoch` on the issuance; the current key is refused (`tecDUPLICATE`), and an auditor key may then be registered after the issuer key.

---

## Transaction Types

| Transaction | Purpose | Key fields |
|---|---|---|
| `ConfidentialMPTConvert` | Public → confidential | `MPTAmount` (public decimal), `HolderEncryptionKey`, `HolderEncryptedAmount`, `IssuerEncryptedAmount`, `AuditorEncryptedAmount`, `BlindingFactor`, `ZKProof` |
| `ConfidentialMPTMergeInbox` | Merge inbox into spendable confidential balance | `MPTokenIssuanceID` |
| `ConfidentialMPTConvertBack` | Confidential → public | encrypted amounts + `ZKProof` |
| `ConfidentialMPTSend` | Confidential transfer | `Destination`, `SenderEncryptedAmount`, `DestinationEncryptedAmount`, `IssuerEncryptedAmount`, `AuditorEncryptedAmount`, `AmountCommitment`, `BalanceCommitment`, `ZKProof`, optional `CredentialIDs` |
| `ConfidentialMPTClawback` | Issuer claws back confidential funds | encrypted amounts + proof |

All amounts encrypted under holder/issuer/auditor keys are supplied by the **prover**; the SDK validates shape (hex strings, required fields) and serializes them into the signed blob.

---

## Balance Lifecycle

1. **Convert**: holder moves part of the public balance into the confidential domain. The public `MPTAmount` decreases; `ConfidentialOutstandingAmount` on the issuance grows.
2. **Send**: confidential transfer to another holder's inbox. Commitments prove balance sufficiency without revealing it.
3. **MergeInbox**: recipient folds inbox funds into the spendable confidential balance.
4. **ConvertBack**: holder returns funds to the public domain.
5. **Clawback** (issuer, if allowed): removes confidential funds from a holder.

---

## Ledger Objects

- `LOMPTokenIssuance`: `IssuerEncryptionKey`, `AuditorEncryptionKey`, `IssuerKeyEpoch` and `AuditorKeyEpoch` (rotation counters, absent until the first rotation), `ConfidentialOutstandingAmount` (decimal string — a base-ten UInt64 field), `ImmutableFlags`
- `LOMPToken`: confidential balance/inbox fields (encrypted blobs + counters)

---

## Testing

Without an external prover the positive path cannot be exercised. What the repository's integration suite does instead (`Tests/Xrpl.Tests/Integration/transactions/TestIConfidentialMPT.cs`):

- builds a regular MPT issuance on the nightly stand (no confidential-balance flags or encryption keys — the positive privacy path needs the external prover);
- submits a `ConfidentialMPTConvert` with structurally valid but cryptographically bogus proof material;
- asserts the node answers with a **domain** verdict (any `tem`/`tec` from the ConfidentialTransfer logic), not a parse error — confirming the SDK's serialization of this payload shape is compatible with the node's parser all the way into domain validation.

```bash
docker compose -f .ci-config/docker-compose.batchv11.yml up -d --build
dotnet test Tests/Xrpl.Tests/Xrpl.Tests.csproj --settings test.runsettings --filter "TestIConfidentialMPT"
```

Tests are gated by `AmendmentGuard` and exit inconclusive on nodes without the amendment.

---

## Common Errors

| Error | Meaning |
|---|---|
| `temDISABLED` | The `ConfidentialTransfer` amendment is not active |
| `temBAD_CIPHERTEXT` | Malformed encrypted amount / key material |
| `temBAD_TRANSFER_FEE` | Non-zero `TransferFee` combined with enabling confidential balances |
| `tecBAD_PROOF` | The ZK proof does not verify (a possible protocol verdict for bogus prover input) |
| `terLOCKED` | The issuance or holder balance is locked |

*Русская версия: [ConfidentialMPT-Guide.ru](ConfidentialMPT-Guide.ru.md)*
