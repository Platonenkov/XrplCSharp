# Vault Guide (XLS-65d)

This guide explains how to use XRPL Single Asset Vaults with the XrplCSharp SDK. Vaults enable pooling assets (XRP, IOU tokens, or MPTs) and issuing shares to depositors proportional to their contribution.

> **Note:** Vaults require the Single Asset Vault amendment (XLS-65d). This feature is in draft and subject to change.

## Table of Contents

- [Overview](#overview)
- [Key Concepts](#key-concepts)
- [Transaction Types](#transaction-types)
- [Step-by-Step: Creating and Managing a Vault](#step-by-step-creating-and-managing-a-vault)
- [Vault Data Format](#vault-data-format)
- [Ledger Object: Vault](#ledger-object-vault)
- [Common Errors](#common-errors)
- [Best Practices](#best-practices)

---

## Overview

A Vault is a ledger structure that holds a single type of asset and issues shares (as MPT tokens) to depositors. When a user deposits assets into a vault, they receive shares proportional to their contribution. Shares can later be redeemed for the underlying assets.

```
Depositor A                        Vault (pseudo-account)
┌──────────────────┐              ┌──────────────────────────┐
│  Deposit 100 XRP │ ──────────►  │  Asset: XRP              │
│  Receive shares  │ ◄──────────  │  AssetsTotal: 300 XRP    │
└──────────────────┘              │  Shares (MPT): issued    │
                                  │  Owner: rBroker...       │
Depositor B                       │  ShareMPTID: 000ABC...   │
┌──────────────────┐              └──────────────────────────┘
│  Deposit 200 XRP │ ──────────►
│  Receive shares  │ ◄──────────
└──────────────────┘
```

Each vault creates:
- A **pseudo-account** that holds the pooled assets
- An **MPTokenIssuance** for vault shares

---

## Key Concepts

### Vault Shares (MPT)

When a depositor adds assets, the vault issues shares as MPT tokens. The number of shares depends on the vault's Scale and the current exchange rate.

**Share calculation:** Assets are multiplied by `10^Scale` to convert fractional amounts into whole-number shares. For example, with `Scale = 6`, depositing 20.3 units creates 20,300,000 shares.

- **XRP and MPT vaults:** Fixed Scale of 0 (1 asset unit = 1 share)
- **Trust line token vaults:** Scale 0–18 (default 6)

### Withdrawal Policy

Defines how withdrawals are handled:
- `0x0001` (`vaultStrategyFirstComeFirstServe`) — depositors can redeem any amount of assets provided they hold sufficient shares

### Vault Kind (LendingProtocolV1_1)

A vault is open-ended by default: deposits and withdrawals are accepted at any time. With the LendingProtocolV1_1 amendment a vault can instead be created closed-ended, with three phases fixed at creation:

| Phase | Ends at | Deposits | Withdrawals |
|-------|---------|----------|-------------|
| Subscription | `SubscriptionDate` (inclusive) | accepted | accepted |
| Investment | `RedemptionDate` | refused (`tecEXPIRED`) | refused (`tecTOO_SOON`) |
| Redemption | never | refused (`tecEXPIRED`) | accepted |

`VaultKind`, `SubscriptionDate` and `RedemptionDate` are set on `VaultCreate` and cannot be changed afterwards. A closed-ended vault requires both dates, with the redemption at least one minute and less than thirty years after the subscription; an open-ended vault may carry neither. The dates are `DateTime?` on the models and travel as seconds since the Ripple Epoch.

```csharp
using Xrpl.Models.Ledger;   // VaultKind

VaultCreate vaultTx = new VaultCreate
{
    Account = wallet.ClassicAddress,
    Asset = new IssuedCurrency { Currency = "XRP" },
    VaultKind = (uint)VaultKind.ClosedEnded,
    SubscriptionDate = DateTime.UtcNow.AddDays(7),
    RedemptionDate = DateTime.UtcNow.AddDays(97),
};
```

A node without the amendment refuses a `VaultCreate` that carries any of the three fields (`temDISABLED`). On the ledger object `VaultKind` is absent for an open-ended vault, whether it was created before the amendment or after it.

### Vault Flags

Set only at creation time via `VaultCreate`:
- **tfVaultPrivate** (`0x00010000`) — restricts access to credentialed accounts within a specified Permissioned Domain
- **tfVaultShareNonTransferable** (`0x00020000`) — makes vault shares non-transferable between accounts

### Unrealized Loss

The `LossUnrealized` field tracks potential losses in vault assets (e.g., from clawbacks). When unrealized losses exist, each share's redeemable value decreases proportionally.

---

## Transaction Types

| Transaction | Purpose | Who Submits |
|------------|---------|-------------|
| `VaultCreate` | Create a new vault | Vault Owner |
| `VaultDeposit` | Deposit assets into a vault | Any account |
| `VaultWithdraw` | Redeem shares for assets | Any share holder |
| `VaultSet` | Update vault metadata/settings | Vault Owner |
| `VaultDelete` | Delete an empty vault | Vault Owner |
| `VaultClawback` | Claw back assets from a holder | Asset Issuer |

---

## Step-by-Step: Creating and Managing a Vault

### 1. Create a Vault

```csharp
using Xrpl.Models.Transactions;
using Xrpl.Models.Common;
using Xrpl.Sugar;
using static Xrpl.Models.Common.Common;

// Create an XRP vault
VaultCreate vaultTx = new VaultCreate
{
    Account = wallet.ClassicAddress,
    Asset = new IssuedCurrency { Currency = "XRP" },
};
vaultTx = await client.Autofill(vaultTx);
TransactionSummary result = await client.SubmitAndWait(vaultTx, wallet, true);

// Extract VaultID from metadata
string vaultId = GetCreatedObjectId(result, LedgerEntryType.Vault);
```

### 2. Create a Vault with Optional Fields

```csharp
VaultCreate vaultTx = new VaultCreate
{
    Account = wallet.ClassicAddress,
    Asset = new IssuedCurrency { Currency = "XRP" },
    AssetsMaximum = "1000000000",           // max 1000 XRP (in drops)
    MPTokenMetadata = "48656C6C6F",         // hex-encoded metadata for shares
    Data = "7B226E223A225465737420566175"
         + "6C74222C2277223A226578616D70"
         + "6C652E636F6D227D",              // hex-encoded JSON metadata
    WithdrawalPolicy = 1,                   // FirstComeFirstServe
    Scale = 6,                              // decimal precision (trust line tokens only)
    Flags = (uint)VaultCreateFlags.tfVaultShareNonTransferable,
};
vaultTx = await client.Autofill(vaultTx);
TransactionSummary result = await client.SubmitAndWait(vaultTx, wallet, true);
```

### 3. Deposit Assets

Any account can deposit assets into a vault:

```csharp
VaultDeposit depositTx = new VaultDeposit
{
    Account = depositor.ClassicAddress,
    VaultID = vaultId,
    Amount = new Currency { Value = "50000000", CurrencyCode = "XRP" }, // 50 XRP
};
depositTx = await client.Autofill(depositTx);
await client.SubmitAndWait(depositTx, depositor, true);
```

### 4. Withdraw Assets

Any share holder can redeem shares for assets. Two approaches:

**Asset-based withdrawal** — specify desired asset quantity:

```csharp
VaultWithdraw withdrawTx = new VaultWithdraw
{
    Account = depositor.ClassicAddress,
    VaultID = vaultId,
    Amount = new Currency { Value = "25000000", CurrencyCode = "XRP" }, // 25 XRP
};
withdrawTx = await client.Autofill(withdrawTx);
await client.SubmitAndWait(withdrawTx, depositor, true);
```

**Withdraw to a different account:**

```csharp
VaultWithdraw withdrawTx = new VaultWithdraw
{
    Account = depositor.ClassicAddress,
    VaultID = vaultId,
    Amount = new Currency { Value = "25000000", CurrencyCode = "XRP" },
    Destination = "rRecipient...",
    DestinationTag = 42,
};
```

> **Note:** Transfer fees never apply to VaultWithdraw transactions.

### 5. Update Vault Settings

The vault owner can update mutable fields:

```csharp
VaultSet setTx = new VaultSet
{
    Account = wallet.ClassicAddress,
    VaultID = vaultId,
    AssetsMaximum = "2000000000",   // increase cap to 2000 XRP
    Data = "7B226E223A2255706461746564227D",  // new metadata
};
setTx = await client.Autofill(setTx);
await client.SubmitAndWait(setTx, wallet, true);
```

> **Restriction:** VaultSet can only modify `Data`, `AssetsMaximum`, and `DomainID`. The vault's public/private status is permanent.

### 6. Delete an Empty Vault

The vault must have zero assets and no outstanding shares:

```csharp
VaultDelete deleteTx = new VaultDelete
{
    Account = wallet.ClassicAddress,
    VaultID = vaultId,
};
deleteTx = await client.Autofill(deleteTx);
await client.SubmitAndWait(deleteTx, wallet, true);
```

### 7. Clawback Assets (Issuer Only)

The asset issuer can claw back assets from a vault share holder. Clawbacks cannot be performed on native XRP.

```csharp
VaultClawback clawbackTx = new VaultClawback
{
    Account = issuer.ClassicAddress,
    VaultID = vaultId,
    Holder = "rShareHolder...",
    Amount = new Currency
    {
        Value = "100",
        CurrencyCode = "USD",
        Issuer = issuer.ClassicAddress,
    },
};
clawbackTx = await client.Autofill(clawbackTx);
await client.SubmitAndWait(clawbackTx, issuer, true);
```

> When `Amount` is zero, the issuer claws back all funds up to the total shares the Holder owns.

---

## Vault Data Format

The `Data` field stores arbitrary metadata as a hex-encoded string (max 256 bytes). The SDK provides a helper class `VaultDataFormat` for a recommended JSON structure:

```csharp
using Xrpl.Models.Ledger;

// Create structured data
VaultDataFormat data = new VaultDataFormat
{
    Name = "My Investment Vault",
    Website = "https://example.com",
};

// Convert to hex for VaultCreate/VaultSet
string hex = VaultDataFormat.ToHex(data);

// Parse hex from a Vault ledger entry
LOVault vault = ...; // fetched via ledger_entry
VaultDataFormat parsed = vault.DataParsed;  // [JsonIgnore] helper property
string rawUtf8 = vault.DataRaw;            // [JsonIgnore] raw UTF-8 string
```

The `VaultDataFormat` JSON structure: `{"n":"name","w":"website"}` — whitespace-removed and hex-encoded.

---

## Ledger Object: Vault

Fetch a Vault ledger entry via `ledger_entry`:

```csharp
using Xrpl.Models.Methods;
using Xrpl.Models.Ledger;

LedgerEntryRequest request = new LedgerEntryRequest { Index = vaultId };
LedgerEntryResponse response = await client.LedgerEntry(request);

LOVault vault = (LOVault)response.Node;

Console.WriteLine($"Owner: {vault.Owner}");
Console.WriteLine($"Asset: {vault.Asset}");
Console.WriteLine($"AssetsTotal: {vault.AssetsTotal}");
Console.WriteLine($"AssetsAvailable: {vault.AssetsAvailable}");
Console.WriteLine($"ShareMPTID: {vault.ShareMPTID}");
Console.WriteLine($"Scale: {vault.Scale}");
Console.WriteLine($"Metadata: {vault.DataParsed?.Name}");
```

### Key LOVault Fields

| Field | Type | Description |
|-------|------|-------------|
| `Account` | string | Vault's pseudo-account address |
| `Owner` | string | Vault owner's account address |
| `Asset` | IssuedCurrency | The asset held by the vault |
| `AssetsTotal` | string | Total vault value (Number type) |
| `AssetsAvailable` | string | Currently accessible assets (Number type) |
| `AssetsMaximum` | string | Holding capacity, 0 = unlimited (Number type) |
| `LossUnrealized` | string | Unrealized loss amount (Number type) |
| `ShareMPTID` | string | MPTokenIssuance ID for vault shares |
| `WithdrawalPolicy` | uint? | Withdrawal strategy |
| `Scale` | uint? | Decimal precision for share calculations |
| `LEVersion` | uint? | Schema version (`VaultVersion`), absent on vaults created before cash-basis accounting |
| `VaultKind` | uint? | `VaultKind.ClosedEnded` (1) for a closed-ended vault, absent otherwise |
| `SubscriptionDate` | DateTime? | End of the subscription phase (closed-ended only) |
| `RedemptionDate` | DateTime? | Start of the redemption phase (closed-ended only) |
| `Data` | string | Hex-encoded metadata (max 256 bytes) |
| `Sequence` | uint? | Creation transaction sequence |

### Vault Flags (LOVault)

```csharp
[Flags]
public enum VaultLedgerFlags : uint
{
    lsfVaultPrivate = 0x00010000,
}
```

---

## Common Errors

| Error Code | Cause |
|-----------|-------|
| `tecHAS_OBLIGATIONS` | Cannot delete vault with outstanding shares or assets |
| `tecNO_PERMISSION` | Non-owner attempting VaultSet/VaultDelete, or non-issuer attempting VaultClawback |
| `tecUNFUNDED` | Insufficient balance for VaultDeposit |
| `tecINSUFFICIENT_FUNDS` | Insufficient shares for VaultWithdraw |
| `tecFROZEN` | Trust line between vault pseudo-account and issuer is frozen |
| `temMALFORMED` | Data exceeds 256 bytes, or invalid field values |
| `tecOBJECT_NOT_FOUND` | VaultID does not exist |

---

## Best Practices

1. **Always set AssetsMaximum** — limits exposure and prevents unbounded deposits
2. **Use VaultDataFormat** — provides a standard metadata structure readable by other applications
3. **Check LossUnrealized** — before withdrawing, verify the vault has no unrealized losses that would reduce share value
4. **Private vaults for regulated assets** — use `tfVaultPrivate` with a Permissioned Domain for KYC-gated vaults
5. **Non-transferable shares** — use `tfVaultShareNonTransferable` when shares should not be traded on secondary markets
6. **Scale for trust line tokens** — choose Scale carefully at creation (it cannot be changed later). Higher Scale = finer granularity but larger share numbers
7. **Clawback considerations** — only the asset issuer can clawback, and only for non-XRP assets. Clawbacks create unrealized losses for other depositors
