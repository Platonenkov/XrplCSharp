using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Text.Json.Nodes;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Models.Ledger;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;
using Xrpl.Wallet;

namespace XrplTests.Xrpl.Sugar;

[TestClass]
public class TestUAutofillFees
{
    private const string MAINNET_BASE_FEE = "0.000012"; // 12 drops, 8 chars (mainnet/testnet)
    private const string DEVNET_BASE_FEE = "0.0000012"; // 1.2 drops → 1 drop * 12 = 12 drops (devnet with correction)
    private const uint RESERVE_INC = 2000000; // 2 XRP in drops

    #region BaseFee Tests

    [TestMethod]
    public async Task TestUCalculateFee_StandardPayment_ReturnsBaseFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = CreatePaymentTx();

        await client.CalculateFeePerTransactionType(tx);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        Assert.AreEqual("12", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_DevnetMultiplier_AppliesCorrection()
    {
        // Note: Devnet detection relies on GetFeeXrp returning a 9-char string (7 digits after dot).
        // With mock returning double, trailing zeros are lost: "0.0000010" → "0.000001" (8 chars).
        // Therefore multiplier is NOT applied in this unit test. Integration tests should verify real devnet behavior.
        var client = new FeeTestClient(DEVNET_BASE_FEE, RESERVE_INC);
        var tx = CreatePaymentTx();

        await client.CalculateFeePerTransactionType(tx);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        // DEVNET_BASE_FEE "0.0000012" → 0.0000012 XRP = 1.2 drops → truncated to 1 drop
        // String length is 9, so devnet multiplier (12x) IS applied: 1 * 12 = 12 drops
        Assert.AreEqual("12", tx["Fee"]);
    }

    #endregion

    #region Multisig Fee Tests

    [TestMethod]
    public async Task TestUCalculateFee_Multisig_AddsSignerFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = CreatePaymentTx();

        await client.CalculateFeePerTransactionType(tx, signersCount: 2);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        // signerFee = ScaleValue(netFeeDrops, signersCount) = 12 * 2 = 24
        // Total = baseFee + signerFee = 12 + 24 = 36
        Assert.AreEqual("36", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_Multisig_ZeroSigners_NoExtraFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = CreatePaymentTx();

        await client.CalculateFeePerTransactionType(tx, signersCount: 0);

        Assert.AreEqual("12", tx["Fee"]);
    }

    #endregion

    #region EscrowFinish Fee Tests

    [TestMethod]
    public async Task TestUCalculateFee_EscrowFinishWithFulfillment_UsesFormula()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "EscrowFinish",
            ["Account"] = "rTestAccount",
            ["Owner"] = "rOwner",
            ["OfferSequence"] = 1,
            ["Fulfillment"] = "A0028000" // 8 hex chars = 4 bytes
        };

        await client.CalculateFeePerTransactionType(tx);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        // Formula: ScaleValue(netFeeDrops, 33 + (fulfillmentBytes / 16))
        // fulfillmentBytes = ceiling(8/2) = 4 bytes
        // multiplier = 33 + (4/16) = 33.25
        // netFeeDrops = "12" (from 0.000012 XRP)
        // ScaleValue("12", 33.25) = "399"
        // Math.Ceiling(399) = 399
        Assert.AreEqual("399", tx["Fee"]);
    }

    #endregion

    #region Reserve Fee Tests

    [TestMethod]
    public async Task TestUCalculateFee_AccountDelete_UsesReserveFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "AccountDelete",
            ["Account"] = "rTestAccount",
            ["Destination"] = "rDestination"
        };

        await client.CalculateFeePerTransactionType(tx);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        Assert.AreEqual("2000000", tx["Fee"]); // Reserve fee
    }

    [TestMethod]
    public async Task TestUCalculateFee_AMMCreate_UsesReserveFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "AMMCreate",
            ["Account"] = "rTestAccount",
            ["Amount"] = new Dictionary<string, object> { ["value"] = "100" },
            ["Amount2"] = new Dictionary<string, object> { ["value"] = "100" },
            ["TradingFee"] = 100
        };

        await client.CalculateFeePerTransactionType(tx);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        Assert.AreEqual("2000000", tx["Fee"]);
    }

    #endregion

    #region Batch Fee Tests

    [TestMethod]
    public async Task TestUCalculateFee_Batch_CalculatesCorrectly()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "Batch",
            ["Account"] = "rTestAccount",
            ["RawTransactions"] = new JsonArray
            {
                new JsonObject
                {
                    ["RawTransaction"] = new JsonObject
                    {
                        ["TransactionType"] = "Payment",
                        ["Account"] = "rAccount1"
                    }
                },
                new JsonObject
                {
                    ["RawTransaction"] = new JsonObject
                    {
                        ["TransactionType"] = "Payment",
                        ["Account"] = "rAccount2"
                    }
                }
            }
        };

        await client.CalculateFeePerTransactionType(tx);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        // Base * 3 + 2 inner payments = 12*3 + 12 + 12 = 60
        Assert.AreEqual("60", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_Batch_WithReserveInner_AddsReserveFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "Batch",
            ["Account"] = "rTestAccount",
            ["RawTransactions"] = new JsonArray
            {
                new JsonObject
                {
                    ["RawTransaction"] = new JsonObject
                    {
                        ["TransactionType"] = "Payment",
                        ["Account"] = "rAccount1"
                    }
                },
                new JsonObject
                {
                    ["RawTransaction"] = new JsonObject
                    {
                        ["TransactionType"] = "AMMCreate",
                        ["Account"] = "rAccount2"
                    }
                }
            }
        };

        await client.CalculateFeePerTransactionType(tx);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        // Base * 3 + Payment + AMMCreate = 12*3 + 12 + 2000000 = 2000048
        Assert.AreEqual("2000048", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_Batch_NoRawTransactions_ThrowsValidation()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "Batch",
            ["Account"] = "rTestAccount"
        };

        await Helper.ThrowsExceptionAsync<ValidationException>(async () =>
        {
            await client.CalculateFeePerTransactionType(tx);
        });
    }

    #endregion

    #region ConfidentialMPT Fee Tests

    [TestMethod]
    public async Task TestUCalculateFee_ConfidentialMPTSend_AppliesConfidentialMultiplier()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "ConfidentialMPTSend",
            ["Account"] = "rTestAccount"
        };

        await client.CalculateFeePerTransactionType(tx);

        // rippled: Transactor::calculateBaseFee(view, tx, kConfidentialFeeMultiplier)
        // = base * 1 + base * 9 = base * 10 = 120
        Assert.AreEqual("120", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_ConfidentialMPTClawback_Multisig_AddsSignerFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "ConfidentialMPTClawback",
            ["Account"] = "rTestAccount"
        };

        await client.CalculateFeePerTransactionType(tx, signersCount: 2);

        // base * (1 + 2 signers) + base * 9 = 36 + 108 = 144
        Assert.AreEqual("144", tx["Fee"]);
    }

    #endregion

    #region LoanSet Fee Tests

    [TestMethod]
    public async Task TestUCalculateFee_LoanSet_UsesCounterpartySignerListSize()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC)
        {
            CounterpartySignerLists = CreateSignerList(3)
        };
        var tx = CreateLoanSetTx();

        await client.CalculateFeePerTransactionType(tx);

        // base * (1 + 3 counterparty signers) = 48
        Assert.AreEqual("48", tx["Fee"]);
        Assert.AreEqual(1, client.AccountInfoCalls);
        // A signer list set in the last ledger is not in `validated` yet; missing it would underpay.
        Assert.AreEqual(LedgerIndexType.Current, client.LastAccountInfoRequest?.LedgerIndex?.LedgerIndexType);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanSet_CounterpartyWithoutSignerList_ChargesOneSignature()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = CreateLoanSetTx();

        await client.CalculateFeePerTransactionType(tx);

        // base * (1 + 1 counterparty signature) = 24
        Assert.AreEqual("24", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanSet_ExistingCounterpartySignature_CountsActualSigners()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC);
        var tx = CreateLoanSetTx();
        tx["CounterpartySignature"] = new JsonObject
        {
            ["Signers"] = new JsonArray
            {
                new JsonObject { ["Signer"] = new JsonObject { ["Account"] = "rSigner1" } },
                new JsonObject { ["Signer"] = new JsonObject { ["Account"] = "rSigner2" } }
            }
        };

        await client.CalculateFeePerTransactionType(tx);

        // Signature already present: count it instead of querying the counterparty.
        // base * (1 + 2 signers) = 36
        Assert.AreEqual("36", tx["Fee"]);
        Assert.AreEqual(0, client.AccountInfoCalls);
    }

    #endregion

    #region Cancellation

    /// <summary>
    /// A cancelled token must stop fee calculation rather than be absorbed by the fallback that
    /// exists for a counterparty account which does not exist yet.
    /// </summary>
    /// <remarks>
    /// Both lookups sit behind a broad catch, so without an exception filter the
    /// OperationCanceledException became a silent "assume one signer" and autofill carried on
    /// writing a fee the caller never asked for.
    /// </remarks>
    [TestMethod]
    public async Task TestUCalculateFee_LoanSet_CancellationIsNotSwallowedByTheSignerFallback()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC)
        {
            CounterpartySignerLists = CreateSignerList(3)
        };
        var tx = CreateLoanSetTx();

        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.CalculateFeePerTransactionType(tx, 0, cts.Token));

        Assert.IsFalse(tx.ContainsKey("Fee"), "A cancelled autofill must not leave a fee behind.");
    }

    /// <summary>The same for the Loan lookup, whose fallback is a null object.</summary>
    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_CancellationIsNotSwallowedByTheLoanFallback()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC)
        {
            LoanEntry = CreateLoan(paymentRemaining: 50)
        };
        var tx = CreateLoanPayTx(amount: "10000");

        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.CalculateFeePerTransactionType(tx, 0, cts.Token));

        Assert.IsFalse(tx.ContainsKey("Fee"), "A cancelled autofill must not leave a fee behind.");
    }

    /// <summary>
    /// The filter must not turn every failure into a hard error: a lookup that fails on its own —
    /// the object is missing — still falls back while the caller's token is untouched.
    /// </summary>
    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_FailedLookupStillFallsBackWhenNotCancelled()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC) { LedgerEntryThrows = true };
        var tx = CreateLoanPayTx(amount: "10000");

        using CancellationTokenSource cts = new CancellationTokenSource();

        await client.CalculateFeePerTransactionType(tx, 0, cts.Token);

        Assert.IsTrue(tx.ContainsKey("Fee"), "An unreadable Loan object is a fallback, not a failure.");
    }

    #endregion

    #region LoanPay Fee Tests

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_FullPaymentFlag_UsesBaseFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC) { LoanEntry = CreateLoan(paymentRemaining: 50) };
        var tx = CreateLoanPayTx(amount: "10000");
        tx["Flags"] = (uint)LoanPayFlags.tfLoanFullPayment;

        await client.CalculateFeePerTransactionType(tx);

        Assert.AreEqual("12", tx["Fee"]);
        Assert.AreEqual(0, client.LedgerEntryCalls);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_FewPaymentsRemaining_UsesBaseFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC) { LoanEntry = CreateLoan(paymentRemaining: 5) };
        var tx = CreateLoanPayTx(amount: "10000");

        await client.CalculateFeePerTransactionType(tx);

        // PaymentRemaining <= kLoanPaymentsPerFeeIncrement: no extra charge
        Assert.AreEqual("12", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_ManyPayments_ChargesPerFiveIncrements()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC) { LoanEntry = CreateLoan(paymentRemaining: 50) };
        var tx = CreateLoanPayTx(amount: "1000"); // 1000 / 100 = 10 payments → ceil(10/5) = 2 increments

        await client.CalculateFeePerTransactionType(tx);

        Assert.AreEqual("24", tx["Fee"]);
        // A loan created in the last ledger is not in `validated` yet; missing it would underpay.
        Assert.AreEqual(LedgerIndexType.Current, client.LastLedgerEntryRequest?.LedgerIndex?.LedgerIndexType);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_FewPaymentsRemainingWithLargeAmount_StillChargesMaxIncrements()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC) { LoanEntry = CreateLoan(paymentRemaining: 6) };
        var tx = CreateLoanPayTx(amount: "1000000");

        await client.CalculateFeePerTransactionType(tx);

        // rippled reads PaymentRemaining only as the <= 5 short-circuit and never clamps the
        // estimate by it, so an amount covering 100 regular payments costs the full 20 increments
        // even with 6 payments left. Clamping here would underpay and hit telINSUF_FEE_P.
        Assert.AreEqual("240", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_PeriodicPaymentNearDecimalLimit_DoesNotOverflow()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC)
        {
            LoanEntry = CreateLoan(paymentRemaining: 50, periodicPayment: "79000000000000000000000000000")
        };
        var tx = CreateLoanPayTx(amount: "1000");

        await client.CalculateFeePerTransactionType(tx);

        // The amount does not even cover one payment: one increment, and no arithmetic overflow.
        Assert.AreEqual("12", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_HugeAmount_CapsAtMaxIncrements()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC) { LoanEntry = CreateLoan(paymentRemaining: 500) };
        var tx = CreateLoanPayTx(amount: "1000000"); // 10000 payments, capped at 100/5 = 20 increments

        await client.CalculateFeePerTransactionType(tx);

        Assert.AreEqual("240", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_LoanNotFound_UsesBaseFee()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC) { LedgerEntryThrows = true };
        var tx = CreateLoanPayTx(amount: "1000");

        await client.CalculateFeePerTransactionType(tx);

        // rippled falls back to the normal cost and lets preclaim reject it
        Assert.AreEqual("12", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_Multisig_MultipliesWholeCost()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC) { LoanEntry = CreateLoan(paymentRemaining: 50) };
        var tx = CreateLoanPayTx(amount: "1000");

        await client.CalculateFeePerTransactionType(tx, signersCount: 1);

        // rippled multiplies the full Transactor cost: (base * (1 + 1)) * 2 increments = 48
        Assert.AreEqual("48", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_LoanPay_IouAmount_RoundsPeriodicPaymentToLoanScale()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC)
        {
            // PeriodicPayment 1.001 rounds up to 1.01 at scale -2, service fee 0.09 → 1.10 per payment
            LoanEntry = CreateLoan(paymentRemaining: 50, periodicPayment: "1.001", loanServiceFee: "0.09", loanScale: -2)
        };
        var tx = CreateLoanPayTx(amount: new Dictionary<string, object>
        {
            ["currency"] = "USD",
            ["issuer"] = "rIssuer",
            ["value"] = "11"
        });

        await client.CalculateFeePerTransactionType(tx);

        // 11 / 1.10 = 10 payments → ceil(10/5) = 2 increments = 24
        Assert.AreEqual("24", tx["Fee"]);
    }

    #endregion

    #region MaxFee Tests

    [TestMethod]
    public async Task TestUCalculateFee_ExceedsMaxFee_CapsAtMax()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC, maxFeeXRP: "0.000050"); // 50 drops max
        var tx = CreatePaymentTx();

        await client.CalculateFeePerTransactionType(tx, signersCount: 10);

        Assert.IsTrue(tx.ContainsKey("Fee"));
        // Would be 12 + (12*10) = 12 + 120 = 132, but capped at 50 (maxFee = 0.000050 XRP)
        Assert.AreEqual("50", tx["Fee"]);
    }

    [TestMethod]
    public async Task TestUCalculateFee_AccountDelete_NotCapped()
    {
        var client = new FeeTestClient(MAINNET_BASE_FEE, RESERVE_INC, maxFeeXRP: "0.000050");
        var tx = new Dictionary<string, object>
        {
            ["TransactionType"] = "AccountDelete",
            ["Account"] = "rTestAccount",
            ["Destination"] = "rDestination"
        };

        await client.CalculateFeePerTransactionType(tx);

        // AccountDelete should NOT be capped
        Assert.AreEqual("2000000", tx["Fee"]);
    }

    #endregion

    #region Helpers

    private static Dictionary<string, object> CreatePaymentTx() => new()
    {
        ["TransactionType"] = "Payment",
        ["Account"] = "rTestAccount",
        ["Destination"] = "rDestination",
        ["Amount"] = "1000000"
    };

    private static Dictionary<string, object> CreateLoanSetTx() => new()
    {
        ["TransactionType"] = "LoanSet",
        ["Account"] = "rTestAccount",
        ["LoanBrokerID"] = "0000000000000000000000000000000000000000000000000000000000000001",
        ["Counterparty"] = "rCounterparty"
    };

    private static Dictionary<string, object> CreateLoanPayTx(object amount) => new()
    {
        ["TransactionType"] = "LoanPay",
        ["Account"] = "rTestAccount",
        ["LoanID"] = "0000000000000000000000000000000000000000000000000000000000000002",
        ["Amount"] = amount
    };

    private static LOLoan CreateLoan(
        uint paymentRemaining,
        string periodicPayment = "100",
        string loanServiceFee = "0",
        int loanScale = 0) => new()
        {
            PaymentRemaining = paymentRemaining,
            PeriodicPayment = periodicPayment,
            LoanServiceFee = loanServiceFee,
            LoanScale = loanScale
        };

    private static LOSignerList[] CreateSignerList(int entries)
    {
        var list = new LOSignerList { SignerEntries = new List<SignerEntryWrapper>() };
        for (int i = 0; i < entries; i++)
        {
            list.SignerEntries.Add(new SignerEntryWrapper
            {
                SignerEntry = new SignerEntry { Account = $"rSigner{i}", SignerWeight = 1 }
            });
        }
        return new[] { list };
    }

    #endregion
}

/// <summary>
/// Minimal mock IXrplClient for fee calculation tests.
/// Implements required methods for CalculateFeePerTransactionType.
/// </summary>
internal class FeeTestClient : IXrplClient
{
    private readonly string _feeXrp;
    private readonly uint _reserveInc;

    public FeeTestClient(string feeXrp, uint reserveInc, string maxFeeXRP = "5")
    {
        _feeXrp = feeXrp;
        _reserveInc = reserveInc;
        this.maxFeeXRP = maxFeeXRP;
        this.feeCushion = 1.0;
    }

    public Connection connection => null!;
    public long DroppedStreamMessages => 0;

    public long StaleSessionFramesDropped => 0;

    // Declared rather than inherited: this substitute returns null from connection, and the
    // interface defaults forward there.
    public long FallbackDispatchedStreamMessages => 0;

    // Declared, never raised: this client answers requests, it does not stream. Being able to
    // declare them at all is the point of the events moving onto IXrplClient - a substitute
    // client could not carry them while they lived only on Connection.
#pragma warning disable CS0067
    public event OnError OnError;
    public event OnWarning OnWarning;
    public event OnServerWarning OnServerWarning;
    public event OnConnected OnConnected;
    public event OnDisconnect OnDisconnect;
    public event OnSessionEnded OnSessionEnded;
    public event OnPing OnPing;
    public event OnLedgerClosed OnLedgerClosed;
    public event OnTransaction OnTransaction;
    public event OnValidationReceived OnValidationReceived;
    public event OnManifestReceived OnManifestReceived;
    public event OnPeerStatusChange OnPeerStatusChange;
    public event OnConsensusPhase OnConsensusPhase;
    public event OnPathFind OnPathFind;
    public event OnBookChanges OnBookChanges;
    public event OnServerStatus OnServerStatus;
    public event Action<ConnectionStatusInfo> OnConnectionStatus;
#pragma warning restore CS0067
    public double feeCushion { get; set; }
    public string maxFeeXRP { get; set; }
    public uint? networkID { get; set; }

    /// <summary>Loan ledger object returned by <see cref="LedgerEntry"/>, if any.</summary>
    public LOLoan? LoanEntry { get; set; }

    /// <summary>Signer lists returned by <see cref="AccountInfo"/>, if any.</summary>
    public LOSignerList[]? CounterpartySignerLists { get; set; }

    /// <summary>When true, <see cref="LedgerEntry"/> fails as it would for a missing object.</summary>
    public bool LedgerEntryThrows { get; set; }

    public int AccountInfoCalls { get; private set; }
    public int LedgerEntryCalls { get; private set; }

    public AccountInfoRequest? LastAccountInfoRequest { get; private set; }
    public LedgerEntryRequest? LastLedgerEntryRequest { get; private set; }

    public Task<XrplResponse<ServerInfo>> ServerInfo(ServerInfoRequest request, CancellationToken cancellationToken = default)
    {
        var info = new ServerInfo
        {
            Info = new Info()
            {
                LoadFactor = 1,
                ValidatedLedger = new ValidatedLedger()
                {
                    BaseFeeXrp = decimal.Parse(_feeXrp, System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        };
        return Task.FromResult(new XrplResponse<ServerInfo>(info, default, null, null, null, null, false));
    }

    public Task<XrplResponse<ServerState>> ServerState(ServerStateRequest request, CancellationToken cancellationToken = default)
    {
        var state = new ServerState
        {
            State = new State()
            {
                ValidatedLedger = new StateLedger()
                {
                    ReserveInc = _reserveInc
                }
            }
        };
        return Task.FromResult(new XrplResponse<ServerState>(state, default, null, null, null, null, false));
    }

    public Task<XrplResponse<ServerFeatures>> ServerFeatures(string feature = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<uint> GetLedgerIndex(CancellationToken cancellationToken = default) => Task.FromResult(100u);
    // virtual, so a test that needs a balance can answer one without a second 180-line
    // substitute; this class answers nothing here, as before
    public virtual Task<string> GetXrpBalance(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Dictionary<string, object>> Autofill(Dictionary<string, object> tx, int? signersCount = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<T> Autofill<T>(T tx, int? signersCount = null, CancellationToken cancellationToken = default) where T : ITransactionRequest => throw new NotSupportedException();

    public Task ChangeServer(string server, XrplClient.ClientOptions? options = null, System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public string EnsureClassicAddress(string address) => throw new NotSupportedException();

    #region Not Implemented

    public string Url() => throw new NotSupportedException();
    public Task Connect(System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Disconnect() => throw new NotSupportedException();

    public Task DisconnectAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public bool IsConnected() => throw new NotSupportedException();
    public Task<XrplResponse<object>> Subscribe(SubscribeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<object>> Unsubscribe(UnsubscribeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<object>> Ping(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<Fee>> Fee(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<AccountInfo>> AccountInfo(AccountInfoRequest request, CancellationToken cancellationToken = default)
    {
        // Honour the token the way a real client does, so tests can assert that a caller's
        // cancellation reaches autofill instead of being turned into a fee fallback.
        cancellationToken.ThrowIfCancellationRequested();
        AccountInfoCalls++;
        LastAccountInfoRequest = request;
        AccountInfo info = new AccountInfo { SignerLists = CounterpartySignerLists };
        return Task.FromResult(new XrplResponse<AccountInfo>(info, default, null, null, null, null, false));
    }

    public Task<XrplResponse<AccountOffers>> AccountOffers(AccountOffersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<AccountCurrencies>> AccountCurrencies(AccountCurrenciesRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<AccountLines>> AccountLines(AccountLinesRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<AccountChannels>> AccountChannels(AccountChannelsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<AccountObjects>> AccountObjects(AccountObjectsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<AccountTransactions>> AccountTransactions(AccountTransactionsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<GatewayBalancesResponse>> GatewayBalances(GatewayBalancesRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<NoRippleCheck>> NoRippleCheck(NoRippleCheckRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<LOLedger>> Ledger(LedgerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<LOBaseLedger>> LedgerClosed(LedgerClosedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<LOLedgerCurrentIndex>> LedgerCurrent(LedgerCurrentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<LOLedgerData>> LedgerData(LedgerDataRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<LedgerEntryResponse>> LedgerEntry(LedgerEntryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LedgerEntryCalls++;
        LastLedgerEntryRequest = request;
        if (LedgerEntryThrows)
            throw new XrplException("entryNotFound");
        LedgerEntryResponse response = new LedgerEntryResponse { Index = request.Index, Node = LoanEntry };
        return Task.FromResult(new XrplResponse<LedgerEntryResponse>(response, default, null, null, null, null, false));
    }

    public Task<Submit> Submit(Dictionary<string, object> tx, XrplWallet wallet, bool autoFill = true, bool failHard = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Submit> Submit(ITransactionRequest tx, XrplWallet wallet, bool autoFill = true, bool failHard = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<TransactionResponse>> TxV1(TxRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    // virtual for the same reason GetXrpBalance is: a test that needs a transaction back can
    // answer one without a second 180-line substitute
    public virtual Task<XrplResponse<TransactionSummary>> TxV2(TxRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<BookOffers>> BookOffers(BookOffersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<DepositAuthorized>> DepositAuthorized(DepositAuthorizedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<NFTBuyOffers>> NFTBuyOffers(NFTBuyOffersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<NFTSellOffers>> NFTSellOffers(NFTSellOffersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<NFTInfo>> NFTInfo(NFTInfoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<NFTHistory>> NFTHistory(NFTHistoryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<AccountNFTs>> AccountNFTs(AccountNFTsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<AMMInfoResponse>> AmmInfo(AMMInfoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<object>> Random(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<object>> AnyRequest(BaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<Dictionary<string, object>>> Request(Dictionary<string, object> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<T>> GRequest<T, R>(R request, CancellationToken cancellationToken = default) where R : BaseRequest => throw new NotSupportedException();
    public Task<XrplResponse<SimulateResponse>> Simulate(SimulateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<PathFindResponse>> PathFind(PathFindCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<PathFindResponse>> PathFindClose(PathFindCloseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<PathFindResponse>> PathFindStatus(PathFindStatusRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<RipplePathFindResponse>> RipplePathFind(RipplePathFindRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<ChannelAuthorizeResponse>> ChannelAuthorize(ChannelAuthorizeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<ChannelVerifyResponse>> ChannelVerify(ChannelVerifyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<ServerDefinitionsResponse>> ServerDefinitions(ServerDefinitionsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<VaultInfoResponse>> VaultInfo(VaultInfoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<XrplResponse<TransactionEntryResponse>> TransactionEntry(TransactionEntryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() { }

    #endregion
}
