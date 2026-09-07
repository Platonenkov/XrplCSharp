using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.Client.Json;
using Xrpl.Keypairs;
using Xrpl.Wallet;

namespace Xrpl.Tests.Wallet.Tests
{
    /// <summary>
    /// Byte-level pinning of the sponsored (XLS-68) and multisig signing outputs, with fixed
    /// ed25519 seeds. Captured from the pre-refactor implementation, so that the unified
    /// Sign/Submit refactor (issue #43) would keep every blob byte-identical; a diff here means
    /// the wire format changed.
    /// <para>
    /// SponsoredBlob was re-pinned once, when fixCleanup3_4_0 gave each signing role its own hash
    /// prefix: the sponsor now signs different bytes, so its signature inside the blob changed.
    /// Nothing else did - the transaction body and the submitter's own TxnSignature are the bytes
    /// captured originally - and what makes the new value right is not this pin but rippled
    /// accepting it, which the integration suite checks against a node carrying the amendment.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestUSigningPinned
    {
        private const string SubmitterSeed = "sEdVJXQmtqNy1pp8uMqsqgxMGL9QdzP";
        private const string SponsorSeed = "sEdTTqBarUA64vciRMqd1KwpBguQuXJ";
        private const string DestinationSeed = "sEdVPTJ6emfG3hCFdubKMpaskvkWLrT";
        private const string Signer1Seed = "sEdVUGxDJ7sqTupycsVNowrQMeJn7UP";
        private const string Signer2Seed = "sEdVYaN7HpU9U7S17zkzPW7pCKqWXzR";

        private const string SponsoredBlob =
            "1200002400000007201B007A1200204A000000016140000000000F424068400000000000000C7321ED54F2F5E9A5DFD23BD4" +
            "89173623C7D093A293C154BFDA1D9A9BA12626E7BD95A07440A23DC3577B6F545B77519D34663F8896F88E682B90E2B1157F" +
            "E4CECDC2A1FE917EC50731A8C2B776C7BC0A2076CEBC69FFAB5338EDF2E741D261C502E69F4A078114618C7D24D6B77E9F01" +
            "96A04852B0FD814E96CD9A8314470455C34F3FBC4CE4E46ADE9E1CFB2CE0DD2744801B14F00DA3229BBA108A9EA2BF7D3177" +
            "89FBF8E939BCE0267321EDF4DBF4E5536C90D3FB6709A3FDBC3FBF6E06E0D06BED3F10B45733496E1E5F907440E57E3AD595" +
            "4890DAE08FB85B342BC04E3CA377D50983DDDE328C86E592F836E5CDF8124CF8AD23807663B744774E5870C6DAE593D23F43" +
            "85481E1995C076ED0EE1";

        private const string MultisigBlob =
            "1200002400000008201B007A12016140000000001E848068400000000000001873008114618C7D24D6B77E9F0196A04852B0" +
            "FD814E96CD9A8314470455C34F3FBC4CE4E46ADE9E1CFB2CE0DD2744F3E0107321EDDC3283AEC3136499ACF20966B24AA997" +
            "579F01E20B67DBCA8177D36CE68757FF74407629DDC04E98D03E9708C67BB829ABB6AA498AC5CB919CE5F3CDA37BC7E7B091" +
            "726BE044FF19723449F1F0EC0460FC75ABA6BE26E31CDC42B83D1CCB0EC89305811401FA21766806EC90A873FE5438FEBC8B" +
            "EECDA9FBE1E0107321ED2C8A38858DF25D11C6A2BFD1BD6068C4EB3D39A873B04C58A53582DBEE9C6F727440162C19EF7DCA" +
            "F99361772E567B36ECBE8CB7E324731B962038F1C007CDBD087C34ACEFB7CEB4544569B67AE84A4DC6488B7C2B0B177410DB" +
            "849B80DD59E2910F811446268ADD60CB13AE4DD82B0165923BCD8D3C6A5AE1F1";

        private static XrplWallet Submitter => XrplWallet.FromSeed(SubmitterSeed);
        private static XrplWallet Sponsor => XrplWallet.FromSeed(SponsorSeed);
        private static XrplWallet Destination => XrplWallet.FromSeed(DestinationSeed);

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

        [TestMethod]
        public void TestUPinned_V1_SignSponsored()
        {
            var result = SponsorSigningHelper.SignSponsored(PreparedSponsoredTx(), Submitter, Sponsor);
            Assert.AreEqual(SponsoredBlob, result.TxBlob);
        }

        [TestMethod]
        public void TestUPinned_V2_Combine()
        {
            JsonObject prepared = PreparedSponsoredTx();
            Dictionary<string, object> preparedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(prepared.ToJsonString(), XrplJsonOptions.Default);
            var sponsorPart = Sponsor.SignAsSponsor(preparedDict);

            JsonObject submitterTx = prepared.DeepClone().AsObject();
            byte[] preimage = global::Xrpl.AddressCodec.Utils.FromHex(XrplBinaryCodec.EncodeForSigning(submitterTx));
            submitterTx["TxnSignature"] = XrplKeypairs.Sign(preimage, Submitter.PrivateKey);
            string submitterBlob = XrplBinaryCodec.Encode(submitterTx);

            var combined = SponsorSigningHelper.CombineSponsorSignatures(submitterBlob, sponsorPart.TxBlob);
            Assert.AreEqual(SponsoredBlob, combined.TxBlob);
        }

        [TestMethod]
        public void TestUPinned_V3_SubmitterSign()
        {
            Dictionary<string, object> preparedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(PreparedSponsoredTx().ToJsonString(), XrplJsonOptions.Default);
            var sponsorPart = Sponsor.SignAsSponsor(preparedDict);
            var final = SponsorSigningHelper.SubmitterSign(sponsorPart.TxBlob, Submitter);
            Assert.AreEqual(SponsoredBlob, final.TxBlob);
        }

        [TestMethod]
        public void TestUPinned_Multisig_TwoSigners()
        {
            XrplWallet signer1 = XrplWallet.FromSeed(Signer1Seed);
            XrplWallet signer2 = XrplWallet.FromSeed(Signer2Seed);

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
            var first = signer1.Sign(plain, multisign: true);
            Dictionary<string, object> afterFirst = JsonSerializer.Deserialize<Dictionary<string, object>>(
                XrplBinaryCodec.Decode(first.TxBlob).ToJsonString(), XrplJsonOptions.Default);
            var second = signer2.Sign(afterFirst, multisign: true);

            Assert.AreEqual(MultisigBlob, second.TxBlob);
        }
    }
}
