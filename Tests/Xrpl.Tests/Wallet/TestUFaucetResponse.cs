using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.Client.Exceptions;
using Xrpl.Wallet;

namespace Xrpl.Tests.Wallet.Tests
{
    /// <summary>
    /// Reading the faucet's answer. The body is a third party's HTTP response and the only part
    /// of funding a wallet that can be exercised without a network, which is the reason these
    /// exist: every way the response can disappoint used to arrive as the same flat message, or
    /// as a NullReferenceException from the middle of the read.
    /// </summary>
    [TestClass]
    public class TestUFaucetResponse
    {
        [TestMethod]
        public void ReadFaucetAddress_TakesTheAddressFromAWellFormedBody()
        {
            string body = @"{
                ""account"": {
                    ""xAddress"": ""T7dRN2ktZGYSTgFdCzYYVbdKPKTvXbGgfe1MJfBLnkYbUQK"",
                    ""classicAddress"": ""rGmaiHAmQ4Kmoc9zAdKQ4rr8YLQGDRXWmE"",
                    ""secret"": ""sEd7f3s4YGCyMLpqBBSGw6dHDGXQqL9""
                },
                ""amount"": 100,
                ""balance"": 100
            }";

            Assert.AreEqual("rGmaiHAmQ4Kmoc9zAdKQ4rr8YLQGDRXWmE", WalletSugar.ReadFaucetAddress(body));
        }

        [TestMethod]
        public void ReadFaucetAddress_WithNoAccount_SaysSoAndQuotesTheBody()
        {
            string body = @"{""error"": ""Rate limit exceeded""}";

            XRPLFaucetException ex = Assert.ThrowsExactly<XRPLFaucetException>(() => WalletSugar.ReadFaucetAddress(body));
            StringAssert.Contains(ex.Message, "no account address");
            StringAssert.Contains(ex.Message, "Rate limit exceeded",
                "the body is what a reader needs to see - a rate limit reads nothing like a broken faucet");
        }

        [TestMethod]
        public void ReadFaucetAddress_WithAnAccountButNoClassicAddress_IsRefused()
        {
            string body = @"{""account"": {""xAddress"": ""T7dRN2ktZGYSTgFdCzYYVbdKPKTvXbGgfe1MJfBLnkYbUQK""}}";

            Assert.ThrowsExactly<XRPLFaucetException>(() => WalletSugar.ReadFaucetAddress(body));
        }

        /// <summary>
        /// A null JSON literal deserializes to a null object rather than throwing, which is how
        /// this used to reach <c>faucetWallet.Account</c> and fail as a NullReferenceException.
        /// </summary>
        [TestMethod]
        public void ReadFaucetAddress_WithANullBody_IsAFaucetFailureNotANullReference()
        {
            XRPLFaucetException ex = Assert.ThrowsExactly<XRPLFaucetException>(() => WalletSugar.ReadFaucetAddress("null"));
            StringAssert.Contains(ex.Message, "no account address");
            Assert.IsNull(ex.InnerException, "nothing threw here - the deserializer simply handed back null");
        }

        [TestMethod]
        public void ReadFaucetAddress_WithSomethingThatIsNotJson_KeepsTheParseFailure()
        {
            XRPLFaucetException ex = Assert.ThrowsExactly<XRPLFaucetException>(
                () => WalletSugar.ReadFaucetAddress("<html><body>502 Bad Gateway</body></html>"));

            Assert.IsInstanceOfType<System.Text.Json.JsonException>(ex.InnerException,
                "the parse failure is the cause and has to survive the wrapping");
        }

        /// <summary>
        /// The faucet hands back the funded wallet's seed, and an exception message is the one
        /// thing a caller is certain to log. Quoting a body verbatim put that seed in the log.
        /// </summary>
        [TestMethod]
        public void ReadFaucetAddress_NeverRepeatsTheSeedBackInTheMessage()
        {
            const string seed = "sEd7f3s4YGCyMLpqBBSGw6dHDGXQqL9";
            string body = @"{""account"": {""secret"": """ + seed + @"""}, ""error"": ""Rate limit exceeded""}";

            XRPLFaucetException ex = Assert.ThrowsExactly<XRPLFaucetException>(() => WalletSugar.ReadFaucetAddress(body));

            Assert.IsFalse(ex.Message.Contains(seed), "the seed must not reach the message");
            StringAssert.Contains(ex.Message, "Rate limit exceeded", "the rest of the body is why it is quoted at all");
        }

        [TestMethod]
        public void Redact_MasksEveryNameThatCanCarryAKey()
        {
            string body = @"{""secret"": ""s1"", ""seed"": ""s2"", ""master_seed"": ""s3"", ""private_key"": ""s4"", ""account"": ""rKeepMe""}";

            string redacted = WalletSugar.Redact(body);

            foreach (string secret in new[] { "s1", "s2", "s3", "s4" })
            {
                Assert.IsFalse(redacted.Contains(@"""" + secret + @""""), $"{secret} survived redaction: {redacted}");
            }
            StringAssert.Contains(redacted, "rKeepMe", "an address is not a secret and stays readable");
        }

        /// <summary>
        /// An address is not a secret. <c>xAddress</c> was on the mask list on the assumption it
        /// might be one; the faucets return it as the X-address form of the funded account, so
        /// masking it removed a diagnostic and protected nothing.
        /// </summary>
        [TestMethod]
        public void Redact_LeavesTheAddressesReadable()
        {
            string body = @"{""account"": {""xAddress"": ""T7dRN2ktZGYSTgFdCzYYVbdKPKTvXbGgfe1MJfBLnkYbUQK"", ""classicAddress"": ""rGmaiHAmQ4Kmoc9zAdKQ4rr8YLQGDRXWmE""}, ""seed"": ""sEdSECRET""}";

            string redacted = WalletSugar.Redact(body);

            StringAssert.Contains(redacted, "T7dRN2ktZGYSTgFdCzYYVbdKPKTvXbGgfe1MJfBLnkYbUQK");
            StringAssert.Contains(redacted, "rGmaiHAmQ4Kmoc9zAdKQ4rr8YLQGDRXWmE");
            Assert.IsFalse(redacted.Contains("sEdSECRET"), "the seed still goes");
        }

        [TestMethod]
        public void Redact_CapsALongBodySoAMessageStaysAMessage()
        {
            string redacted = WalletSugar.Redact(new string('x', 4000));

            Assert.IsTrue(redacted.Length < 600, $"expected a capped body, got {redacted.Length} characters");
            StringAssert.EndsWith(redacted, "...");
        }

        [TestMethod]
        public void FaucetException_KeepsTheCauseItWasGiven()
        {
            InvalidOperationException cause = new InvalidOperationException("the socket went away");

            XRPLFaucetException ex = new XRPLFaucetException("could not reach the faucet", cause);

            Assert.AreSame(cause, ex.InnerException);
            Assert.AreEqual("could not reach the faucet", ex.Message);
        }
    }
}
