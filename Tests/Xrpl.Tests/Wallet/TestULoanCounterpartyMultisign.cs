using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Keypairs;
using Xrpl.Wallet;

namespace XrplTests.Xrpl.Wallet
{
    /// <summary>
    /// XLS-66 LoanSet with a multisig borrower: the borrower's signers produce portable
    /// Signer entries, and the composer places them under CounterpartySignature.Signers.
    /// </summary>
    [TestClass]
    public class TestULoanCounterpartyMultisign
    {
        private static readonly XrplWallet Broker = XrplWallet.Generate();
        private static readonly XrplWallet Borrower = XrplWallet.Generate();
        private static readonly XrplWallet Signer1 = XrplWallet.Generate();
        private static readonly XrplWallet Signer2 = XrplWallet.Generate("secp256k1");
        private static readonly XrplWallet Stranger = XrplWallet.Generate();

        private const string BrokerId = "1111111111111111111111111111111111111111111111111111111111111111";

        private static Dictionary<string, object> Prepared() => new Dictionary<string, object>
        {
            ["TransactionType"] = "LoanSet",
            ["Account"] = Broker.ClassicAddress,
            ["LoanBrokerID"] = BrokerId,
            ["Counterparty"] = Borrower.ClassicAddress,
            ["PrincipalRequested"] = "10000000",
            ["Fee"] = "360",
            ["Sequence"] = 5u,
            ["SigningPubKey"] = Broker.PublicKey,
        };

        private static Dictionary<string, object> ToDict(JsonObject json) =>
            JsonSerializer.Deserialize<Dictionary<string, object>>(json.ToJsonString(), XrplJsonOptions.Default);

        [TestMethod]
        public void TestUCombine_MultisigBorrower_EntriesLandInCounterpartySignature()
        {
            Dictionary<string, object> prepared = Prepared();
            SignatureResult brokerPart = Broker.Sign(new Dictionary<string, object>(prepared));
            SignatureResult part1 = Signer1.Sign(new Dictionary<string, object>(prepared), multisign: true);
            SignatureResult part2 = Signer2.Sign(new Dictionary<string, object>(prepared), multisign: true);

            SignatureResult composed = LoanSigningHelper.CombineLoanSignatures(
                new[] { brokerPart.TxBlob, part1.TxBlob, part2.TxBlob },
                new[] { Signer1.ClassicAddress, Signer2.ClassicAddress });

            JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
            Assert.AreEqual(Broker.PublicKey, decoded["SigningPubKey"].GetValue<string>(), "the broker keeps the single main signature");
            Assert.IsNotNull(decoded["TxnSignature"], "the broker's TxnSignature must be present");
            Assert.IsNull(decoded["Signers"], "borrower-side entries must not land in the main Signers");

            JsonObject counterparty = decoded["CounterpartySignature"].AsObject();
            Assert.AreEqual("", counterparty["SigningPubKey"].GetValue<string>(), "the multisig form carries an empty SigningPubKey");
            Assert.IsNull(counterparty["TxnSignature"]);
            JsonArray signers = counterparty["Signers"].AsArray();
            Assert.AreEqual(2, signers.Count, "both borrower signers must be present");

            // Each entry verifies over the multisign preimage of the composed transaction:
            // the outer tx without signature fields, with the broker's SigningPubKey, plus the signer's account
            JsonObject forSigning = decoded.WithoutFields("TxnSignature", "Signers", "CounterpartySignature");
            foreach (JsonNode entry in signers)
            {
                JsonObject signer = entry["Signer"].AsObject();
                string account = signer["Account"].GetValue<string>();
                byte[] preimage = global::Xrpl.AddressCodec.Utils.FromHex(XrplBinaryCodec.EncodeForMultiSigning(
                    forSigning, account, global::Xrpl.BinaryCodec.Hashing.HashPrefix.CounterpartyTransactionMultiSig));
                Assert.IsTrue(
                    XrplKeypairs.Verify(preimage, signer["TxnSignature"].GetValue<string>(), signer["SigningPubKey"].GetValue<string>()),
                    $"the entry of {account} must verify over the multisign preimage");
            }

            // Sorted by account id bytes, as rippled requires
            List<string> order = signers.Select(e => e["Signer"]["Account"].GetValue<string>()).ToList();
            List<string> expected = order
                .OrderBy(a => global::Xrpl.AddressCodec.XrplCodec.DecodeAccountID(a), new ByteArrayComparer())
                .ToList();
            CollectionAssert.AreEqual(expected, order, "Signers must be sorted by account id");
        }

        [TestMethod]
        public void TestUCompose_UnlistedSigner_StaysOnTheBrokerSide()
        {
            Dictionary<string, object> prepared = Prepared();
            prepared["SigningPubKey"] = "";
            SignatureResult brokerSigner = Stranger.Sign(new Dictionary<string, object>(prepared), multisign: true);
            SignatureResult borrowerSigner = Signer1.Sign(new Dictionary<string, object>(prepared), multisign: true);

            SignatureResult composed = SignatureComposer.ComposeSignatures(
                new[] { brokerSigner.TxBlob, borrowerSigner.TxBlob },
                counterpartySignerAccounts: new[] { Signer1.ClassicAddress });

            JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
            Assert.AreEqual(1, decoded["Signers"].AsArray().Count, "the unlisted signer is a broker-side multisigner");
            Assert.AreEqual(Stranger.ClassicAddress, decoded["Signers"][0]["Signer"]["Account"].GetValue<string>());
            Assert.AreEqual(1, decoded["CounterpartySignature"]["Signers"].AsArray().Count);
            Assert.AreEqual(Signer1.ClassicAddress, decoded["CounterpartySignature"]["Signers"][0]["Signer"]["Account"].GetValue<string>());
        }

        [TestMethod]
        public void TestUCompose_SignerOnSponsorAndCounterpartySides_Throws()
        {
            Dictionary<string, object> prepared = Prepared();
            SignatureResult brokerPart = Broker.Sign(new Dictionary<string, object>(prepared));
            SignatureResult part1 = Signer1.Sign(new Dictionary<string, object>(prepared), multisign: true);

            ValidationException ex = Assert.ThrowsExactly<ValidationException>(() =>
                SignatureComposer.ComposeSignatures(
                    new[] { brokerPart.TxBlob, part1.TxBlob },
                    new[] { Signer1.ClassicAddress },
                    new[] { Signer1.ClassicAddress }));
            StringAssert.Contains(ex.Message, "Ambiguous signer role");
        }

        /// <summary>
        /// The two-argument form still exists as its own overload. Folding it into the
        /// three-argument method with a default would be source-compatible and binary-breaking:
        /// an assembly compiled against it would call a method that no longer exists.
        /// </summary>
        [TestMethod]
        public void TestUCompose_TwoArgumentOverload_RoutesSponsorSideOnly()
        {
            Dictionary<string, object> prepared = Prepared();
            prepared["SigningPubKey"] = "";
            SignatureResult sponsorSigner = Signer1.Sign(new Dictionary<string, object>(prepared), multisign: true);
            SignatureResult otherSigner = Stranger.Sign(new Dictionary<string, object>(prepared), multisign: true);

            SignatureResult composed = SignatureComposer.ComposeSignatures(
                new[] { sponsorSigner.TxBlob, otherSigner.TxBlob },
                new[] { Signer1.ClassicAddress });

            JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
            Assert.AreEqual(1, decoded["SponsorSignature"]["Signers"].AsArray().Count);
            Assert.AreEqual(1, decoded["Signers"].AsArray().Count);
            Assert.IsNull(decoded["CounterpartySignature"], "the two-argument form routes nothing to the counterparty side");
        }

        [TestMethod]
        public void TestUCombine_SingleAndMultisigCounterparty_Throws()
        {
            Dictionary<string, object> prepared = Prepared();
            SignatureResult brokerPart = Broker.Sign(new Dictionary<string, object>(prepared));
            SignatureResult single = Borrower.SignAsLoanCounterparty(new Dictionary<string, object>(prepared));
            SignatureResult part1 = Signer1.Sign(new Dictionary<string, object>(prepared), multisign: true);

            ValidationException ex = Assert.ThrowsExactly<ValidationException>(() =>
                LoanSigningHelper.CombineLoanSignatures(
                    new[] { brokerPart.TxBlob, single.TxBlob, part1.TxBlob },
                    new[] { Signer1.ClassicAddress }));
            StringAssert.Contains(ex.Message, "one or the other");
        }

        [TestMethod]
        public void TestUCombine_NotALoanSet_Throws()
        {
            Dictionary<string, object> payment = new Dictionary<string, object>
            {
                ["TransactionType"] = "Payment",
                ["Account"] = Broker.ClassicAddress,
                ["Destination"] = Borrower.ClassicAddress,
                ["Amount"] = "1000000",
                ["Fee"] = "12",
                ["Sequence"] = 5u,
            };
            SignatureResult part = Broker.Sign(payment);

            ValidationException ex = Assert.ThrowsExactly<ValidationException>(() =>
                LoanSigningHelper.CombineLoanSignatures(new[] { part.TxBlob }, new[] { Signer1.ClassicAddress }));
            StringAssert.Contains(ex.Message, "LoanSet");
        }

        private sealed class ByteArrayComparer : IComparer<byte[]>
        {
            public int Compare(byte[] x, byte[] y)
            {
                for (int i = 0; i < x.Length && i < y.Length; i++)
                {
                    int c = x[i].CompareTo(y[i]);
                    if (c != 0) return c;
                }
                return x.Length.CompareTo(y.Length);
            }
        }
    }
}
