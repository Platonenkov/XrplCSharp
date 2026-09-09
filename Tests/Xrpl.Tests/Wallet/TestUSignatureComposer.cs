using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Keypairs;
using Xrpl.Models.Transactions;
using Xrpl.Wallet;

namespace Xrpl.Tests.Wallet.Tests
{
    /// <summary>
    /// SignatureComposer (#43): assembling a fully signed transaction from
    /// partially signed blobs, including routing portable Signer entries into
    /// the sponsor section. Outputs are pinned against the explicit flows.
    /// </summary>
    [TestClass]
    public class TestUSignatureComposer
    {
        private const string SubmitterSeed = "sEdVJXQmtqNy1pp8uMqsqgxMGL9QdzP";
        private const string SponsorSeed = "sEdTTqBarUA64vciRMqd1KwpBguQuXJ";
        private const string DestinationSeed = "sEdVPTJ6emfG3hCFdubKMpaskvkWLrT";
        private const string Signer1Seed = "sEdVUGxDJ7sqTupycsVNowrQMeJn7UP";
        private const string Signer2Seed = "sEdVYaN7HpU9U7S17zkzPW7pCKqWXzR";

        private static XrplWallet Submitter => XrplWallet.FromSeed(SubmitterSeed);
        private static XrplWallet Sponsor => XrplWallet.FromSeed(SponsorSeed);
        private static XrplWallet Destination => XrplWallet.FromSeed(DestinationSeed);
        private static XrplWallet Signer1 => XrplWallet.FromSeed(Signer1Seed);
        private static XrplWallet Signer2 => XrplWallet.FromSeed(Signer2Seed);

        private static JsonObject PreparedSponsoredTx() => new JsonObject
        {
            ["TransactionType"] = "Payment",
            ["Account"] = Submitter.ClassicAddress,
            ["Destination"] = Destination.ClassicAddress,
            ["Amount"] = "1000000",
            ["Fee"] = "12",
            ["Sequence"] = 7u,
            ["LastLedgerSequence"] = 8000000u,
            ["Sponsor"] = Sponsor.ClassicAddress,
            ["SponsorFlags"] = 1u,
            ["SigningPubKey"] = Submitter.PublicKey,
        };

        private static Dictionary<string, object> ToDict(JsonObject json) =>
            JsonSerializer.Deserialize<Dictionary<string, object>>(json.ToJsonString(), XrplJsonOptions.Default);

        private static string SubmitterOnlyBlob()
        {
            JsonObject tx = PreparedSponsoredTx();
            byte[] preimage = global::Xrpl.AddressCodec.Utils.FromHex(XrplBinaryCodec.EncodeForSigning(tx));
            tx["TxnSignature"] = XrplKeypairs.Sign(preimage, Submitter.PrivateKey);
            return XrplBinaryCodec.Encode(tx);
        }

        [TestMethod]
        public void TestUCompose_SubmitterPlusSponsorParts_MatchesPinnedBlob()
        {
            string sponsorPart = Sponsor.Sign(ToDict(PreparedSponsoredTx())).TxBlob;
            string submitterPart = SubmitterOnlyBlob();

            var composed = SignatureComposer.ComposeSignatures(new[] { submitterPart, sponsorPart });

            var pinned = SponsorSigningHelper.SignSponsored(PreparedSponsoredTx(), Submitter, Sponsor);
            Assert.AreEqual(pinned.TxBlob, composed.TxBlob);
        }

        [TestMethod]
        public void TestUCompose_IndependentMultisigParts_MatchesAccumulatedFlow()
        {
            var plain = new Dictionary<string, object>
            {
                ["TransactionType"] = "Payment",
                ["Account"] = Submitter.ClassicAddress,
                ["Destination"] = Destination.ClassicAddress,
                ["Amount"] = "2000000",
                ["Fee"] = "24",
                ["Sequence"] = 8u,
                ["LastLedgerSequence"] = 8000001u,
                ["SigningPubKey"] = "",
            };
            // Each device signs independently — no accumulation between them
            string part1 = Signer1.Sign(new Dictionary<string, object>(plain), multisign: true).TxBlob;
            string part2 = Signer2.Sign(new Dictionary<string, object>(plain), multisign: true).TxBlob;

            var composed = SignatureComposer.ComposeSignatures(new[] { part1, part2 });

            // Reference: the accumulated flow (signer1 -> signer2), pinned in TestUSigningPinned
            var first = Signer1.Sign(new Dictionary<string, object>(plain), multisign: true);
            Dictionary<string, object> afterFirst = JsonSerializer.Deserialize<Dictionary<string, object>>(
                XrplBinaryCodec.Decode(first.TxBlob).ToJsonString(), XrplJsonOptions.Default);
            var accumulated = Signer2.Sign(afterFirst, multisign: true);

            Assert.AreEqual(accumulated.TxBlob, composed.TxBlob);
        }

        [TestMethod]
        public void TestUCompose_SponsorMultisigEntries_RoutedIntoSponsorSection()
        {
            // Sponsee signs single; two devices produce portable Signer entries
            // that belong to the SPONSOR's SignerList
            string submitterPart = SubmitterOnlyBlob();
            JsonObject baseTx = PreparedSponsoredTx();
            string part1 = Signer1.Sign(ToDict(baseTx), multisign: true).TxBlob;
            string part2 = Signer2.Sign(ToDict(baseTx), multisign: true).TxBlob;

            var composed = SignatureComposer.ComposeSignatures(
                new[] { submitterPart, part1, part2 },
                sponsorSignerAccounts: new[] { Signer1.ClassicAddress, Signer2.ClassicAddress });

            JsonObject decoded = XrplBinaryCodec.Decode(composed.TxBlob).AsObject();
            Assert.IsNotNull(decoded["TxnSignature"], "main signature must be present");
            JsonObject sponsorSig = decoded["SponsorSignature"]!.AsObject();
            Assert.AreEqual("", sponsorSig["SigningPubKey"]!.GetValue<string>(), "sponsor multisig form uses an empty SigningPubKey");
            Assert.AreEqual(2, sponsorSig["Signers"]!.AsArray().Count);
            Assert.IsNull(decoded["Signers"], "no entries may leak into the main Signers section");
        }

        [TestMethod]
        public void TestUSignatureObject_EmptyOrMixedShapes_Throw()
        {
            // Empty object: neither signature form
            Assert.ThrowsExactly<ValidationException>(
                () => SignatureObject.FromJsonObject(new JsonObject()));

            // Bare TxnSignature without the signing key
            Assert.ThrowsExactly<ValidationException>(
                () => SignatureObject.FromJsonObject(new JsonObject { ["TxnSignature"] = "DEADBEEF" }));

            // Multisig form must keep the envelope SigningPubKey empty
            Assert.ThrowsExactly<ValidationException>(
                () => SignatureObject.FromJsonObject(new JsonObject
                {
                    ["SigningPubKey"] = "ABCDEF",
                    ["Signers"] = new JsonArray(new JsonObject
                    {
                        ["Signer"] = new JsonObject
                        {
                            ["Account"] = "rWYkbWkCeg8dP6rXALnjgZSjjLyih5NXm",
                            ["SigningPubKey"] = "AB",
                            ["TxnSignature"] = "CD",
                        },
                    }),
                }));
        }

        [TestMethod]
        public void TestUCompose_NoMainSignature_Throws()
        {
            string sponsorPart = Sponsor.Sign(ToDict(PreparedSponsoredTx())).TxBlob;
            ValidationException ex = Assert.ThrowsExactly<ValidationException>(
                () => SignatureComposer.ComposeSignatures(new[] { sponsorPart }));
            StringAssert.Contains(ex.Message, "not signed by all participants");
        }

        [TestMethod]
        public void TestUCompose_MismatchedBodies_Throws()
        {
            string sponsorPart = Sponsor.Sign(ToDict(PreparedSponsoredTx())).TxBlob;
            JsonObject other = PreparedSponsoredTx();
            other["Amount"] = "999";
            byte[] preimage = global::Xrpl.AddressCodec.Utils.FromHex(XrplBinaryCodec.EncodeForSigning(other));
            other["TxnSignature"] = XrplKeypairs.Sign(preimage, Submitter.PrivateKey);
            string mismatched = XrplBinaryCodec.Encode(other);

            Assert.ThrowsExactly<ValidationException>(
                () => SignatureComposer.ComposeSignatures(new[] { mismatched, sponsorPart }));
        }
    }
}
