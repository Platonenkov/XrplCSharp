using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xrpl.Client;

namespace Xrpl.Tests
{
    /// <summary>
    /// Item 6 of issue #179: a send never reconnects, a send on a socket that is not open fails,
    /// and the failure is observable.
    /// </summary>
    /// <remarks>
    /// <c>SendMessageAsync</c> used to answer a socket that was not <c>Open</c> with
    /// <c>Connect()</c> - <c>ConnectAsync</c> on an already used <c>ClientWebSocket</c>, which
    /// throws, disposes the socket and raises <c>OnConnectionError</c> - and then went on to send
    /// regardless. The connection layer read that error as a failure of the connection.
    /// </remarks>
    [TestClass]
    public class TestUWebSocketClientSend
    {
        /// <summary>
        /// A socket the server closed is not open. Sending on it must report a send failure and
        /// nothing else - no connection error, no disposal.
        /// </summary>
        [TestMethod]
        public async Task TestSendOnAClosedSocketDoesNotReconnectIt()
        {
            using CloseAfterHandshakeServer server = new CloseAfterHandshakeServer(closeFirst: 1);
            WebSocketClient client = WebSocketClient.Create(server.Url);

            int connectionErrors = 0;
            int sendErrors = 0;
            TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.OnConnectionError((_, _) =>
            {
                Interlocked.Increment(ref connectionErrors);
                return Task.CompletedTask;
            });
            client.OnError((_, _) =>
            {
                Interlocked.Increment(ref sendErrors);
                return Task.CompletedTask;
            });
            client.OnDisconnect((_, _, _) =>
            {
                closed.TrySetResult(true);
                return Task.CompletedTask;
            });

            await client.Connect();

            Task finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(closed.Task, finished, "Precondition: the server must have closed the connection.");
            Assert.AreNotEqual(System.Net.WebSockets.WebSocketState.Open, client.State, "Precondition: the socket must not be open.");

            client.SendMessage("{\"command\":\"ping\"}");
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Assert.AreEqual(
                0,
                connectionErrors,
                "A send on a socket that is not open tried to reconnect it: ConnectAsync on a used socket threw, and the throw was reported as a connection error.");
            Assert.IsFalse(client.IsDisposed, "The send disposed the socket.");
            Assert.AreEqual(1, sendErrors, "The failed send must be reported through the error callback, once.");

            Exception failure = null;
            try
            {
                await client.SendMessageAsync(Encoding.UTF8.GetBytes("{\"command\":\"ping\"}"));
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOfType<InvalidOperationException>(
                failure,
                $"A send on a socket that is not open must fault, not {failure?.GetType().Name ?? "complete"}.");

            client.Dispose();
        }

        /// <summary>
        /// A socket that never connected has nothing to send into. The task says so; the
        /// fire-and-forget entry point reports it and does not throw.
        /// </summary>
        [TestMethod]
        public async Task TestSendOnASocketThatNeverConnectedFaults()
        {
            WebSocketClient client = WebSocketClient.Create("ws://127.0.0.1:1/");

            int sendErrors = 0;
            client.OnError((_, _) =>
            {
                Interlocked.Increment(ref sendErrors);
                return Task.CompletedTask;
            });

            Exception failure = null;
            try
            {
                await client.SendMessageAsync(Encoding.UTF8.GetBytes("x"));
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOfType<InvalidOperationException>(failure, $"Expected InvalidOperationException, got {failure?.GetType().Name ?? "no exception"}.");

            client.SendMessage("x");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(1, sendErrors, "The fire-and-forget send must report its failure through the error callback.");

            client.Dispose();
        }
    }
}
