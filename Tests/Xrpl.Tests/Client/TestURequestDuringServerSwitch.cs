using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;

using System.Threading.Tasks;

using Xrpl.Client;

using TimeoutException = Xrpl.Client.Exceptions.TimeoutException;

namespace Xrpl.Tests
{
    /// <summary>
    /// Regression tests for issue #177 - a request created while the client is retiring its
    /// connection is written to the socket that is being retired and nothing ever completes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every retirement path (<c>ChangeServer</c>, the ping/network fast reconnect,
    /// <c>Disconnect</c>, <c>DisconnectAndWaitAsync</c>, the failed-OnConnected-handler path) used
    /// to sweep the pending requests with <c>RejectAllWithCancellation()</c> and only afterwards
    /// clear <c>ws</c>. Between those two points <c>ShouldBeConnected()</c> still reported the
    /// retired socket as usable, so a request issued there passed the connectivity check and was
    /// sent into a socket that was on its way out. The sweep had already run, so nothing rejected
    /// it; the send is <c>async void</c> and report-only, so a failure did not reject it either.
    /// The caller waited out the whole <c>RequestTimeout</c>. The fix clears <c>ws</c> before the
    /// sweep on every one of those paths.
    /// </para>
    /// <para>
    /// The window is not a thread race that has to be won by luck. <c>RequestManager</c> builds its
    /// <see cref="TaskCompletionSource{TResult}"/> without
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so on a thread pool
    /// context the consumer's continuation runs <b>inline, inside the sweep itself</b> - which is
    /// what these tests exercise. Under a single-threaded synchronization context (Blazor
    /// WebAssembly, where this was observed) the continuation is posted instead and lands on the
    /// first real yield of the retirement path, still ahead of the <c>ws</c> clear whenever a ping
    /// is in flight.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestURequestDuringServerSwitch
    {
        /// <summary>
        /// Deliberately short so a request that falls into the window fails the test in seconds
        /// instead of the 40s production default - the assertions below are about the request not
        /// waiting this out at all.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// What a correctly handled request may take: it either fails fast or is carried over to
        /// the new connection. Comfortably below <see cref="RequestTimeout"/> so the two outcomes
        /// cannot be confused.
        /// </summary>
        private static readonly TimeSpan AcceptableBound = TimeSpan.FromSeconds(4);

        private CreateMockRippled _firstRippled;
        private CreateMockRippled _secondRippled;
        private XrplClient _client;
        private int _firstPort;
        private int _secondPort;

        private static Dictionary<string, object> ServerInfoResponse() => new Dictionary<string, object>
        {
            { "type", "response" },
            { "status", "success" },
            { "result", new Dictionary<string, object>
                {
                    { "info", new Dictionary<string, object>
                        {
                            { "build_version", "test-mock" },
                            { "complete_ledgers", "1-1" },
                            { "server_state", "full" },
                        }
                    },
                }
            },
        };

        private static Dictionary<string, object> AccountInfoResponse() => new Dictionary<string, object>
        {
            { "type", "response" },
            { "status", "success" },
            { "result", new Dictionary<string, object>
                {
                    { "account_data", new Dictionary<string, object>
                        {
                            { "Account", "rTestAccountForIssue177000000000000" },
                            { "Balance", "1000000" },
                            { "Sequence", 1 },
                        }
                    },
                }
            },
        };

        private static Dictionary<string, object> AccountInfoRequest() => new Dictionary<string, object>
        {
            { "command", "account_info" },
            { "account", "rTestAccountForIssue177000000000000" },
        };

        [TestInitialize]
        public void MyTestInitialize()
        {
            _firstPort = TestUtils.GetFreePort();
            _secondPort = TestUtils.GetFreePort();

            // The first node answers server_info but sits on account_info far longer than the test
            // runs: that is how a request is kept pending so the retirement sweep has something to
            // reject, and the rejection is what hands control back to the consumer.
            _firstRippled = new CreateMockRippled(_firstPort) { suppressOutput = true };
            _firstRippled.AddResponse("server_info", ServerInfoResponse());
            _firstRippled.AddDelayedResponse("account_info", AccountInfoResponse(), TimeSpan.FromMinutes(5));
            _firstRippled.Start();

            // The second node answers everything at once - a request carried over to it must come
            // back quickly.
            _secondRippled = new CreateMockRippled(_secondPort) { suppressOutput = true };
            _secondRippled.AddResponse("server_info", ServerInfoResponse());
            _secondRippled.AddResponse("account_info", AccountInfoResponse());
            _secondRippled.Start();
        }

        [TestCleanup]
        public async Task MyTestCleanup()
        {
            if (_client != null)
            {
                try
                {
                    await _client.Disconnect();
                }
                catch
                {
                    // The test may have left the client mid-switch; cleanup is not an assertion.
                }

                _client = null;
            }

            _firstRippled?.Stop();
            _secondRippled?.Stop();
        }

        private XrplClient CreateClient(int port) => new XrplClient(
            $"ws://127.0.0.1:{port}",
            new XrplClient.ClientOptions
            {
                RequestTimeout = RequestTimeout,
                RequestPolicy = RequestFailurePolicy.WaitForConnection,
                ConnectionAcquisitionTimeout = TimeSpan.FromSeconds(5),
                ConnectionAttemptTimeout = TimeSpan.FromSeconds(5),
                ReconnectBaseDelay = TimeSpan.FromMilliseconds(100),
                ReconnectMaxDelay = TimeSpan.FromSeconds(1),
                UseCustomPing = false,
                UseCheckHealth = false,
            });

        /// <summary>
        /// Starts a request that the first node will not answer and returns once it is actually
        /// pending in the request manager, so the retirement sweep is guaranteed to find it.
        /// </summary>
        private async Task<Task<XrplResponse<Dictionary<string, object>>>> StartPendingRequestAsync()
        {
            Task<XrplResponse<Dictionary<string, object>>> pending =
                _client.connection.Request(AccountInfoRequest());

            // Give the send a moment to leave; nothing observable marks "in flight", and the
            // request only has to exist as a promise for the sweep to reach it.
            await Task.Delay(300);

            Assert.IsFalse(
                pending.IsCompleted,
                "Precondition: the first node must leave this request unanswered.");

            return pending;
        }

        /// <summary>
        /// A request issued from the rejection continuation of a swept request - the position the
        /// issue describes - must not be written into the socket <c>ChangeServer</c> is retiring.
        /// </summary>
        [TestMethod]
        public async Task TestRequestIssuedWhileChangingServerDoesNotHang()
        {
            _client = CreateClient(_firstPort);
            await _client.Connect();
            Assert.IsTrue(_client.connection.IsConnected(), "Precondition: connected to the first node.");

            Task<XrplResponse<Dictionary<string, object>>> pending = await StartPendingRequestAsync();

            // The socket the client is about to retire. Captured so the test can say which socket
            // the follow-up request actually saw, rather than only that it hung.
            WebSocketClient retiredSocket = _client.connection.ws;
            Assert.IsNotNull(retiredSocket, "Precondition: a live socket to retire.");

            Task<XrplResponse<Dictionary<string, object>>> followUp = null;
            WebSocketClient socketSeenByFollowUp = null;

            // ExecuteSynchronously, not an await: the continuation has to run on the thread that
            // completes the promise, which is the thread inside the retirement sweep. That is the
            // consumer shape the issue reports - a second value read from the response handler of
            // the first - and it is what puts the follow-up request in the window.
            Task continuation = pending.ContinueWith(
                _ =>
                {
                    socketSeenByFollowUp = _client.connection.ws;
                    followUp = _client.connection.Request(AccountInfoRequest());
                },
                TaskContinuationOptions.ExecuteSynchronously);

            await _client.connection.ChangeServer($"ws://127.0.0.1:{_secondPort}");

            await continuation;
            Assert.IsNotNull(followUp, "The rejection of the first request must have issued a follow-up.");

            Assert.AreNotSame(
                retiredSocket,
                socketSeenByFollowUp,
                "A request issued during the switch still saw the socket being retired as the active one - " +
                "the connectivity check it passed was about a socket that was already on its way out.");

            Task finished = await Task.WhenAny(followUp, Task.Delay(AcceptableBound));

            Assert.AreSame(
                followUp,
                finished,
                $"A request issued while ChangeServer was retiring the old socket is still pending after " +
                $"{AcceptableBound.TotalSeconds:F0}s. It was written to the retired socket and nothing will " +
                $"complete it before RequestTimeout ({RequestTimeout.TotalSeconds:F0}s) expires.");

            // Either outcome is correct: sent on the new connection (it completed within the bound,
            // which is all a success has to show), or refused outright. What is not correct is
            // waiting out RequestTimeout. A failure is captured first and judged afterwards, so an
            // assertion failure is reported as itself rather than caught here.
            Exception failure = null;
            try
            {
                await followUp;
            }
            catch (Exception error)
            {
                failure = error;
            }

            if (failure is not null)
            {
                Assert.IsNotInstanceOfType<TimeoutException>(
                    failure,
                    "The follow-up request waited out RequestTimeout instead of being handled.");
                Assert.IsInstanceOfType<Xrpl.Client.Exceptions.NotConnectedException>(
                    failure,
                    $"A refused follow-up must say the client is not connected, not fail with {failure.GetType().Name}: {failure.Message}");
            }
        }

        /// <summary>
        /// The same window on the user disconnect path. Here there is no new connection to carry
        /// the request over to, so the only correct outcome is an immediate refusal.
        /// </summary>
        /// <remarks>
        /// Not regression coverage for the ordering - this passed before the fix too. On this
        /// path the socket is really closed, so its close callback runs the second sweep in
        /// <c>OnceClose</c>, which happened to catch the follow-up. <c>ChangeServer</c> filters that
        /// callback out as a retiring session, which is why only the test above turned red. Kept
        /// so that the refusal stays immediate if that incidental second sweep ever goes away.
        /// </remarks>
        [TestMethod]
        public async Task TestRequestIssuedWhileDisconnectingFailsFast()
        {
            _client = CreateClient(_firstPort);
            await _client.Connect();
            Assert.IsTrue(_client.connection.IsConnected(), "Precondition: connected to the first node.");

            Task<XrplResponse<Dictionary<string, object>>> pending = await StartPendingRequestAsync();

            Task<XrplResponse<Dictionary<string, object>>> followUp = null;

            Task continuation = pending.ContinueWith(
                _ => { followUp = _client.connection.Request(AccountInfoRequest()); },
                TaskContinuationOptions.ExecuteSynchronously);

            await _client.Disconnect();
            _client = null; // Disconnected already; keep cleanup from doing it twice.

            await continuation;
            Assert.IsNotNull(followUp, "The rejection of the first request must have issued a follow-up.");

            Task finished = await Task.WhenAny(followUp, Task.Delay(AcceptableBound));

            Assert.AreSame(
                followUp,
                finished,
                $"A request issued while Disconnect() was retiring the socket is still pending after " +
                $"{AcceptableBound.TotalSeconds:F0}s - it went into the socket being closed and waits out " +
                $"RequestTimeout ({RequestTimeout.TotalSeconds:F0}s).");

            // Captured first, judged afterwards - see the test above.
            Exception failure = null;
            try
            {
                await followUp;
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsNotNull(failure, "A request issued during Disconnect() must not succeed - there is no connection to serve it.");
            Assert.IsNotInstanceOfType<TimeoutException>(
                failure,
                "The follow-up request waited out RequestTimeout instead of being refused.");
            Assert.IsTrue(
                failure is Xrpl.Client.Exceptions.NotConnectedException
                    or Xrpl.Client.Exceptions.DisconnectedException
                    or OperationCanceledException,
                $"A request issued during Disconnect() must be refused as not connected, not {failure.GetType().Name}: {failure.Message}");
        }
    }
}
