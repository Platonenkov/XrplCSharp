using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.Client.Exceptions;
using Xrpl.Keypairs;
using Xrpl.Wallet;

namespace Xrpl.Tests.Wallet.Tests
{
    /// <summary>
    /// Unit tests for sponsored transaction signing (XLS-68):
    /// the sponsor co-signs the same preimage as the submitter via SponsorSignature
    /// (inner STObject excluded from signing fields but present in the serialized blob).
    /// </summary>
    [TestClass]
    public class TestUSponsorSigning
    {
        private const uint SpfSponsorFee = 1;
        private const uint SpfSponsorReserve = 2;

        private static JsonObject BuildSponsoredPayment(XrplWallet submitter, XrplWallet sponsor, XrplWallet destination) => new JsonObject
        {
            ["TransactionType"] = "Payment",
            ["Account"] = submitter.ClassicAddress,
            ["Destination"] = destination.ClassicAddress,
            ["Amount"] = "1000000",
            ["Sequence"] = 5u,
            ["Fee"] = "12",
            ["SigningPubKey"] = submitter.PublicKey,
            ["Sponsor"] = sponsor.ClassicAddress,
            ["SponsorFlags"] = SpfSponsorFee | SpfSponsorReserve,
        };

        /// <summary>
        /// What the submitter's own TxnSignature covers. Since fixCleanup3_4_0 that is no longer
        /// what the sponsor signs: the two preimages differ in their four-byte prefix.
        /// </summary>
        private static byte[] SubmitterPreimage(JsonObject tx) =>
            global::Xrpl.AddressCodec.Utils.FromHex(XrplBinaryCodec.EncodeForSigning(tx));

        [TestMethod]
        public void TestUSignSponsored_V1_BothSignaturesVerify()
        {
            XrplWallet submitter = XrplWallet.Generate();
            XrplWallet sponsor = XrplWallet.Generate();
            XrplWallet destination = XrplWallet.Generate();

            JsonObject tx = BuildSponsoredPayment(submitter, sponsor, destination);
            SignatureResult result = SponsorSigningHelper.SignSponsored(tx, submitter, sponsor);

            JsonObject decoded = XrplBinaryCodec.Decode(result.TxBlob).AsObject();
            Assert.AreEqual(sponsor.ClassicAddress, decoded["Sponsor"]?.GetValue<string>());
            Assert.AreEqual(SpfSponsorFee | SpfSponsorReserve, decoded["SponsorFlags"]?.GetValue<uint>());

            JsonObject sponsorSig = decoded["SponsorSignature"]!.AsObject();
            Assert.AreEqual(sponsor.PublicKey, sponsorSig["SigningPubKey"]?.GetValue<string>());

            // Both signatures must verify over the same preimage (without signature fields)
            JsonObject preimageTx = decoded.DeepClone().AsObject();
            preimageTx.Remove("SponsorSignature");
            preimageTx.Remove("TxnSignature");
            byte[] preimage = SponsorSigningHelper.GetSponsorPreimage(preimageTx);

            Assert.IsTrue(XrplKeypairs.Verify(preimage, sponsorSig["TxnSignature"]!.GetValue<string>(), sponsor.PublicKey),
                "SponsorSignature must verify over the sponsor preimage.");
            Assert.IsTrue(XrplKeypairs.Verify(SubmitterPreimage(preimageTx), decoded["TxnSignature"]!.GetValue<string>(), submitter.PublicKey),
                "Submitter TxnSignature must verify over the transaction preimage.");
        }

        [TestMethod]
        public void TestUSignSponsored_V2_CombineParallelSignatures()
        {
            XrplWallet submitter = XrplWallet.Generate();
            XrplWallet sponsor = XrplWallet.Generate();
            XrplWallet destination = XrplWallet.Generate();

            JsonObject tx = BuildSponsoredPayment(submitter, sponsor, destination);
            var txDict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                tx.ToJsonString(), Xrpl.Client.Json.XrplJsonOptions.Default);

            SignatureResult sponsorPart = sponsor.SignAsSponsor(txDict);
            SignatureResult submitterPart = submitter.Sign(txDict);

            SignatureResult combined = SponsorSigningHelper.CombineSponsorSignatures(submitterPart.TxBlob, sponsorPart.TxBlob);

            JsonObject decoded = XrplBinaryCodec.Decode(combined.TxBlob).AsObject();
            Assert.IsNotNull(decoded["TxnSignature"]);
            Assert.IsNotNull(decoded["SponsorSignature"]);
            Assert.AreEqual(sponsor.PublicKey, decoded["SponsorSignature"]!["SigningPubKey"]?.GetValue<string>());

            JsonObject preimageTx = decoded.DeepClone().AsObject();
            preimageTx.Remove("SponsorSignature");
            preimageTx.Remove("TxnSignature");
            byte[] preimage = SponsorSigningHelper.GetSponsorPreimage(preimageTx);

            Assert.IsTrue(XrplKeypairs.Verify(preimage, decoded["SponsorSignature"]!["TxnSignature"]!.GetValue<string>(), sponsor.PublicKey),
                "Combined SponsorSignature must verify over the sponsor preimage.");
            Assert.IsTrue(XrplKeypairs.Verify(SubmitterPreimage(preimageTx), decoded["TxnSignature"]!.GetValue<string>(), submitter.PublicKey),
                "Combined TxnSignature must verify over the transaction preimage.");
        }

        [TestMethod]
        public void TestUSignSponsored_V3_SequentialSigning()
        {
            XrplWallet submitter = XrplWallet.Generate();
            XrplWallet sponsor = XrplWallet.Generate();
            XrplWallet destination = XrplWallet.Generate();

            JsonObject tx = BuildSponsoredPayment(submitter, sponsor, destination);
            var txDict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                tx.ToJsonString(), Xrpl.Client.Json.XrplJsonOptions.Default);

            SignatureResult withSponsor = sponsor.SignAsSponsor(txDict);
            SignatureResult final = SponsorSigningHelper.SubmitterSign(withSponsor.TxBlob, submitter);

            JsonObject decoded = XrplBinaryCodec.Decode(final.TxBlob).AsObject();

            JsonObject preimageTx = decoded.DeepClone().AsObject();
            preimageTx.Remove("SponsorSignature");
            preimageTx.Remove("TxnSignature");
            byte[] preimage = SponsorSigningHelper.GetSponsorPreimage(preimageTx);

            Assert.IsTrue(XrplKeypairs.Verify(preimage, decoded["SponsorSignature"]!["TxnSignature"]!.GetValue<string>(), sponsor.PublicKey));
            Assert.IsTrue(XrplKeypairs.Verify(SubmitterPreimage(preimageTx), decoded["TxnSignature"]!.GetValue<string>(), submitter.PublicKey));
        }

        [TestMethod]
        public void TestUSignAsSponsor_WrongSponsorAccount_Throws()
        {
            XrplWallet submitter = XrplWallet.Generate();
            XrplWallet sponsor = XrplWallet.Generate();
            XrplWallet stranger = XrplWallet.Generate();
            XrplWallet destination = XrplWallet.Generate();

            JsonObject tx = BuildSponsoredPayment(submitter, sponsor, destination);
            var txDict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                tx.ToJsonString(), Xrpl.Client.Json.XrplJsonOptions.Default);

            Assert.ThrowsExactly<ValidationException>(() => stranger.SignAsSponsor(txDict));
        }

        [TestMethod]
        public void TestUSponsorSignature_ExcludedFromPreimage_IncludedInBlob()
        {
            XrplWallet submitter = XrplWallet.Generate();
            XrplWallet sponsor = XrplWallet.Generate();
            XrplWallet destination = XrplWallet.Generate();

            JsonObject tx = BuildSponsoredPayment(submitter, sponsor, destination);
            SignatureResult result = SponsorSigningHelper.SignSponsored(tx, submitter, sponsor);

            // Preimage of the tx WITH SponsorSignature must equal preimage WITHOUT it
            JsonObject decoded = XrplBinaryCodec.Decode(result.TxBlob).AsObject();
            JsonObject withSig = decoded.DeepClone().AsObject();
            withSig.Remove("TxnSignature");
            JsonObject withoutSig = withSig.DeepClone().AsObject();
            withoutSig.Remove("SponsorSignature");

            CollectionAssert.AreEqual(
                SponsorSigningHelper.GetSponsorPreimage(withoutSig),
                SponsorSigningHelper.GetSponsorPreimage(withSig),
                "SponsorSignature must not affect the signing preimage (kNotSigning).");

            // ...but must round-trip through the binary encoding
            Assert.IsNotNull(decoded["SponsorSignature"], "SponsorSignature must be serialized in the blob.");
        }
    }
}
