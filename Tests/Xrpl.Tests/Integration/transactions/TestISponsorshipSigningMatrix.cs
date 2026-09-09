using System.Collections.Generic;
using System.Linq;
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
using Xrpl.Models.Ledger;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;
using Xrpl.Wallet;

namespace XrplTests.Xrpl.ClientLib.Integration;

/// <summary>
/// Full signing matrix for sponsored transactions (#43): every combination of
/// single/multisig on the submitter and the sponsor side, composition routing,
/// quorum and ambiguity fail-fast paths, and RegularKey submitters — all
/// executed against the live node.
/// </summary>
[TestClass]
[TestCategory("Sponsorship")]
public class TestISponsorshipSigningMatrix
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

    private static async Task SubmitTesAsync(TransactionRequest tx, XrplWallet wallet)
        => ValidateResult(await client.SubmitAndWait(await client.Autofill(tx), wallet, autofill: false));

    private static async Task<(XrplWallet sponsor, XrplWallet sponsee, XrplWallet destination)> SetupSponsorshipAsync()
    {
        XrplWallet sponsor = XrplWallet.Generate();
        XrplWallet sponsee = XrplWallet.Generate();
        XrplWallet destination = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, sponsor, sponsee, destination);

        await SubmitTesAsync(new SponsorshipSet
        {
            Account = sponsor.ClassicAddress,
            Sponsee = sponsee.ClassicAddress,
            FeeAmountDelta = new Currency { ValueAsXrp = 10m },
        }, sponsor);
        return (sponsor, sponsee, destination);
    }

    private static async Task SetSignerListAsync(XrplWallet owner, params XrplWallet[] signers)
    {
        await SubmitTesAsync(new SignerListSet
        {
            Account = owner.ClassicAddress,
            SignerQuorum = (uint)signers.Length,
            SignerEntries = signers
                .Select(s => new SignerEntryWrapper { SignerEntry = new SignerEntry { Account = s.ClassicAddress, SignerWeight = 1 } })
                .ToList(),
        }, owner);
    }

    /// <summary>
    /// Prepared sponsored payment dict: Fee set explicitly high enough for any
    /// signer combination in the matrix, SigningPubKey per the main-signature
    /// form (submitter pubkey for single, "" for multisig).
    /// </summary>
    private static async Task<Dictionary<string, object>> PreparedPaymentAsync(
        XrplWallet sponsee, XrplWallet destination, XrplWallet sponsor, string mainSigningPubKey)
    {
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "Payment",
            ["Account"] = sponsee.ClassicAddress,
            ["Destination"] = destination.ClassicAddress,
            ["Amount"] = "1000000",
            ["Fee"] = "200",
            ["Sponsor"] = sponsor.ClassicAddress,
            ["SponsorFlags"] = (uint)SponsorCoverage.spfSponsorFee,
        };
        tx = await client.Autofill(tx);
        tx["SigningPubKey"] = mainSigningPubKey;
        return tx;
    }

    private static Dictionary<string, object> Reparse(string blob) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(
            XrplBinaryCodec.Decode(blob).ToJsonString(), XrplJsonOptions.Default);

    private static async Task SubmitBlobTesAsync(string blob)
    {
        Submit response = await client.SubmitRequest(blob, true);
        if (response is not { EngineResult: "tesSUCCESS" or "terQUEUED" })
            throw new RippleException($"Submission failed: {response.EngineResult}");
    }

    /// <summary>Single sponsee + single sponsor signing in parallel, online composition (V2).</summary>
    [TestMethod]
    public async Task Unified_SingleSponsee_SingleSponsor_ParallelCompose()
    {
        var (sponsor, sponsee, destination) = await SetupSponsorshipAsync();
        Dictionary<string, object> prepared = await PreparedPaymentAsync(sponsee, destination, sponsor, sponsee.PublicKey);

        string sponseePart = sponsee.Sign(new Dictionary<string, object>(prepared)).TxBlob;
        string sponsorPart = sponsor.Sign(new Dictionary<string, object>(prepared)).TxBlob;

        SignatureResult composed = await client.ComposeSignatures(new[] { sponseePart, sponsorPart });
        await SubmitBlobTesAsync(composed.TxBlob);
    }

    /// <summary>Multisig sponsee (2-of-2 SignerList) + single sponsor, ledger-routed composition.</summary>
    [TestMethod]
    public async Task Unified_MultisigSponsee_SingleSponsor_Compose()
    {
        var (sponsor, sponsee, destination) = await SetupSponsorshipAsync();
        XrplWallet signer1 = XrplWallet.Generate();
        XrplWallet signer2 = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, signer1, signer2);
        await SetSignerListAsync(sponsee, signer1, signer2);

        Dictionary<string, object> prepared = await PreparedPaymentAsync(sponsee, destination, sponsor, "");

        string part1 = signer1.Sign(new Dictionary<string, object>(prepared), multisign: true).TxBlob;
        string part2 = signer2.Sign(new Dictionary<string, object>(prepared), multisign: true).TxBlob;
        string sponsorPart = sponsor.Sign(new Dictionary<string, object>(prepared)).TxBlob;

        SignatureResult composed = await client.ComposeSignatures(new[] { part1, part2, sponsorPart });

        JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
        Assert.AreEqual(2, decoded["Signers"]!.AsArray().Count, "both entries must land in the main Signers");
        Assert.IsNotNull(decoded["SponsorSignature"]!["TxnSignature"], "the sponsor co-signature must be single-form");

        await SubmitBlobTesAsync(composed.TxBlob);
    }

    /// <summary>Single sponsee + multisig sponsor (2-of-2 SignerList), entries routed into SponsorSignature.Signers.</summary>
    [TestMethod]
    public async Task Unified_SingleSponsee_MultisigSponsor_Compose()
    {
        var (sponsor, sponsee, destination) = await SetupSponsorshipAsync();
        XrplWallet signer1 = XrplWallet.Generate();
        XrplWallet signer2 = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, signer1, signer2);
        await SetSignerListAsync(sponsor, signer1, signer2);

        Dictionary<string, object> prepared = await PreparedPaymentAsync(sponsee, destination, sponsor, sponsee.PublicKey);

        string sponseePart = sponsee.Sign(new Dictionary<string, object>(prepared)).TxBlob;
        string part1 = signer1.Sign(new Dictionary<string, object>(prepared), multisign: true).TxBlob;
        string part2 = signer2.Sign(new Dictionary<string, object>(prepared), multisign: true).TxBlob;

        SignatureResult composed = await client.ComposeSignatures(new[] { sponseePart, part1, part2 });

        JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
        Assert.IsNull(decoded["Signers"], "no entries may land in the main Signers");
        JsonObject sponsorSig = decoded["SponsorSignature"]!.AsObject();
        Assert.AreEqual("", sponsorSig["SigningPubKey"]!.GetValue<string>());
        Assert.AreEqual(2, sponsorSig["Signers"]!.AsArray().Count, "both entries must land in SponsorSignature.Signers");

        await SubmitBlobTesAsync(composed.TxBlob);
    }

    /// <summary>Multisig on both sides — four portable entries, two sections, one composition.</summary>
    [TestMethod]
    public async Task Unified_MultisigBothSides_Compose()
    {
        var (sponsor, sponsee, destination) = await SetupSponsorshipAsync();
        XrplWallet accSigner1 = XrplWallet.Generate();
        XrplWallet accSigner2 = XrplWallet.Generate();
        XrplWallet spnSigner1 = XrplWallet.Generate();
        XrplWallet spnSigner2 = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, accSigner1, accSigner2, spnSigner1, spnSigner2);
        await SetSignerListAsync(sponsee, accSigner1, accSigner2);
        await SetSignerListAsync(sponsor, spnSigner1, spnSigner2);

        Dictionary<string, object> prepared = await PreparedPaymentAsync(sponsee, destination, sponsor, "");

        // The one shape where the transaction cannot say which signature an entry is: the main
        // signature is multi-signed too, so an entry could belong to either side. The account's
        // signers take the default role, the sponsor's state theirs.
        string[] parts =
        {
            accSigner1.Sign(new Dictionary<string, object>(prepared), multisign: true).TxBlob,
            accSigner2.Sign(new Dictionary<string, object>(prepared), multisign: true).TxBlob,
            spnSigner1.Sign(new Dictionary<string, object>(prepared), true, null, SignatureRole.Sponsor).TxBlob,
            spnSigner2.Sign(new Dictionary<string, object>(prepared), true, null, SignatureRole.Sponsor).TxBlob,
        };

        SignatureResult composed = await client.ComposeSignatures(parts);

        JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
        Assert.AreEqual(2, decoded["Signers"]!.AsArray().Count);
        Assert.AreEqual(2, decoded["SponsorSignature"]!["Signers"]!.AsArray().Count);

        await SubmitBlobTesAsync(composed.TxBlob);
    }

    /// <summary>The quorum pre-check fails fast when the collected weight is short.</summary>
    [TestMethod]
    public async Task Unified_QuorumShortfall_FailsFast()
    {
        var (sponsor, sponsee, destination) = await SetupSponsorshipAsync();
        XrplWallet signer1 = XrplWallet.Generate();
        XrplWallet signer2 = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, signer1, signer2);
        await SetSignerListAsync(sponsee, signer1, signer2); // quorum 2

        Dictionary<string, object> prepared = await PreparedPaymentAsync(sponsee, destination, sponsor, "");
        string onlyOne = signer1.Sign(new Dictionary<string, object>(prepared), multisign: true).TxBlob;
        string sponsorPart = sponsor.Sign(new Dictionary<string, object>(prepared)).TxBlob;

        ValidationException ex = await Assert.ThrowsExactlyAsync<ValidationException>(
            () => client.ComposeSignatures(new[] { onlyOne, sponsorPart }));
        StringAssert.Contains(ex.Message, "Insufficient signatures");
    }

    /// <summary>A signer present in both SignerLists is an explicit composition error.</summary>
    [TestMethod]
    public async Task Unified_AmbiguousSigner_FailsFast()
    {
        var (sponsor, sponsee, destination) = await SetupSponsorshipAsync();
        XrplWallet shared = XrplWallet.Generate();
        XrplWallet second = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, shared, second);
        await SetSignerListAsync(sponsee, shared, second);
        await SetSignerListAsync(sponsor, shared, second);

        Dictionary<string, object> prepared = await PreparedPaymentAsync(sponsee, destination, sponsor, "");
        string part = shared.Sign(new Dictionary<string, object>(prepared), multisign: true).TxBlob;

        ValidationException ex = await Assert.ThrowsExactlyAsync<ValidationException>(
            () => client.ComposeSignatures(new[] { part }));
        StringAssert.Contains(ex.Message, "Ambiguous signer role");
    }

    /// <summary>RegularKey submitter: the wallet address differs from tx.Account, the standard Sign still works.</summary>
    [TestMethod]
    public async Task Unified_RegularKeySponsee_StandardSign()
    {
        var (sponsor, sponsee, destination) = await SetupSponsorshipAsync();
        XrplWallet regular = XrplWallet.Generate();

        await SubmitTesAsync(new SetRegularKey
        {
            Account = sponsee.ClassicAddress,
            RegularKey = regular.ClassicAddress,
        }, sponsee);

        Dictionary<string, object> prepared = await PreparedPaymentAsync(sponsee, destination, sponsor, regular.PublicKey);

        // Sponsor first (standard Sign routes to the co-signature), the regular
        // key holder finalizes with the standard Sign
        string sponsorPart = sponsor.Sign(new Dictionary<string, object>(prepared)).TxBlob;
        SignatureResult final = regular.Sign(Reparse(sponsorPart));

        await SubmitBlobTesAsync(final.TxBlob);
    }
}
