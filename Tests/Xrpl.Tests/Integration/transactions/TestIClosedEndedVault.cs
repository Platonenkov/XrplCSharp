using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Models;
using Xrpl.Models.Common;
using Xrpl.Models.Ledger;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;

using static Xrpl.Models.Common.Common;
using Xrpl.Sugar;
using Xrpl.Utils.Hashes;
using Xrpl.Wallet;

namespace XrplTests.Xrpl.ClientLib.Integration;

/// <summary>
/// The fields rippled develop added to the vault transactions after 3.3.0, driven end to end:
/// a closed-ended vault (rippled #7921, LendingProtocolV1_1) through its three phases, and a
/// <c>VaultWithdraw</c> that proves deposit authorization with <c>CredentialIDs</c>.
/// </summary>
/// <remarks>
/// Gated on LendingProtocolV1_1: the release stand (3.3.0) knows none of these fields, and a
/// node that cannot parse a field answers <c>invalidTransaction</c> rather than a result code.
/// Runs on the nightly stand (<c>.ci-config/docker-compose.batchv11.yml</c>) and on devnet.
/// </remarks>
[TestClass]
[TestCategory("Vault")]
public class TestIClosedEndedVault : TestIVaultBase
{
    private static IXrplClient client;
    private static bool lendingProtocolV11Active;

    protected override IXrplClient GetClient() => client;

    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext testContext)
    {
        client = await CreateStandaloneClient();
        lendingProtocolV11Active = await AmendmentGuard.IsEnabledAsync(client, AmendmentGuard.LendingProtocolV11);
    }

    [ClassCleanup]
    public static void ClassCleanup() => client?.Dispose();

    [TestInitialize]
    public void CheckAmendment()
    {
        if (!lendingProtocolV11Active)
        {
            Assert.Inconclusive("LendingProtocolV1_1 is not enabled on the test node; the closed-ended vault fields need the nightly stand (.ci-config/docker-compose.batchv11.yml).");
        }
    }

    /// <summary>
    /// Subscription: deposits and the vault's own fields as written. Investment: deposit is
    /// <c>tecEXPIRED</c>, withdrawal is <c>tecTOO_SOON</c>. Redemption: withdrawal succeeds.
    /// </summary>
    /// <remarks>
    /// The phase clock is the parent close time of the ledger a transaction applies in, so the
    /// marks are placed relative to the validated close time rather than the wall clock, and the
    /// gap between them is rippled's minimum (<c>kMinInvestmentPeriod</c>, three minutes since #8151).
    /// </remarks>
    [TestMethod]
    public async Task TestClosedEndedVault_Phases()
    {
        XrplWallet wallet = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletAsync(client, wallet, nodeType);

        DateTime closeTime = await ValidatedCloseTimeAsync();
        DateTime subscriptionDate = WholeSeconds(closeTime.AddSeconds(20));
        DateTime redemptionDate = subscriptionDate.AddSeconds(180);

        VaultCreate createTx = new VaultCreate
        {
            Account = wallet.ClassicAddress,
            Asset = new IssuedCurrency { Currency = "XRP" },
            VaultKind = (uint)VaultKind.ClosedEnded,
            SubscriptionDate = subscriptionDate,
            RedemptionDate = redemptionDate,
        };
        createTx = await client.Autofill(createTx);
        TransactionSummary createResult = await client.SubmitAndWait(createTx, wallet, true);
        ValidateResult(createResult);

        string vaultId = GetCreatedObjectId(createResult);
        Assert.IsNotNull(vaultId, "VaultID should be present in metadata");

        LOVault vault = await ReadVaultAsync(vaultId);
        Assert.AreEqual((uint)VaultKind.ClosedEnded, vault.VaultKind, "VaultKind on the ledger object");
        Assert.AreEqual(subscriptionDate, vault.SubscriptionDate, "SubscriptionDate on the ledger object");
        Assert.AreEqual(redemptionDate, vault.RedemptionDate, "RedemptionDate on the ledger object");

        // Subscription phase: deposits are accepted
        ValidateResult(await SubmitAsync(Deposit(wallet, vaultId), wallet));

        // Investment phase: neither deposits nor withdrawals
        await WaitForCloseTimeAsync(subscriptionDate);
        await AssertResultAsync("tecEXPIRED", () => SubmitAsync(Deposit(wallet, vaultId), wallet));
        await AssertResultAsync("tecTOO_SOON", () => SubmitAsync(Withdraw(wallet, vaultId), wallet));

        // Redemption phase: withdrawals are accepted
        await WaitForCloseTimeAsync(redemptionDate);
        ValidateResult(await SubmitAsync(Withdraw(wallet, vaultId), wallet));
    }

    /// <summary>
    /// An open-ended vault created after the amendment carries no VaultKind at all: the field is
    /// SoeDefault on the ledger object, and the model reads that absence as null.
    /// </summary>
    [TestMethod]
    public async Task TestOpenEndedVault_CarriesNoVaultKind()
    {
        XrplWallet wallet = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletAsync(client, wallet, nodeType);

        VaultCreate createTx = new VaultCreate
        {
            Account = wallet.ClassicAddress,
            Asset = new IssuedCurrency { Currency = "XRP" },
            VaultKind = (uint)VaultKind.OpenEnded,
        };
        createTx = await client.Autofill(createTx);
        TransactionSummary createResult = await client.SubmitAndWait(createTx, wallet, true);
        ValidateResult(createResult);

        LOVault vault = await ReadVaultAsync(GetCreatedObjectId(createResult));
        Assert.IsNull(vault.VaultKind, "an open-ended vault has no VaultKind field");
        Assert.IsNull(vault.SubscriptionDate);
        Assert.IsNull(vault.RedemptionDate);
    }

    /// <summary>
    /// XLS-70 on a vault withdrawal: the destination requires deposit authorization through a
    /// credential, the depositor withdraws to it with <c>CredentialIDs</c>. Without the field the
    /// same withdrawal is <c>tecNO_PERMISSION</c>, which is what proves the field reached the node.
    /// </summary>
    [TestMethod]
    public async Task TestVaultWithdraw_WithCredentialIDs()
    {
        XrplWallet walletIssuer = XrplWallet.Generate();
        XrplWallet walletDepositor = XrplWallet.Generate();
        XrplWallet walletRecipient = XrplWallet.Generate();
        await IntegrationTestConfig.TryFundWalletsAsync(client, nodeType, walletIssuer, walletDepositor, walletRecipient);

        string credentialType = ToHex("vault_withdraw_xls70");
        await CreateAndAcceptCredentialAsync(walletIssuer, walletDepositor, credentialType);

        AccountSet enableDepositAuth = new AccountSet
        {
            Account = walletRecipient.ClassicAddress,
            SetFlag = AccountSetAsfFlags.asfDepositAuth,
        };
        ValidateResult(await SubmitAsync(enableDepositAuth, walletRecipient));

        DepositPreauth preauth = new DepositPreauth
        {
            Account = walletRecipient.ClassicAddress,
            AuthorizeCredentials = new List<AuthorizeCredentialEntry>
            {
                new AuthorizeCredentialEntry
                {
                    Credential = new AuthorizeCredentialBody
                    {
                        Issuer = walletIssuer.ClassicAddress,
                        CredentialType = credentialType,
                    },
                },
            },
        };
        ValidateResult(await SubmitAsync(preauth, walletRecipient));

        VaultCreate createTx = new VaultCreate
        {
            Account = walletDepositor.ClassicAddress,
            Asset = new IssuedCurrency { Currency = "XRP" },
        };
        TransactionSummary createResult = await SubmitAsync(createTx, walletDepositor);
        ValidateResult(createResult);
        string vaultId = GetCreatedObjectId(createResult);

        ValidateResult(await SubmitAsync(Deposit(walletDepositor, vaultId, "3000000"), walletDepositor));

        // The recipient is behind deposit authorization: no credential, no withdrawal to it
        await AssertResultAsync("tecNO_PERMISSION", () => SubmitAsync(
            Withdraw(walletDepositor, vaultId, walletRecipient.ClassicAddress), walletDepositor));

        string credentialId = Hashes.HashCredential(
            walletDepositor.ClassicAddress,
            walletIssuer.ClassicAddress,
            credentialType);

        VaultWithdraw withdraw = Withdraw(walletDepositor, vaultId, walletRecipient.ClassicAddress);
        withdraw.CredentialIDs = new List<string> { credentialId };
        ValidateResult(await SubmitAsync(withdraw, walletDepositor));
    }

    private static VaultDeposit Deposit(XrplWallet wallet, string vaultId, string drops = "1000000") => new VaultDeposit
    {
        Account = wallet.ClassicAddress,
        VaultID = vaultId,
        Amount = new Currency { Value = drops, CurrencyCode = "XRP" },
    };

    private static VaultWithdraw Withdraw(XrplWallet wallet, string vaultId, string destination = null) => new VaultWithdraw
    {
        Account = wallet.ClassicAddress,
        VaultID = vaultId,
        Amount = new Currency { Value = "1000000", CurrencyCode = "XRP" },
        Destination = destination,
    };

    private static async Task<TransactionSummary> SubmitAsync<T>(T tx, XrplWallet wallet) where T : TransactionRequest
    {
        tx = await client.Autofill(tx);
        return await client.SubmitAndWait(tx, wallet, true);
    }

    /// <summary>SubmitAndWait throws <see cref="RippleException"/> for tec codes; the code is in the message.</summary>
    private static async Task AssertResultAsync(string expected, Func<Task<TransactionSummary>> submit)
    {
        try
        {
            await submit();
            Assert.Fail($"Expected {expected}, the transaction succeeded");
        }
        catch (RippleException ex)
        {
            Assert.IsTrue(ex.Message.Contains(expected, StringComparison.Ordinal), $"Expected {expected} but got: {ex.Message}");
        }
    }

    private static async Task<LOVault> ReadVaultAsync(string vaultId)
    {
        LedgerEntryResponse entryResponse = await client.LedgerEntry(new LedgerEntryRequest { Index = vaultId }).Typed();
        Assert.IsInstanceOfType(entryResponse?.Node, typeof(LOVault), "ledger_entry should deserialize to LOVault");
        return (LOVault)entryResponse.Node;
    }

    private static async Task CreateAndAcceptCredentialAsync(XrplWallet issuer, XrplWallet subject, string credentialType)
    {
        CredentialCreate create = new CredentialCreate
        {
            Account = issuer.ClassicAddress,
            Subject = subject.ClassicAddress,
            CredentialType = credentialType,
        };
        ValidateResult(await SubmitAsync(create, issuer));

        CredentialAccept accept = new CredentialAccept
        {
            Account = subject.ClassicAddress,
            Issuer = issuer.ClassicAddress,
            CredentialType = credentialType,
        };
        ValidateResult(await SubmitAsync(accept, subject));
    }

    private static string ToHex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text));

    /// <summary>The wire carries whole seconds; a mark with sub-second ticks would never read back equal.</summary>
    private static DateTime WholeSeconds(DateTime value) =>
        new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static async Task<DateTime> ValidatedCloseTimeAsync()
    {
        LOLedger ledger = await client.Ledger(new LedgerRequest { LedgerIndex = new LedgerIndex(LedgerIndexType.Validated) }).Typed();
        LedgerEntity entity = (LedgerEntity)ledger.LedgerEntity;
        return entity.CloseTime ?? throw new InvalidOperationException("validated ledger has no close_time");
    }

    /// <summary>
    /// Waits until the validated close time is strictly past <paramref name="target"/>: the phase
    /// boundaries are inclusive on the earlier side (a close time equal to SubscriptionDate is
    /// still the subscription phase), and the next transaction applies against that close time.
    /// </summary>
    private static async Task WaitForCloseTimeAsync(DateTime target)
    {
        TimeSpan budget = TimeSpan.FromSeconds(240);
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            DateTime lastSeen = await ValidatedCloseTimeAsync();
            if (lastSeen > target)
                return;

            if (elapsed.Elapsed >= budget)
            {
                Assert.Fail(
                    $"the validated close time did not pass {target:O} within {budget.TotalSeconds:F0}s; " +
                    $"last seen {lastSeen:O}, short by {(target - lastSeen).TotalSeconds:F1}s");
            }

            await IntegrationTestConfig.LedgerAcceptAsync(client, nodeType);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static string GetCreatedObjectId(TransactionSummary result)
    {
        if (result.Meta?.AffectedNodes == null) return null;

        foreach (AffectedNode node in result.Meta.AffectedNodes)
        {
            if (node.CreatedNode is { } created && created.LedgerEntryType == LedgerEntryType.Vault)
                return created.LedgerIndex;
        }
        return null;
    }
}
