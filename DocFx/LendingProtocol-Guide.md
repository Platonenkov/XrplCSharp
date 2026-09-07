# Lending Protocol Guide (XLS-66d)

This guide explains how to use the XRPL Lending Protocol with the XrplCSharp SDK. The lending protocol enables on-ledger collateralized loans managed through loan brokers and vaults.

> **Note:** The Lending Protocol requires the `LendingProtocol` amendment (XLS-66d). This feature is in draft and subject to change. Requires rippled 3.1.0+.

## Table of Contents

- [Overview](#overview)
- [Key Concepts](#key-concepts)
- [Transaction Types](#transaction-types)
- [Step-by-Step: Setting Up a Loan Broker](#step-by-step-setting-up-a-loan-broker)
- [Step-by-Step: Creating and Managing a Loan](#step-by-step-creating-and-managing-a-loan)
- [CounterpartySignature (LoanSet Co-Signing)](#counterpartysignature-loanset-co-signing)
- [Ledger Objects](#ledger-objects)
- [Number Type](#number-type)
- [Common Errors](#common-errors)
- [Best Practices](#best-practices)

---

## Overview

The XRPL Lending Protocol introduces on-ledger collateralized lending with the following architecture:

```text
Broker (Lender)                        Borrower
┌──────────────────────┐              ┌──────────────────────┐
│                      │   LoanSet    │                      │
│  Vault ◄── Deposit   │ ◄──────────► │  Receives principal  │
│  Cover ◄── Deposit   │   (co-signed)│  Repays via LoanPay  │
│  LoanBroker ── Loan  │              │                      │
└──────────────────────┘              └──────────────────────┘
```

A **broker** (lender) creates a vault to hold lending assets, sets up a loan broker with lending parameters, and issues loans to borrowers. The **borrower** co-signs the loan agreement and repays through periodic payments.

---

## Key Concepts

### Vault

A vault holds the assets available for lending. Created via `VaultCreate`, it stores XRP or IOU tokens that the broker can lend out. Before creating a loan broker, you must create and fund a vault.

### Loan Broker

A **LoanBroker** is a ledger object that represents a lending entity. It references a vault and defines lending parameters such as cover rates, management fees, and debt limits. Created via `LoanBrokerSet`.

### Cover

The broker must deposit **cover** (collateral from the broker's side) into the loan broker. Cover protects borrowers and ensures the broker has skin in the game. Managed via `LoanBrokerCoverDeposit` and `LoanBrokerCoverWithdraw`.

### Loan

A **Loan** is a ledger object representing an active loan between a broker and a borrower. Created via `LoanSet` (requires co-signing by both parties). The loan tracks principal, interest rates, payment schedule, and outstanding balances.

### CounterpartySignature

`LoanSet` is a special transaction that requires **two signatures**: the broker (submitter) signs the transaction normally, and the borrower (counterparty) provides a `CounterpartySignature`. Both sign the same transaction, each under its own hash prefix since rippled's `fixCleanup3_4_0`.

### Number Type

Loan-specific numeric fields (e.g., `PrincipalRequested`, `DebtMaximum`) use the XRPL `Number` type — a 12-byte format consisting of an 8-byte signed mantissa and a 4-byte signed exponent. This is different from the standard `Amount` type used for payments.

---

## Transaction Types

| Transaction | Purpose | Who Submits |
|------------|---------|-------------|
| `LoanBrokerSet` | Create or update a loan broker | Broker |
| `LoanBrokerDelete` | Delete a loan broker | Broker |
| `LoanBrokerCoverDeposit` | Deposit cover into a broker | Broker |
| `LoanBrokerCoverWithdraw` | Withdraw cover from a broker | Broker |
| `LoanBrokerCoverClawback` | Clawback cover from a holder | Broker |
| `LoanSet` | Create a new loan (co-signed) | Broker + Borrower |
| `LoanDelete` | Delete a fully repaid loan | Broker |
| `LoanManage` | Manage loan state (default/impair/unimpair) | Broker |
| `LoanPay` | Make a payment on a loan | Borrower |

---

## Step-by-Step: Setting Up a Loan Broker

### 1. Create a Vault

The broker first creates a vault to hold lending assets:

```csharp
using Xrpl.Models.Transactions;
using Xrpl.Models.Common;
using Xrpl.Sugar;
using static Xrpl.Models.Common.Common;

VaultCreate vaultTx = new VaultCreate
{
    Account = walletBroker.ClassicAddress,
    Asset = new IssuedCurrency { Currency = "XRP" },
};
vaultTx = await client.Autofill(vaultTx);
TransactionSummary vaultResult = await client.SubmitAndWait(vaultTx, walletBroker, true);

// Extract VaultID from metadata
string vaultId = GetCreatedObjectId(vaultResult, LedgerEntryType.Vault);
```

### 2. Deposit Assets into the Vault

Fund the vault so the broker has assets to lend:

```csharp
VaultDeposit depositTx = new VaultDeposit
{
    Account = walletBroker.ClassicAddress,
    VaultID = vaultId,
    Amount = new Currency { Value = "100000000", CurrencyCode = "XRP" }, // 100 XRP
};
depositTx = await client.Autofill(depositTx);
await client.SubmitAndWait(depositTx, walletBroker, true);
```

### 3. Create the Loan Broker

Create a broker that references the funded vault:

```csharp
LoanBrokerSet brokerTx = new LoanBrokerSet
{
    Account = walletBroker.ClassicAddress,
    VaultID = vaultId,
};
brokerTx = await client.Autofill(brokerTx);
TransactionSummary brokerResult = await client.SubmitAndWait(brokerTx, walletBroker, true);

string brokerId = GetCreatedObjectId(brokerResult, LedgerEntryType.LoanBroker);
```

### 4. Configure Broker Parameters (Optional)

Update the broker with lending parameters:

```csharp
LoanBrokerSet updateTx = new LoanBrokerSet
{
    Account = walletBroker.ClassicAddress,
    VaultID = vaultId,
    CoverRateMinimum = 15000,        // 150% minimum cover rate
    CoverRateLiquidation = 12000,    // 120% liquidation threshold
    ManagementFeeRate = 100,         // 1% management fee (basis points / 100)
};
updateTx = await client.Autofill(updateTx);
await client.SubmitAndWait(updateTx, walletBroker, true);
```

### 5. Deposit Cover

Deposit cover into the broker to enable loan issuance:

```csharp
LoanBrokerCoverDeposit coverTx = new LoanBrokerCoverDeposit
{
    Account = walletBroker.ClassicAddress,
    LoanBrokerID = brokerId,
    Amount = new Currency { Value = "50000000", CurrencyCode = "XRP" }, // 50 XRP
};
coverTx = await client.Autofill(coverTx);
await client.SubmitAndWait(coverTx, walletBroker, true);
```

---

## Step-by-Step: Creating and Managing a Loan

### 1. Create a Loan (LoanSet)

`LoanSet` requires co-signing by both the broker and the borrower. See [CounterpartySignature](#counterpartysignature-loanset-co-signing) for the full signing flow.

```csharp
LoanSet loanTx = new LoanSet
{
    Account = walletBroker.ClassicAddress,
    LoanBrokerID = brokerId,
    Counterparty = walletBorrower.ClassicAddress,
    PrincipalRequested = "10000000",  // Number type (not drops)
};

// Requires special co-signing — see CounterpartySignature section below
TransactionSummary result = await SubmitLoanSetWithCounterpartySig(
    client, loanTx, walletBroker, walletBorrower);

string loanId = GetCreatedObjectId(result, LedgerEntryType.Loan);
```

### 2. Make a Loan Payment

The borrower makes payments on the loan:

```csharp
LoanPay payTx = new LoanPay
{
    Account = walletBorrower.ClassicAddress,
    LoanID = loanId,
    Amount = new Currency { Value = "10000000", CurrencyCode = "XRP" },
};
payTx = await client.Autofill(payTx);
TransactionSummary result = await client.SubmitAndWait(payTx, walletBorrower, true);
```

### 3. Delete a Fully Repaid Loan

After the loan is fully repaid, the broker can delete it:

```csharp
LoanDelete deleteTx = new LoanDelete
{
    Account = walletBroker.ClassicAddress,
    LoanID = loanId,
};
deleteTx = await client.Autofill(deleteTx);
await client.SubmitAndWait(deleteTx, walletBroker, true);
```

> **Important:** You cannot delete a loan that has outstanding obligations (`tecHAS_OBLIGATIONS`). The loan must be fully repaid first.

### 4. Manage Loan State

The broker can mark a loan as defaulted, impaired, or restore it:

```csharp
// Mark loan as defaulted
LoanManage manageTx = new LoanManage
{
    Account = walletBroker.ClassicAddress,
    LoanID = loanId,
    Flags = LoanManageFlags.tfLoanDefault,
};
manageTx = await client.Autofill(manageTx);
await client.SubmitAndWait(manageTx, walletBroker, true);
```

**LoanManage flags (mutually exclusive):**
- `tfLoanDefault` — mark loan as defaulted
- `tfLoanImpair` — mark loan as impaired
- `tfLoanUnimpair` — restore loan from impaired state

### 5. Delete the Loan Broker

When the broker has no outstanding loans:

```csharp
LoanBrokerDelete deletebrokerTx = new LoanBrokerDelete
{
    Account = walletBroker.ClassicAddress,
    LoanBrokerID = brokerId,
};
deletebrokerTx = await client.Autofill(deletebrokerTx);
await client.SubmitAndWait(deletebrokerTx, walletBroker, true);
```

---

## CounterpartySignature (LoanSet Co-Signing)

`LoanSet` is unique among XRPL transactions — it requires **two signatures**. The broker signs the transaction as normal (`TxnSignature`), and the borrower provides a `CounterpartySignature` — an inner STObject containing the borrower's `SigningPubKey` and `TxnSignature`.

The SDK provides `LoanSigningHelper` and `XrplWallet.SignAsLoanCounterparty()` with three signing patterns, analogous to Batch signing (V1/V2/V3).

### Preparation (Common to All Patterns)

```csharp
using Xrpl.Wallet;

// Autofill handles fee calculation (including CounterpartySignature overhead)
loanTx = await client.Autofill(loanTx);
// PrepareForSigning sets broker's SigningPubKey and removes signature fields
JsonObject prepared = LoanSigningHelper.PrepareForSigning(loanTx, brokerWallet);
```

### V1 — Automatic (Both Keys Available Locally)

Use when both broker and borrower wallets are available on the same device:

```csharp
SignatureResult result = LoanSigningHelper.SignLoanSet(prepared, brokerWallet, borrowerWallet);
await client.SubmitRequest(result.TxBlob);
```

### V2 — Parallel (Keys on Separate Devices, Sign Independently)

Use when broker and borrower sign independently and a third party combines the signatures:

```csharp
// Device A (broker): signs the transaction normally
var brokerDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
    prepared.ToJsonString(), XrplJsonOptions.Default);
SignatureResult brokerSig = brokerWallet.Sign(brokerDict);

// Device B (borrower): signs as counterparty (independent copy of the payload)
var borrowerDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
    prepared.ToJsonString(), XrplJsonOptions.Default);
SignatureResult counterpartySig = borrowerWallet.SignAsLoanCounterparty(borrowerDict);

// Combiner: merge both signatures into a single blob
SignatureResult combined = LoanSigningHelper.CombineLoanSignatures(
    brokerSig.TxBlob, counterpartySig.TxBlob);
await client.SubmitRequest(combined.TxBlob);
```

### V3 — Sequential (Borrower Signs First, Passes to Broker)

Use in the real-world scenario where the borrower signs first and sends the partially signed blob to the broker:

```csharp
// Step 1: Borrower receives prepared tx JSON, signs as counterparty
var txDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
    prepared.ToJsonString(), XrplJsonOptions.Default);
SignatureResult withCounterparty = borrowerWallet.SignAsLoanCounterparty(txDict);
// withCounterparty.TxBlob is sent to the broker (e.g. via API, QR code, etc.)

// Step 2: Broker receives the partially signed blob, adds TxnSignature
SignatureResult fullySigned = LoanSigningHelper.BrokerSign(
    withCounterparty.TxBlob, brokerWallet);
await client.SubmitRequest(fullySigned.TxBlob);
```

> **Important:** Do not use `brokerWallet.Sign()` on a partially signed LoanSet blob — it does not handle `CounterpartySignature` correctly. Always use `LoanSigningHelper.BrokerSign()` for the V3 pattern.

### Multisig Borrower (Counterparty with a SignerList)

`CounterpartySignature` takes the multisig form when the borrower is a multisig account: an empty `SigningPubKey` and a `Signers` array that the node checks against the **counterparty's** SignerList, under the counterparty multisign prefix, which since `fixCleanup3_4_0` is not the one `tx.Signers` uses. Each signer of the borrower's list signs with the standard multisign call - the SDK sees that the broker signs single and works the side out for itself - and the composer places the entries.

```csharp
// The fee covers one base fee per counterparty signer (rippled LoanSet::calculateBaseFee),
// so autofill with the signer count before anyone signs
Dictionary<string, object> autofilled = await client.Autofill(loanTx.ToDictionary(), signersCount: 2);
JsonObject prepared = LoanSigningHelper.PrepareForSigning(
    JsonNode.Parse(JsonSerializer.Serialize(autofilled, XrplJsonOptions.Default)).AsObject(), brokerWallet);
var preparedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(prepared.ToJsonString(), XrplJsonOptions.Default);

// Broker: single main signature. Borrower's signers: portable multisign entries
SignatureResult brokerPart = brokerWallet.Sign(new Dictionary<string, object>(preparedDict));
SignatureResult part1 = signer1.Sign(new Dictionary<string, object>(preparedDict), multisign: true);
SignatureResult part2 = signer2.Sign(new Dictionary<string, object>(preparedDict), multisign: true);

// Ledger-driven: the composer looks the signers up in the Counterparty's SignerList
// and pre-checks the quorum by weight
SignatureResult composed = await client.ComposeSignatures(new[] { brokerPart.TxBlob, part1.TxBlob, part2.TxBlob });

// Offline: name the borrower's signers yourself
SignatureResult offline = LoanSigningHelper.CombineLoanSignatures(
    new[] { brokerPart.TxBlob, part1.TxBlob, part2.TxBlob },
    new[] { signer1.ClassicAddress, signer2.ClassicAddress });

await client.SubmitRequest(composed.TxBlob);
```

A signer that appears in more than one SignerList (the broker's, the sponsor's, the borrower's) is an error for the ledger-driven composer; compose offline with explicit sides in that case.

### Key Points

- Both parties sign the **same transaction** (serialized for signing, without any signature fields), each under the hash prefix of its role
- The signing preimage uses the **broker's** `SigningPubKey` (the submitting account)
- `CounterpartySignature` is an STObject with `isSigningField = false` — it is excluded from the signing preimage
- `Autofill` automatically calculates the correct fee for LoanSet (includes CounterpartySignature overhead)

---

## Ledger Objects

The lending protocol creates the following ledger objects:

| Object | Description | Created By |
|--------|-------------|------------|
| `Vault` | Holds lending assets | `VaultCreate` |
| `LoanBroker` | Lending entity with parameters and cover | `LoanBrokerSet` |
| `Loan` | Active loan between broker and borrower | `LoanSet` |

### LoanBroker Fields

| Field | Type | Description |
|-------|------|-------------|
| `Account` | AccountID | Broker account |
| `Asset` | Issue | Primary lending asset |
| `Asset2` | Issue | Secondary asset (collateral) |
| `CoverAvailable` | Number | Available cover amount |
| `AssetsAvailable` | Number | Available assets for lending |
| `AssetsTotal` | Number | Total assets in vault |
| `DebtTotal` | Number | Total outstanding debt |
| `DebtMaximum` | Number | Maximum allowed debt |
| `CoverRateMinimum` | UInt32 | Minimum cover rate (e.g. 15000 = 150%) |
| `CoverRateLiquidation` | UInt32 | Liquidation threshold |
| `ManagementFeeRate` | UInt16 | Fee rate (0-10000 basis points) |

### Loan Fields

| Field | Type | Description |
|-------|------|-------------|
| `Borrower` | AccountID | Borrower account |
| `LoanBrokerID` | Hash256 | Reference to loan broker |
| `LoanSequence` | UInt32 | Sequence number within the broker |
| `PrincipalOutstanding` | Number | Remaining principal |
| `TotalValueOutstanding` | Number | Total amount owed |
| `PeriodicPayment` | Number | Amount due per interval |
| `InterestRate` | UInt32 | Annual interest rate |
| `PaymentInterval` | UInt32 | Seconds between payments |
| `GracePeriod` | UInt32 | Seconds before late fees apply |
| `PaymentRemaining` | UInt32 | Remaining payments |
| `StartDate` | UInt32 | Loan start (Ripple epoch) |

> `Counterparty`, `PrincipalRequested` and `PaymentTotal` are fields of the **`LoanSet` transaction**, not of the `Loan` object. rippled records the requested principal as `PrincipalOutstanding`, so the ledger entry carries no `PrincipalRequested` — and neither does `LOLoan`.

### Querying Loan State

Use `account_objects` to retrieve loans owned by an account:

```csharp
using Xrpl.Models.Methods;

var request = new AccountObjectsRequest(walletBorrower.ClassicAddress);
var response = await client.AccountObjects(request);

foreach (var obj in response.AccountObjectList)
{
    if (obj.LedgerEntryType == LedgerEntryType.Loan)
    {
        Console.WriteLine($"Loan: {obj}");
    }
}
```

---

## Number Type

Loan fields use the XRPL `Number` type instead of `Amount`. The `Number` type is a 12-byte format:

- **8 bytes** — signed int64 mantissa (big-endian)
- **4 bytes** — signed int32 exponent (big-endian)

The actual value = `mantissa × 10^exponent`.

### Normalization

Non-zero values are normalized so that the mantissa is in the range `[10^18, long.MaxValue]`. Zero is represented as `mantissa=0, exponent=Int32.MinValue`.

### Example

The value `10000000000000` (10^13) is normalized to:
- Mantissa: `1000000000000000000` (10^18)
- Exponent: `-5`
- Binary: `0x0DE0B6B3A7640000 FFFFFFFB` (12 bytes)

### In Transaction Models

Number fields are represented as `string` in C# models (e.g., `PrincipalRequested = "10000000"`). The binary codec handles normalization and serialization automatically.

---

## Common Errors

| Error Code | Cause | Solution |
|-----------|-------|---------|
| `tecINSUFFICIENT_FUNDS` | Broker vault lacks funds for the loan | Deposit more assets into the vault via `VaultDeposit` |
| `tecHAS_OBLIGATIONS` | Cannot delete a loan with outstanding balance | Fully repay the loan via `LoanPay` before deleting |
| `tecNO_ENTRY` | Referenced LoanBrokerID or LoanID not found | Verify the ID is correct and the object exists |
| `tecNO_PERMISSION` | Action not allowed (e.g., overpayment without flag) | Check that the account has the required permissions |
| `tecINSUFFICIENT_PAYMENT` | Payment amount too small | Increase the payment amount |
| `temBAD_SIGNER` | Missing or invalid CounterpartySignature | Ensure borrower co-signs the LoanSet (see co-signing section) |
| `telINSUF_FEE_P` | Fee too low after adding CounterpartySignature | Re-run Autofill or increase the fee before submit |
| `invalid SerialIter geti32` | Number type encoding error | Ensure Number fields are 12 bytes (8 mantissa + 4 exponent) |

---

## Best Practices

1. **Fund the vault before creating loans** — create the vault, deposit assets (`VaultDeposit`), create the broker (`LoanBrokerSet`), deposit cover (`LoanBrokerCoverDeposit`), then create loans.

2. **Autofill handles LoanSet fee** — `Autofill` automatically calculates the correct fee including `CounterpartySignature` overhead. No manual fee adjustment is needed.

3. **Filter by LedgerEntryType when extracting IDs** — `GetCreatedObjectId` should filter by specific type (`LedgerEntryType.Vault`, `LedgerEntryType.LoanBroker`, `LedgerEntryType.Loan`) to avoid picking up `DirectoryNode` or other created entries.

4. **Fully repay before deleting** — `LoanDelete` fails with `tecHAS_OBLIGATIONS` if any balance remains. Use `LoanPay` to clear the debt first.

5. **Use reasonable PrincipalRequested values** — ensure the vault has sufficient assets to cover the requested principal. The value is in the Number type format.

6. **Validate all results** — always check `TransactionResult` for success:
   ```csharp
   if (result.Meta?.TransactionResult != "tesSUCCESS")
       throw new Exception($"Transaction failed: {result.Meta?.TransactionResult}");
   ```

7. **LoanManage flags are mutually exclusive** — only set one of `tfLoanDefault`, `tfLoanImpair`, or `tfLoanUnimpair` per transaction.

8. **LoanPay flags are mutually exclusive** — `tfLoanOverpayment`, `tfLoanFullPayment`, and `tfLoanLatePayment` cannot be combined. Note that `tfLoanOverpayment` may require specific broker/loan configuration to be permitted.

9. **Testing on standalone** — LendingProtocol amendment must be enabled on rippled 3.1.0+:
   ```json
   { "command": "feature", "feature": "LendingProtocol", "vetoed": false }
   ```

---

## Related Resources

- [XLS-66d Specification](https://github.com/XRPLF/XRPL-Standards/tree/master/XLS-0066d-lending-protocol)
- [Vault Specification (XLS-65d)](https://github.com/XRPLF/XRPL-Standards/tree/master/XLS-0065d-vault)
- [XRPL Documentation](https://xrpl.org/docs/)
