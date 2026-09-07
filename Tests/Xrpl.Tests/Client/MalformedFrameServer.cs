using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Xrpl.Tests
{
    /// <summary>
    /// WebSocket server that, on its first <c>poisonFirst</c> connections, answers the first
    /// request with a frame the protocol forbids - a reserved opcode - and serves every
    /// connection after that normally.
    /// </summary>
    /// <remarks>
    /// A forbidden frame makes the client's <c>ReceiveAsync</c> throw a
    /// <see cref="System.Net.WebSockets.WebSocketException"/> that is not a network error, on a
    /// connection that was established and in use. That is the failure shape the receive loop used
    /// to report twice - once as a handshake-style connection error, once as a close - and this
    /// server is how a test gets one on demand. A dropped TCP connection cannot stand in for it:
    /// that arrives as a network error and takes the other branch.
    /// </remarks>
    internal sealed class MalformedFrameServer : WebSocketTestServerBase
    {
        private const string ServerInfoEnvelope =
            "{\"id\":__ID__,\"status\":\"success\",\"type\":\"response\",\"result\":{\"info\":" +
            "{\"build_version\":\"test-mock\",\"complete_ledgers\":\"1-1\",\"server_state\":\"full\"}}}";

        private readonly int _poisonFirst;

        private int _connections;

        public MalformedFrameServer(int poisonFirst)
        {
            _poisonFirst = poisonFirst;
            StartAccepting();
        }

        /// <summary>How many connections completed the handshake so far, poisoned ones included.</summary>
        public int Connections => Volatile.Read(ref _connections);

        protected override bool ServesManyClients => true;

        protected override async Task ServeAsync(NetworkStream stream)
        {
            int connection = Interlocked.Increment(ref _connections);
            bool poison = connection <= _poisonFirst;

            while (!Token.IsCancellationRequested)
            {
                string request = await ReadTextFrameAsync(stream).ConfigureAwait(false);
                if (request == null)
                {
                    return;
                }

                if (poison)
                {
                    // FIN + reserved opcode 0xB, no payload. The client's receive fails on the
                    // header alone; the connection is then dropped behind it.
                    await stream.WriteAsync(new byte[] { 0x8B, 0x00 }, Token).ConfigureAwait(false);
                    await stream.FlushAsync(Token).ConfigureAwait(false);
                    await Task.Delay(300, Token).ConfigureAwait(false);
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(request);
                string id = document.RootElement.TryGetProperty("id", out JsonElement requestId)
                    ? requestId.GetRawText()
                    : "null";

                byte[] response = Encoding.UTF8.GetBytes(ServerInfoEnvelope.Replace("__ID__", id));
                await WriteFragmentedMessageAsync(stream, response, fragments: 1).ConfigureAwait(false);
            }
        }
    }
}
