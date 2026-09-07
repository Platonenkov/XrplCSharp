using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models;
using Xrpl.Models.Common;
using Xrpl.Models.Ledger;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;
using Xrpl.Wallet;

using static Xrpl.Models.Common.Common;

namespace XrplTests.Xrpl.ClientLib.Integration;

public abstract class TestILoanBase
{
    public TestContext TestContext { get; set; }
    protected abstract IXrplClient GetClient();
    protected static TestNodeType nodeType = IntegrationTestConfig.CurrentNodeType;

    protected static void ValidateResult(Submit res)
    {
        if (res is not { EngineResult: "tesSUCCESS" or "terQUEUED" })
            throw new RippleException($"Transaction failed: {res.EngineResult}");
    }

    protected static void ValidateResult(TransactionSummary res)
    {
        if (res is not { Meta: { TransactionResult: "tesSUCCESS" or "terQUEUED" } })
            throw new RippleException($"Final tx result is not success: {res.Meta?.TransactionResult}");
    }

    protected static string GetCreatedObjectId(TransactionSummary result, LedgerEntryType entryType = LedgerEntryType.Unknown)
    {
        if (result.Meta?.AffectedNodes == null) return null;

        foreach (AffectedNode node in result.Meta.AffectedNodes)
        {
            if (node.CreatedNode != null)
            {
                if (entryType == LedgerEntryType.Unknown || node.CreatedNode.LedgerEntryType == entryType)
                    return node.CreatedNode.LedgerIndex;
            }
        }
        return null;
    }

    protected static string GetCreatedObjectId(TransactionResponse result, LedgerEntryType entryType = LedgerEntryType.Unknown)
    {
        if (result.Meta?.AffectedNodes == null) return null;

        foreach (AffectedNode node in result.Meta.AffectedNodes)
        {
            if (node.CreatedNode != null)
            {
                if (entryType == LedgerEntryType.Unknown || node.CreatedNode.LedgerEntryType == entryType)
                    return node.CreatedNode.LedgerIndex;
            }
        }
        return null;
    }

    #region Closed-ended vaults (LendingProtocolV1_1)

    /// <summary>
    /// How much of the subscription phase is left once the vault exists. It has to cover the
    /// VaultCreate itself and the deposit that follows: rippled accepts a vault deposit in the
    /// subscription phase only - <c>tecEXPIRED</c> afterwards - and each of the two waits for a
    /// ledger of its own.
    /// </summary>
    private const int SubscriptionWindowSeconds = 30;

    /// <summary>
    /// Length of the investment phase, where a loan can be originated.
    /// </summary>
    /// <remarks>
    /// LoanSet refuses a loan whose final payment falls within <c>kLoanRedemptionBuffer</c> (60 s)
    /// of the vault's redemption date, and rippled's default schedule is one payment 60 s out, so
    /// the loan alone asks for 120 s of room. The window is wider so a test can spend time between
    /// the broker and the loan, and it stays far below the 30-year ceiling on the phase.
    /// </remarks>
    private const int InvestmentWindowSeconds = 900;

    private static bool? closedEndedRequired;

    /// <summary>
    /// Whether a LoanBroker on this node requires a closed-ended vault. Since LendingProtocolV1_1
    /// <c>LoanBrokerSet::preclaim</c> refuses an open-ended one with <c>tecNO_PERMISSION</c>,
    /// because the lending protocol is written around the subscription / investment / redemption
    /// phases. A node without the amendment does not know the fields at all and answers
    /// <c>invalidTransaction</c> to a transaction carrying them, so the open-ended path has to stay.
    /// </summary>
    protected static async Task<bool> ClosedEndedVaultRequiredAsync(IXrplClient client)
    {
        closedEndedRequired ??= await AmendmentGuard.IsEnabledAsync(client, AmendmentGuard.LendingProtocolV11);
        return closedEndedRequired.Value;
    }

    /// <summary>
    /// Builds the VaultCreate a LoanBroker can be attached to on this node, with the phase dates
    /// measured from the ledger clock rather than the machine's.
    /// </summary>
    protected static async Task<VaultCreate> BuildBrokerVaultAsync(
        IXrplClient client,
        string owner,
        IssuedCurrency asset)
    {
        VaultCreate tx = new VaultCreate
        {
            Account = owner,
            Asset = asset,
        };

        if (!await ClosedEndedVaultRequiredAsync(client))
            return tx;

        DateTime subscriptionDate = (await IntegrationTestConfig.ValidatedCloseTimeAsync(client))
            .AddSeconds(SubscriptionWindowSeconds);

        tx.VaultKind = (uint)VaultKind.ClosedEnded;
        tx.SubscriptionDate = subscriptionDate;
        tx.RedemptionDate = subscriptionDate.AddSeconds(InvestmentWindowSeconds);
        return tx;
    }

    /// <summary>
    /// Waits until the vault has left the subscription phase, which is where LoanSet needs it
    /// (<c>tecTOO_SOON</c> before, <c>tecEXPIRED</c> once redemption starts). A vault carrying no
    /// SubscriptionDate is open-ended and has no phases at all: there is nothing to wait for.
    /// </summary>
    protected static async Task EnterInvestmentPhaseAsync(IXrplClient client, string vaultId)
    {
        LedgerEntryResponse entry = await client.LedgerEntry(new LedgerEntryRequest { Index = vaultId }).Typed();
        if (entry?.Node is not LOVault vault || vault.SubscriptionDate is not DateTime subscriptionDate)
            return;

        await IntegrationTestConfig.WaitForCloseTimeAsync(client, subscriptionDate, nodeType);
    }

    #endregion

    /// <summary>
    /// Creates a Vault for the given wallet and returns its VaultID from metadata.
    /// LoanBrokerSet requires an existing Vault owned by the submitting account.
    /// </summary>
    protected static async Task<string> CreateVaultForBroker(IXrplClient client, XrplWallet wallet)
    {
        VaultCreate vaultTx = await BuildBrokerVaultAsync(
            client,
            wallet.ClassicAddress,
            new IssuedCurrency { Currency = "XRP" });
        vaultTx = await client.Autofill(vaultTx);
        TransactionSummary vaultResult = await client.SubmitAndWait(vaultTx, wallet, true);
        ValidateResult(vaultResult);

        string vaultId = GetCreatedObjectId(vaultResult, LedgerEntryType.Vault);
        Assert.IsNotNull(vaultId, "VaultID should be present in metadata after VaultCreate");
        return vaultId;
    }

    /// <summary>
    /// Creates a LoanBroker for the given wallet (creating a Vault first, depositing funds,
    /// and creating broker cover) and returns the LoanBrokerID.
    /// </summary>
    protected static async Task<string> CreateBroker(IXrplClient client, XrplWallet wallet)
    {
        // The vault deposit (100 XRP) and the broker cover (50 XRP) below exceed a single
        // faucet payout, so the account is topped up before anything is spent
        await IntegrationTestConfig.EnsureBalanceAsync(client, wallet, 200m);

        string vaultId = await CreateVaultForBroker(client, wallet);

        // Deposit XRP into the vault so the broker has funds to lend
        VaultDeposit depositTx = new VaultDeposit
        {
            Account = wallet.ClassicAddress,
            VaultID = vaultId,
            Amount = new Currency { Value = "100000000", CurrencyCode = "XRP" }, // 100 XRP
        };
        depositTx = await client.Autofill(depositTx);
        TransactionSummary depositResult = await client.SubmitAndWait(depositTx, wallet, true);
        ValidateResult(depositResult);

        LoanBrokerSet brokerTx = new LoanBrokerSet
        {
            Account = wallet.ClassicAddress,
            VaultID = vaultId,
        };
        brokerTx = await client.Autofill(brokerTx);
        TransactionSummary brokerResult = await client.SubmitAndWait(brokerTx, wallet, true);
        ValidateResult(brokerResult);

        string brokerId = GetCreatedObjectId(brokerResult, LedgerEntryType.LoanBroker);
        Assert.IsNotNull(brokerId, "LoanBrokerID should be present in metadata");

        // Deposit cover so the broker can issue loans
        LoanBrokerCoverDeposit coverTx = new LoanBrokerCoverDeposit
        {
            Account = wallet.ClassicAddress,
            LoanBrokerID = brokerId,
            Amount = new Currency { Value = "50000000", CurrencyCode = "XRP" }, // 50 XRP
        };
        coverTx = await client.Autofill(coverTx);
        TransactionSummary coverResult = await client.SubmitAndWait(coverTx, wallet, true);
        ValidateResult(coverResult);

        // The caller's next move is usually a LoanSet, which rippled only originates in the
        // investment phase; the deposit above had to happen before it, in the subscription phase.
        await EnterInvestmentPhaseAsync(client, vaultId);

        return brokerId;
    }

    /// <summary>
    /// Autofills and prepares a LoanSet transaction for co-signing.
    /// Returns the prepared JsonObject with SigningPubKey set.
    /// Fee for CounterpartySignature overhead is handled by Autofill.
    /// </summary>
    protected static async Task<JsonObject> PrepareLoanSet(
        IXrplClient client,
        LoanSet loanTx,
        XrplWallet brokerWallet)
    {
        // A LoanSet always carries a CounterpartySignature, which is a role signature
        await AmendmentGuard.RequireRoleSignaturesAsync(client);

        loanTx = await client.Autofill(loanTx);
        return LoanSigningHelper.PrepareForSigning(loanTx, brokerWallet);
    }

    /// <summary>
    /// V1 — Automatic: both keys available locally.
    /// Signs and submits a LoanSet transaction using LoanSigningHelper.
    /// </summary>
    protected static async Task<TransactionSummary> SubmitLoanSetV1(
        IXrplClient client,
        LoanSet loanTx,
        XrplWallet brokerWallet,
        XrplWallet borrowerWallet)
    {
        JsonObject prepared = await PrepareLoanSet(client, loanTx, brokerWallet);
        SignatureResult result = LoanSigningHelper.SignLoanSet(prepared, brokerWallet, borrowerWallet);
        return await SubmitSignedLoanSet(client, result.TxBlob);
    }

    /// <summary>
    /// V2 — Parallel: broker and borrower sign independently, then combine.
    /// Simulates keys on separate devices.
    /// </summary>
    protected static async Task<TransactionSummary> SubmitLoanSetV2(
        IXrplClient client,
        LoanSet loanTx,
        XrplWallet brokerWallet,
        XrplWallet borrowerWallet)
    {
        JsonObject prepared = await PrepareLoanSet(client, loanTx, brokerWallet);
        string preparedJson = prepared.ToJsonString();

        // Device A (broker): signs the transaction normally (adds TxnSignature)
        Dictionary<string, object> brokerDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
            preparedJson, XrplJsonOptions.Default);
        SignatureResult brokerSig = brokerWallet.Sign(brokerDict);

        // Device B (borrower): signs as counterparty (adds CounterpartySignature) — independent copy
        Dictionary<string, object> borrowerDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
            preparedJson, XrplJsonOptions.Default);
        SignatureResult counterpartySig = borrowerWallet.SignAsLoanCounterparty(borrowerDict);

        // Combiner: merge both signatures
        SignatureResult combined = LoanSigningHelper.CombineLoanSignatures(brokerSig.TxBlob, counterpartySig.TxBlob);
        return await SubmitSignedLoanSet(client, combined.TxBlob);
    }

    /// <summary>
    /// V3 — Sequential: borrower signs first, passes to broker who signs and submits.
    /// Simulates real-world flow where keys never leave their respective devices.
    /// </summary>
    protected static async Task<TransactionSummary> SubmitLoanSetV3(
        IXrplClient client,
        LoanSet loanTx,
        XrplWallet brokerWallet,
        XrplWallet borrowerWallet)
    {
        JsonObject prepared = await PrepareLoanSet(client, loanTx, brokerWallet);

        // Step 1: Borrower receives prepared tx, signs as counterparty (adds CounterpartySignature)
        Dictionary<string, object> txDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
            prepared.ToJsonString(), XrplJsonOptions.Default);
        SignatureResult withCounterparty = borrowerWallet.SignAsLoanCounterparty(txDict);

        // Step 2: Broker receives the partially signed blob, adds TxnSignature via BrokerSign
        SignatureResult fullySigned = LoanSigningHelper.BrokerSign(withCounterparty.TxBlob, brokerWallet);

        return await SubmitSignedLoanSet(client, fullySigned.TxBlob);
    }

    /// <summary>
    /// Default method (backward compatible) — uses V1 (automatic) signing.
    /// </summary>
    protected static Task<TransactionSummary> SubmitLoanSetWithCounterpartySig(
        IXrplClient client,
        LoanSet loanTx,
        XrplWallet brokerWallet,
        XrplWallet borrowerWallet)
        => SubmitLoanSetV1(client, loanTx, brokerWallet, borrowerWallet);

    /// <summary>
    /// Submits a signed LoanSet blob and waits for the result.
    /// </summary>
    protected static async Task<TransactionSummary> SubmitSignedLoanSet(IXrplClient client, string txBlob)
    {
        Submit submitResult = await client.SubmitRequest(txBlob, failHard: false);
        if (submitResult is not { EngineResult: "tesSUCCESS" or "terQUEUED" })
            throw new RippleException($"LoanSet submit failed: {submitResult.EngineResult} - {submitResult.EngineResultMessage}");

        // Poll tx lookup until metadata is available (ledger_accept runs every 4s in CI)
        string txHash = global::Xrpl.Utils.Hashes.HashLedger.HashSignedTx(txBlob);
        TxRequest txReq = new TxRequest(txHash);
        TransactionResponse txResponse = null;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(1000);
            try
            {
                txResponse = await client.TxV1(txReq).Typed();
                if (txResponse?.Meta != null) break;
            }
            catch
            {
                // tx may not be found yet — retry
            }
        }
        if (txResponse?.Meta == null)
            throw new RippleException($"LoanSet tx not validated in time: {txHash}");

        return new TransactionSummary { Meta = txResponse.Meta };
    }

    /// <summary>
    /// Creates an MPT issuance, authorizes it for the holder, funds the holder with MPT tokens,
    /// creates an MPT-backed Vault, deposits MPT into it, creates a LoanBroker backed by that vault,
    /// deposits cover, and returns the broker ID along with the MPT issuance ID.
    /// </summary>
    protected static async Task<(string BrokerId, string MptIssuanceId)> CreateMptBroker(
        IXrplClient client,
        XrplWallet issuerWallet,
        XrplWallet holderWallet)
    {
        // 1. Enable clawback on issuer (required before any issuances for clawback support)
        AccountSet clawbackSetTx = new AccountSet
        {
            Account = issuerWallet.ClassicAddress,
            SetFlag = AccountSetAsfFlags.asfAllowTrustLineClawback,
        };
        clawbackSetTx = await client.Autofill(clawbackSetTx);
        TransactionSummary clawbackSetResult = await client.SubmitAndWait(clawbackSetTx, issuerWallet, true);
        ValidateResult(clawbackSetResult);

        // 2. Create MPT issuance with transfer + clawback enabled
        MPTokenIssuanceCreate mptCreateTx = new MPTokenIssuanceCreate
        {
            Account = issuerWallet.ClassicAddress,
            Flags = MPTokenIssuanceCreateFlags.tfMPTCanTransfer | MPTokenIssuanceCreateFlags.tfMPTCanClawback,
        };
        mptCreateTx = await client.Autofill(mptCreateTx);
        TransactionSummary mptCreateResult = await client.SubmitAndWait(mptCreateTx, issuerWallet, true);
        ValidateResult(mptCreateResult);

        string issuanceId = mptCreateResult.Meta?.MptIssuanceId;
        Assert.IsNotNull(issuanceId, "MPTokenIssuanceID should be present in metadata");

        // 3. Holder authorizes MPT
        MPTokenAuthorize authTx = new MPTokenAuthorize
        {
            Account = holderWallet.ClassicAddress,
            MPTokenIssuanceID = issuanceId,
        };
        authTx = await client.Autofill(authTx);
        TransactionSummary authResult = await client.SubmitAndWait(authTx, holderWallet, true);
        ValidateResult(authResult);

        // 4. Issuer sends MPT to holder (so holder can deposit to vault later)
        Payment paymentTx = new Payment
        {
            Account = issuerWallet.ClassicAddress,
            Destination = holderWallet.ClassicAddress,
            Amount = new Currency
            {
                Value = "500",
                MPTokenIssuanceID = issuanceId,
            },
        };
        paymentTx = await client.Autofill(paymentTx);
        TransactionSummary payResult = await client.SubmitAndWait(paymentTx, issuerWallet, true);
        ValidateResult(payResult);

        // 5. Create MPT-backed vault
        VaultCreate vaultCreateTx = await BuildBrokerVaultAsync(
            client,
            issuerWallet.ClassicAddress,
            new IssuedCurrency { MptIssuanceId = issuanceId });
        vaultCreateTx = await client.Autofill(vaultCreateTx);
        TransactionSummary vaultCreateResult = await client.SubmitAndWait(vaultCreateTx, issuerWallet, true);
        ValidateResult(vaultCreateResult);

        string vaultId = GetCreatedObjectId(vaultCreateResult, LedgerEntryType.Vault);
        Assert.IsNotNull(vaultId, "VaultID should be present in metadata after VaultCreate");

        // 6. Deposit MPT into the vault so it has liquidity
        VaultDeposit depositTx = new VaultDeposit
        {
            Account = holderWallet.ClassicAddress,
            VaultID = vaultId,
            Amount = new Currency
            {
                Value = "200",
                MPTokenIssuanceID = issuanceId,
            },
        };
        depositTx = await client.Autofill(depositTx);
        TransactionSummary depositResult = await client.SubmitAndWait(depositTx, holderWallet, true);
        ValidateResult(depositResult);

        // 7. Create LoanBroker backed by MPT vault
        LoanBrokerSet brokerTx = new LoanBrokerSet
        {
            Account = issuerWallet.ClassicAddress,
            VaultID = vaultId,
        };
        brokerTx = await client.Autofill(brokerTx);
        TransactionSummary brokerResult = await client.SubmitAndWait(brokerTx, issuerWallet, true);
        ValidateResult(brokerResult);

        string brokerId = GetCreatedObjectId(brokerResult, LedgerEntryType.LoanBroker);
        Assert.IsNotNull(brokerId, "LoanBrokerID should be present in metadata");

        // 8. Deposit cover (MPT) into the broker
        LoanBrokerCoverDeposit coverTx = new LoanBrokerCoverDeposit
        {
            Account = issuerWallet.ClassicAddress,
            LoanBrokerID = brokerId,
            Amount = new Currency
            {
                Value = "100",
                MPTokenIssuanceID = issuanceId,
            },
        };
        coverTx = await client.Autofill(coverTx);
        TransactionSummary coverResult = await client.SubmitAndWait(coverTx, issuerWallet, true);
        ValidateResult(coverResult);

        await EnterInvestmentPhaseAsync(client, vaultId);

        return (brokerId, issuanceId);
    }

    protected static async Task<IXrplClient> CreateStandaloneClient()
    {
        return await IntegrationTestConfig.CreateClientAsync();
    }
}
