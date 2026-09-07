using System;
using System.Threading;
using System.Threading.Tasks;

namespace Xrpl.Client
{
    /// <summary>
    /// Represents an isolated connection session with its own WebSocketClient and state.
    /// Used for per-session isolation during ChangeServer operations.
    /// 
    /// Each session has:
    /// - Unique session ID for callback validation
    /// - Own WebSocketClient instance
    /// - Own intentional disconnect flag (not shared with other sessions)
    /// - Completion TaskCompletionSource that signals when OnDisconnected has run
    /// 
    /// This prevents race conditions where old session callbacks affect new session state.
    /// </summary>
    internal class ConnectionSession
    {
        private static long _sessionIdCounter = 0;
        
        public long SessionId { get; }

        /// <summary>
        /// The connection transition this session was created under - see
        /// <c>Connection.TakeOver</c>.
        /// </summary>
        /// <remarks>
        /// A socket callback carries the session, and the session carries the generation, so a
        /// close or a failure can tell whether the transition that opened this socket is still the
        /// one in charge of the connection. If it is not, a later operation owns the connection now
        /// and the callback confines itself to announcing that the session ended.
        /// </remarks>
        public long Generation { get; }

        public WebSocketClient? Socket { get; private set; }
        public bool IsIntentionalDisconnect { get; set; }
        public bool IsRetiring { get; private set; }
        
        private readonly TaskCompletionSource<bool> _completionTcs;

        private int _endNotified;

        private int _opened;

        /// <summary>
        /// Whether this session's socket ever finished connecting.
        /// </summary>
        /// <remarks>
        /// The session object is created before <c>ws.Connect()</c> is even called, so its
        /// existence says nothing about whether a connection happened. A failed attempt still
        /// reaches the close callback and would otherwise be announced as a session that ended -
        /// once per retry - when nothing was ever subscribed to lose.
        /// </remarks>
        public bool IsOpened => Volatile.Read(ref _opened) == 1;

        /// <summary>
        /// Records that this session's socket finished connecting.
        /// </summary>
        public void MarkAsOpened()
        {
            Volatile.Write(ref _opened, value: 1);
        }

        /// <summary>
        /// Task that completes when this session's OnDisconnected callback has finished.
        /// </summary>
        public Task Completion => _completionTcs.Task;

        public ConnectionSession(WebSocketClient socket, long generation)
        {
            SessionId = Interlocked.Increment(ref _sessionIdCounter);
            Generation = generation;
            Socket = socket;
            IsIntentionalDisconnect = false;
            IsRetiring = false;
            _completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        
        /// <summary>
        /// Claims the right to announce that this session has ended, for the first caller only.
        /// </summary>
        /// <remarks>
        /// Several paths retire a session and more than one of them can reach the announcement:
        /// a deliberate retirement (ChangeServer, fast reconnect) races the socket's own close
        /// callback. The consumer must hear about the session once, so the guard belongs to the
        /// session rather than to any one of those paths.
        /// </remarks>
        /// <returns><c>true</c> for the caller that won; <c>false</c> for every later one.</returns>
        public bool TryMarkEndNotified()
        {
            return Interlocked.Exchange(ref _endNotified, value: 1) == 0;
        }

        /// <summary>
        /// Marks this session as retiring. Callbacks from retiring sessions are ignored.
        /// </summary>
        public void MarkAsRetiring()
        {
            IsRetiring = true;
            IsIntentionalDisconnect = true;
            Socket?.SetIntentionalDisconnect();
        }
        
        /// <summary>
        /// Signals that this session has completed its cleanup.
        /// Called after OnDisconnected has run.
        /// </summary>
        public void CompleteSession()
        {
            _completionTcs.TrySetResult(true);
        }
        
        /// <summary>
        /// Initiates graceful close of this session's socket.
        /// Does not block - cleanup happens asynchronously.
        /// </summary>
        public async Task GracefulCloseAsync()
        {
            var socket = Socket;
            if (socket == null)
            {
                CompleteSession();
                return;
            }
            
            try
            {
                await socket.InitiateGracefulCloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Swallow exceptions - this is background cleanup
            }
            finally
            {
                // Session completion is signaled by OnceClose callback, not here
                // (unless socket was null)
            }
        }
        
        /// <summary>
        /// Clears the socket reference. Call after session cleanup.
        /// </summary>
        public void ClearSocket()
        {
            Socket = null;
        }
    }
}
