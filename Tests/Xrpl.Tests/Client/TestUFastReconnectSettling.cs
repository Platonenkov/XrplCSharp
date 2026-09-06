using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

using Xrpl.Client;

namespace Xrpl.Tests
{
    /// <summary>
    /// The fast-reconnect path (<c>RetireCurrentSessionAndReconnectAsync</c>) runs inside the ping
    /// check that triggers it. Two things followed from that and went unnoticed because the path
    /// only shows its timing on a live node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It waited for the ping to finish - its own ping - and so waited out the whole
    /// <c>WaitForPingToFinishAsync</c> timeout (3 s) on every ping-triggered reconnect.
    /// </para>
    /// <para>
    /// And when its own connection attempt failed at the socket, the failure callback started the
    /// reconnect loop on the same cancellation source; the loop connected first, <c>OnceOpen</c>
    /// retired the source, the fast reconnect's wait came back cancelled, and its catch read that
    /// as a failure: it started a second loop, whose first attempt retired the live socket and
    /// opened another. One reconnect became two, with <c>RestoringConnection</c> reported on a
    /// client that was connected.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestUFastReconnectSettling
    {
        private XrplClient _client;

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

        private static Dictionary<string, object> EmptyResponse() => new Dictionary<string, object>
        {
            { "type", "response" },
            { "status", "success" },
            { "result", new Dictionary<string, object>() },
        };

        /// <summary>
        /// The two knobs that make the inactivity path reachable in under a second, plus a reconnect
        /// backoff short enough for a whole failed-then-succeeded sequence to fit in a test.
        /// </summary>
        private static XrplClient.ClientOptions FastReconnectOptions() => new XrplClient.ClientOptions
        {
            RequestPolicy = RequestFailurePolicy.ImmediateFail,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(100),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(500),
            ConnectionAcquisitionTimeout = TimeSpan.FromSeconds(5),
            ConnectionAttemptTimeout = TimeSpan.FromSeconds(3),
            UseCustomPing = true,
            HealthCheckInterval = TimeSpan.FromMilliseconds(200),
            InactivityTimeout = TimeSpan.FromMilliseconds(500),
        };

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
                    // Cleanup is not an assertion.
                }

                _client = null;
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.IsTrue(clock.Elapsed < timeout, $"Timed out after {timeout.TotalSeconds:F0}s waiting for: {what}");
                await Task.Delay(50);
            }
        }

        /// <summary>
        /// From the moment the health check hands a silent connection to the fast-reconnect path
        /// to the moment the old session is announced as ended, nothing has to wait for anything:
        /// the requests are swept, the socket is retired in the background. Three seconds in that
        /// gap is <c>WaitForPingToFinishAsync</c> timing out on the ping this path runs inside.
        /// </summary>
        [TestMethod]
        public async Task TestFastReconnectDoesNotWaitOutItsOwnPingTimeout()
        {
            using SilentOnPingServer server = new SilentOnPingServer();
            _client = new XrplClient(server.Url, FastReconnectOptions());

            Stopwatch clock = Stopwatch.StartNew();
            long restoringAt = -1;
            long sessionEndedAt = -1;
            object gate = new object();

            _client.OnConnectionStatus += info =>
            {
                if (info.ConnectionState != XrpConnectionState.RestoringConnection)
                {
                    return;
                }

                lock (gate)
                {
                    if (restoringAt < 0)
                    {
                        restoringAt = clock.ElapsedMilliseconds;
                    }
                }
            };

            _client.OnSessionEnded += (reason, description) =>
            {
                lock (gate)
                {
                    if (sessionEndedAt < 0)
                    {
                        sessionEndedAt = clock.ElapsedMilliseconds;
                    }
                }

                return Task.CompletedTask;
            };

            await _client.Connect();
            Assert.IsTrue(_client.connection.IsConnected(), "Precondition: the client must be connected.");

            await WaitUntilAsync(
                () => { lock (gate) { return sessionEndedAt >= 0; } },
                TimeSpan.FromSeconds(15),
                "the silent connection to be retired by the fast-reconnect path");

            long gap;
            lock (gate)
            {
                Assert.IsTrue(restoringAt >= 0, "RestoringConnection must have been reported before the session ended.");
                gap = sessionEndedAt - restoringAt;
            }

            Assert.IsTrue(
                gap < 2000,
                $"Retiring the session took {gap}ms after RestoringConnection was reported. The fast-reconnect " +
                "path waited out WaitForPingToFinishAsync's timeout for the ping check it is itself running in.");
        }

        /// <summary>
        /// A fast reconnect whose own attempt fails at the socket hands the sequence to the reconnect
        /// loop on the same source. When that loop connects, the fast reconnect is done - it must not
        /// read the cancellation of its wait as a failure and start reconnecting a connected client.
        /// </summary>
        [TestMethod]
        public async Task TestReconnectLoopSettlingAFastReconnectDoesNotReconnectAgain()
        {
            SilentOnPingServer silentServer = new SilentOnPingServer();
            CreateMockRippled replacement = null;
            int port = silentServer.Port;

            try
            {
                _client = new XrplClient(silentServer.Url, FastReconnectOptions());

                TaskCompletionSource<bool> loopIsDelaying = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                object gate = new object();
                bool retired = false;
                int connectedAfterRetire = 0;
                WebSocketClient socketAfterRetire = null;
                List<string> restoringAfterConnected = new List<string>();

                _client.OnConnectionStatus += info =>
                {
                    lock (gate)
                    {
                        switch (info.ConnectionState)
                        {
                            case XrpConnectionState.RestoringConnection when !retired:
                                // The health check just handed the silent connection to the
                                // fast-reconnect path, and this handler runs before its attempt
                                // starts. Take the server down now, so that attempt fails at the
                                // socket and the sequence falls to the reconnect loop.
                                retired = true;
                                silentServer.Dispose();
                                break;

                            case XrpConnectionState.RestoringConnection when connectedAfterRetire > 0:
                                restoringAfterConnected.Add(info.Message);
                                break;

                            case XrpConnectionState.RestoringConnection
                                when info.Message.StartsWith("Reconnecting in", StringComparison.Ordinal):
                                // The loop's first, immediate attempt failed too; it is now waiting
                                // out a backoff, which is the window to bring a server up in.
                                loopIsDelaying.TrySetResult(true);
                                break;

                            case XrpConnectionState.Connected when retired:
                                connectedAfterRetire++;
                                socketAfterRetire ??= _client.connection.ws;
                                break;
                        }
                    }
                };

                await _client.Connect();
                Assert.IsTrue(_client.connection.IsConnected(), "Precondition: the client must be connected.");

                Task finished = await Task.WhenAny(loopIsDelaying.Task, Task.Delay(TimeSpan.FromSeconds(15)));
                Assert.AreSame(loopIsDelaying.Task, finished, "The reconnect loop never reached a delayed attempt.");

                replacement = new CreateMockRippled(port) { suppressOutput = true };
                replacement.AddResponse("server_info", ServerInfoResponse());
                replacement.AddResponse("ping", EmptyResponse());
                replacement.Start();

                await WaitUntilAsync(
                    () => { lock (gate) { return connectedAfterRetire > 0; } },
                    TimeSpan.FromSeconds(15),
                    "the reconnect loop to connect to the replacement server");

                // Long enough for a second loop's first attempt (CalcBackoff(1) = 2 x base delay)
                // to have retired the socket and reconnected, had one been started.
                await Task.Delay(TimeSpan.FromSeconds(3));

                lock (gate)
                {
                    Assert.AreEqual(
                        0,
                        restoringAfterConnected.Count,
                        "RestoringConnection was reported on a connected client: " + string.Join(" | ", restoringAfterConnected));
                    Assert.AreEqual(
                        1,
                        connectedAfterRetire,
                        "The client connected more than once: the fast reconnect started a second loop after the first one had already connected.");
                    Assert.AreSame(
                        socketAfterRetire,
                        _client.connection.ws,
                        "The socket the loop opened was retired and replaced by a reconnect nobody needed.");
                }
            }
            finally
            {
                replacement?.Stop();
                silentServer.Dispose();
            }
        }
    }
}
