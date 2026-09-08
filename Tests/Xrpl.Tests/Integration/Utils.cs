using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Xrpl.Models.Methods;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Models.Common;
using Xrpl.Models.Ledger;
using Xrpl.Models.Transactions;
using Xrpl.Utils.Hashes;
using Xrpl.Wallet;
using Xrpl.Sugar;

// https://github.com/XRPLF/xrpl.js/blob/main/packages/xrpl/test/integration/utils.ts

namespace XrplTests.Xrpl.ClientLib.Integration
{
    /// <summary>
    /// Shared lock for standalone master account operations.
    /// Prevents sequence number conflicts when parallel test classes
    /// fund wallets from the same master account simultaneously.
    /// </summary>
    internal static class StandaloneLock
    {
        internal static readonly SemaphoreSlim MasterFunding = new(1, 1);

        /// <summary>
        /// Limits how many faucet calls are in flight at once.
        /// </summary>
        /// <remarks>
        /// It was one, from a time when every call went through a shared filler wallet whose
        /// sequence could not take concurrency. Each call now funds its own destination and
        /// shares nothing, so the only reason left to hold a limit is the faucet's own rate
        /// limit. Fully serialised, funding a run's worth of accounts took minutes that
        /// individual tests spent inside their own timeouts.
        /// </remarks>
        internal static readonly SemaphoreSlim FaucetFunding = new(3, 3);
    }

    /// <summary>
    /// Specifies the type of XRPL node to connect to for integration tests.
    /// </summary>
    public enum TestNodeType
    {
        /// <summary>
        /// XRPL Testnet - public test network with faucet funding.
        /// </summary>
        TestNet,

        /// <summary>
        /// XRPL Devnet - development network with faucet funding.
        /// </summary>
        DevNet,

        /// <summary>
        /// Local standalone rippled node - uses master account for funding.
        /// Requires running: docker run -p 6006:6006 -it xrpllabsofficial/xrpld:1.12.0
        /// </summary>
        Standalone,

        /// <summary>
        /// XRPL Mainnet - production network, use with caution.
        /// </summary>
        MainNet
    }

    /// <summary>
    /// Configuration and utilities for integration tests supporting multiple node types.
    /// Automatically handles client creation and wallet funding based on the node type.
    /// </summary>
    public static class IntegrationTestConfig
    {
        /// <summary>
        /// Current node type for integration tests.
        /// Can be set via environment variable XRPL_TEST_NODE or directly.
        /// Default is TestNet.
        /// </summary>
        public static TestNodeType CurrentNodeType { get; set; } = GetNodeTypeFromEnvironment();

        /// <summary>
        /// Master account address for standalone node funding.
        /// </summary>
        public const string MasterAccount = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";

        /// <summary>
        /// Master account secret for standalone node funding.
        /// </summary>
        public const string MasterSecret = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";

        /// <summary>
        /// Minimum XRP balance threshold for funding check.
        /// </summary>
        public const decimal MinBalanceThreshold = 50m;

        /// <summary>
        /// Environment variable that replaces the WebSocket URL for any node type,
        /// e.g. a standalone stand on non-default ports or a private devnet node.
        /// </summary>
        public const string NodeUrlEnvironmentVariable = "XRPL_TEST_NODE_URL";

        /// <summary>
        /// Gets the WebSocket URL for the specified node type.
        /// <see cref="NodeUrlEnvironmentVariable"/>, when set, wins over the built-in defaults.
        /// </summary>
        /// <param name="nodeType">The type of node to connect to.</param>
        /// <returns>WebSocket URL string.</returns>
        public static string GetNodeUrl(TestNodeType nodeType)
        {
            string overrideUrl = Environment.GetEnvironmentVariable(NodeUrlEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideUrl))
                return overrideUrl.Trim();

            return nodeType switch
            {
                TestNodeType.TestNet => "wss://s.altnet.rippletest.net:51233",
                TestNodeType.DevNet => "wss://s.devnet.rippletest.net:51233",
                TestNodeType.Standalone => "ws://localhost:6006",
                TestNodeType.MainNet => "wss://xrplcluster.com",
                _ => throw new ArgumentOutOfRangeException(nameof(nodeType), nodeType, null)
            };
        }

        /// <summary>
        /// Gets the node type from the XRPL_TEST_NODE environment variable.
        /// Defaults to Standalone when the variable is not set.
        /// </summary>
        private static TestNodeType GetNodeTypeFromEnvironment()
        {
            var envValue = Environment.GetEnvironmentVariable("XRPL_TEST_NODE");
            if (string.IsNullOrEmpty(envValue))
                return TestNodeType.Standalone;

            return envValue.ToLowerInvariant() switch
            {
                "testnet" or "test" => TestNodeType.TestNet,
                "devnet" or "dev" => TestNodeType.DevNet,
                "standalone" or "local" => TestNodeType.Standalone,
                "mainnet" or "main" => TestNodeType.MainNet,
                _ => TestNodeType.TestNet
            };
        }

        /// <summary>
        /// Creates and connects an XRPL client for the current node type.
        /// </summary>
        /// <param name="nodeType">Optional node type override. Uses CurrentNodeType if null.</param>
        /// <returns>Connected IXrplClient instance.</returns>
        public static async Task<IXrplClient> CreateClientAsync(TestNodeType? nodeType = null)
        {
            var type = nodeType ?? CurrentNodeType;
            var url = GetNodeUrl(type);
            var client = new XrplClient(url);

            client.connection.OnConnected += () =>
            {
                Console.WriteLine($"[IntegrationTest] Connected to {type} at {url}");
                return Task.CompletedTask;
            };

            client.connection.OnDisconnect += (code, description) =>
            {
                Console.WriteLine($"[IntegrationTest] Disconnected: {code}, {description}");
                return Task.CompletedTask;
            };

            client.connection.OnError += (error, errorMessage, message, data) =>
            {
                Console.WriteLine($"[IntegrationTest] Error: {message}");
                return Task.CompletedTask;
            };

            await client.Connect();
            return client;
        }

        /// <summary>
        /// Funds a wallet using the appropriate method for the current node type.
        /// For testnet/devnet: uses the faucet API.
        /// For standalone: uses the master account with ledger_accept.
        /// </summary>
        /// <param name="client">Connected XRPL client.</param>
        /// <param name="wallet">Wallet to fund.</param>
        /// <param name="nodeType">Optional node type override.</param>
        public static async Task FundWalletAsync(IXrplClient client, XrplWallet wallet, TestNodeType? nodeType = null)
        {
            TestNodeType type = nodeType ?? CurrentNodeType;

            if (type == TestNodeType.Standalone)
            {
                await FundFromMasterAsync(client, wallet);
            }
            else if (type == TestNodeType.TestNet || type == TestNodeType.DevNet)
            {
                await FundFromFaucetAsync(client, wallet, type);
            }
            else
            {
                throw new InvalidOperationException($"Cannot fund wallet on {type}");
            }
        }

        /// <summary>
        /// Attempts to fund a wallet only if the balance is below threshold.
        /// </summary>
        /// <param name="client">Connected XRPL client.</param>
        /// <param name="wallet">Wallet to check and potentially fund.</param>
        /// <param name="nodeType">Optional node type override.</param>
        public static async Task TryFundWalletAsync(IXrplClient client, XrplWallet wallet, TestNodeType? nodeType = null)
        {
            try
            {
                var balance = await client.GetXrpFreeBalance(wallet.ClassicAddress);
                Console.WriteLine($"[IntegrationTest] Balance {wallet.ClassicAddress}: {balance} XRP");

                if (balance <= MinBalanceThreshold)
                {
                    await FundWalletAsync(client, wallet, nodeType);
                    Console.WriteLine($"[IntegrationTest] Funded {wallet.ClassicAddress}");
                }
            }
            catch (Exception)
            {
                await FundWalletAsync(client, wallet, nodeType);
                Console.WriteLine($"[IntegrationTest] Funded new account {wallet.ClassicAddress}");
            }
        }

        /// <summary>
        /// Waits until <paramref name="address"/> is visible on <paramref name="client"/>.
        /// </summary>
        /// <remarks>
        /// Funding confirms the account on the connection that funded it, and on a standalone
        /// stand that is the whole network. A public endpoint is a cluster behind one name, so a
        /// second connection can land on a server that has not seen the account yet and answer
        /// <c>srcActNotFound</c> for it - which is what the path-finding tests, the only ones
        /// that open a second client, hit intermittently on devnet.
        /// </remarks>
        public static async Task WaitForAccountAsync(IXrplClient client, string address)
        {
            const int MaxAttempts = 15;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await client.AccountInfo(new AccountInfoRequest(address)).Typed();
                    return;
                }
                catch (RippleException) when (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
        }

        /// <summary>
        /// Runs a path-finding request, retrying while the node answers <c>srcActNotFound</c>.
        /// </summary>
        /// <remarks>
        /// Funding confirms the account on the validated ledger, but rippled answers path finding
        /// from a ledger snapshot of its own that can lag behind it, so a freshly created source
        /// can be absent from path finding for a few ledgers after it plainly exists. Seen on
        /// devnet; the stand closes ledgers on demand and never shows it.
        /// </remarks>
        public static async Task<T> RetryWhileSourceMissingAsync<T>(Func<Task<T>> request)
        {
            const int MaxAttempts = 10;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await request();
                }
                catch (RippledException error) when (attempt < MaxAttempts && error.Message.Contains("srcActNotFound"))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
        }

        /// <summary>
        /// Tops a wallet up until it holds at least <paramref name="minimumXrp"/>.
        /// A public faucet hands out a fixed amount per call (100 XRP on devnet), which is less
        /// than some flows need in one account - a lending broker funds a vault and its cover -
        /// so the call is repeated instead of assumed to be enough. On the standalone stand the
        /// master payment covers any realistic minimum and the loop exits after the first check.
        /// </summary>
        /// <param name="client">Connected XRPL client.</param>
        /// <param name="wallet">Wallet to top up.</param>
        /// <param name="minimumXrp">Balance the wallet must reach.</param>
        /// <param name="nodeType">Optional node type override.</param>
        public static async Task EnsureBalanceAsync(
            IXrplClient client, XrplWallet wallet, decimal minimumXrp, TestNodeType? nodeType = null)
        {
            const int MaxTopUps = 6;
            for (int attempt = 0; ; attempt++)
            {
                decimal balance;
                try
                {
                    balance = await client.GetXrpFreeBalance(wallet.ClassicAddress);
                }
                catch (Exception)
                {
                    // The account does not exist yet: fund it before reading a balance again
                    balance = 0m;
                }

                if (balance >= minimumXrp)
                    return;

                if (attempt == MaxTopUps)
                    throw new InvalidOperationException(
                        $"Could not fund {wallet.ClassicAddress} up to {minimumXrp} XRP after {MaxTopUps} top-ups (balance {balance} XRP).");

                await FundWalletAsync(client, wallet, nodeType);
            }
        }

        /// <summary>
        /// Funds multiple wallets, checking balance before each.
        /// </summary>
        /// <param name="client">Connected XRPL client.</param>
        /// <param name="nodeType">Optional node type override.</param>
        /// <param name="wallets">Wallets to fund.</param>
        public static async Task TryFundWalletsAsync(IXrplClient client, TestNodeType? nodeType, params XrplWallet[] wallets)
        {
            foreach (var wallet in wallets)
            {
                await TryFundWalletAsync(client, wallet, nodeType);
            }
        }

        /// <summary>
        /// Number of faucet calls attempted for one wallet before giving up.
        /// </summary>
        private const int FaucetAttempts = 3;

        /// <summary>
        /// The faucet that funds accounts on <paramref name="type"/>.
        /// </summary>
        private static string FaucetHostFor(TestNodeType type) => type switch
        {
            TestNodeType.DevNet => WalletSugar.FaucetNetwork.Devnet,
            TestNodeType.TestNet => WalletSugar.FaucetNetwork.Testnet,
            _ => throw new InvalidOperationException($"There is no faucet for {type}."),
        };

        /// <summary>
        /// Funds a wallet straight from the testnet/devnet faucet.
        /// Calls are serialized via StandaloneLock so parallel test classes do not
        /// hammer the faucet rate limit; each failed call is retried with a growing delay.
        /// </summary>
        private static async Task FundFromFaucetAsync(IXrplClient client, XrplWallet wallet, TestNodeType type)
        {
            await StandaloneLock.FaucetFunding.WaitAsync();
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        // The host is named rather than left to WalletSugar.GetFaucetHost, which
                        // infers it from the connection URL and throws when it recognises nothing:
                        // XRPL_TEST_NODE_URL may point at a node whose hostname says nothing about
                        // which network it is on.
                        await client.FundWallet(wallet, FaucetHostFor(type));
                        decimal balance = await client.GetXrpFreeBalance(wallet.ClassicAddress);
                        Console.WriteLine($"[IntegrationTest] Faucet funded {wallet.ClassicAddress}: {balance} XRP");
                        return;
                    }
                    catch (Exception ex) when (attempt < FaucetAttempts)
                    {
                        Console.WriteLine($"[IntegrationTest] Faucet attempt {attempt}/{FaucetAttempts} for {wallet.ClassicAddress} failed: {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                    }
                }
            }
            finally
            {
                StandaloneLock.FaucetFunding.Release();
            }
        }

        /// <summary>
        /// Funds a wallet from the standalone master account.
        /// Uses Submit + LedgerAccept instead of SubmitAndWait to avoid
        /// LastLedgerSequence expiration when parallel tests advance the ledger.
        /// Serialized via StandaloneLock to prevent sequence number conflicts.
        /// </summary>
        private static async Task FundFromMasterAsync(IXrplClient client, XrplWallet wallet)
        {
            await StandaloneLock.MasterFunding.WaitAsync();
            try
            {
                const int maxRetries = 3;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    Payment payment = new Payment
                    {
                        Account = MasterAccount,
                        Destination = wallet.ClassicAddress,
                        Amount = new Currency { Value = "400000000", CurrencyCode = "XRP" }
                    };
                    var master = XrplWallet.FromSeed(MasterSecret);
                    Submit response = await client.Submit(payment, master, true);

                    if (response.EngineResult == "tesSUCCESS")
                    {
                        await LedgerAcceptAsync(client);
                        Console.WriteLine($"[IntegrationTest] Master funded {wallet.ClassicAddress}");
                        return;
                    }

                    if (response.EngineResult == "tefMAX_LEDGER" && attempt < maxRetries - 1)
                    {
                        Console.WriteLine($"[IntegrationTest] FundFromMaster got {response.EngineResult}, retrying ({attempt + 1}/{maxRetries})...");
                        await Task.Delay(1000);
                        continue;
                    }

                    throw new Exception($"Master funding failed: {response.EngineResult}");
                }
            }
            finally
            {
                StandaloneLock.MasterFunding.Release();
            }
        }

        /// <summary>
        /// Advances the ledger on standalone node.
        /// No-op on public networks.
        /// </summary>
        /// <param name="client">Connected XRPL client.</param>
        /// <param name="nodeType">Optional node type override.</param>
        public static async Task LedgerAcceptAsync(IXrplClient client, TestNodeType? nodeType = null)
        {
            var type = nodeType ?? CurrentNodeType;
            if (type != TestNodeType.Standalone)
                return;

            var request = new BaseRequest { Command = "ledger_accept" };
            await client.AnyRequest(request);
        }

        /// <summary>
        /// Close time of the latest validated ledger - the clock rippled's own time gates read.
        /// </summary>
        /// <remarks>
        /// Wall-clock time is not a substitute. A standalone stand advances its ledger only when
        /// something calls <c>ledger_accept</c>, so its close time trails the machine's clock by
        /// however long the last ledger stayed open.
        /// </remarks>
        public static async Task<DateTime> ValidatedCloseTimeAsync(IXrplClient client)
        {
            LOLedger ledger = await client.Ledger(new LedgerRequest { LedgerIndex = new LedgerIndex(LedgerIndexType.Validated) }).Typed();
            LedgerEntity entity = (LedgerEntity)ledger.LedgerEntity;
            return entity.CloseTime ?? throw new InvalidOperationException("validated ledger has no close_time");
        }

        /// <summary>
        /// Waits until the validated close time is strictly past <paramref name="target"/>.
        /// </summary>
        /// <remarks>
        /// Strictly: rippled's time gates are <c>now &gt; mark</c> (<c>after()</c> in View.cpp), so a
        /// close time equal to the mark is still too early, and standalone close times move in
        /// coarse steps that land on equality readily.
        /// <para>
        /// Bounded, because a ledger that stops advancing is a node failure, and a test that waits
        /// on it forever reports nothing. The failure names the last close time seen and how far
        /// short of the mark it was, which separates a stalled node from a mark set too far ahead.
        /// </para>
        /// </remarks>
        public static async Task WaitForCloseTimeAsync(
            IXrplClient client,
            DateTime target,
            TestNodeType? nodeType = null,
            TimeSpan? budget = null)
        {
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            DateTime seenFirst = await ValidatedCloseTimeAsync(client);

            // The budget follows the wait rather than a constant, because the two are the same
            // quantity seen from opposite ends: a mark 180 s away cannot be reached inside 120 s,
            // and a fixed default turns that into "the node stalled". The slack covers the ledger
            // the mark has to be strictly passed by, plus the poll interval.
            TimeSpan limit = budget ?? (seenFirst >= target
                ? TimeSpan.FromSeconds(30)
                : target - seenFirst + TimeSpan.FromSeconds(60));

            while (true)
            {
                DateTime lastSeen = await ValidatedCloseTimeAsync(client);
                if (lastSeen > target)
                    return;

                if (elapsed.Elapsed >= limit)
                {
                    Assert.Fail(
                        $"the validated close time did not pass {target:O} within {limit.TotalSeconds:F0}s; " +
                        $"last seen {lastSeen:O}, short by {(target - lastSeen).TotalSeconds:F1}s");
                }

                // The standalone stand has a sidecar closing a ledger every few seconds, but a
                // stand raised without one would never move; on a public network this is a no-op.
                await LedgerAcceptAsync(client, nodeType);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        /// <summary>
        /// Returns true if running on a public network with faucet support.
        /// </summary>
        public static bool IsFaucetNetwork(TestNodeType? nodeType = null)
        {
            var type = nodeType ?? CurrentNodeType;
            return type == TestNodeType.TestNet || type == TestNodeType.DevNet;
        }

        /// <summary>
        /// Returns true if running on standalone node.
        /// </summary>
        public static bool IsStandalone(TestNodeType? nodeType = null)
        {
            var type = nodeType ?? CurrentNodeType;
            return type == TestNodeType.Standalone;
        }
    }

    /// <summary>
    /// Legacy utilities for standalone integration tests.
    /// Consider using IntegrationTestConfig for new tests.
    /// </summary>
    public class Utils
    {
        private static string masterAccount = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";
        private static string masterSecret = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";

        public static async Task LedgerAccept(IXrplClient client)
        {
            await IntegrationTestConfig.LedgerAcceptAsync(client);
        }

        public static async Task FundAccount(IXrplClient client, XrplWallet wallet)
        {
            if (!IntegrationTestConfig.IsStandalone())
            {
                await IntegrationTestConfig.FundWalletAsync(client, wallet);
                return;
            }

            await StandaloneLock.MasterFunding.WaitAsync();
            try
            {
                const int maxRetries = 3;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    Payment payment = new Payment
                    {
                        Account = masterAccount,
                        Destination = wallet.ClassicAddress,
                        Amount = new Currency { Value = "400000000", CurrencyCode = "XRP" }
                    };
                    var values = JsonSerializer.Deserialize<Dictionary<string, object>>(payment.ToJson(), global::Xrpl.Client.Json.XrplJsonOptions.Default);
                    var master = XrplWallet.FromSeed(masterSecret);
                    Submit response = await client.Submit(values, master);

                    if (response.EngineResult == "tesSUCCESS")
                    {
                        await LedgerAccept(client);
                        return;
                    }

                    if (response.EngineResult == "tefMAX_LEDGER" && attempt < maxRetries - 1)
                    {
                        Console.WriteLine($"[IntegrationTest] FundAccount got {response.EngineResult}, retrying ({attempt + 1}/{maxRetries})...");
                        await Task.Delay(1000);
                        continue;
                    }

                    throw new XrplException($"Response not successful, {response.EngineResult}");
                }
            }
            finally
            {
                StandaloneLock.MasterFunding.Release();
            }
        }

        public static async Task<XrplWallet> GenerateFundedWallet(IXrplClient client)
        {
            XrplWallet wallet = XrplWallet.Generate();
            await FundAccount(client, wallet);
            return wallet;
        }

        /// <summary>
        /// Waits until the transaction is in a ledger and checks the result recorded there.
        /// </summary>
        /// <remarks>
        /// This used to look the transaction up once, immediately after submission, and ignore
        /// what came back. On the standalone stand the caller had just forced a ledger close, so
        /// the lookup happened to succeed; on any network where ledgers close on their own it
        /// raced, and nothing was asserted about the result either way. It is a poll now, and the
        /// result the ledger recorded is checked - which is what the callers of
        /// <see cref="TestTransaction(IXrplClient, Dictionary{string, object}, XrplWallet)"/>
        /// were assumed to be verifying all along.
        /// </remarks>
        public static async Task VerifySubmittedTransaction(IXrplClient client, object tx, string? hashTx = null)
        {
            string hash;
            if (hashTx != null)
                hash = hashTx;
            else if (tx is string txStr)
                hash = HashLedger.HashSignedTx(txStr);
            else if (tx is JsonNode txNode)
                hash = HashLedger.HashSignedTx(txNode);
            else
                hash = HashLedger.HashSignedTx(JsonNode.Parse(
                    JsonSerializer.Serialize(tx, global::Xrpl.Client.Json.XrplJsonOptions.Default)));

            const int MaxAttempts = 20;
            for (int attempt = 1; ; attempt++)
            {
                string ledgerResult = null;
                try
                {
                    // Metadata is written when the transaction is applied in a ledger, so its
                    // presence is what separates a settled result from a provisional one
                    TransactionResponse data = await client.TxV1(new TxRequest(hash)).Typed();
                    ledgerResult = data.Meta?.TransactionResult;
                }
                catch (RippledException error) when (error.Message.Contains("txnNotFound"))
                {
                    // Submitted, not yet in a ledger this node will answer for
                }

                if (ledgerResult is not null)
                {
                    Assert.AreEqual("tesSUCCESS", ledgerResult, $"transaction {hash} was recorded with {ledgerResult}");
                    return;
                }

                if (attempt == MaxAttempts)
                {
                    Assert.Fail($"transaction {hash} did not reach a ledger after {MaxAttempts} lookups.");
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        public static async Task TestTransaction(IXrplClient client, Dictionary<string, object> transaction, XrplWallet wallet)
        {
            await LedgerAccept(client);
            Submit response = await client.Submit(transaction, wallet);
            Assert.AreEqual("tesSUCCESS", response.EngineResult);
            if (response.TxJson is JsonObject txJsonObj)
                txJsonObj.Remove("hash");
            await LedgerAccept(client);
            await VerifySubmittedTransaction(client, response.TxJson);
        }

        public static async Task TestTransaction(IXrplClient client, ITransactionRequest transaction, XrplWallet wallet)
        {
            await TestTransaction(client, transaction.ToDictionary(), wallet);
        }
    }
}
