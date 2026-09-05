using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Xrpl.AddressCodec;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;

// https://github.com/XRPLF/xrpl.js/blob/main/packages/xrpl/src/Wallet/fundWallet.ts

namespace Xrpl.Wallet
{
    public static class WalletSugar
    {
        //Interval to check an account balance
        const int INTERVAL_SECONDS = 1;
        //Maximum attempts to retrieve a balance
        const int MAX_ATTEMPTS = 20;

        public class Funded
        {
            public XrplWallet Wallet;
            public double Balance;

            public Funded(XrplWallet wallet, double balance)
            {
                Wallet = wallet;
                Balance = balance;
            }
        }

        public class FaucetAccount
        {
            [JsonPropertyName("xAddress")]
            public string XAddress { get; set; }

            [JsonPropertyName("classicAddress")]
            public string ClassicAddress { get; set; }

        }

        public class FaucetWallet
        {
            [JsonPropertyName("account")]
            public FaucetAccount Account { get; set; }

            [JsonPropertyName("amount")]
            public double Amount { get; set; }

            /// <summary>
            /// The payment the faucet says it sent. This is the whole answer to "did the faucet
            /// pay?", so it is preferred over watching a balance whenever the faucet supplies it.
            /// </summary>
            [JsonPropertyName("transactionHash")]
            public string TransactionHash { get; set; }

        }

        public static class FaucetNetwork
        {
            public static readonly string Testnet = "faucet.altnet.rippletest.net";
            public static readonly string Devnet = "faucet.devnet.rippletest.net";
            public static readonly string NFTDevnet = "faucet-nft.ripple.com";
        }

        /// <inheritdoc cref="FundWallet(IXrplClient, XrplWallet, string, CancellationToken)"/>
        public static Task<Funded> FundWallet(this IXrplClient client, XrplWallet? wallet = null, string? faucetHost = null)
            => FundWallet(client, wallet, faucetHost, CancellationToken.None);

        /// <summary>
        /// Funds a wallet from the network's faucet, giving up when <paramref name="cancellationToken"/>
        /// is cancelled. The wait for the faucet payment to be validated is tens of seconds, which is
        /// the reason this overload exists: a cancelled call reports cancellation rather than a
        /// faucet failure.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A <paramref name="wallet"/> of <c>null</c> means one is generated here, and it is handed
        /// back only on success - the returned <see cref="Funded"/> is the only reference to it.
        /// The faucet may already have created and paid that account by the time the call fails or
        /// is cancelled, and the seed goes with the stack frame: the funds are then unreachable and
        /// a retry strands another account. Pass a wallet whenever the answer has to survive a
        /// failure, which is every case where the account is meant to be used again.
        /// </para>
        /// </remarks>
        public static async Task<Funded> FundWallet(this IXrplClient client, XrplWallet? wallet, string? faucetHost, CancellationToken cancellationToken)
        {
            //if (!client.IsConnected())
            //{
            //    throw new RippledError("Client not connected, cannot call faucet");
            //}
            // Generate a new Wallet if no existing Wallet is provided or its address is invalid to fund
            XrplWallet walletToFund = (wallet != null && XrplCodec.IsValidClassicAddress(wallet.ClassicAddress)) ? wallet : XrplWallet.Generate();

            Baseline startingBalance = await ReadBaselineAsync(client, walletToFund.ClassicAddress, cancellationToken).ConfigureAwait(false);

            // Create the POST request body

            Dictionary<string, object> json = new Dictionary<string, object>
            {
                { "destination", walletToFund.ClassicAddress },
            };
            string jsonData = JsonSerializer.Serialize(json, XrplJsonOptions.Default);
            byte[] postBody = Encoding.UTF8.GetBytes(jsonData);
            Dictionary<string, object> httpOptions = GetHTTPOptions(client, postBody, faucetHost);
            return await ReturnPromise(httpOptions, client, startingBalance, walletToFund, jsonData, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// One client for the process. A new <see cref="HttpClient"/> per call holds its socket
        /// open past disposal and exhausts the pool under any load, and the faucet host varies,
        /// so the address goes on the request rather than on the client.
        /// </summary>
        private static readonly HttpClient FaucetClient = CreateFaucetClient();

        private static HttpClient CreateFaucetClient()
        {
            // A client that lives as long as the process keeps the address it first resolved
            // unless the handler is told to retire pooled connections. Faucet hosts do move, and
            // the per-call client this replaced re-resolved every time by accident of being new.
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            };
            HttpClient httpsClient = new HttpClient(handler);
            httpsClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return httpsClient;
        }

        /// <summary>
        /// Whether the caller asked to stop. An <see cref="OperationCanceledException"/> is not
        /// evidence of that on its own, and the type is the only thing two very different events
        /// have in common: <see cref="HttpClient"/> reports its own
        /// <see cref="HttpClient.Timeout"/> as one, and this library's WebSocket client raises one
        /// with no token behind it for every pending request whenever the connection drops
        /// (<c>RequestManager.RejectAllWithCancellation</c>). Only the token can say, so it is
        /// what every catch filter in this file asks.
        /// </summary>
        internal static bool IsCallerCancellation(Exception err, CancellationToken cancellationToken)
        {
            return err is OperationCanceledException && cancellationToken.IsCancellationRequested;
        }

        /// <summary>
        /// Whether <paramref name="err"/> is the transport failing rather than the caller giving up.
        /// </summary>
        private static bool IsTransportFailure(Exception err, CancellationToken cancellationToken)
        {
            if (err is OperationCanceledException)
            {
                return !IsCallerCancellation(err, cancellationToken);
            }

            return err is HttpRequestException || err is IOException;
        }

        /// <summary>
        /// The balance an account held before the faucet was asked, and whether that is a
        /// measurement. An account that is not on the ledger holds nothing, and zero is the
        /// answer; a read that failed for any other reason leaves no answer at all, and treating
        /// the zero as one lets a balance the account already had stand in for a payment that
        /// never arrived.
        /// </summary>
        internal readonly struct Baseline
        {
            private Baseline(double value, bool known)
            {
                Value = value;
                Known = known;
            }

            public double Value { get; }

            public bool Known { get; }

            public static Baseline Of(double value) => new Baseline(value, true);

            public static readonly Baseline Unknown = new Baseline(0, false);
        }

        internal static async Task<Baseline> ReadBaselineAsync(
            IXrplClient client, string address, CancellationToken cancellationToken = default)
        {
            try
            {
                return Baseline.Of(Convert.ToDouble(
                    await client.GetXrpBalance(address, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception err) when (!IsCallerCancellation(err, cancellationToken))
            {
                // The ordinary case: the account is not on the ledger until the faucet pays, and
                // the node says so. That is a balance of nothing, and it is a measurement.
                return err.Classify().Category == XrplErrorCategory.NotFound
                    ? Baseline.Of(0)
                    : Baseline.Unknown;
            }
        }

        private static async Task<Funded> ReturnPromise(
              Dictionary<string, object> options,
              IXrplClient client,
              Baseline startingBalance,
              XrplWallet walletToFund,
              string postBody,
              CancellationToken cancellationToken
        )
        {
            string hostname = (string)options["hostname"];
            Uri endpoint = new Uri($"https://{hostname}{(string)options["path"]}");

            using StringContent contentData = new StringContent(postBody, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await FaucetClient.PostAsync(endpoint, contentData, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception err) when (IsTransportFailure(err, cancellationToken))
            {
                throw new XRPLFaucetException($"The faucet at {hostname} could not be reached: {err.Message}", err);
            }

            using (response)
            {
                byte[] chunks;
                try
                {
                    chunks = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception err) when (IsTransportFailure(err, cancellationToken))
                {
                    throw new XRPLFaucetException(
                        $"The faucet at {hostname} answered {(int)response.StatusCode} {response.StatusCode} but the body could not be read: {err.Message}", err);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new XRPLFaucetException(
                        $"The faucet at {hostname} answered {(int)response.StatusCode} {response.StatusCode}: {Redact(Encoding.UTF8.GetString(chunks))}");
                }

                return await OnEnd(
                    response,
                    chunks,
                    client,
                    startingBalance,
                    walletToFund,
                    cancellationToken
                ).ConfigureAwait(false);
            }
        }

        private static Dictionary<string, object> GetHTTPOptions(
              IXrplClient client,
              byte[] postBody,
              string hostname
        )
        {
            Dictionary<string, object> options = new Dictionary<string, object>
            {
                { "hostname", hostname ?? GetFaucetHost(client) },
                { "port", 443 },
                { "path", "/accounts" },
                { "method", "POST" },
                { "headers", new Dictionary<string, object> {
                    { "Content-Type", "application/json" },
                    { "Content-Length", postBody.Length }
                } }
            };
            return options;
        }
        private static async Task<Funded> OnEnd(
            HttpResponseMessage response,
            byte[] chunks,
            IXrplClient client,
            Baseline startingBalance,
            XrplWallet walletToFund,
            CancellationToken cancellationToken
        )
        {
            string body = Encoding.UTF8.GetString(chunks);

            // TryGetValues, because a response without a Content-Type is still a response - a
            // proxy between here and the faucet can send one - and GetValues throws on a header
            // that is not there, which reports the wrong thing about the wrong party
            string contentType = response.Content.Headers.TryGetValues("Content-Type", out IEnumerable<string> values)
                ? values.FirstOrDefault()
                : null;

            // "application/json; charset=utf-8"
            if (contentType != null && contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessSuccessfulResponse(
                    client,
                    body,
                    startingBalance,
                    walletToFund,
                    cancellationToken
                ).ConfigureAwait(false);
            }

            throw new XRPLFaucetException(
                $"The faucet answered {(int)response.StatusCode} {response.StatusCode} with content type {contentType ?? "(none)"} rather than JSON: {Redact(body)}");
        }

        /// <summary>
        /// What may be repeated back from a faucet body. An exception message is the one thing a
        /// caller is certain to log, so the value of anything that names a secret is masked and
        /// the rest is capped. Quoting the body is still worth it: a rate limit says so in it.
        /// <para>
        /// The faucets these hosts run return a seed only when no <c>destination</c> is sent,
        /// which this library always sends - so nothing here is known to leak today, and the mask
        /// is for the host that does. <c>xAddress</c> was on this list and is not: it is the
        /// X-address form of the funded account, and masking it cost diagnostics for nothing.
        /// </para>
        /// </summary>
        internal static string Redact(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return body;
            }

            string masked = SecretValue.Replace(body, "$1\"***\"");
            return masked.Length <= MaxQuotedBody ? masked : masked.Substring(0, MaxQuotedBody) + "...";
        }

        private const int MaxQuotedBody = 512;

        private static readonly Regex SecretValue = new Regex(
            "(\"(?:secret|seed|master_seed|master_seed_hex|private_key|passphrase)\"\\s*:\\s*)\"[^\"]*\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The address the faucet says it funded. The body is a third party's HTTP response, so
        /// each way of being wrong is named here rather than surfacing further along as a
        /// <see cref="NullReferenceException"/> with nothing to say about the faucet.
        /// </summary>
        internal static FaucetWallet ReadFaucetResponse(string body)
        {
            FaucetWallet faucetWallet;
            try
            {
                faucetWallet = JsonSerializer.Deserialize<FaucetWallet>(body, XrplJsonOptions.Default);
            }
            catch (JsonException err)
            {
                throw new XRPLFaucetException($"The faucet response is not JSON this can read: {err.Message}", err);
            }

            if (string.IsNullOrEmpty(faucetWallet?.Account?.ClassicAddress))
            {
                throw new XRPLFaucetException($"The faucet response carries no account address: {Redact(body)}");
            }

            return faucetWallet;
        }

        /// <summary>The address the faucet says it funded.</summary>
        internal static string ReadFaucetAddress(string body) => ReadFaucetResponse(body).Account.ClassicAddress;

        /// <summary>
        /// Waits for the payment the faucet named to be validated, and refuses anything but a
        /// <c>tes</c> result. A transaction the ledger has not heard of yet is the ordinary state
        /// of this wait - the faucet answers before its payment is validated - so
        /// <c>txnNotFound</c> is a reason to look again rather than a failure.
        /// </summary>
        internal static async Task<TransactionSummary> AwaitFaucetPaymentAsync(
            IXrplClient client,
            string transactionHash,
            string expectedDestination,
            CancellationToken cancellationToken = default,
            int attempts = MAX_ATTEMPTS,
            int intervalSeconds = INTERVAL_SECONDS)
        {
            Exception lastLookupFailure = null;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);

                TransactionSummary payment;
                try
                {
                    payment = await client.TxV2(new TxRequest(transactionHash) { ApiVersion = 2 }, cancellationToken).Typed();
                }
                catch (Exception err) when (IsRetryableReadFailure(err, cancellationToken))
                {
                    lastLookupFailure = err;
                    continue;
                }

                if (payment?.Validated != true)
                {
                    // Seen but not validated: nothing failed, the ledger has not closed on it
                    lastLookupFailure = null;
                    continue;
                }

                string result = payment.Meta?.TransactionResult;
                if (string.IsNullOrEmpty(result) || !result.StartsWith("tes", StringComparison.Ordinal))
                {
                    // No result code is not a pass. A validated transaction has one, and without
                    // it there is nothing here that says the payment succeeded
                    throw new XRPLFaucetException(
                        $"The faucet's payment {transactionHash} was validated with {result ?? "no result code"}");
                }

                // The hash is the faucet's claim about what it did, and a claim is worth what it
                // can be checked against: any validated transaction on the ledger would satisfy a
                // lookup, including one that pays somebody else
                string destination = (payment.Transaction as IDestination)?.Destination;
                if (!string.Equals(destination, expectedDestination, StringComparison.Ordinal))
                {
                    throw new XRPLFaucetException(
                        $"The faucet named payment {transactionHash}, which is validated but pays {destination ?? "an account this cannot read"} rather than {expectedDestination}");
                }

                return payment;
            }

            throw new XRPLFaucetException(
                $"The faucet accepted the request for {expectedDestination} and named payment {transactionHash}, which was not validated within {intervalSeconds} * {attempts} seconds",
                lastLookupFailure);
        }

        /// <summary>
        /// The balance to report once the faucet's payment has validated. There is no comparison
        /// left to make at this point - the payment is a fact - so a read that fails is only a
        /// missing number, and it is reported as that rather than as a funding failure.
        /// </summary>
        private static async Task<double> ReadFundedBalanceAsync(
            IXrplClient client,
            string address,
            string transactionHash,
            CancellationToken cancellationToken)
        {
            Exception lastReadFailure = null;

            for (int attempt = 0; attempt < BALANCE_READ_ATTEMPTS; attempt++)
            {
                try
                {
                    return Convert.ToDouble(await client.GetXrpBalance(address, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception err) when (IsRetryableReadFailure(err, cancellationToken))
                {
                    lastReadFailure = err;
                    await Task.Delay(TimeSpan.FromSeconds(INTERVAL_SECONDS), cancellationToken).ConfigureAwait(false);
                }
            }

            throw new XRPLFaucetException(
                $"The faucet's payment {transactionHash} to {address} was validated, but the balance could not be read afterwards",
                lastReadFailure);
        }

        private const int BALANCE_READ_ATTEMPTS = 3;

        private static async Task<Funded> ProcessSuccessfulResponse(
              IXrplClient client,
              string body,
              Baseline startingBalance,
              XrplWallet walletToFund,
              CancellationToken cancellationToken
        )
        {
            FaucetWallet faucet = ReadFaucetResponse(body);
            string fundedAddress = faucet.Account.ClassicAddress;

            if (!string.Equals(fundedAddress, walletToFund.ClassicAddress, StringComparison.Ordinal))
            {
                // Every request names its destination, so the answer is about that account or it
                // is about nothing this call can use
                throw new XRPLFaucetException(
                    $"The faucet answered about {fundedAddress}, which is not the {walletToFund.ClassicAddress} it was asked to fund");
            }

            if (!string.IsNullOrEmpty(faucet.TransactionHash))
            {
                // The faucet named the payment it sent, which answers the question outright.
                // Nothing here compares balances, so a balance the account already held cannot
                // stand in for a payment that never arrived.
                await AwaitFaucetPaymentAsync(client, faucet.TransactionHash, walletToFund.ClassicAddress, cancellationToken).ConfigureAwait(false);
                return new Funded(walletToFund, await ReadFundedBalanceAsync(client, walletToFund.ClassicAddress, faucet.TransactionHash, cancellationToken).ConfigureAwait(false));
            }

            if (!startingBalance.Known)
            {
                // Without the payment named and without a baseline, a rise cannot be told from a
                // balance that was always there. Saying so beats guessing in the caller's favour.
                throw new XRPLFaucetException(
                    $"The faucet at this host does not name the payment it sent, and the balance of {walletToFund.ClassicAddress} could not be read before the request, so there is nothing to tell a payment from the funds the account already held");
            }

            PollOutcome poll;
            try
            {
                // Check at regular interval if the address is enabled on the XRPL and funded
                poll = await PollForFundedBalance(
                    client,
                    walletToFund.ClassicAddress,
                    startingBalance.Value,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception err) when (!IsCallerCancellation(err, cancellationToken) && err is not XRPLFaucetException)
            {
                // The cause travels with it: reading a balance fails through the network, the node
                // or the JSON, and a caller handed only the sentence cannot tell which
                throw new XRPLFaucetException(
                    $"Could not read the balance of {walletToFund.ClassicAddress} while waiting for the faucet: {err.Message}", err);
            }

            if (poll.Balance <= startingBalance.Value)
            {
                // One sentence, and it is the one that is always true: the balance did not rise.
                // What the last failed read was cannot pick the wording - an account the faucet
                // never paid answers actNotFound on every attempt, so a failure is present in
                // exactly the ordinary case - but it is the only account of a client-side outage,
                // so it rides along as the cause.
                throw new XRPLFaucetException(
                    $"The faucet accepted the request for {fundedAddress}, but the balance of {walletToFund.ClassicAddress} did not rise above {startingBalance.Value} within {INTERVAL_SECONDS} * {MAX_ATTEMPTS} seconds",
                    poll.LastReadFailure);
            }

            return new Funded(walletToFund, poll.Balance);
        }

        /// <summary>
        /// Polls until the funded account's balance rises above <paramref name="originalBalance"/>,
        /// and returns that balance; returns <paramref name="originalBalance"/> unchanged when the
        /// budget of <see cref="MAX_ATTEMPTS"/> polls runs out.
        /// </summary>
        /// <remarks>
        /// Every piece of state here belongs to the call. The previous implementation drove a
        /// <see cref="System.Timers.Timer"/> through static fields - the poll budget, the address,
        /// the balances and the result - which broke it two ways: the budget was never reset, so
        /// after roughly twenty polls every later call reported failure without polling at all,
        /// and two concurrent calls overwrote each other's address and result, so one wallet's
        /// balance could be reported for another.
        /// </remarks>
        internal static async Task<double> GetUpdatedBalance(
            IXrplClient client,
            string address,
            double originalBalance,
            CancellationToken cancellationToken = default
        )
        {
            return (await PollForFundedBalance(client, address, originalBalance, cancellationToken).ConfigureAwait(false)).Balance;
        }

        /// <summary>
        /// The balance the poll ended on, and the last reason it could not read one. Not being
        /// able to read is the normal case at first - the account is not on the ledger until the
        /// faucet payment validates - so the loop keeps going; but if the balance never rises,
        /// that last failure is the only account of why, and dropping it leaves the caller with
        /// a message blaming the faucet for what may have been a disconnected client.
        /// </summary>
        internal readonly struct PollOutcome
        {
            public PollOutcome(double balance, Exception lastReadFailure)
            {
                Balance = balance;
                LastReadFailure = lastReadFailure;
            }

            public double Balance { get; }

            public Exception LastReadFailure { get; }
        }

        /// <summary>
        /// Whether a failed balance read is worth another attempt. Not being able to read is the
        /// ordinary state of this wait: the account is not on the ledger until the faucet payment
        /// validates, which arrives as an <see cref="XrplException"/>. A connection that dropped
        /// and is expected back says the same thing through a token-less
        /// <see cref="OperationCanceledException"/>, and abandoning the wait for it while
        /// retrying a request timeout for the full budget had the asymmetry backwards.
        /// </summary>
        private static bool IsRetryableReadFailure(Exception err, CancellationToken cancellationToken)
        {
            if (err is OperationCanceledException)
            {
                return !IsCallerCancellation(err, cancellationToken);
            }

            return err is XrplException || err is RippleException;
        }

        internal static async Task<PollOutcome> PollForFundedBalance(
            IXrplClient client,
            string address,
            double originalBalance,
            CancellationToken cancellationToken = default,
            int attempts = MAX_ATTEMPTS,
            int intervalSeconds = INTERVAL_SECONDS
        )
        {
            Exception lastReadFailure = null;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                // The faucet payment needs a ledger to close, so wait before the first read
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);

                double newBalance;
                try
                {
                    newBalance = Convert.ToDouble(await client.GetXrpBalance(address, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception err) when (IsRetryableReadFailure(err, cancellationToken))
                {
                    // The account is not on the ledger yet and the faucet payment has not been
                    // validated, or the connection dropped and is expected back
                    lastReadFailure = err;
                    continue;
                }

                if (newBalance > originalBalance)
                {
                    return new PollOutcome(newBalance, null);
                }

                // A reading that did not rise is not a failure to read
                lastReadFailure = null;
            }

            return new PollOutcome(originalBalance, lastReadFailure);
        }

        public static string GetFaucetHost(IXrplClient client)
        {
            string connectionUrl = client.Url();
            // 'altnet' for Ripple Testnet server and 'testnet' for XRPL Labs Testnet server
            if (connectionUrl.Contains("altnet") || connectionUrl.Contains("testnet"))
            {
                return FaucetNetwork.Testnet;
            }

            if (connectionUrl.Contains("devnet"))
            {
                return FaucetNetwork.Devnet;
            }

            if (connectionUrl.Contains("xls20-sandbox"))
            {
                return FaucetNetwork.NFTDevnet;
            }

            throw new XRPLFaucetException("Faucet URL is not defined or inferrable.");
        }
    }
}