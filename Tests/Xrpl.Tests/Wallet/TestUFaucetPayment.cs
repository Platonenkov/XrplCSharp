using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;
using Xrpl.Models.Transactions;
using Xrpl.Wallet;

using XrplTests.Xrpl.Sugar;

namespace Xrpl.Tests.Wallet.Tests
{
    /// <summary>
    /// Waiting on the payment the faucet named, rather than on a balance rising. The faucet
    /// answers with the hash of the transaction it sent, and that is the whole answer to whether
    /// it paid - a balance the account already held cannot stand in for one that never arrived.
    /// </summary>
    [TestClass]
    public class TestUFaucetPayment
    {
        /// <summary>Answers the scripted sequence, one entry per <c>tx</c> lookup.</summary>
        private sealed class ScriptedTxClient : FeeTestClient
        {
            private readonly Queue<Func<TransactionSummary>> _answers;

            public ScriptedTxClient(params Func<TransactionSummary>[] answers) : base("0.00001", 2)
            {
                _answers = new Queue<Func<TransactionSummary>>(answers);
            }

            public int Calls { get; private set; }

            public override Task<XrplResponse<TransactionSummary>> TxV2(TxRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                Func<TransactionSummary> answer = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
                return Task.FromResult(new XrplResponse<TransactionSummary>(
                    answer(), default(RawJson), 2, "success", null, null, false));
            }
        }

        private static TransactionSummary Payment(bool validated, string result) => new TransactionSummary
        {
            Validated = validated,
            Meta = result is null ? null : new Meta { TransactionResult = result },
        };

        private static Func<TransactionSummary> Throws(Exception err) => () => throw err;

        private static Task<TransactionSummary> AwaitAsync(IXrplClient client) =>
            WalletSugar.AwaitFaucetPaymentAsync(client, "ABC123", "rFunded", default, attempts: 3, intervalSeconds: 0);

        [TestMethod]
        public async Task Payment_ValidatedWithTes_IsAccepted()
        {
            ScriptedTxClient client = new ScriptedTxClient(() => Payment(true, "tesSUCCESS"));

            TransactionSummary payment = await AwaitAsync(client);

            Assert.AreEqual("tesSUCCESS", payment.Meta?.TransactionResult);
            Assert.AreEqual(1, client.Calls);
        }

        /// <summary>
        /// The faucet answers before its payment is validated, so a transaction the node has not
        /// heard of yet is the ordinary state of this wait rather than a failure.
        /// </summary>
        [TestMethod]
        public async Task Payment_NotFoundAtFirst_IsWaitedFor()
        {
            ScriptedTxClient client = new ScriptedTxClient(
                Throws(new RippledException("txnNotFound", null)),
                () => Payment(true, "tesSUCCESS"));

            TransactionSummary payment = await AwaitAsync(client);

            Assert.AreEqual("tesSUCCESS", payment.Meta?.TransactionResult);
            Assert.AreEqual(2, client.Calls);
        }

        [TestMethod]
        public async Task Payment_SeenButNotValidated_IsWaitedFor()
        {
            ScriptedTxClient client = new ScriptedTxClient(
                () => Payment(false, null),
                () => Payment(true, "tesSUCCESS"));

            await AwaitAsync(client);

            Assert.AreEqual(2, client.Calls);
        }

        /// <summary>
        /// This is the case the balance comparison could not see: the faucet's payment reached a
        /// ledger and failed there, so no money arrived, while the account may hold plenty.
        /// </summary>
        [TestMethod]
        public async Task Payment_ValidatedWithAFailure_IsRefusedAndNamesTheCode()
        {
            ScriptedTxClient client = new ScriptedTxClient(() => Payment(true, "tecUNFUNDED_PAYMENT"));

            XRPLFaucetException ex = await Assert.ThrowsExactlyAsync<XRPLFaucetException>(() => AwaitAsync(client));

            StringAssert.Contains(ex.Message, "tecUNFUNDED_PAYMENT");
            StringAssert.Contains(ex.Message, "ABC123");
        }

        [TestMethod]
        public async Task Payment_NeverValidated_ReportsTheWaitAndKeepsTheLastFailure()
        {
            RippledException notFound = new RippledException("txnNotFound", null);
            ScriptedTxClient client = new ScriptedTxClient(Throws(notFound));

            XRPLFaucetException ex = await Assert.ThrowsExactlyAsync<XRPLFaucetException>(() => AwaitAsync(client));

            StringAssert.Contains(ex.Message, "was not validated");
            Assert.AreSame(notFound, ex.InnerException);
            Assert.AreEqual(3, client.Calls, "the whole budget is spent before giving up");
        }

        /// <summary>
        /// A balance of nothing on an account the ledger does not have is a measurement; a read
        /// that failed for any other reason is not, and the difference is what kept an
        /// already-funded wallet from being reported as freshly paid.
        /// </summary>
        [TestMethod]
        public async Task Baseline_IsKnownOnlyWhenTheLedgerAnswered()
        {
            ScriptedBalance answered = new ScriptedBalance(() => "25");
            WalletSugar.Baseline known = await WalletSugar.ReadBaselineAsync(answered, "rTest");
            Assert.IsTrue(known.Known);
            Assert.AreEqual(25d, known.Value);

            ScriptedBalance missing = new ScriptedBalance(Throws2(new RippledException("Account not found.",
                new ErrorResponse { Error = XrplErrorCodes.ActNotFound })));
            WalletSugar.Baseline absent = await WalletSugar.ReadBaselineAsync(missing, "rTest");
            Assert.IsTrue(absent.Known, "an account the ledger does not have holds nothing, and that is an answer");
            Assert.AreEqual(0d, absent.Value);

            ScriptedBalance dropped = new ScriptedBalance(Throws2(new DisconnectedException("websocket closed")));
            WalletSugar.Baseline unknown = await WalletSugar.ReadBaselineAsync(dropped, "rTest");
            Assert.IsFalse(unknown.Known, "a read that failed leaves no baseline at all");
        }

        private static Func<string> Throws2(Exception err) => () => throw err;

        private sealed class ScriptedBalance : FeeTestClient
        {
            private readonly Func<string> _answer;

            public ScriptedBalance(Func<string> answer) : base("0.00001", 2) => _answer = answer;

            public override Task<string> GetXrpBalance(string address, CancellationToken cancellationToken = default)
                => Task.FromResult(_answer());
        }
    }
}
