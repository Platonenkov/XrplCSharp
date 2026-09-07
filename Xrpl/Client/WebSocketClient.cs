

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xrpl.Client
{
    public enum SocketFailureReason
    {
        None,
        NetworkDrop,
        ServerClosed,
        Unknown
    }

    //credit: https://github.com/Badiboy/WebSocketWrapper/blob/master/WebSocketWrapper.cs
    public class WebSocketClient : IDisposable
    {

        private const int ReceiveChunkSize = 1048576;
        private const int SendChunkSize = 1048576;

        private ClientWebSocket _ws;
        private readonly Uri _uri;
        private readonly IReadOnlyDictionary<string, string>? _handshakeHeaders;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly CancellationToken _cancellationToken;
        private Task? _receiveTask;
        private readonly SemaphoreSlim _disconnectLock = new SemaphoreSlim(1, 1);

        // One message at a time. ClientWebSocket serializes frames, not messages: two messages
        // larger than SendChunkSize sent concurrently could interleave their frames. The lock
        // also gives a send that queued behind another one a place to notice that the socket was
        // retired in the meantime - see SendMessageAsync.
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private volatile bool _isIntentionalDisconnect;
        
        public SocketFailureReason FailureReason { get; private set; } = SocketFailureReason.None;

        private Func<WebSocketClient, Task> _onConnected;
        private Func<Exception, WebSocketClient, Task> _onConnectionError;
        private Func<byte[], WebSocketClient, Task> _onMessageBinary;
        private Func<string, WebSocketClient, Task> _onMessageString;
        private Func<Exception, WebSocketClient, Task> _onError;
        private Func<WebSocketCloseStatus?, string?, WebSocketClient, Task> _onDisconnected;
        private Func<WebSocketClient, Task> _onClosed;

        protected WebSocketClient(string uri, IReadOnlyDictionary<string, string>? handshakeHeaders = null)
        {
            _uri = new Uri(uri);
            _handshakeHeaders = handshakeHeaders;
            _cancellationToken = _cancellationTokenSource.Token;
        }
        /// <summary>
        /// Cancel work without setting intentional disconnect flag.
        /// Use CancelIntentionally() for user-initiated cancellations.
        /// </summary>
        public void Cancel() => _cancellationTokenSource.Cancel();
        
        /// <summary>
        /// Cancel work as an intentional disconnect.
        /// Sets the intentional disconnect flag before cancelling.
        /// Use this for user-initiated disconnects.
        /// </summary>
        public void CancelIntentionally()
        {
            _isIntentionalDisconnect = true;
            _cancellationTokenSource.Cancel();
        }

        /// <summary>
        /// Sets the intentional disconnect flag to true without cancelling.
        /// Use this before Cancel() if you need to set the flag earlier.
        /// </summary>
        public void SetIntentionalDisconnect()
        {
            _isIntentionalDisconnect = true;
        }
        
        /// <summary>
        /// Resets the intentional disconnect flag to false.
        /// Called after successful connection to enable error detection.
        /// </summary>
        public void ResetIntentionalDisconnect()
        {
            _isIntentionalDisconnect = false;
            FailureReason = SocketFailureReason.None;
        }

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        /// <param name="uri">The URI of the WebSocket server.</param>
        /// <param name="handshakeHeaders">
        /// Optional HTTP headers to put on the upgrade handshake (e.g. <c>Authorization</c>).
        /// Ignored under WebAssembly — the browser WebSocket API cannot set request headers.
        /// </param>
        /// <returns>Instance of the created WebSocketWrapper</returns>
        public static WebSocketClient Create(string uri, IReadOnlyDictionary<string, string>? handshakeHeaders = null)
        {
            return new WebSocketClient(uri, handshakeHeaders);
        }

        /// <summary>
        /// Connects to the WebSocket server.
        /// </summary>
        /// <returns>Self</returns>
        public async Task<WebSocketClient> Connect()
        {
            if (_ws == null)
            {
                _ws = new ClientWebSocket();
                if (!OperatingSystem.IsBrowser())
                {
                    _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    ApplyHandshakeHeaders(_ws);
                }
            }

            await ConnectAsync();
            return this;
        }

        private void ApplyHandshakeHeaders(ClientWebSocket socket)
        {
            if (_handshakeHeaders is null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> header in _handshakeHeaders)
            {
                socket.Options.SetRequestHeader(header.Key, header.Value);
            }
        }

        /// <summary>
        /// Disconnects from the WebSocket server.
        /// </summary>
        /// <returns>Self</returns>
        internal WebSocketClient Disconnect()
        {
            _ = DisconnectInternalAsync();
            return this;
        }
        
        /// <summary>
        /// Disconnects from the WebSocket server and waits for completion.
        /// Use this when you need to ensure the socket is fully closed before proceeding.
        /// </summary>
        /// <returns>Task that completes when disconnection is finished</returns>
        internal Task DisconnectAsync()
        {
            return DisconnectInternalAsync();
        }

        /// <summary>
        /// Get the current state of the WebSocket client.
        /// </summary>
        public WebSocketState State
        {
            get
            {
                if (_ws == null)
                    return WebSocketState.None;

                return _ws.State;
            }
        }

        /// <summary>
        /// Set the Action to call when the connection has been established.
        /// </summary>
        /// <param name="onConnect">The Action to call</param>
        /// <returns>Self</returns>
        internal WebSocketClient OnConnect(Func<WebSocketClient, Task> onConnect)
        {
            _onConnected = onConnect;
            return this;
        }

        /// <summary>
        /// Set the Action to call when the connection fails.
        /// </summary>
        /// <param name="onConnectionError">The Action to call</param>
        /// <returns>Self</returns>
        internal WebSocketClient OnConnectionError(Func<Exception, WebSocketClient, Task> onConnectionError)
        {
            if (_cancellationTokenSource.IsCancellationRequested)
                return this;
            _onConnectionError = onConnectionError;
            return this;
        }

        /// <summary>
        /// Set the Action to call when the socket reports an error.
        /// </summary>
        /// <param name="onError">The Action to call</param>
        /// <returns>Self</returns>
        internal WebSocketClient OnError(Func<Exception, WebSocketClient, Task> onError)
        {
            if (_cancellationTokenSource.IsCancellationRequested)
                return this;
            _onError = onError;
            return this;
        }

        /// <summary>
        /// Set the Action to call when the connection has been terminated.
        /// </summary>
        /// <param name="onDisconnect">The Action to call</param>
        /// <returns>Self</returns>
        internal WebSocketClient OnDisconnect(Func<WebSocketCloseStatus?, string?, WebSocketClient, Task> onDisconnect)
        {
            _onDisconnected = onDisconnect;
            return this;
        }

        /// <summary>
        /// Set the Action to call when the connection has been closed.
        /// </summary>
        /// <param name="onClosed">The Action to call</param>
        /// <returns>Self</returns>
        internal WebSocketClient OnClosed(Func<WebSocketClient, Task> onClosed)
        {
            if (_cancellationTokenSource.IsCancellationRequested)
                return this;
            _onClosed = onClosed;
            return this;
        }

        /// <summary>
        /// Set the Action to call when a messages has been received.
        /// </summary>
        /// <param name="onMessage">The Action to call.</param>
        /// <returns>Self</returns>
        internal WebSocketClient OnBinaryMessage(Func<byte[], WebSocketClient, Task> onMessage)
        {
            if (_cancellationTokenSource.IsCancellationRequested)
                return this;
            _onMessageBinary = onMessage;
            return this;
        }

        /// <summary>
        /// Set the Action to call when a messages has been received.
        /// </summary>
        /// <param name="onMessage">The Action to call.</param>
        /// <returns>Self</returns>
        internal WebSocketClient OnMessageReceived(Func<string, WebSocketClient, Task> onMessage)
        {
            if (_cancellationTokenSource.IsCancellationRequested)
                return this;
            _onMessageString = onMessage;
            return this;
        }

        /// <summary>
        /// Sends a UTF-8 string to the WebSocket server, fire-and-forget.
        /// </summary>
        /// <remarks>
        /// A failure is reported through the error callback and nowhere else - this is the
        /// keepalive's entry point, and a consumer's, and neither has a request to reject. A
        /// caller that does have one uses <see cref="SendMessageAsync(byte[])"/> and observes the
        /// task. Neither entry point ever reconnects: a send on a socket that is not open fails,
        /// it does not call <see cref="Connect"/>. It used to - <c>ConnectAsync</c> on an already
        /// used <see cref="ClientWebSocket"/> throws, the catch disposed the socket and raised
        /// <c>OnConnectionError</c>, and the send went ahead regardless.
        /// </remarks>
        /// <param name="message">The message to send</param>
        public void SendMessage(string message)
        {
            _ = ReportSendFailureAsync(SendMessageAsync(Encoding.UTF8.GetBytes(message)));
        }

        private async Task ReportSendFailureAsync(Task send)
        {
            try
            {
                await send.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The socket was cancelled underneath the send - a close the owner asked for.
            }
            catch (Exception e)
            {
                Debug.WriteLine($"{DateTime.Now}WebSocket send failed: {e.GetType().Name}: {e.Message}");
                CallOnError(e);
            }
        }

        /// <summary>
        /// Sends a byte array to the WebSocket server. The returned task faults if the message
        /// could not be written, so the owner of a request can reject it instead of leaving it to
        /// its timeout.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The write is issued synchronously when the send lock is free: everything up to the
        /// first incomplete await of <see cref="ClientWebSocket.SendAsync(ArraySegment{byte}, WebSocketMessageType, bool, CancellationToken)"/>
        /// runs on the caller's thread. <c>Connection</c> relies on that - it starts the send
        /// under the lock its retirement paths take, so a retirement either finds the request not
        /// yet sent, or already handed to the socket.
        /// </para>
        /// <para>
        /// A send that queued behind another one re-checks the socket once it holds the lock: the
        /// retirement marks the socket before it lets go of it, and that mark is what refuses a
        /// message that would otherwise reach a server the client has left. Between that check and
        /// the write there is no lock - a few instructions - and that is the residue this method
        /// does not close.
        /// </para>
        /// </remarks>
        /// <param name="message">The data to send</param>
        /// <exception cref="InvalidOperationException">The socket is not open.</exception>
        public async Task SendMessageAsync(byte[] message)
        {
            ClientWebSocket? socket = _ws;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("The WebSocket is not open; a send does not open it.");
            }

            await _sendLock.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isIntentionalDisconnect || socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("The WebSocket was closed before the message could be written.");
                }

                int messagesCount = (int)Math.Ceiling((double)message.Length / SendChunkSize);

                for (int i = 0; i < messagesCount; i++)
                {
                    int offset = SendChunkSize * i;
                    int count = SendChunkSize;
                    bool lastMessage = (i + 1) == messagesCount;

                    if ((count * (i + 1)) > message.Length)
                    {
                        count = message.Length - offset;
                    }

                    await socket.SendAsync(new ArraySegment<byte>(message, offset, count), WebSocketMessageType.Binary, lastMessage, _cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ConnectAsync()
        {
            try
            {
                await _ws.ConnectAsync(_uri, _cancellationToken);
                CallOnConnected();
                _receiveTask = ReceiveLoopAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                if (!IsDisposed)
                    Dispose();
                return;
            }
            catch (Exception ex) when (IsNetworkException(ex))
            {
                // Network-related exception during connection handshake - treat as NetworkDrop
                // This covers IOException, SocketException, WebSocketException, HttpRequestException,
                // and any exception chain containing these network-related types
                // This prevents Critical logging in consuming apps like DaddyWallet during reconnect attempts
                FailureReason = SocketFailureReason.NetworkDrop;
                _ws?.Dispose();
                _ws = null;
                await CallOnDisconnectedAsync(null, "Network error during connection");
            }
            catch (Exception e)
            {

                _ws?.Dispose();
                _ws = null;
                CallOnConnectionError(e);
            }
        }

        private async Task DisconnectInternalAsync()
        {
            await _disconnectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_ws == null)
                    return;

                _isIntentionalDisconnect = true;

                if (!_cancellationTokenSource.IsCancellationRequested)
                    _cancellationTokenSource.Cancel();

                Task? localReceiveTask = _receiveTask;
                if (localReceiveTask != null)
                {
                    try
                    {
                        await localReceiveTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                try
                {
                    if (_ws.State == WebSocketState.Open)
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    _ws.Dispose();
                    _ws = null;
                }
            }
            finally
            {
                try
                {
                    _disconnectLock.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        
        /// <summary>
        /// Initiates a graceful WebSocket close WITHOUT cancelling the receive loop.
        /// This allows ReceiveAsync to complete naturally when the server responds with Close frame,
        /// avoiding ObjectDisposedException/OperationCanceledException that occur with CTS cancellation.
        /// 
        /// CRITICAL: Does NOT call Dispose() forcefully after timeout.
        /// If server doesn't respond quickly, the socket will timeout naturally via TCP keepalive.
        /// This prevents ObjectDisposedException from being logged as Critical in MAUI.
        /// </summary>
        internal async Task InitiateGracefulCloseAsync()
        {
            var socket = _ws;
            if (socket == null)
                return;

            _isIntentionalDisconnect = true;
            
            // Send Close frame to server WITHOUT cancelling CTS.
            // The receive loop will get Close frame from server and exit naturally.
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure, 
                        "Server change", 
                        CancellationToken.None
                    ).ConfigureAwait(false);
                }
            }
            catch
            {
                // If CloseOutputAsync fails, just return - socket will be GC'd eventually
                return;
            }
            
            // Wait for receive loop to complete naturally (server sends Close frame response)
            // This typically takes 100-500ms for responsive servers
            Task? localReceiveTask = _receiveTask;
            if (localReceiveTask != null)
            {
                try
                {
                    // Wait up to 10 seconds for natural completion
                    // After this, socket will be GC'd when TCP times out - no forced Dispose!
                    await Task.WhenAny(
                        localReceiveTask, 
                        Task.Delay(TimeSpan.FromSeconds(10))
                    ).ConfigureAwait(false);
                    
                    // Only dispose if receive loop actually completed
                    if (localReceiveTask.IsCompleted)
                    {
                        try { socket.Dispose(); } catch { }
                    }
                    // If still running, do NOT dispose - let it timeout naturally
                    // The socket reference will be GC'd when receive loop eventually completes
                }
                catch { }
            }
            else
            {
                // No receive task, safe to dispose immediately
                try { socket.Dispose(); } catch { }
            }
        }

        private async Task ReceiveLoopAsync()
        {
            // One receive buffer per connection, rented rather than allocated: at ReceiveChunkSize
            // it lands on the large object heap, so a reconnect loop would otherwise burn a fresh
            // uncompactable megabyte per session. Returned in the finally below, which only runs
            // once the loop has stopped awaiting the socket.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveChunkSize);

            // Scratch buffer for messages that arrive in more than one chunk. It grows to the
            // largest message seen on this connection and is then reused, so steady-state assembly
            // costs nothing beyond the single exact-sized array handed to the callbacks. Rented for
            // the same reason as the receive buffer above - at these sizes it is a large-object-heap
            // array that would otherwise be thrown away per connection.
            byte[]? assemblyBuffer = null;

            try
            {
                // Continue receiving while Open OR CloseSent (waiting for server's Close frame after CloseOutputAsync)
                while (_ws != null &&
                       (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseSent) &&
                       !_cancellationToken.IsCancellationRequested)
                {
                    byte[]? completeMessage = null;
                    int assembledLength = 0;

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer, 0, ReceiveChunkSize), _cancellationToken).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            WebSocketCloseStatus? closeStatus = result.CloseStatus;
                            string? closeDescription = result.CloseStatusDescription;

                            _onClosed?.Invoke(this);
                            await CallOnDisconnectedAsync(closeStatus, closeDescription).ConfigureAwait(false);
                            return;
                        }
                        else if (result.EndOfMessage && assembledLength == 0)
                        {
                            // Whole message arrived in a single chunk - copy it out directly.
                            completeMessage = new byte[result.Count];
                            Buffer.BlockCopy(buffer, 0, completeMessage, 0, result.Count);
                        }
                        else if (result.Count > 0)
                        {
                            EnsureAssemblyCapacity(ref assemblyBuffer, assembledLength + result.Count, assembledLength);
                            Buffer.BlockCopy(buffer, 0, assemblyBuffer, assembledLength, result.Count);
                            assembledLength += result.Count;
                        }

                    } while (!result.EndOfMessage);

                    if (completeMessage == null)
                    {
                        completeMessage = new byte[assembledLength];
                        if (assembledLength > 0)
                        {
                            Buffer.BlockCopy(assemblyBuffer!, 0, completeMessage, 0, assembledLength);
                        }
                    }

                    CallOnMessage(completeMessage);
                }

                // Reached only when the loop ended without throwing - its condition went false
                // between iterations. Every other way out of this method reports the close; this
                // one reported nothing, so a caller waiting to hear that the connection ended
                // simply never heard.
                //
                // It is not a rare corner. A message handler runs inline here, on this thread, and
                // the request continuation it completes runs inline in turn - so a caller that
                // disconnects right after the response that woke it does so inside CallOnMessage
                // above. The loop comes back round to find the cancellation already set, leaves by
                // its condition, and the disconnect is never announced. That is what made a user
                // Disconnect() silent against a fast peer while looking correct against a slow one.
                //
                // Classified the same way as the catches below: cancelled or disposed is a normal
                // close, a socket that simply stopped being Open is a drop.
                if (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
                }
                else
                {
                    FailureReason = SocketFailureReason.NetworkDrop;
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Connection is no longer open").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
            {
                await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
            {
                await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // TaskCanceledException typically indicates network drop - no error callback, just reconnect
                FailureReason = SocketFailureReason.NetworkDrop;
                await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Network error").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Non-intentional cancellation - treat as network drop, no error callback
                FailureReason = SocketFailureReason.NetworkDrop;
                await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Operation canceled").ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                if (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
                    return;
                }
                FailureReason = SocketFailureReason.NetworkDrop;
                await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Socket disposed").ConfigureAwait(false);
            }
            catch (System.IO.IOException)
            {
                if (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
                    return;
                }
                // IOException indicates network drop - no error callback, just reconnect
                FailureReason = SocketFailureReason.NetworkDrop;
                await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Network error").ConfigureAwait(false);
            }
            catch (WebSocketException ex) when (ex.InnerException is IOException || ex.InnerException is System.Net.Sockets.SocketException)
            {
                // WebSocketException wrapping network error - treat as network drop, no error callback
                if (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
                    return;
                }
                FailureReason = SocketFailureReason.NetworkDrop;
                await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Network error").ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                if (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
                    return;
                }
                
                // Check if this is a network exception (even with nested HttpRequestException)
                // before surfacing via error callback
                if (IsNetworkException(ex))
                {
                    FailureReason = SocketFailureReason.NetworkDrop;
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Network error").ConfigureAwait(false);
                    return;
                }
                
                // Not a network exception - surface the error
                FailureReason = SocketFailureReason.Unknown;
                _onConnectionError?.Invoke(ex, this);
                await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "WebSocket error: " + ex.Message).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNetworkException(ex))
            {
                // Network-related exception - treat as network drop, no error callback
                if (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
                    return;
                }
                FailureReason = SocketFailureReason.NetworkDrop;
                await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Network error").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_isIntentionalDisconnect || _cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    await CallOnDisconnectedAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected").ConfigureAwait(false);
                    return;
                }
                FailureReason = SocketFailureReason.Unknown;
                _onConnectionError?.Invoke(ex, this);
                await CallOnDisconnectedAsync(WebSocketCloseStatus.EndpointUnavailable, "Unknown error: " + ex.Message).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

                if (assemblyBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(assemblyBuffer);
                }
            }
        }

        /// <summary>
        /// Grows <paramref name="assemblyBuffer"/> so it can hold <paramref name="requiredLength"/>
        /// bytes, doubling the current capacity and carrying over the first
        /// <paramref name="preserveLength"/> bytes already assembled.
        /// </summary>
        private static void EnsureAssemblyCapacity(ref byte[]? assemblyBuffer, int requiredLength, int preserveLength)
        {
            if (assemblyBuffer != null && assemblyBuffer.Length >= requiredLength)
            {
                return;
            }

            int capacity = assemblyBuffer?.Length ?? ReceiveChunkSize;
            while (capacity < requiredLength)
            {
                capacity = capacity <= Array.MaxLength / 2 ? capacity * 2 : requiredLength;
            }

            // Rent may hand back a longer array than asked for; the growth above keys off the
            // actual length, so the next doubling starts from what we really got.
            byte[] grown = ArrayPool<byte>.Shared.Rent(capacity);
            if (preserveLength > 0)
            {
                Buffer.BlockCopy(assemblyBuffer!, 0, grown, 0, preserveLength);
            }

            if (assemblyBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(assemblyBuffer);
            }

            assemblyBuffer = grown;
        }

        private void CallOnMessage(byte[] result)
        {
            _onMessageBinary?.Invoke(result, this);
            _onMessageString?.Invoke(Encoding.UTF8.GetString(result), this);
        }


        private async Task CallOnDisconnectedAsync(WebSocketCloseStatus? closeStatus = null, string? closeDescription = null)
        {
            var handler = _onDisconnected;
            if (handler != null)
            {
                await handler.Invoke(closeStatus, closeDescription, this).ConfigureAwait(false);
            }
        }

        private void CallOnConnected()
        {
            _onConnected?.Invoke(this);
        }

        private void CallOnConnectionError(Exception e)
        {
            _onConnectionError?.Invoke(e, this);
        }

        private void CallOnError(Exception e)
        {
            _onError?.Invoke(e, this);
        }
        
        private static bool IsNetworkException(Exception ex)
        {
            // Check for transport-level network exceptions (actual connection loss/timeout)
            // Be careful NOT to classify TLS/auth failures as network drops
            
            // SocketException - always indicates transport-level issue
            if (ex is System.Net.Sockets.SocketException)
                return true;
            
            // IOException - check for transport messages or SocketException inner
            if (ex is IOException ioEx)
            {
                // Has SocketException inner = definitely transport error
                if (ioEx.InnerException is System.Net.Sockets.SocketException)
                    return true;
                
                // Well-known transport error messages (MAUI/WinHTTP often throws these without inner exception)
                var msg = ioEx.Message;
                if (msg.Contains("transport connection") || 
                    msg.Contains("forcibly closed") ||
                    msg.Contains("Operation canceled") ||
                    msg.Contains("Operation timed out") ||
                    msg.Contains("Connection reset"))
                    return true;
            }
            
            // System.TimeoutException - always network-related
            if (ex is System.TimeoutException)
                return true;
            
            // Xrpl.Client.Exceptions.TimeoutException - ping timeout from RequestManager
            // This indicates network stall, not a server error
            if (ex is Xrpl.Client.Exceptions.TimeoutException)
                return true;
            
            // TaskCanceledException wrapping network exception
            if (ex is TaskCanceledException tce && tce.InnerException != null && IsNetworkException(tce.InnerException))
                return true;
            
            // OperationCanceledException wrapping network exception  
            if (ex is OperationCanceledException oce && oce.InnerException != null && IsNetworkException(oce.InnerException))
                return true;
            
            // WebSocketException - check inner chain and message patterns
            if (ex is WebSocketException wsEx)
            {
                // WebSocketException wrapping any transport exception in chain
                if (wsEx.InnerException != null && IsNetworkException(wsEx.InnerException))
                    return true;
                
                // Check message for common network error patterns
                var msg = wsEx.Message;
                if (msg.Contains("Unable to connect") ||
                    msg.Contains("connect to the remote server") ||
                    msg.Contains("connection was closed") ||
                    msg.Contains("Connection reset"))
                    return true;
            }
            
            // HttpRequestException - check inner chain and message patterns
            if (ex is System.Net.Http.HttpRequestException httpEx)
            {
                // HttpRequestException wrapping any transport exception in chain
                if (httpEx.InnerException != null && IsNetworkException(httpEx.InnerException))
                    return true;
                
                // Check message for DNS/connection failures
                var msg = httpEx.Message;
                if (msg.Contains("nodename nor servname") ||  // iOS/macOS DNS failure
                    msg.Contains("Name or service not known") || // Linux DNS failure
                    msg.Contains("Unable to connect") ||
                    msg.Contains("No such host is known") ||  // Windows DNS failure
                    msg.Contains("Connection refused") ||
                    msg.Contains("Network is unreachable"))
                    return true;
            }
            
            // Check for platform-specific HRESULTs on any exception type
            var hresult = ex.HResult;
            if (hresult == unchecked((int)0x80072EE2) || // ERROR_WINHTTP_TIMEOUT
                hresult == unchecked((int)0x80072EFD) || // ERROR_WINHTTP_CANNOT_CONNECT
                hresult == unchecked((int)0x80072EE7) || // ERROR_WINHTTP_NAME_NOT_RESOLVED  
                hresult == unchecked((int)0x80072EFE) || // ERROR_WINHTTP_CONNECTION_ERROR
                hresult == unchecked((int)0x80072F78) || // ERROR_WINHTTP_CONNECTION_RESET
                hresult == unchecked((int)0x80004005) || // E_FAIL - generic failure, often wraps network errors
                hresult == unchecked((int)0xFFFDFFFF))   // iOS/macOS DNS failure (from user's log)
            {
                // For E_FAIL (0x80004005), only treat as network if message matches
                if (hresult == unchecked((int)0x80004005))
                {
                    var msg = ex.Message;
                    if (msg.Contains("Unable to connect") ||
                        msg.Contains("connect to the remote server"))
                        return true;
                    // E_FAIL with other messages might be TLS/auth - check inner
                    if (ex.InnerException != null)
                        return IsNetworkException(ex.InnerException);
                    return false;
                }
                return true;
            }
            
            // Check message patterns on any exception type as last resort
            var exMsg = ex.Message;
            if (exMsg.Contains("nodename nor servname") ||  // iOS/macOS DNS failure
                exMsg.Contains("Name or service not known") || // Linux DNS failure
                exMsg.Contains("No such host is known"))  // Windows DNS failure
                return true;
            
            // Recursively check inner exception
            if (ex.InnerException != null)
                return IsNetworkException(ex.InnerException);
            
            return false;
        }

        public bool IsDisposed;
        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            
            if (_cancellationTokenSource?.IsCancellationRequested == false)
                CancelIntentionally();
            else
                _isIntentionalDisconnect = true;
            
            _ws?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}