using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using Xrpl.Models.Utils;
using Xrpl.Sugar;
using Xrpl.Wallet;

namespace XrplTests.Xrpl.ClientLib.Integration;

/// <summary>
/// Batch × sponsorship interplay (#43/#47) on the live node:
/// - a reserve-sponsored inner tx whose sponsor authorizes as a BATCH SIGNER
///   (the SponsorSignature marker mechanism per rippled Batch::preflight);
/// - a fee-sponsored OUTER batch co-signed by the sponsor the normal way.
/// </summary>
[TestClass]
[TestCategory("Sponsorship")]
public class TestIBatchSponsorship
{
    private static bool sponsorAmendmentActive;

    public TestContext TestContext { get; set; }
    private static IXrplClient client;
    private static TestNodeType nodeType = IntegrationTestConfig.CurrentNodeType;

    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext testContext)
    {
        client = await IntegrationTestConfig.CreateClientAsync(nodeType);
        sponsorAmendmentActive = await AmendmentGuard.IsEnabledAsync(client, AmendmentGuard.Sponsor);
    }

    [TestInitialize]
    public async Task CheckSponsorAmendment()
    {
        if (!sponsorAmendmentActive)
        {
            Assert.Inconclusive("Sponsor amendment (XLS-68) is not enabled on the test node.");
        }

        await AmendmentGuard.RequireRoleSignaturesAsync(client);
    }

    [ClassCleanup]
    public static void ClassCleanup() => client?.Dispose();

    private static void ValidateResult(TransactionSummary res)
    {
        if (res is not { Meta: { TransactionResult: "tesSUCCESS" or "terQUEUED" } })
            throw new RippleException($"Transaction failed: {res.Meta?.TransactionResult}");
    }

    private static Dictionary<string, object> Reparse(string blob) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(
            XrplBinaryCodec.Decode(blob).ToJsonString(), XrplJsonOptions.Default);

    private static async Task SponsorshipSetAsync(XrplWallet sponsor, XrplWallet sponsee)
    {
        var setup = new SponsorshipSet
        {
            Account = sponsor.ClassicAddress,
            Sponsee = sponsee.ClassicAddress,
            FeeAmountDelta = new Currency { ValueAsXrp = 10m },
            RemainingOwnerCountDelta = 3,
        };
        setup = await client.Autofill(setup);
        ValidateResult(await client.SubmitAndWait(setup, sponsor, autofill: false));
    }

    private static async Task SubmitBlobTesAsync(string blob)
    {
        Submit response = await client.SubmitRequest(blob, true);
        if (response is not { EngineResult: "tesSUCCESS" or "terQUEUED" })
            throw new RippleException($"Submission failed: {response.EngineResult}");
    }

    /// <summary>
    /// Inner TrustSet is reserve-sponsored; the sponsor authorizes by signing
    /// the whole batch as a batch signer (routed by the SponsorSignature
    /// marker). Verifies the resulting RippleState carries the sponsor.
    /// </summary>
    [TestMethod]
    public async Task Batch_SponsoredReserveInner_SponsorAsBatchSigner()
    {
        XrplWallet root = XrplWallet.Generate();
        XrplWallet holder = XrplWallet.Generate();
        XrplWallet sponsor = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, root, holder, sponsor);
        await SponsorshipSetAsync(sponsor, holder);

        var inner1 = new Payment
        {
            Account = root.ClassicAddress,
            Destination = holder.ClassicAddress,
            Amount = new Currency { ValueAsXrp = 1m },
            Fee = new Currency { Value = "0" },
        }.ToBatchTx();

        var inner2 = new TrustSet
        {
            Account = holder.ClassicAddress,
            LimitAmount = new Currency
            {
                CurrencyCode = "USD",
                Issuer = root.ClassicAddress,
                Value = "1000",
            },
            Fee = new Currency { Value = "0" },
            Sponsor = sponsor.ClassicAddress,
            SponsorFlags = SponsorCoverage.spfSponsorReserve,
        }.ToBatchTx();

        var batch = new Batch
        {
            Account = root.ClassicAddress,
            Flags = BatchFlags.tfAllOrNothing,
            RawTransactions = new List<RawTransactionWrapper> { inner1, inner2 },
            Fee = new Currency { Value = "500" },
        };
        batch = await client.Autofill(batch);

        // Inject the SponsorSignature MARKER (empty object) into the sponsored
        // inner: per rippled Batch::preflight it makes the sponsor a required
        // batch signer while carrying no signature material itself
        Dictionary<string, object> batchDict = batch.ToDictionary();
        var rawList = ((IEnumerable<object>)batchDict["RawTransactions"]).Cast<Dictionary<string, object>>().ToList();
        var sponsoredInner = (Dictionary<string, object>)rawList[1]["RawTransaction"];
        sponsoredInner["SponsorSignature"] = new Dictionary<string, object>();
        batchDict["RawTransactions"] = rawList.Cast<object>().ToList();

        // Standard Sign all the way: holder and sponsor are both routed to
        // batch-signer parts, the root finalizes with the main signature
        SignatureResult holderPart = holder.Sign(batchDict);
        SignatureResult sponsorPart = sponsor.Sign(Reparse(holderPart.TxBlob));
        SignatureResult final = root.Sign(Reparse(sponsorPart.TxBlob));

        await SubmitBlobTesAsync(final.TxBlob);

        // The created trust line must carry the sponsor on the holder's side
        // (poll: the ledger-acceptor closes a ledger every 4 seconds)
        AccountObjectsRequest request = new AccountObjectsRequest(holder.ClassicAddress)
        {
            Type = LedgerEntryType.RippleState,
        };
        LORippleState line = null;
        for (int attempt = 0; attempt < 10 && line is null; attempt++)
        {
            await Task.Delay(2000);
            AccountObjects objects = await client.AccountObjects(request).Typed();
            line = objects?.AccountObjectList?.OfType<LORippleState>().FirstOrDefault();
        }
        Assert.IsNotNull(line, "the sponsored trust line must appear in the ledger");
        Assert.IsTrue(
            sponsor.ClassicAddress == line.HighSponsor || sponsor.ClassicAddress == line.LowSponsor,
            $"the trust line reserve must be sponsored by {sponsor.ClassicAddress} (High: {line.HighSponsor}, Low: {line.LowSponsor})");
    }

    /// <summary>
    /// The maximum legal co-signing combination in one Batch: the inner
    /// TrustSet is reserve-sponsored and the sponsor authorizes as a batch
    /// signer THROUGH ITS SIGNERLIST — a nested-multisig BatchSigner entry
    /// (BatchSigner.Signers, per rippled Batch::checkBatchSign) — the
    /// sponsor-role counterpart of TestBatchMultiAccountsWithInnerMultiSign,
    /// routed by the marker instead of inner ownership. Loan/Vault inners are
    /// protocol-forbidden (kDisabledTxTypes), so LoanSet co-signing stays a
    /// standalone flow and cannot join a batch.
    /// </summary>
    [TestMethod]
    public async Task Batch_SponsoredReserveInner_SponsorSignsViaSignerList()
    {
        XrplWallet root = XrplWallet.Generate();
        XrplWallet holder = XrplWallet.Generate();
        XrplWallet sponsor = XrplWallet.Generate();
        XrplWallet signer1 = XrplWallet.Generate();
        XrplWallet signer2 = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, root, holder, sponsor, signer1, signer2);
        await SponsorshipSetAsync(sponsor, holder);

        // The sponsor authorizes through a SignerList, not its master key
        var signerList = new SignerListSet
        {
            Account = sponsor.ClassicAddress,
            SignerQuorum = 2,
            SignerEntries = new List<SignerEntryWrapper>
            {
                new SignerEntryWrapper { SignerEntry = new SignerEntry { Account = signer1.ClassicAddress, SignerWeight = 1 } },
                new SignerEntryWrapper { SignerEntry = new SignerEntry { Account = signer2.ClassicAddress, SignerWeight = 1 } },
            },
        };
        signerList = await client.Autofill(signerList);
        ValidateResult(await client.SubmitAndWait(signerList, sponsor, autofill: false));

        var inner1 = new Payment
        {
            Account = root.ClassicAddress,
            Destination = holder.ClassicAddress,
            Amount = new Currency { ValueAsXrp = 1m },
            Fee = new Currency { Value = "0" },
        }.ToBatchTx();

        var inner2 = new TrustSet
        {
            Account = holder.ClassicAddress,
            LimitAmount = new Currency
            {
                CurrencyCode = "USD",
                Issuer = root.ClassicAddress,
                Value = "1000",
            },
            Fee = new Currency { Value = "0" },
            Sponsor = sponsor.ClassicAddress,
            SponsorFlags = SponsorCoverage.spfSponsorReserve,
        }.ToBatchTx();

        var batch = new Batch
        {
            Account = root.ClassicAddress,
            Flags = BatchFlags.tfAllOrNothing,
            RawTransactions = new List<RawTransactionWrapper> { inner1, inner2 },
            Fee = new Currency { Value = "500" },
        };
        batch = await client.Autofill(batch);

        Dictionary<string, object> batchDict = batch.ToDictionary();
        var rawList = ((IEnumerable<object>)batchDict["RawTransactions"]).Cast<Dictionary<string, object>>().ToList();
        var sponsoredInner = (Dictionary<string, object>)rawList[1]["RawTransaction"];
        sponsoredInner["SponsorSignature"] = new Dictionary<string, object>();
        batchDict["RawTransactions"] = rawList.Cast<object>().ToList();

        // Holder: a single-form batch signature. Sponsor: nested multisig —
        // each list signer signs FOR the sponsor via the standard Sign
        SignatureResult holderPart = holder.Sign(batchDict);
        SignatureResult signer1Part = signer1.Sign(Reparse(holderPart.TxBlob), multisign: true, signingFor: sponsor.ClassicAddress);
        SignatureResult signer2Part = signer2.Sign(Reparse(signer1Part.TxBlob), multisign: true, signingFor: sponsor.ClassicAddress);
        SignatureResult final = root.Sign(Reparse(signer2Part.TxBlob));

        await SubmitBlobTesAsync(final.TxBlob);

        // The created trust line must carry the sponsor on the holder's side
        AccountObjectsRequest request = new AccountObjectsRequest(holder.ClassicAddress)
        {
            Type = LedgerEntryType.RippleState,
        };
        LORippleState line = null;
        for (int attempt = 0; attempt < 10 && line is null; attempt++)
        {
            await Task.Delay(2000);
            AccountObjects objects = await client.AccountObjects(request).Typed();
            line = objects?.AccountObjectList?.OfType<LORippleState>().FirstOrDefault();
        }
        Assert.IsNotNull(line, "the sponsored trust line must appear in the ledger");
        Assert.IsTrue(
            sponsor.ClassicAddress == line.HighSponsor || sponsor.ClassicAddress == line.LowSponsor,
            $"the trust line reserve must be sponsored by {sponsor.ClassicAddress} (High: {line.HighSponsor}, Low: {line.LowSponsor})");
    }

    /// <summary>
    /// The OUTER batch fee is sponsored: the sponsor co-signs the batch itself
    /// via the standard Sign (SponsorSignature on the outer transaction).
    /// </summary>
    [TestMethod]
    public async Task Batch_OuterFeeSponsored_SponsorCoSigns()
    {
        XrplWallet root = XrplWallet.Generate();
        XrplWallet destination = XrplWallet.Generate();
        XrplWallet sponsor = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, root, destination, sponsor);
        await SponsorshipSetAsync(sponsor, root);

        var inner1 = new Payment
        {
            Account = root.ClassicAddress,
            Destination = destination.ClassicAddress,
            Amount = new Currency { ValueAsXrp = 1m },
            Fee = new Currency { Value = "0" },
        }.ToBatchTx();

        var inner2 = new Payment
        {
            Account = root.ClassicAddress,
            Destination = destination.ClassicAddress,
            Amount = new Currency { ValueAsXrp = 2m },
            Fee = new Currency { Value = "0" },
        }.ToBatchTx();

        var batch = new Batch
        {
            Account = root.ClassicAddress,
            Flags = BatchFlags.tfAllOrNothing,
            RawTransactions = new List<RawTransactionWrapper> { inner1, inner2 },
            Fee = new Currency { Value = "500" },
            Sponsor = sponsor.ClassicAddress,
            SponsorFlags = SponsorCoverage.spfSponsorFee,
        };
        batch = await client.Autofill(batch);

        Dictionary<string, object> batchDict = batch.ToDictionary();
        batchDict["SigningPubKey"] = root.PublicKey;

        // The sponsor co-signs the outer batch via the standard Sign,
        // then the root finalizes with the main signature
        SignatureResult sponsorPart = sponsor.Sign(batchDict);
        SignatureResult final = root.Sign(Reparse(sponsorPart.TxBlob));

        await SubmitBlobTesAsync(final.TxBlob);
    }
}
