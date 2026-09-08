using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.BinaryCodec.Hashing;
using Xrpl.Wallet;

namespace Xrpl.Tests.Wallet.Tests
{
    /// <summary>
    /// The four-byte prefixes that go in front of a signing preimage, read out of rippled's own
    /// <c>HashPrefix.h</c> rather than restated.
    /// </summary>
    /// <remarks>
    /// A wrong prefix is invisible to a test that names it: the SDK signs and verifies with the
    /// same constant, agrees with itself, and only a node refuses the signature. So the values
    /// come from the vendored header (<c>Fixtures/HashPrefix.h</c>, see its <c>.ref</c>) and the
    /// shape of a role preimage is checked against the protocol's own rule - the transaction's
    /// preimage with four bytes in front of it, and nothing else different.
    /// </remarks>
    [TestClass]
    public class TestURoleSigningPrefixes
    {
        /// <summary>rippled <c>makeHashPrefix</c>: three ASCII letters, then a zero byte.</summary>
        private static readonly Regex Declaration = new Regex(
            @"(?<name>\w+)\s*=\s*detail::makeHashPrefix\('(?<a>.)',\s*'(?<b>.)',\s*'(?<c>.)'\)",
            RegexOptions.Compiled);

        private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "HashPrefix.h");

        /// <summary>rippled's name for each prefix -> the name this SDK gives the same value.</summary>
        private static readonly Dictionary<string, HashPrefix> Mapping = new(StringComparer.Ordinal)
        {
            ["TxSign"] = HashPrefix.TransactionSig,
            ["TxMultiSign"] = HashPrefix.TransactionMultiSig,
            ["CounterpartyTxSign"] = HashPrefix.CounterpartyTransactionSig,
            ["CounterpartyTxMultiSign"] = HashPrefix.CounterpartyTransactionMultiSig,
            ["SponsorTxSign"] = HashPrefix.SponsorTransactionSig,
            ["SponsorTxMultiSign"] = HashPrefix.SponsorTransactionMultiSig,
            ["Batch"] = HashPrefix.Batch,
            ["PaymentChannelClaim"] = HashPrefix.PaymentChannelClaim,
        };

        private static Dictionary<string, uint> ParseUpstream()
        {
            if (!File.Exists(FixturePath))
                throw new InvalidOperationException($"Vendored HashPrefix.h not found at {FixturePath}");

            string header = File.ReadAllText(FixturePath);
            Dictionary<string, uint> values = new(StringComparer.Ordinal);

            foreach (Match match in Declaration.Matches(header))
            {
                uint value = ((uint)match.Groups["a"].Value[0] << 24)
                    | ((uint)match.Groups["b"].Value[0] << 16)
                    | ((uint)match.Groups["c"].Value[0] << 8);
                values[match.Groups["name"].Value] = value;
            }

            // Guards the guard: a regex that stopped matching would make every assertion below
            // pass over an empty table.
            if (values.Count < Mapping.Count)
            {
                throw new InvalidOperationException(
                    $"Parsed only {values.Count} prefixes from the vendored HashPrefix.h; the header layout changed, " +
                    "update the parser before trusting this test");
            }

            return values;
        }

        [TestMethod]
        public void TestUPrefixes_MatchTheVendoredProtocolHeader()
        {
            Dictionary<string, uint> upstream = ParseUpstream();

            foreach (KeyValuePair<string, HashPrefix> pair in Mapping)
            {
                Assert.IsTrue(upstream.ContainsKey(pair.Key), $"rippled declares no prefix named {pair.Key}");
                Assert.AreEqual(upstream[pair.Key], (uint)pair.Value, $"{pair.Key} (rippled) vs {pair.Value} (SDK)");
            }
        }

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
