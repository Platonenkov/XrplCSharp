using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Xrpl.Tests
{
    /// <summary>
    /// WebSocket server that closes its first <c>closeFirst</c> connections right after the
    /// handshake - a close frame, then the TCP connection - and serves every connection after
    /// that, answering each request with the same <c>server_info</c> body.
    /// </summary>
    /// <remarks>
    /// A socket that opens and closes at once is the shape of the reconnect loop's narrowest
    /// window: the loop sees its attempt succeed, and the close arrives while it is deciding
    /// whether it is done. Whether the client comes back from that depends on the loop and the
    /// close callback agreeing on who reconnects, which is what this server is for. The shared
    /// mock cannot do it - it serves every connection it accepts.
    /// </remarks>
    internal sealed class CloseAfterHandshakeServer : WebSocketTestServerBase
    {
        private const string ServerInfoEnvelope =
            "{\"id\":__ID__,\"status\":\"success\",\"type\":\"response\",\"result\":{\"info\":" +
            "{\"build_version\":\"test-mock\",\"complete_ledgers\":\"1-1\",\"server_state\":\"full\"}}}";

        private readonly int _closeFirst;

        private int _connections;

        public CloseAfterHandshakeServer(int closeFirst)
        {
            _closeFirst = closeFirst;
            StartAccepting();
        }

        /// <summary>How many connections completed the handshake so far, closed ones included.</summary>
        public int Connections => Volatile.Read(ref _connections);

        protected override bool ServesManyClients => true;

        protected override async Task ServeAsync(NetworkStream stream)
        {
            int connection = Interlocked.Increment(ref _connections);
            if (connection <= _closeFirst)
            {
                // A close frame with no status code: FIN + opcode 0x8, empty payload. Returning
                // lets the base dispose the connection behind it.
                await stream.WriteAsync(new byte[] { 0x88, 0x00 }, Token).ConfigureAwait(false);
                await stream.FlushAsync(Token).ConfigureAwait(false);
                return;
            }

            while (!Token.IsCancellationRequested)
            {
                string request = await ReadTextFrameAsync(stream).ConfigureAwait(false);
                if (request == null)
                {
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
