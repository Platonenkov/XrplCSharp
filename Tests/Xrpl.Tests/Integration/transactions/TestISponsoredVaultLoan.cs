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
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;
using Xrpl.Wallet;

using static Xrpl.Models.Common.Common;

namespace XrplTests.Xrpl.ClientLib.Integration;

/// <summary>
/// The <c>Sponsor</c> field (XLS-68) on the Vault and Loan transaction types.
/// </summary>
/// <remarks>
/// These are the types rippled forbids inside a Batch (<c>Batch::preflight</c>
/// kDisabledTxTypes), so whether they take a sponsor at all is worth establishing rather
/// than assuming. They do: <c>preflight1Sponsor</c> in Transactor.cpp constrains only
/// <c>spfSponsorReserve</c>, through the allow-list in <c>isReserveSponsorAllowed</c>, and
/// no Vault or Loan type is on it. Fee sponsorship is unconstrained, which is what these
/// tests use.
/// <para>
/// <c>LoanSet</c> is the interesting one: sponsored, it carries three signatures at once -
/// the broker's own, the borrower's <c>CounterpartySignature</c> and the sponsor's
/// <c>SponsorSignature</c> - and the composer has to place all three.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Sponsorship")]
public class TestISponsoredVaultLoan : TestILoanBase
{
    private static bool sponsorAmendmentActive;

    private static IXrplClient client;
    protected override IXrplClient GetClient() => client;

    private const string CurrencyCode = "USD";

    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext testContext)
    {
        client = await CreateStandaloneClient();
        sponsorAmendmentActive = await AmendmentGuard.IsEnabledAsync(client, AmendmentGuard.Sponsor);
    }

    [TestInitialize]
    public void CheckSponsorAmendment()
    {
        if (!sponsorAmendmentActive)
        {
            Assert.Inconclusive("Sponsor amendment (XLS-68) is not enabled on the test node.");
        }
    }

    [ClassCleanup]
    public static void ClassCleanup() => client?.Dispose();

    #region Helpers

    /// <summary>Submits a setup transaction unsponsored, and fails loudly if the ledger refuses it.</summary>
    private static async Task SubmitPlainAsync(ITransactionRequest tx, XrplWallet signer, string context)
    {
        ITransactionRequest autofilled = await client.Autofill(tx);
        TransactionSummary res = await client.SubmitAndWait(autofilled, signer, true);
        if (res is not { Meta: { TransactionResult: "tesSUCCESS" or "terQUEUED" } })
            throw new RippleException($"{context} failed: {res.Meta?.TransactionResult}");
    }

    /// <summary>
    /// Opens a fee sponsorship from <paramref name="sponsor"/> for <paramref name="sponsee"/>.
    /// </summary>
    /// <remarks>
    /// The Sponsorship entry lands in the sponsee's owner directory as well, so anything that
    /// needs an empty directory - <c>asfAllowTrustLineClawback</c> - has to happen first.
    /// </remarks>
    private static async Task OpenSponsorshipAsync(XrplWallet sponsor, XrplWallet sponsee)
    {
        await SubmitPlainAsync(new SponsorshipSet
        {
            Account = sponsor.ClassicAddress,
            Sponsee = sponsee.ClassicAddress,
            FeeAmountDelta = new Currency { ValueAsXrp = 20m },
            RemainingOwnerCountDelta = 10,
        }, sponsor, "SponsorshipSet");
    }

    /// <summary>Stamps the sponsor onto the transaction and submits it with both signatures.</summary>
    private static async Task<TransactionSummary> SubmitSponsoredAsync<T>(T tx, XrplWallet sponsee, XrplWallet sponsor)
        where T : TransactionRequest
    {
        Assert.AreEqual(sponsee.ClassicAddress, tx.Account, "the sponsored transaction must be the sponsee's");
        tx.Sponsor = sponsor.ClassicAddress;
        tx.SponsorFlags = SponsorCoverage.spfSponsorFee;

        TransactionSummary res = await client.SubmitAndWaitSponsored(tx, sponsee, sponsor);
        Assert.IsTrue(res.Meta?.TransactionResult is "tesSUCCESS",
            $"sponsored {tx.TransactionType} must validate with tesSUCCESS, got {res.Meta?.TransactionResult}");
        return res;
    }

    /// <summary>
    /// Creates a broker on an existing vault and deposits cover, returning the LoanBrokerID.
    /// </summary>
    /// <remarks>
    /// <see cref="TestILoanBase.CreateBroker"/> makes the vault itself and does not hand the
    /// VaultID back, and LoanBrokerSet needs it on every call, update included.
    /// </remarks>
    private static async Task<string> CreateBrokerOnVault(XrplWallet broker, string vaultId)
    {
        await SubmitPlainAsync(new VaultDeposit
        {
            Account = broker.ClassicAddress,
            VaultID = vaultId,
            Amount = new Currency { Value = "100000000", CurrencyCode = "XRP" },
        }, broker, "VaultDeposit");

        TransactionSummary brokerResult = await client.SubmitAndWait(
            await client.Autofill(new LoanBrokerSet { Account = broker.ClassicAddress, VaultID = vaultId }), broker, true);
        ValidateResult(brokerResult);
        string brokerId = GetCreatedObjectId(brokerResult, LedgerEntryType.LoanBroker);
        Assert.IsNotNull(brokerId, "the LoanBrokerSet must report the new LoanBroker");

        await SubmitPlainAsync(new LoanBrokerCoverDeposit
        {
            Account = broker.ClassicAddress,
            LoanBrokerID = brokerId,
            Amount = new Currency { Value = "50000000", CurrencyCode = "XRP" },
        }, broker, "LoanBrokerCoverDeposit");

        return brokerId;
    }

    private static Currency Iou(string issuer, string value) =>
        new Currency { CurrencyCode = CurrencyCode, Issuer = issuer, Value = value };

    private static string ToHex(string text) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(text));

    #endregion

    /// <summary>
    /// An XRP vault owned by the sponsee: created, configured, funded, drained and removed,
    /// every step with the sponsor covering the fee.
    /// </summary>
    [TestMethod]
    public async Task Sponsored_Vault_Create_Set_Deposit_Withdraw_Delete()
    {
        XrplWallet sponsor = XrplWallet.Generate();
        XrplWallet owner = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, sponsor, owner);
        await IntegrationTestConfig.EnsureBalanceAsync(client, owner, 150m);
        await OpenSponsorshipAsync(sponsor, owner);

        TransactionSummary created = await SubmitSponsoredAsync(new VaultCreate
        {
            Account = owner.ClassicAddress,
            Asset = new IssuedCurrency { Currency = "XRP" },
        }, owner, sponsor);
        string vaultId = GetCreatedObjectId(created, LedgerEntryType.Vault);
        Assert.IsNotNull(vaultId, "the VaultCreate must report the new Vault");

        await SubmitSponsoredAsync(new VaultSet
        {
            Account = owner.ClassicAddress,
            VaultID = vaultId,
            Data = ToHex("sponsored vault"),
        }, owner, sponsor);

        await SubmitSponsoredAsync(new VaultDeposit
        {
            Account = owner.ClassicAddress,
            VaultID = vaultId,
            Amount = new Currency { ValueAsXrp = 10m },
        }, owner, sponsor);

        await SubmitSponsoredAsync(new VaultWithdraw
        {
            Account = owner.ClassicAddress,
            VaultID = vaultId,
            Amount = new Currency { ValueAsXrp = 10m },
        }, owner, sponsor);

        // Without MemoData: it is an optional field of VaultDelete on rippled's develop branch
        // and the release build the CI stand runs answers temDISABLED for it, so carrying it
        // here would make this test say more about the stand's version than about sponsorship
        await SubmitSponsoredAsync(new VaultDelete
        {
            Account = owner.ClassicAddress,
            VaultID = vaultId,
        }, owner, sponsor);
    }

    /// <summary>
    /// Clawing back from a vault is the issuer's move, so the sponsee here issues the asset,
    /// owns the vault, and a separate holder is the one clawed back from.
    /// </summary>
    [TestMethod]
    public async Task Sponsored_VaultClawback()
    {
        XrplWallet sponsor = XrplWallet.Generate();
        XrplWallet issuer = XrplWallet.Generate();
        XrplWallet holder = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, sponsor, issuer, holder);

        // Both flags before the sponsorship: asfAllowTrustLineClawback needs an empty owner
        // directory, and the Sponsorship entry counts against it
        await SubmitPlainAsync(new AccountSet { Account = issuer.ClassicAddress, SetFlag = AccountSetAsfFlags.asfAllowTrustLineClawback }, issuer, "asfAllowTrustLineClawback");
        await SubmitPlainAsync(new AccountSet { Account = issuer.ClassicAddress, SetFlag = AccountSetAsfFlags.asfDefaultRipple }, issuer, "asfDefaultRipple");
        await OpenSponsorshipAsync(sponsor, issuer);

        TransactionSummary created = await SubmitSponsoredAsync(new VaultCreate
        {
            Account = issuer.ClassicAddress,
            Asset = new IssuedCurrency { Currency = CurrencyCode, Issuer = issuer.ClassicAddress },
        }, issuer, sponsor);
        string vaultId = GetCreatedObjectId(created, LedgerEntryType.Vault);
        Assert.IsNotNull(vaultId, "the VaultCreate must report the new Vault");

        await SubmitPlainAsync(new TrustSet
        {
            Account = holder.ClassicAddress,
            LimitAmount = Iou(issuer.ClassicAddress, "1000"),
        }, holder, "TrustSet");
        await SubmitPlainAsync(new Payment
        {
            Account = issuer.ClassicAddress,
            Destination = holder.ClassicAddress,
            Amount = Iou(issuer.ClassicAddress, "100"),
        }, issuer, "issue tokens");
        await SubmitPlainAsync(new VaultDeposit
        {
            Account = holder.ClassicAddress,
            VaultID = vaultId,
            Amount = Iou(issuer.ClassicAddress, "100"),
        }, holder, "VaultDeposit by holder");

        await SubmitSponsoredAsync(new VaultClawback
        {
            Account = issuer.ClassicAddress,
            VaultID = vaultId,
            Holder = holder.ClassicAddress,
            Amount = Iou(issuer.ClassicAddress, "40"),
        }, issuer, sponsor);
    }

    /// <summary>
    /// The broker lifecycle with the sponsor covering every fee: reconfigure, add cover,
    /// take cover back, and delete.
    /// </summary>
    [TestMethod]
    public async Task Sponsored_LoanBroker_Set_CoverDeposit_CoverWithdraw_Delete()
    {
        XrplWallet sponsor = XrplWallet.Generate();
        XrplWallet broker = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, sponsor, broker);
        await IntegrationTestConfig.EnsureBalanceAsync(client, broker, 200m);

        string vaultId = await CreateVaultForBroker(client, broker);
        string brokerId = await CreateBrokerOnVault(broker, vaultId);
        await OpenSponsorshipAsync(sponsor, broker);

        // Two rules from LoanBrokerSet::preflight, both easy to trip: VaultID is required even
        // when the transaction updates a broker that already exists, and a transaction that
        // names a LoanBrokerID may not carry ManagementFeeRate, CoverRateMinimum or
        // CoverRateLiquidation - those are set once, at creation, and an update carrying one
        // is temINVALID
        await SubmitSponsoredAsync(new LoanBrokerSet
        {
            Account = broker.ClassicAddress,
            VaultID = vaultId,
            LoanBrokerID = brokerId,
            Data = ToHex("sponsored broker"),
        }, broker, sponsor);

        await SubmitSponsoredAsync(new LoanBrokerCoverDeposit
        {
            Account = broker.ClassicAddress,
            LoanBrokerID = brokerId,
            Amount = new Currency { Value = "10000000", CurrencyCode = "XRP" },
        }, broker, sponsor);

        await SubmitSponsoredAsync(new LoanBrokerCoverWithdraw
        {
            Account = broker.ClassicAddress,
            LoanBrokerID = brokerId,
            Amount = new Currency { Value = "5000000", CurrencyCode = "XRP" },
        }, broker, sponsor);

        await SubmitSponsoredAsync(new LoanBrokerDelete
        {
            Account = broker.ClassicAddress,
            LoanBrokerID = brokerId,
        }, broker, sponsor);
    }

    /// <summary>
    /// Clawing broker cover back is the asset issuer's move and rippled refuses it on a native
    /// asset (<c>LoanBrokerCoverClawback::preclaim</c>: "Cannot clawback native asset"), so the
    /// broker here sits on an IOU vault and the issuer is a separate, sponsored account.
    /// </summary>
    [TestMethod]
    public async Task Sponsored_LoanBrokerCoverClawback()
    {
        XrplWallet sponsor = XrplWallet.Generate();
        XrplWallet issuer = XrplWallet.Generate();
        XrplWallet broker = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, sponsor, issuer, broker);

        // Both issuer flags before the sponsorship: the Sponsorship entry counts against the
        // empty owner directory asfAllowTrustLineClawback requires
        await SubmitPlainAsync(new AccountSet { Account = issuer.ClassicAddress, SetFlag = AccountSetAsfFlags.asfAllowTrustLineClawback }, issuer, "asfAllowTrustLineClawback");
        await SubmitPlainAsync(new AccountSet { Account = issuer.ClassicAddress, SetFlag = AccountSetAsfFlags.asfDefaultRipple }, issuer, "asfDefaultRipple");
        await OpenSponsorshipAsync(sponsor, issuer);

        await SubmitPlainAsync(new TrustSet
        {
            Account = broker.ClassicAddress,
            LimitAmount = Iou(issuer.ClassicAddress, "1000000"),
        }, broker, "TrustSet");
        await SubmitPlainAsync(new Payment
        {
            Account = issuer.ClassicAddress,
            Destination = broker.ClassicAddress,
            Amount = Iou(issuer.ClassicAddress, "10000"),
        }, issuer, "issue tokens to the broker");

        TransactionSummary vaultResult = await client.SubmitAndWait(
            await client.Autofill(await BuildBrokerVaultAsync(
                client,
                broker.ClassicAddress,
                new IssuedCurrency { Currency = CurrencyCode, Issuer = issuer.ClassicAddress })), broker, true);
        ValidateResult(vaultResult);
        string vaultId = GetCreatedObjectId(vaultResult, LedgerEntryType.Vault);
        Assert.IsNotNull(vaultId, "the VaultCreate must report the new Vault");

        await SubmitPlainAsync(new VaultDeposit
        {
            Account = broker.ClassicAddress,
            VaultID = vaultId,
            Amount = Iou(issuer.ClassicAddress, "5000"),
        }, broker, "VaultDeposit");

        TransactionSummary brokerResult = await client.SubmitAndWait(
            await client.Autofill(new LoanBrokerSet { Account = broker.ClassicAddress, VaultID = vaultId }), broker, true);
        ValidateResult(brokerResult);
        string brokerId = GetCreatedObjectId(brokerResult, LedgerEntryType.LoanBroker);
        Assert.IsNotNull(brokerId, "the LoanBrokerSet must report the new LoanBroker");

        await SubmitPlainAsync(new LoanBrokerCoverDeposit
        {
            Account = broker.ClassicAddress,
            LoanBrokerID = brokerId,
            Amount = Iou(issuer.ClassicAddress, "1000"),
        }, broker, "LoanBrokerCoverDeposit");

        await SubmitSponsoredAsync(new LoanBrokerCoverClawback
        {
            Account = issuer.ClassicAddress,
            LoanBrokerID = brokerId,
            Amount = Iou(issuer.ClassicAddress, "400"),
        }, issuer, sponsor);
    }

    /// <summary>
    /// A sponsored LoanSet carries three signatures: the broker's own, the borrower's
    /// CounterpartySignature and the sponsor's SponsorSignature, all over one preimage.
    /// </summary>
    [TestMethod]
    public async Task Sponsored_LoanSet_ThreeWaySignature()
    {
        XrplWallet sponsor = XrplWallet.Generate();
        XrplWallet broker = XrplWallet.Generate();
        XrplWallet borrower = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, sponsor, broker, borrower);

        string brokerId = await CreateBroker(client, broker);
        await OpenSponsorshipAsync(sponsor, broker);

        LoanSet loanTx = new LoanSet
        {
            Account = broker.ClassicAddress,
            LoanBrokerID = brokerId,
            Counterparty = borrower.ClassicAddress,
            PrincipalRequested = "10000000",
            Sponsor = sponsor.ClassicAddress,
            SponsorFlags = SponsorCoverage.spfSponsorFee,
        };
        Dictionary<string, object> autofilled = await client.Autofill(loanTx.ToDictionary());
        JsonObject prepared = LoanSigningHelper.PrepareForSigning(
            JsonNode.Parse(JsonSerializer.Serialize(autofilled, XrplJsonOptions.Default)).AsObject(), broker);
        Dictionary<string, object> preparedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
            prepared.ToJsonString(), XrplJsonOptions.Default);

        // Each party signs its own copy of the same preimage; Sign routes by role
        string brokerPart = broker.Sign(new Dictionary<string, object>(preparedDict)).TxBlob;
        string borrowerPart = borrower.Sign(new Dictionary<string, object>(preparedDict)).TxBlob;
        string sponsorPart = sponsor.Sign(new Dictionary<string, object>(preparedDict)).TxBlob;

        SignatureResult composed = SignatureComposer.ComposeSignatures(new[] { brokerPart, borrowerPart, sponsorPart });

        JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
        Assert.IsNotNull(decoded["TxnSignature"], "the broker's own signature must be present");
        Assert.IsNotNull(decoded["CounterpartySignature"], "the borrower's co-signature must be present");
        Assert.IsNotNull(decoded["SponsorSignature"], "the sponsor's co-signature must be present");

        TransactionSummary result = await SubmitSignedLoanSet(client, composed.TxBlob);
        ValidateResult(result);
    }

    /// <summary>
    /// What a borrower does with a loan, sponsored: pay it, have its state managed, and
    /// have it removed once repaid.
    /// </summary>
    [TestMethod]
    public async Task Sponsored_Loan_Manage_Pay_Delete()
    {
        XrplWallet sponsor = XrplWallet.Generate();
        XrplWallet broker = XrplWallet.Generate();
        XrplWallet borrower = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, sponsor, broker, borrower);

        string brokerId = await CreateBroker(client, broker);

        LoanSet loanTx = new LoanSet
        {
            Account = broker.ClassicAddress,
            LoanBrokerID = brokerId,
            Counterparty = borrower.ClassicAddress,
            PrincipalRequested = "10000000",
        };
        TransactionSummary loanResult = await SubmitLoanSetWithCounterpartySig(client, loanTx, broker, borrower);
        ValidateResult(loanResult);
        string loanId = GetCreatedObjectId(loanResult, LedgerEntryType.Loan);
        Assert.IsNotNull(loanId, "the LoanSet must report the new Loan");

        // The borrower pays, the broker manages and deletes: two sponsees, one sponsor
        await OpenSponsorshipAsync(sponsor, borrower);
        await OpenSponsorshipAsync(sponsor, broker);

        await SubmitSponsoredAsync(new LoanManage
        {
            Account = broker.ClassicAddress,
            LoanID = loanId,
            Flags = LoanManageFlags.tfLoanImpair,
        }, broker, sponsor);

        // The full principal: anything less is tecINSUFFICIENT_PAYMENT
        await SubmitSponsoredAsync(new LoanPay
        {
            Account = borrower.ClassicAddress,
            LoanID = loanId,
            Amount = new Currency { Value = "10000000", CurrencyCode = "XRP" },
        }, borrower, sponsor);

        await SubmitSponsoredAsync(new LoanDelete
        {
            Account = broker.ClassicAddress,
            LoanID = loanId,
        }, broker, sponsor);
    }
}
