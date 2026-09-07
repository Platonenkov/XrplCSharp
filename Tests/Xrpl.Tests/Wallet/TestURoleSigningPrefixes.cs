using System;
using System.Linq;
using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.BinaryCodec.Hashing;
using Xrpl.Wallet;

namespace Xrpl.Tests.Wallet.Tests
{
    /// <summary>
    /// The four-byte prefixes rippled's <c>fixCleanup3_4_0</c> gives the signing roles, checked
    /// against the protocol rather than against a captured output.
    /// </summary>
    /// <remarks>
    /// A pinned blob cannot say whether a preimage is right: regenerate it from the same code and
    /// it agrees with itself. What is checkable without a node is the shape of the change rippled
    /// describes: a role preimage is the transaction's own preimage with a different four-byte
    /// prefix, and the prefixes are ASCII tags built by <c>makeHashPrefix</c> in
    /// <c>include/xrpl/protocol/HashPrefix.h</c>. Everything past those four bytes must be
    /// identical, or the roles would be signing different transactions rather than the same one
    /// in different capacities.
    /// </remarks>
    [TestClass]
    public class TestURoleSigningPrefixes
    {
        private static JsonObject SampleTransaction()
        {
            XrplWallet submitter = XrplWallet.FromSeed("sEdVJXQmtqNy1pp8uMqsqgxMGL9QdzP");
            XrplWallet other = XrplWallet.FromSeed("sEdTTqBarUA64vciRMqd1KwpBguQuXJ");

            return new JsonObject
            {
                ["TransactionType"] = "Payment",
                ["Account"] = submitter.ClassicAddress,
                ["Destination"] = other.ClassicAddress,
                ["Amount"] = "1000000",
                ["Fee"] = "12",
                ["Sequence"] = 7u,
                ["SigningPubKey"] = submitter.PublicKey,
            };
        }

        /// <summary>rippled <c>makeHashPrefix</c>: three ASCII letters, then a zero byte.</summary>
        private static uint Tag(char a, char b, char c) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8);

        [TestMethod]
        public void TestURolePrefixes_MatchTheProtocolTags()
        {
            Assert.AreEqual(Tag('S', 'T', 'X'), (uint)HashPrefix.TransactionSig, "TxSign");
            Assert.AreEqual(Tag('S', 'M', 'T'), (uint)HashPrefix.TransactionMultiSig, "TxMultiSign");
            Assert.AreEqual(Tag('C', 'P', 'T'), (uint)HashPrefix.CounterpartyTransactionSig, "CounterpartyTxSign");
            Assert.AreEqual(Tag('C', 'P', 'M'), (uint)HashPrefix.CounterpartyTransactionMultiSig, "CounterpartyTxMultiSign");
            Assert.AreEqual(Tag('S', 'P', 'N'), (uint)HashPrefix.SponsorTransactionSig, "SponsorTxSign");
            Assert.AreEqual(Tag('S', 'P', 'M'), (uint)HashPrefix.SponsorTransactionMultiSig, "SponsorTxMultiSign");
        }

        [TestMethod]
        public void TestURolePreimage_DiffersFromTheTransactionOnlyInThePrefix()
        {
            JsonObject tx = SampleTransaction();
            string baseline = XrplBinaryCodec.EncodeForSigning(tx);

            foreach (HashPrefix prefix in new[] { HashPrefix.SponsorTransactionSig, HashPrefix.CounterpartyTransactionSig })
            {
                string role = XrplBinaryCodec.EncodeForSigning(tx, prefix);

                Assert.AreEqual(baseline.Length, role.Length, $"{prefix}: the preimage may only differ in its prefix");
                Assert.AreEqual(baseline.Substring(8), role.Substring(8), $"{prefix}: the transaction bytes must be identical");
                Assert.AreEqual(((uint)prefix).ToString("X8"), role.Substring(0, 8), $"{prefix}: leading four bytes");
                Assert.AreNotEqual(baseline, role, $"{prefix}: a role signature must not cover the transaction's own bytes");
            }
        }

        [TestMethod]
        public void TestURoleMultiSigningPreimage_DiffersFromTheTransactionOnlyInThePrefix()
        {
            JsonObject tx = SampleTransaction();
            string signer = XrplWallet.FromSeed("sEdVUGxDJ7sqTupycsVNowrQMeJn7UP").ClassicAddress;
            string baseline = XrplBinaryCodec.EncodeForMultiSigning(tx, signer);

            foreach (HashPrefix prefix in new[] { HashPrefix.SponsorTransactionMultiSig, HashPrefix.CounterpartyTransactionMultiSig })
            {
                string role = XrplBinaryCodec.EncodeForMultiSigning(tx, signer, prefix);

                Assert.AreEqual(baseline.Substring(8), role.Substring(8), $"{prefix}: the transaction and signer bytes must be identical");
                Assert.AreEqual(((uint)prefix).ToString("X8"), role.Substring(0, 8), $"{prefix}: leading four bytes");
            }
        }

        /// <summary>
        /// Every prefix is distinct: two roles sharing one would be the very substitution the
        /// amendment closes, where a signature made for one role is accepted for another.
        /// </summary>
        [TestMethod]
        public void TestUEveryPrefix_IsDistinct()
        {
            uint[] prefixes = Enum.GetValues<HashPrefix>().Select(p => (uint)p).ToArray();
            Assert.AreEqual(prefixes.Length, prefixes.Distinct().Count(), "hash prefixes must be unique");
        }
    }
}
