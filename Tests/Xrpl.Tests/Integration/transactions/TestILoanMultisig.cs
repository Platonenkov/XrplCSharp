using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models.Common;
using Xrpl.Models.Ledger;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;
using Xrpl.Wallet;

namespace XrplTests.Xrpl.ClientLib.Integration;

/// <summary>
/// XLS-66 LoanSet where the borrower (Counterparty) is a multisig account: its
/// SignerList members sign with the standard multisign call and the composer routes
/// the entries into CounterpartySignature.Signers, which the node verifies against
/// the borrower's SignerList.
/// </summary>
[TestClass]
[TestCategory("Loan")]
public class TestILoanMultisig : TestILoanBase
{
    private static IXrplClient client;
    protected override IXrplClient GetClient() => client;

    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext testContext)
    {
        client = await CreateStandaloneClient();
    }

    [ClassCleanup]
    public static void ClassCleanup() => client?.Dispose();

    private sealed record MultisigLoan(XrplWallet Broker, XrplWallet Signer1, XrplWallet Signer2, Dictionary<string, object> Prepared);

    /// <summary>
    /// Funds broker, borrower and two signers, gives the borrower a 2-of-2 SignerList,
    /// and prepares a LoanSet whose fee already covers the two counterparty signers.
    /// </summary>
    private static async Task<MultisigLoan> SetupAsync()
    {
        // The borrower's entries land in CounterpartySignature.Signers, a role signature
        await AmendmentGuard.RequireRoleSignaturesAsync(client);

        XrplWallet broker = XrplWallet.Generate();
        XrplWallet borrower = XrplWallet.Generate();
        XrplWallet signer1 = XrplWallet.Generate();
        XrplWallet signer2 = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, broker, borrower, signer1, signer2);

        string brokerId = await CreateBroker(client, broker);

        SignerListSet signerList = new SignerListSet
        {
            Account = borrower.ClassicAddress,
            SignerQuorum = 2,
            SignerEntries = new List<SignerEntryWrapper>
            {
                new SignerEntryWrapper { SignerEntry = new SignerEntry { Account = signer1.ClassicAddress, SignerWeight = 1 } },
                new SignerEntryWrapper { SignerEntry = new SignerEntry { Account = signer2.ClassicAddress, SignerWeight = 1 } },
            },
        };
        signerList = await client.Autofill(signerList);
        ValidateResult(await client.SubmitAndWait(signerList, borrower, true));

        LoanSet loanTx = new LoanSet
        {
            Account = broker.ClassicAddress,
            LoanBrokerID = brokerId,
            Counterparty = borrower.ClassicAddress,
            PrincipalRequested = "10000000",
        };
        // rippled LoanSet::calculateBaseFee charges one base fee per counterparty signer
        Dictionary<string, object> autofilled = await client.Autofill(loanTx.ToDictionary(), signersCount: 2);
        JsonObject prepared = LoanSigningHelper.PrepareForSigning(
            JsonNode.Parse(JsonSerializer.Serialize(autofilled, XrplJsonOptions.Default)).AsObject(), broker);
        Dictionary<string, object> preparedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(prepared.ToJsonString(), XrplJsonOptions.Default);

        return new MultisigLoan(broker, signer1, signer2, preparedDict);
    }

    private static void AssertCounterpartyMultisig(SignatureResult composed)
    {
        JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
        Assert.IsNull(decoded["Signers"], "the broker signs single, nothing may land in the main Signers");
        JsonObject counterparty = decoded["CounterpartySignature"].AsObject();
        Assert.AreEqual("", counterparty["SigningPubKey"].GetValue<string>());
        Assert.AreEqual(2, counterparty["Signers"].AsArray().Count, "both borrower signers must be present");
    }

    [TestMethod]
    public async Task TestLoanSet_MultisigCounterparty_LedgerRoutedCompose()
    {
        MultisigLoan loan = await SetupAsync();

        string brokerPart = loan.Broker.Sign(new Dictionary<string, object>(loan.Prepared)).TxBlob;
        string part1 = loan.Signer1.Sign(new Dictionary<string, object>(loan.Prepared), multisign: true).TxBlob;
        string part2 = loan.Signer2.Sign(new Dictionary<string, object>(loan.Prepared), multisign: true).TxBlob;

        // The composer looks the signers up in the Counterparty's SignerList
        SignatureResult composed = await client.ComposeSignatures(new[] { brokerPart, part1, part2 });
        AssertCounterpartyMultisig(composed);

        TransactionSummary result = await SubmitSignedLoanSet(client, composed.TxBlob);
        ValidateResult(result);
    }

    [TestMethod]
    public async Task TestLoanSet_MultisigCounterparty_OfflineCompose()
    {
        MultisigLoan loan = await SetupAsync();

        string brokerPart = loan.Broker.Sign(new Dictionary<string, object>(loan.Prepared)).TxBlob;
        string part1 = loan.Signer1.Sign(new Dictionary<string, object>(loan.Prepared), multisign: true).TxBlob;
        string part2 = loan.Signer2.Sign(new Dictionary<string, object>(loan.Prepared), multisign: true).TxBlob;

        SignatureResult composed = LoanSigningHelper.CombineLoanSignatures(
            new[] { brokerPart, part1, part2 },
            new[] { loan.Signer1.ClassicAddress, loan.Signer2.ClassicAddress });
        AssertCounterpartyMultisig(composed);

        TransactionSummary result = await SubmitSignedLoanSet(client, composed.TxBlob);
        ValidateResult(result);
    }

    /// <summary>
    /// One signer of a 2-of-2 list is not a quorum: the ledger-driven composer refuses
    /// before the node would answer tefBAD_QUORUM.
    /// </summary>
    [TestMethod]
    public async Task TestLoanSet_MultisigCounterparty_BelowQuorum_ComposeFails()
    {
        MultisigLoan loan = await SetupAsync();

        string brokerPart = loan.Broker.Sign(new Dictionary<string, object>(loan.Prepared)).TxBlob;
        string part1 = loan.Signer1.Sign(new Dictionary<string, object>(loan.Prepared), multisign: true).TxBlob;

        global::Xrpl.Client.Exceptions.ValidationException ex = await Assert.ThrowsExactlyAsync<global::Xrpl.Client.Exceptions.ValidationException>(
            () => client.ComposeSignatures(new[] { brokerPart, part1 }));
        StringAssert.Contains(ex.Message, "Counterparty");
    }
}
