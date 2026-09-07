using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Xrpl.Client;
using Xrpl.Client.Exceptions;

namespace Xrpl.Tests
{
    /// <summary>
    /// Regression tests for issue #179: a transition of the connection has one owner, and an
    /// operation that finds itself superseded stands down instead of overriding what came after
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four windows the issue lists are races between concurrent operations, and three of
    /// them are a handful of instructions wide - not something the public API can hit on demand.
    /// What it can do is issue the second operation from a callback the first one runs, which is
    /// the consumer shape the issue describes and which lands the second operation inside the
    /// first one's yields deterministically. Each test below is such a callback, and each asserts
    /// the outcome the issue asks for: the later operation wins, the earlier one reports that it
    /// lost, and nothing is done twice.
    /// </para>
    /// <para>
    /// The loop-exit window (<c>ReconnectLoopAsync</c> against <c>OnceClose</c>) is exercised in
    /// its reachable form: a server that closes the connection the moment the handshake completes,
    /// so the close lands while the loop is deciding whether its attempt succeeded. That does not
    /// open the exact gap - nothing from outside can - but it is the path through it, and a client
    /// that fails to come back here is the wedge the issue describes.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestUConnectionTransitionOwner
    {
        private CreateMockRippled _firstRippled;
        private CreateMockRippled _secondRippled;
        private CreateMockRippled _thirdRippled;
        private XrplClient _client;
        private int _firstPort;
        private int _secondPort;
        private int _thirdPort;

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

        private static CreateMockRippled StartMock(int port)
        {
            CreateMockRippled mock = new CreateMockRippled(port) { suppressOutput = true };
            mock.AddResponse("server_info", ServerInfoResponse());
            mock.AddResponse("ping", EmptyResponse());

            // Start() binds, listens and hands off to BeginAccept without blocking, so the port
            // is accepting when it returns - see TestUReconnectSessionRaces.
            mock.Start();
            return mock;
        }

        [TestInitialize]
        public void MyTestInitialize()
        {
            _firstPort = TestUtils.GetFreePort();
            _secondPort = TestUtils.GetFreePort();
            _thirdPort = TestUtils.GetFreePort();
            _firstRippled = StartMock(_firstPort);
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
                catch (Exception)
                {
                    // The client may already be down; cleanup must not mask the test result.
                }

                _client = null;
            }

            _firstRippled?.Stop();
            _secondRippled?.Stop();
            _thirdRippled?.Stop();
        }

        private static XrplClient.ClientOptions Options() => new XrplClient.ClientOptions
        {
            RequestPolicy = RequestFailurePolicy.ImmediateFail,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(50),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(400),
            MaxReconnectAttempts = 100,
            StopAfterMaxAttempts = false,
            ConnectionAcquisitionTimeout = TimeSpan.FromSeconds(10),
            ConnectionAttemptTimeout = TimeSpan.FromSeconds(5),
            UseCustomPing = false,
        };

        /// <summary>
        /// The health check hands a silent connection to the fast-reconnect path in well under a
        /// second with these - the same knobs <c>TestUFastReconnectSettling</c> uses.
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

        private static string UrlOf(int port) => $"ws://127.0.0.1:{port}";

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
        /// Window 4 of the issue. <c>ChangeServer</c> yields several times before it connects; a
        /// <c>Disconnect()</c> landing in one of those yields used to be overridden - the switch
        /// reset the disconnect and connected, and the client was online after the consumer had
        /// taken it down. Issued from the session-ended handler, the disconnect lands in exactly
        /// that yield every time.
        /// </summary>
        [TestMethod]
        public async Task TestDisconnectFromSessionEndedHandlerWinsOverChangeServer()
        {
            _secondRippled = StartMock(_secondPort);
            _client = new XrplClient(UrlOf(_firstPort), Options());
            await _client.Connect();
            Assert.IsTrue(_client.connection.IsConnected(), "Precondition: connected to the first server.");

            int connectedAfterSwitch = 0;
            bool switching = false;

            _client.OnSessionEnded += async (reason, _) =>
            {
                if (reason == SessionEndReason.ServerChanged)
                {
                    await _client.Disconnect();
                }
            };
            _client.OnConnected += () =>
            {
                if (Volatile.Read(ref switching))
                {
                    Interlocked.Increment(ref connectedAfterSwitch);
                }

                return Task.CompletedTask;
            };

            Volatile.Write(ref switching, true);
            Exception failure = null;
            try
            {
                await _client.connection.ChangeServer(UrlOf(_secondPort));
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOfType<NotConnectedException>(
                failure,
                $"A ChangeServer that a Disconnect() overtook must say the client is disconnected, not {failure?.GetType().Name ?? "return normally"}.");

            // Room for the connection nobody should be making.
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.IsFalse(_client.connection.IsConnected(), "The client is online after the consumer disconnected it: ChangeServer overrode the Disconnect().");
            Assert.IsNull(_client.connection.ws, "A socket is installed after the consumer disconnected the client.");
            Assert.AreEqual(XrpConnectionState.Disconnected, _client.connection.CurrentConnectionState);
            Assert.AreEqual(0, connectedAfterSwitch, "OnConnected was raised for a switch the consumer cancelled with Disconnect().");
        }

        /// <summary>
        /// Two switches, the second issued from the first one's session-ended handler. The first
        /// used to return success after the second had connected elsewhere - and, worse, wrote its
        /// own target into the url afterwards, so the client reported a server it was not on.
        /// </summary>
        [TestMethod]
        public async Task TestLaterChangeServerFromSessionEndedHandlerSupersedesTheEarlierOne()
        {
            _secondRippled = StartMock(_secondPort);
            _thirdRippled = StartMock(_thirdPort);
            _client = new XrplClient(UrlOf(_firstPort), Options());
            await _client.Connect();
            Assert.IsTrue(_client.connection.IsConnected(), "Precondition: connected to the first server.");

            int connectedAfterSwitch = 0;
            int nested = 0;
            bool switching = false;
            Exception nestedFailure = null;

            _client.OnSessionEnded += async (reason, _) =>
            {
                if (reason == SessionEndReason.ServerChanged && Interlocked.Exchange(ref nested, 1) == 0)
                {
                    try
                    {
                        await _client.connection.ChangeServer(UrlOf(_thirdPort));
                    }
                    catch (Exception error)
                    {
                        nestedFailure = error;
                    }
                }
            };
            _client.OnConnected += () =>
            {
                if (Volatile.Read(ref switching))
                {
                    Interlocked.Increment(ref connectedAfterSwitch);
                }

                return Task.CompletedTask;
            };

            Volatile.Write(ref switching, true);
            Exception failure = null;
            try
            {
                await _client.connection.ChangeServer(UrlOf(_secondPort));
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsNull(nestedFailure, $"The later switch is the one that should have won, but it failed: {nestedFailure}");
            Assert.IsInstanceOfType<OperationCanceledException>(
                failure,
                $"A ChangeServer that a later ChangeServer overtook must report that it was superseded, not {failure?.GetType().Name ?? "return normally"}.");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.IsTrue(_client.connection.IsConnected(), "The client must be connected after the later switch.");
            Assert.AreEqual(UrlOf(_thirdPort), _client.connection.GetUrl(), "The client reports a server other than the one it is connected to.");
            Assert.AreEqual(1, connectedAfterSwitch, "The client connected more than once for two switches of which only the later one should have connected.");
        }

        /// <summary>
        /// Window 3 of the issue, in its reachable form. A status handler that answers
        /// <c>RestoringConnection</c> with a switch to another server starts a transition of its
        /// own from inside the fast reconnect. The fast reconnect used to carry on regardless -
        /// its attempt was cancelled underneath it, it read that as a failure and started a
        /// reconnect loop, reporting "Reconnection failed" for a switch that was under way. Now
        /// it stands down at the check that follows the callback.
        /// </summary>
        [TestMethod]
        public async Task TestChangeServerFromRestoringConnectionHandlerSupersedesTheFastReconnect()
        {
            using SilentOnPingServer silentServer = new SilentOnPingServer();
            _secondRippled = StartMock(_secondPort);
            _client = new XrplClient(silentServer.Url, FastReconnectOptions());

            object gate = new object();
            bool switched = false;
            int connectedAfterSwitch = 0;
            List<string> reconnectFailures = new List<string>();
            List<string> restoringAfterConnected = new List<string>();

            _client.OnConnectionStatus += info =>
            {
                lock (gate)
                {
                    if (info.ConnectionState == XrpConnectionState.RestoringConnection && !switched)
                    {
                        // The health check just handed the silent connection to the fast-reconnect
                        // path. Move the client elsewhere from inside its own notification.
                        switched = true;
                        _ = _client.connection.ChangeServer(UrlOf(_secondPort));
                        return;
                    }

                    if (info.ConnectionState == XrpConnectionState.RestoringConnection && switched)
                    {
                        if (info.Message.StartsWith("Reconnection failed", StringComparison.Ordinal))
                        {
                            reconnectFailures.Add(info.Message);
                        }

                        if (connectedAfterSwitch > 0)
                        {
                            restoringAfterConnected.Add(info.Message);
                        }
                    }

                    if (info.ConnectionState == XrpConnectionState.Connected && switched)
                    {
                        connectedAfterSwitch++;
                    }
                }
            };

            await _client.Connect();
            Assert.IsTrue(_client.connection.IsConnected(), "Precondition: connected to the silent server.");

            await WaitUntilAsync(
                () => { lock (gate) { return connectedAfterSwitch > 0; } },
                TimeSpan.FromSeconds(15),
                "the switch issued from the RestoringConnection handler to connect");

            // Long enough for a reconnect loop the fast reconnect should not have started to
            // report itself.
            await Task.Delay(TimeSpan.FromSeconds(3));

            lock (gate)
            {
                Assert.AreEqual(
                    0,
                    reconnectFailures.Count,
                    "The fast reconnect reported a failure of its own attempt after a switch from its status handler had taken the connection over: " + string.Join(" | ", reconnectFailures));
                Assert.AreEqual(
                    0,
                    restoringAfterConnected.Count,
                    "RestoringConnection was reported on a client the switch had connected: " + string.Join(" | ", restoringAfterConnected));
                Assert.AreEqual(1, connectedAfterSwitch, "The client connected more than once after the switch.");
            }

            Assert.IsTrue(_client.connection.IsConnected(), "The client must be connected to the server the handler switched to.");
            Assert.AreEqual(UrlOf(_secondPort), _client.connection.GetUrl());
        }

        /// <summary>
        /// Window 1 of the issue, in its reachable form: the reconnect loop's attempt succeeds and
        /// the connection closes at once, so the close is processed while the loop is deciding
        /// whether it is done. The loop and the close callback have to agree on who reconnects;
        /// if neither does, the client stays down with a server that is up.
        /// </summary>
        [TestMethod]
        public async Task TestReconnectLoopSurvivesACloseRightAfterItsAttemptOpened()
        {
            using CloseAfterHandshakeServer server = new CloseAfterHandshakeServer(closeFirst: 3);
            _client = new XrplClient(server.Url, Options());

            // Straight to the connection: the client's Connect() adds a server_info read, and the
            // closing connections would have that failing for reasons that are not this test's.
            await _client.connection.Connect(CancellationToken.None);

            // Connect() returns as soon as a socket is open, which can be before the server's close
            // frame for it has even arrived - so the outcome is waited for, not asserted at once:
            // the client must end up connected on a connection the server did not close.
            await WaitUntilAsync(
                () => server.Connections >= 4 && _client.connection.IsConnected(),
                TimeSpan.FromSeconds(15),
                $"the client to reach the server after it stopped closing connections (state: {_client.connection.CurrentConnectionState}, connections seen: {server.Connections})");

            Dictionary<string, object> response = await _client.connection
                .Request(new Dictionary<string, object> { { "command", "server_info" } })
                .Typed();
            Assert.IsNotNull(response, "The client must be usable on the connection the loop ended on.");
        }

        /// <summary>
        /// A <c>Disconnect()</c> issued while a handshake is pending must leave nothing behind: not
        /// the socket that was connecting, and not a connection it would have made.
        /// </summary>
        [TestMethod]
        public async Task TestDisconnectDuringAPendingHandshakeLeavesNoSocketBehind()
        {
            // Accepts TCP and never answers the upgrade: the handshake stays pending.
            using TcpListener silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;

            _client = new XrplClient(UrlOf(port), Options());

            Task connecting = _client.connection.Connect(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.IsFalse(connecting.IsCompleted, "Precondition: the handshake must still be pending.");

            await _client.Disconnect();

            Exception failure = null;
            try
            {
                await connecting;
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOfType<NotConnectedException>(
                failure,
                $"A Connect() that a Disconnect() overtook must say the client is disconnected, not {failure?.GetType().Name ?? "return normally"}.");
            Assert.IsNull(_client.connection.ws, "The socket that was connecting is still installed after Disconnect().");
            Assert.AreEqual(XrpConnectionState.Disconnected, _client.connection.CurrentConnectionState);
        }

        /// <summary>
        /// A <c>Disconnect()</c> from inside the <c>OnConnected</c> handler must be the last word:
        /// the connect path that ran the handler used to report <c>Connected</c> on top of it and
        /// start a ping timer nothing would ever stop.
        /// </summary>
        [TestMethod]
        public async Task TestDisconnectFromConnectedHandlerIsNotOverriddenByConnected()
        {
            _client = new XrplClient(UrlOf(_firstPort), Options());

            object gate = new object();
            List<XrpConnectionState> statesAfterDisconnect = new List<XrpConnectionState>();
            bool disconnected = false;

            _client.OnConnectionStatus += info =>
            {
                lock (gate)
                {
                    if (disconnected)
                    {
                        statesAfterDisconnect.Add(info.ConnectionState);
                    }
                }
            };
            _client.OnConnected += async () =>
            {
                await _client.Disconnect();
                lock (gate)
                {
                    disconnected = true;
                }
            };

            try
            {
                await _client.connection.Connect(CancellationToken.None);
            }
            catch (Exception)
            {
                // Expected: the handler disconnected the client it was connecting.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Assert.IsFalse(_client.connection.IsConnected(), "The client is connected after its OnConnected handler disconnected it.");
            Assert.AreEqual(XrpConnectionState.Disconnected, _client.connection.CurrentConnectionState, "The connect path reported Connected over the handler's Disconnect().");
            Assert.IsFalse(_client.connection.IsPingTimerRunning, "A ping timer was started for a connection the handler had already taken down.");
            lock (gate)
            {
                Assert.IsFalse(
                    statesAfterDisconnect.Contains(XrpConnectionState.Connected),
                    "Connected was reported after the handler's Disconnect(): " + string.Join(", ", statesAfterDisconnect));
            }
        }

        /// <summary>
        /// <c>Connect()</c> after a user <c>Disconnect()</c>, to a server that is down, must leave
        /// the client reconnecting. The intentional-disconnect flag the disconnect left behind
        /// used to make the failed handshake look like a user disconnect - "closed permanently",
        /// no loop - and <c>ChangeServer</c> was the only path that cleared it.
        /// </summary>
        [TestMethod]
        public async Task TestConnectAfterUserDisconnectReconnectsWhenTheServerComesUp()
        {
            XrplClient.ClientOptions options = Options();
            options.ConnectionAcquisitionTimeout = TimeSpan.FromSeconds(3);
            options.ConnectionAttemptTimeout = TimeSpan.FromSeconds(3);

            // A client pointed at a port where nothing listens yet. Disconnect() on a client that
            // never connected still records a user disconnect, which is all the flag needs.
            _client = new XrplClient(UrlOf(_secondPort), options);
            await _client.Disconnect();

            // The attempt fails, and the client must be left reconnecting, not "closed permanently".
            try
            {
                await _client.connection.Connect(CancellationToken.None);
            }
            catch (Exception)
            {
                // Expected - nothing is listening there yet.
            }

            Assert.AreNotEqual(
                XrpConnectionState.Disconnected,
                _client.connection.CurrentConnectionState,
                "A server that is down is a connection failure, not a permanent disconnect - the flag of the earlier Disconnect() was read as this connection's.");

            Assert.IsTrue(
                TestUtils.IsPortStillFree(_secondPort),
                $"Port {_secondPort} was taken by another process while the test held it — rerun.");
            _secondRippled = StartMock(_secondPort);

            await WaitUntilAsync(
                () => _client.connection.IsConnected(),
                TimeSpan.FromSeconds(30),
                "the client to reconnect after the server came up");
        }

        /// <summary>
        /// A <c>ChangeServer</c> that a <c>Disconnect()</c> overtakes before the switch is announced
        /// still owes the consumer the end of the session it retired: the retirement silenced the
        /// socket's own close callback, and the disconnect does not know the session.
        /// </summary>
        [TestMethod]
        public async Task TestChangeServerSupersededBeforeAnnouncingStillAnnouncesTheSessionEnd()
        {
            _secondRippled = StartMock(_secondPort);
            _client = new XrplClient(UrlOf(_firstPort), Options());
            await _client.Connect();

            object gate = new object();
            List<SessionEndReason> ended = new List<SessionEndReason>();
            bool disconnecting = false;

            _client.OnSessionEnded += (reason, _) =>
            {
                lock (gate)
                {
                    ended.Add(reason);
                }

                return Task.CompletedTask;
            };
            _client.OnConnectionStatus += info =>
            {
                // The first thing ChangeServer reports, before it announces the session end. A
                // Disconnect() from here takes over before the announcement.
                if (info.ConnectionState == XrpConnectionState.Connecting &&
                    info.Message.StartsWith("ChangeServer", StringComparison.Ordinal) &&
                    !disconnecting)
                {
                    disconnecting = true;
                    _ = _client.Disconnect();
                }
            };

            Exception failure = null;
            try
            {
                await _client.connection.ChangeServer(UrlOf(_secondPort));
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOfType<NotConnectedException>(failure, $"Expected NotConnectedException, got {failure?.GetType().Name ?? "no exception"}.");

            await Task.Delay(TimeSpan.FromSeconds(1));

            lock (gate)
            {
                Assert.AreEqual(1, ended.Count, "OnSessionEnded must be raised exactly once for the session the switch retired: " + string.Join(", ", ended));
                Assert.AreEqual(SessionEndReason.ServerChanged, ended[0]);
            }

            Assert.IsFalse(_client.connection.IsConnected(), "The Disconnect() issued from the status handler must win.");
        }

        /// <summary>
        /// A fast reconnect whose loop ran out of attempts must not start a second series: the
        /// loop reported <c>Disconnected</c> and released its source, and the fast reconnect's own
        /// wait ends with the "failed permanently" refusal. Read as a failure to retry, that
        /// refusal used to buy another full run of attempts, and <c>StopAfterMaxAttempts</c> meant
        /// nothing.
        /// </summary>
        [TestMethod]
        public async Task TestFastReconnectDoesNotRestartAfterTheLoopGaveUp()
        {
            SilentOnPingServer silentServer = new SilentOnPingServer();
            try
            {
                XrplClient.ClientOptions options = FastReconnectOptions();
                options.StopAfterMaxAttempts = true;
                options.MaxReconnectAttempts = 2;
                options.ConnectionAcquisitionTimeout = TimeSpan.FromSeconds(20);

                _client = new XrplClient(silentServer.Url, options);

                object gate = new object();
                bool retired = false;
                int stoppedReports = 0;
                List<string> restoringAfterStopped = new List<string>();

                _client.OnConnectionStatus += info =>
                {
                    lock (gate)
                    {
                        if (info.ConnectionState == XrpConnectionState.RestoringConnection && !retired)
                        {
                            // The health check handed the silent connection to the fast reconnect;
                            // take the server down so every attempt fails at the socket.
                            retired = true;
                            silentServer.Dispose();
                            return;
                        }

                        if (info.ConnectionState == XrpConnectionState.Disconnected &&
                            info.Message.StartsWith("Reconnection stopped", StringComparison.Ordinal))
                        {
                            stoppedReports++;
                            return;
                        }

                        if (info.ConnectionState == XrpConnectionState.RestoringConnection && stoppedReports > 0)
                        {
                            restoringAfterStopped.Add(info.Message);
                        }
                    }
                };

                await _client.Connect();
                Assert.IsTrue(_client.connection.IsConnected(), "Precondition: connected to the silent server.");

                await WaitUntilAsync(
                    () => { lock (gate) { return stoppedReports > 0; } },
                    TimeSpan.FromSeconds(20),
                    "the reconnect loop to give up after MaxReconnectAttempts");

                // Room for a second series to report itself, had one been started.
                await Task.Delay(TimeSpan.FromSeconds(3));

                lock (gate)
                {
                    Assert.AreEqual(
                        0,
                        restoringAfterStopped.Count,
                        "RestoringConnection was reported after the loop had given up: " + string.Join(" | ", restoringAfterStopped));
                    Assert.AreEqual(1, stoppedReports, "The loop gave up more than once - a second series ran.");
                }

                Assert.AreEqual(XrpConnectionState.Disconnected, _client.connection.CurrentConnectionState);
            }
            finally
            {
                silentServer.Dispose();
            }
        }

        /// <summary>
        /// <c>Disconnect()</c> then <c>Connect()</c> at once - the ordinary way to bounce a client.
        /// The disconnect closes its socket in the background, and the close callback is what
        /// announces <see cref="SessionEndReason.UserDisconnected"/>; a <c>Connect()</c> that
        /// retired that session underneath would silence the callback and announce a loss of its
        /// own instead.
        /// </summary>
        [TestMethod]
        public async Task TestConnectRightAfterDisconnectKeepsTheUserDisconnectAnnouncement()
        {
            _client = new XrplClient(UrlOf(_firstPort), Options());
            await _client.Connect();

            object gate = new object();
            List<SessionEndReason> ended = new List<SessionEndReason>();

            _client.OnSessionEnded += (reason, _) =>
            {
                lock (gate)
                {
                    ended.Add(reason);
                }

                return Task.CompletedTask;
            };

            await _client.Disconnect();
            await _client.Connect();
            Assert.IsTrue(_client.connection.IsConnected(), "The client must be connected again.");

            await Task.Delay(TimeSpan.FromSeconds(1));

            lock (gate)
            {
                Assert.AreEqual(1, ended.Count, "The session must end exactly once: " + string.Join(", ", ended));
                Assert.AreEqual(SessionEndReason.UserDisconnected, ended[0], "The session ended because the consumer disconnected, and the announcement must say so.");
            }
        }

        /// <summary>
        /// After a <c>Disconnect()</c> that took a handshake still in flight, a
        /// <c>DisconnectAndWaitAsync</c> must return at once: the cancelled handshake reports no
        /// close, so there is nothing to wait for - and a completion source installed for it would
        /// never complete.
        /// </summary>
        [TestMethod]
        public async Task TestDisconnectAndWaitAfterADisconnectDuringHandshakeReturnsAtOnce()
        {
            using TcpListener silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;

            _client = new XrplClient(UrlOf(port), Options());

            Task connecting = _client.connection.Connect(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await _client.Disconnect();
            try
            {
                await connecting;
            }
            catch (Exception)
            {
                // Expected: the handshake was cancelled by the disconnect.
            }

            Stopwatch clock = Stopwatch.StartNew();
            await _client.DisconnectAndWaitAsync(TimeSpan.FromSeconds(5));
            clock.Stop();

            Assert.IsTrue(
                clock.Elapsed < TimeSpan.FromSeconds(2),
                $"DisconnectAndWaitAsync on a disconnected client waited {clock.Elapsed.TotalSeconds:F1}s - on a completion source nobody completes.");
        }

        /// <summary>
        /// Item 5 of the issue. Under <see cref="RequestFailurePolicy.ImmediateFail"/> a request
        /// refused for want of a connection used to carry the runtime's default message, which a
        /// consumer classifying failures by text could not recognise.
        /// </summary>
        [TestMethod]
        public async Task TestImmediateFailRefusalSaysTheClientIsNotConnected()
        {
            XrplClient.ClientOptions options = Options();
            options.ConnectionAcquisitionTimeout = TimeSpan.FromSeconds(2);
            options.ConnectionAttemptTimeout = TimeSpan.FromSeconds(2);

            _client = new XrplClient(UrlOf(_firstPort), options);
            await _client.Connect();

            // A switch to a port nobody listens on leaves the client reconnecting - the state in
            // which a request reaches the policy at all.
            try
            {
                await _client.connection.ChangeServer(UrlOf(_secondPort));
            }
            catch (Exception)
            {
                // Expected: nothing is listening there.
            }

            Exception failure = null;
            try
            {
                await _client.connection.Request(new Dictionary<string, object> { { "command", "server_info" } });
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOfType<NotConnectedException>(failure, $"Expected NotConnectedException, got {failure?.GetType().Name ?? "no exception"}.");
            StringAssert.Contains(failure.Message, "not connected", StringComparison.OrdinalIgnoreCase);
            Assert.IsFalse(
                failure.Message.Contains("Exception of type", StringComparison.Ordinal),
                "The refusal carries the runtime's default message instead of saying what happened.");

            Assert.AreEqual(
                NotConnectedException.DefaultMessage,
                new NotConnectedException().Message,
                "A NotConnectedException built without a message must still say what it is.");
        }
    }
}
