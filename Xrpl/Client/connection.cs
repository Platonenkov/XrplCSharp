using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Xrpl.AddressCodec;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;

using static System.Runtime.InteropServices.JavaScript.JSType;
using static Xrpl.Client.RequestManager;

using Timer = System.Timers.Timer;

// https://github.com/XRPLF/xrpl.js/blob/main/packages/xrpl/src/client/connection.ts

namespace Xrpl.Client;

public enum ConnectionCloseSeverity
{
    Info,

    Warning,

    Error,
}

public enum RequestFailurePolicy
{
    ImmediateFail,

    WaitForConnection,
}

public enum XrpConnectionState
{
    Disconnected,

    Connecting,

    Connected,

    RestoringConnection,
}

public class ReconnectInfo
{
    public int CurrentAttempt { get; set; }

    public int MaxAttempts { get; set; }

    public TimeSpan RemainingDelay { get; set; }
}

public class ConnectionStatusInfo
{
    public string Message { get; set; }

    public ConnectionCloseSeverity Severity { get; set; }

    public ReconnectInfo? Reconnect { get; set; }

    public XrpConnectionState ConnectionState { get; set; }
}

public class Connection
{
    public event OnError OnError;

    public event OnWarning OnWarning;

    public event OnServerWarning OnServerWarning;

    public event OnConnected OnConnected;

    public event OnDisconnect OnDisconnect;

    /// <inheritdoc cref="IXrplClient.OnSessionEnded" />
    public event OnSessionEnded OnSessionEnded;

    public event OnPing OnPing;

    public event OnLedgerClosed OnLedgerClosed;

    public event OnTransaction OnTransaction;

    public event OnValidationReceived OnValidationReceived;

    public event OnManifestReceived OnManifestReceived;

    public event OnPeerStatusChange OnPeerStatusChange;

    public event OnConsensusPhase OnConsensusPhase;

    public event OnPathFind OnPathFind;

    public event OnBookChanges OnBookChanges;

    public event OnServerStatus OnServerStatus;

    public event Action<ConnectionStatusInfo> OnConnectionStatus;

    public static string Base64Encode(string plainText)
    {
        var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(plainTextBytes);
    }

    public static string Base64Decode(string base64EncodedData)
    {
        var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
        return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
    }

    public class ConnectionOptions
    {
        /// <summary>
        /// Raw <c>user:password</c> pair sent as an HTTP Basic <c>Authorization</c> header on the
        /// WebSocket upgrade handshake. Matches the <c>authorization</c> option of xrpl.js.
        /// </summary>
        /// <remarks>
        /// rippled itself does not check Basic auth on the ws/wss handshake — its <c>user</c>/<c>password</c>
        /// port-stanza settings only apply to plain HTTP JSON-RPC. This option is for reaching a node behind a
        /// reverse proxy or a provider that requires Basic auth. For admin commands over ws/wss use
        /// <see cref="AdminUser"/>/<see cref="AdminPassword"/> instead.
        /// Ignored under WebAssembly — the browser WebSocket API cannot set request headers.
        /// </remarks>
        public string authorization { get; set; }

        /// <summary>
        /// Extra HTTP headers to put on the WebSocket upgrade handshake.
        /// </summary>
        /// <remarks>Ignored under WebAssembly — the browser WebSocket API cannot set request headers.</remarks>
        public Dictionary<string, string> headers { get; set; }

        /// <summary>
        /// Admin user for rippled's <c>admin_user</c> port-stanza setting.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="authorization"/>, these credentials travel inside the JSON body of every
        /// request — that is the only mechanism rippled accepts for admin commands over ws/wss.
        /// Both <see cref="AdminUser"/> and <see cref="AdminPassword"/> must be set for them to be sent.
        /// </remarks>
        public string AdminUser { get; set; }

        /// <summary>
        /// Admin password for rippled's <c>admin_password</c> port-stanza setting. See <see cref="AdminUser"/>.
        /// </summary>
        public string AdminPassword { get; set; }

        /// <summary>
        /// Timeout for individual API requests after connection is established.
        /// This controls how long to wait for a response to a single request (e.g., account_info, submit).
        /// Default: 40 seconds.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(40);

        /// <summary>
        /// Timeout for a single WebSocket connection attempt.
        /// If the connection cannot be established within this time, it will fail and trigger reconnection logic.
        /// Should be shorter than ConnectionAcquisitionTimeout to allow multiple retry attempts.
        /// Default: 30 seconds.
        /// </summary>
        public TimeSpan ConnectionAttemptTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Gets or sets the base delay interval used between automatic reconnection attempts.
        /// </summary>
        public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets the maximum delay between automatic reconnection attempts after a disconnection.
        /// </summary>
        /// <remarks>This value determines the upper bound for the time interval between
        /// reconnection attempts. If the connection is lost, the delay between retries will not exceed this value,
        /// even if a backoff strategy is used.</remarks>
        public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the maximum number of times the system will attempt to reconnect after a disconnection.
        /// </summary>
        /// <remarks>Set this property to limit how many reconnection attempts are made before
        /// giving up. A value of 0 disables automatic reconnection.</remarks>
        public int MaxReconnectAttempts { get; set; } = 5;

        /// <summary>
        /// Gets or sets a value indicating whether the operation should stop after reaching the maximum number of
        /// attempts.
        /// </summary>
        /// <remarks>Set this property to <see langword="true"/> to prevent further retries once
        /// the maximum attempt count is reached. If set to <see langword="false"/>, the operation may continue
        /// beyond the maximum attempts, depending on the retry policy.</remarks>
        public bool StopAfterMaxAttempts { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to use a custom ping<br/>
        /// implementation instead of the default behavior.
        /// </summary>
        public bool UseCustomPing { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to enable periodic background health monitoring of the WebSocket connection.<br/>
        /// When enabled, the connection state is checked every 20 seconds. If the WebSocket is detected as Closed or Aborted,
        /// or if no data has been received for more than 60 seconds, an automatic reconnection is triggered.<br/>
        /// This check does not send any network requests — it only inspects the local connection state.<br/>
        /// Automatically enabled when <see cref="UseCustomPing"/> is set to <see langword="true"/>.<br/>
        /// Default: <see langword="false"/>.
        /// </summary>
        public bool UseCheckHealth { get; set; } = false;

        /// <summary>
        /// Gets or sets how often the background health check runs — the timer that notices a socket
        /// which is no longer Open and hands the client to the fast-reconnect path.<br/>
        /// Default: 20 seconds, the interval this check has always used.
        /// </summary>
        /// <remarks>
        /// Exposed primarily so tests can exercise the ping and fast-reconnect paths without waiting
        /// out the default interval; those paths were previously unreachable from a unit test, which
        /// is why they went uncovered through several fixes. Lowering it in production only makes the
        /// state check more frequent — it sends no network requests of its own.
        /// </remarks>
        public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Gets or sets how long a connection may go without any inbound activity before the health
        /// check treats it as dead and hands it to the fast-reconnect path.<br/>
        /// Default: 60 seconds, the threshold this check has always used.
        /// </summary>
        /// <remarks>
        /// A socket whose peer vanished stays <c>Open</c> until the next I/O, so silence is the only
        /// signal available without sending traffic. Exposed together with
        /// <see cref="HealthCheckInterval"/> so the fast-reconnect path is reachable from a test in
        /// under a second instead of over a minute.
        /// </remarks>
        public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the policy that determines how failed requests are handled.
        /// </summary>
        /// <remarks>
        /// Use this property to specify the strategy for handling request failures,<br/>
        /// such as whether to retry, delay, or fail immediately.<br/>
        /// The selected policy affects how the system responds to transient errors or network issues.</remarks>
        public RequestFailurePolicy RequestPolicy { get; set; } = RequestFailurePolicy.WaitForConnection;

        /// <summary>
        /// Maximum time to wait for connection when using WaitForConnection request policy.
        /// This is the total time allowed for multiple connection attempts, including retry delays.
        /// Must be >= ConnectionAttemptTimeout to allow at least one full connection attempt.
        /// Default: 30 seconds.
        /// </summary>
        public TimeSpan ConnectionAcquisitionTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How many stream messages may wait for the consumer before the oldest are discarded.
        /// </summary>
        /// <remarks>
        /// Stream events are handed to a background reader through a bounded channel, so a slow
        /// handler never blocks the receive loop. What it does instead is fall behind, and past
        /// this many queued messages the oldest are dropped to make room -
        /// <see cref="Connection.DroppedStreamMessages"/> counts them.
        /// <para>
        /// Raise it for a consumer that must not miss events and can absorb the memory (each slot
        /// holds one frame); lower it to bound memory harder, accepting more loss. Default 10 000.
        /// </para>
        /// </remarks>
        public int StreamMessageQueueCapacity { get; set; } = 10000;
    }

    private void ValidateConfig()
    {
        if (config.ConnectionAcquisitionTimeout < config.ConnectionAttemptTimeout)
        {
            throw new ArgumentException(
                $"ConnectionAcquisitionTimeout ({config.ConnectionAcquisitionTimeout.TotalSeconds}s) must be >= ConnectionAttemptTimeout ({config.ConnectionAttemptTimeout.TotalSeconds}s) to allow at least one full connection attempt.");
        }

        // The WASM timer takes this as an int of milliseconds: zero fires once and never repeats,
        // and anything past int.MaxValue or below zero is rejected outright by the timer itself.
        // Fail here instead, where the message can say which option is wrong.
        double healthCheckMs = config.HealthCheckInterval.TotalMilliseconds;
        if (healthCheckMs < 1 || healthCheckMs > int.MaxValue)
        {
            throw new ArgumentException(
                $"HealthCheckInterval ({config.HealthCheckInterval}) must be between 1ms and {int.MaxValue}ms.");
        }

        if (config.InactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"InactivityTimeout ({config.InactivityTimeout}) must be positive - a non-positive value would " +
                "treat every connection as dead on the first health check.");
        }
    }

    // https://github.com/XRPLF/xrpl.js/blob/main/packages/xrpl/src/client/connection.ts createWebSocket
    private static WebSocketClient CreateWebSocket(string url, ConnectionOptions config) =>
        WebSocketClient.Create(url, BuildHandshakeHeaders(config));

    /// <summary>
    /// Builds the HTTP headers put on the WebSocket upgrade handshake: the caller's own
    /// <see cref="ConnectionOptions.headers"/> plus a Basic <c>Authorization</c> header derived
    /// from <see cref="ConnectionOptions.authorization"/>.
    /// </summary>
    internal static Dictionary<string, string>? BuildHandshakeHeaders(ConnectionOptions config)
    {
        bool hasAuthorization = !string.IsNullOrEmpty(config.authorization);
        bool hasHeaders = config.headers is { Count: > 0 };

        if (!hasAuthorization && !hasHeaders)
        {
            return null;
        }

        Dictionary<string, string> headers = hasHeaders
            ? new Dictionary<string, string>(config.headers, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (hasAuthorization)
        {
            headers["Authorization"] = $"Basic {Base64Encode(config.authorization)}";
        }

        return headers;
    }

    public string url { get; private set; }

    // Volatile: this is the connectivity gate. It is cleared under _disconnectLock by the
    // retirement paths and read lock-free by ShouldBeConnected/State/CheckIfNotConnected.
    public volatile WebSocketClient ws;

    private int? reconnectTimeoutID = null;

    private int? heartbeatIntervalID = null;

    private int _reconnectAttempts = 0;

    // Number of consecutive times the consumer OnConnected handler threw.
    // Not part of the reconnect state: OnceOpen clears the reconnect state before invoking the handler,
    // so this counter is the only thing that can bound an endlessly failing handler.
    private int _connectHandlerFailures = 0;

    private static readonly Random _random = new();

    /// <summary>
    /// Guards the reconnect session — <see cref="_reconnectCts"/>, <see cref="_reconnectLoop"/> and
    /// <see cref="_reconnectAttempts"/> — wherever one is read and another written as a unit:
    /// <see cref="StopReconnectLoop"/>, <see cref="StartReconnectLoop"/>,
    /// <see cref="RestartReconnectLoop"/>, <see cref="RetireCurrentSessionAndReconnectAsync"/> and
    /// the ownership-guarded writes in <see cref="ReconnectLoopAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not every touch of these fields is covered: the per-iteration <c>_reconnectAttempts++</c> in
    /// <see cref="ReconnectLoopAsync"/>, the plain resets in <c>ChangeServer</c> and
    /// <c>OnceClose</c>, and the "is a loop already running" pre-checks in
    /// <c>OnConnectionFailed</c> and <c>OnceClose</c> (which read <c>_reconnectLoop</c>, a
    /// non-volatile field, outside the lock) all still run outside it. Those predate this lock; do
    /// not read the list above as "all three fields are always synchronized".
    /// </para>
    /// <para>
    /// <c>volatile</c> alone was not enough: it makes each individual access atomic, not the
    /// sequence of them. The stop path used to read the field three times in a row (Cancel,
    /// Dispose, null it), so a start running in between could have its brand-new source disposed
    /// and cleared by the retiring stop — leaving the loop with a dead source and nobody
    /// reconnecting, which is exactly the permanent wedge this whole area exists to prevent.
    /// </para>
    /// <para>
    /// Nothing that can call back into consumer code runs while the lock is held: cancellation and
    /// disposal of a retired source happen after the lock is released, and the loop body starts
    /// with a yield so that starting it under the lock never runs a notification inline.
    /// </para>
    /// </remarks>
    private readonly object _reconnectStateLock = new object();

    // Volatile so the ownership checks in ReconnectLoopAsync can read it outside the lock:
    // a single reference read is atomic, and those checks only ever compare, never mutate.
    private volatile CancellationTokenSource _reconnectCts;

    private Task _reconnectLoop;

    private SemaphoreSlim _connectLock = new(initialCount: 1, maxCount: 1);

    private DateTime? lastActivityTime = null;

    private Timer? pingTimer = null;

    private CancellationTokenSource? _pingCts = null;

    private Task? _currentPingTask = null;

    private WebSocketClient? _userInitiatedSocket = null;

    private WebSocketClient? _lastActiveSocket = null;

    private readonly HashSet<WebSocketClient> _userInitiatedSockets = new();

    private readonly object _userInitiatedSocketsLock = new();

    private volatile bool _permanentlyDisconnected = false;

    private volatile bool _isIntentionalDisconnect = false;

    // Socket that was closed due to ping timeout - late callbacks from this socket should be ignored
    private volatile WebSocketClient? _pingTimeoutSocket = null;

    // Socket that was closed due to network drop - late callbacks from this socket should be ignored
    private volatile WebSocketClient? _networkDropSocket = null;

    // Fast-path message processing: prioritize request responses (including ping/pong) over stream data
    // to prevent head-of-line blocking that causes ping timeouts under high stream load
    // Channel is created per-session to prevent cross-session message leakage
    // Using Channel<T> instead of BlockingCollection for true async support in WebAssembly
    // Carries the raw frame bytes, not text: stream events pair themselves with the frame the same
    // way a query response does, through AttachFrame, so their Raw/RawTransaction is available
    // without a second UTF-8 encode of a string that was itself decoded from these same bytes.
    // The frame travels wrapped in SessionFrame, which names the session that produced it.
    private Channel<SessionFrame>? _streamMessageChannel = null;

    private long _droppedStreamMessages;

    private long _staleSessionFramesDropped;

    private long _fallbackDispatchedStreamMessages;

    /// <summary>
    /// How many stream frames were dispatched outside the queue.
    /// </summary>
    /// <remarks>
    /// The fallback path holds none of the queue's guarantees: no capacity bound, no eviction
    /// counting, no single-reader ordering. Three things send a frame down it - the processor not
    /// being up yet, the processor having been stopped, and the channel refusing a write because
    /// its writer is already completed - and none of them were visible from outside until this
    /// counter existed.
    /// <para>
    /// Cumulative over the life of the connection, and a non-zero value is not by itself a fault:
    /// a client that was driven before it connected, or after it disconnected, legitimately has
    /// frames here. What means something is an increase across a connect: the startup window this
    /// counter was added to measure is closed, so the number should not move while a connection is
    /// being established.
    /// </para>
    /// </remarks>
    public long FallbackDispatchedStreamMessages => Interlocked.Read(ref _fallbackDispatchedStreamMessages);

    /// <summary>
    /// How many stream frames were discarded because they came from a session that is no longer
    /// active.
    /// </summary>
    /// <remarks>
    /// A socket being retired keeps delivering until its graceful close finishes, so a handful of
    /// frames can arrive after a reconnect or <c>ChangeServer</c> has already moved on. They are
    /// dropped rather than delivered: after a change of network they would otherwise describe a
    /// different chain entirely. Counted separately from
    /// <see cref="DroppedStreamMessages"/>, which is about consumers falling behind - these two
    /// mean different things and a non-zero value here is normal right after a reconnect.
    /// </remarks>
    public long StaleSessionFramesDropped => Interlocked.Read(ref _staleSessionFramesDropped);

    /// <summary>
    /// The id of the session serving this connection, or <see langword="null"/> if there is none.
    /// </summary>
    /// <remarks>
    /// Exists for tests, which need to name the session a frame came from - something only the
    /// socket callbacks can otherwise do.
    /// </remarks>
    internal long? ActiveSessionId
    {
        get
        {
            lock (_sessionLock)
            {
                return _activeSession?.SessionId;
            }
        }
    }

    /// <summary>
    /// Whether the background message processor is up, i.e. whether stream frames are queued
    /// rather than taking the fallback path.
    /// </summary>
    /// <remarks>
    /// Exists for tests. <c>OnceOpen</c> now starts the processor before it resolves the waiters
    /// through <c>connectionManager.ResolveAllAwaiting()</c> and before the <c>OnConnected</c>
    /// callback, so a returned <c>Connect()</c> does imply a queue - it did not until the ping
    /// timer stopped taking the processor down with it.
    /// <para>
    /// <c>Volatile.Read</c> rather than a plain read or <c>_messageProcessorLock</c>: the field is
    /// written under that lock, and a read that never contends with a stop in progress is all
    /// this needs.
    /// </para>
    /// </remarks>
    internal bool IsMessageProcessorRunning => Volatile.Read(ref _streamMessageChannel) != null;

    /// <summary>
    /// Whether the ping timer is up.
    /// </summary>
    /// <remarks>
    /// Exists for tests, and for one question in particular: the ping timer starts at the very end
    /// of <c>OnceOpen</c>, after <c>Connect()</c> has already returned, so a test that wants to
    /// know the processor survived <see cref="StartPingTimer"/> has to wait for it rather than
    /// assume it. Without that wait such a test can pass by asserting too early - before the thing
    /// it is testing has had a chance to go wrong.
    /// <para>
    /// The signal is exact for that purpose: <see cref="StartPingTimer"/> begins by calling
    /// <see cref="StopPingTimerSync"/>, which clears this field, and only assigns it afterwards.
    /// Seeing it non-null therefore means the teardown step - the one that used to take the
    /// message processor with it - is already behind us.
    /// </para>
    /// </remarks>
    internal bool IsPingTimerRunning => Volatile.Read(ref _pingCts) != null;

    /// <summary>
    /// Completes the stream channel's writer without clearing the channel, reproducing the state
    /// <c>DetachMessageProcessor</c> leaves behind for anyone who read
    /// <c>_streamMessageChannel</c> just before it was cleared.
    /// </summary>
    /// <remarks>
    /// Exists for tests: in production that state lasts between one field read and one call, and
    /// is not reachable deliberately.
    /// </remarks>
    internal void CompleteStreamChannelWriterForTests()
    {
        _streamMessageChannel?.Writer.Complete();
    }

    /// <summary>
    /// Marks the session serving this connection as retiring, reproducing the window
    /// <c>ChangeServer</c> and the reconnect loop open between <c>MarkAsRetiring()</c> and the
    /// installation of the replacement session.
    /// </summary>
    /// <remarks>
    /// Exists for tests: in production that window is reachable only by racing a real reconnect.
    /// Marks it exactly the way the two production paths do, under <c>_sessionLock</c>.
    /// <para>
    /// Returns the id rather than leaving the caller to read <see cref="ActiveSessionId"/>
    /// separately: two lock acquisitions would let a reconnect swap the session in between, and a
    /// test that then named the id it read first would be exercising the mismatch path it was
    /// written to avoid - silently, and only sometimes.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The id of the session that was marked, or <see langword="null"/> if there is none.
    /// </returns>
    internal long? MarkActiveSessionRetiringForTests()
    {
        lock (_sessionLock)
        {
            _activeSession?.MarkAsRetiring();
            return _activeSession?.SessionId;
        }
    }

    /// <summary>
    /// How many stream messages have been discarded because the consumer fell behind.
    /// </summary>
    /// <remarks>
    /// The queue feeding stream handlers is bounded (see
    /// <see cref="ConnectionOptions.StreamMessageQueueCapacity"/>) and discards the oldest message
    /// when full, so a slow handler costs events rather than stalling the socket. That discard used
    /// to be entirely silent: nothing threw, nothing logged, and a consumer building state from the
    /// stream simply drifted from the ledger with no way to notice. This counter is the way to
    /// notice - non-zero and rising means handlers are not keeping up.
    /// <para>
    /// Counts across the lifetime of this connection, including across reconnects and
    /// <c>ChangeServer</c>, since the same object serves them all.
    /// </para>
    /// </remarks>
    public long DroppedStreamMessages => Interlocked.Read(ref _droppedStreamMessages);
    private CancellationTokenSource? _messageProcessorCts = null;
    private Task? _messageProcessorTask = null;
    private readonly object _messageProcessorLock = new();

    // Reconnect mode enum for reliable state tracking across all reconnect paths
    private enum ReconnectMode { None, FastReconnect, LoopReconnect }
    
    // Current reconnect mode - set before any reconnect attempt, cleared only when connection stable
    // This ensures all callbacks see the correct reconnect state regardless of timing
    private volatile ReconnectMode _reconnectMode = ReconnectMode.None;
    
    // Legacy flag for backward compatibility (kept for any external checks)
    private volatile bool _isFastReconnectActive = false;

    private volatile XrpConnectionState _currentConnectionState = XrpConnectionState.Disconnected;

    private TaskCompletionSource<bool>? _disconnectTcs = null;

    private readonly object _disconnectLock = new();

    // Per-session isolation for ChangeServer
    private ConnectionSession? _activeSession = null;

    private readonly object _sessionLock = new();

    public XrpConnectionState CurrentConnectionState => _currentConnectionState;

    private string _previousNotifiedMessage = string.Empty;

    private void SetConnectionState(
        XrpConnectionState newState,
        string message,
        ConnectionCloseSeverity severity = ConnectionCloseSeverity.Info,
        ReconnectInfo? reconnect = null)
    {

        var stateChanged = _currentConnectionState != newState;
        _currentConnectionState = newState;

        var hasReconnectInfo = reconnect != null;
        var messageChanged = _previousNotifiedMessage != message;
        var isRestoringConnection = newState == XrpConnectionState.RestoringConnection;

        if (!stateChanged && !hasReconnectInfo && !(isRestoringConnection && messageChanged))
        {
            return;
        }

        _previousNotifiedMessage = message;

        // Contained here, once, rather than at each call site. Every state notification in this class
        // funnels through this method, and several call sites are places where an escaping exception
        // costs the client its reconnect: the fast-reconnect path (running on a ping task whose
        // callers swallow everything) and ReconnectLoopAsync, which notifies before its first
        // connection attempt and would fault with _reconnectCts still installed and no live loop.
        // A consumer's status handler must not be able to take the connection down.
        try
        {
            OnConnectionStatus?.Invoke(
                new ConnectionStatusInfo
                {
                    Message = message,
                    Severity = severity,
                    Reconnect = reconnect,
                    ConnectionState = newState,
                });
        }
        catch (Exception notifyError)
        {
            Debug.WriteLine($"{DateTime.Now}OnConnectionStatus handler threw for state {newState}: {notifyError.Message}");
        }
    }

    /// <summary>
    /// Announces that <paramref name="session"/> has ended, once and only once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session that ends because its socket closed is announced from <see cref="OnceClose"/>,
    /// alongside <see cref="OnDisconnect"/>. The two paths that retire a session deliberately -
    /// <see cref="ChangeServer"/> and <see cref="RetireCurrentSessionAndReconnectAsync"/> - mark it
    /// retiring before the socket goes, which makes <see cref="OnceClose"/> return early by design,
    /// so each has to speak for itself. For the fast-reconnect path a
    /// <see cref="XrpConnectionState.RestoringConnection"/> status at least went out; for
    /// <see cref="ChangeServer"/> nothing did, and that is issue #123: the client reported
    /// <c>Connected</c> while the subscriptions, which the node keeps against the old connection,
    /// were gone for good.
    /// </para>
    /// <para>
    /// The once-per-session guard lives on the session rather than here, for a narrow race the
    /// retiring check does not cover: a socket can close by itself in the moment before the
    /// retirement marks its session, and then <see cref="OnceClose"/> sees a live session and
    /// announces the loss just as the retirement announces the switch. One session, two causes,
    /// and the consumer must still hear about it once.
    /// </para>
    /// <para>
    /// A throwing consumer handler must not reach the caller. <see cref="ChangeServer"/> calls
    /// this after it has torn the old socket down and before it connects the new one, so an
    /// escaping exception would leave the client with no socket, no session and no reconnect
    /// loop - the same reason <see cref="SetConnectionState"/> contains its handler.
    /// </para>
    /// </remarks>
    /// <param name="session">The session that ended; <c>null</c> when there was none, and then
    /// nothing is announced.</param>
    /// <param name="reason">What ended it.</param>
    /// <param name="description">Human-readable detail, for the consumer's log.</param>
    private async Task NotifySessionEndedAsync(
        ConnectionSession? session,
        SessionEndReason reason,
        string description)
    {
        // A session that never got a connected socket carries nothing to lose, and its close
        // callback would otherwise announce an end for every failed retry.
        if (session?.IsOpened != true)
        {
            return;
        }

        if (!session.TryMarkEndNotified())
        {
            return;
        }

        OnSessionEnded handler = OnSessionEnded;
        if (handler is null)
        {
            return;
        }

        try
        {
            await handler.Invoke(reason, description);
        }
        catch (Exception notifyError)
        {
            Debug.WriteLine($"{DateTime.Now}OnSessionEnded handler threw for {reason}: {notifyError.Message}");
        }
    }

    private ReconnectInfo BuildReconnectInfo(int? explicitAttempt = null, TimeSpan? delay = null)
    {
        var attempt = explicitAttempt ?? _reconnectAttempts;
        if (attempt < 1) attempt = 1;
        return new ReconnectInfo
        {
            CurrentAttempt = attempt,
            MaxAttempts = config.MaxReconnectAttempts,
            RemainingDelay = delay ?? TimeSpan.Zero,
        };
    }
    
    private bool IsReconnectActive()
    {
        // Reconnect mode is the authoritative source of truth
        // It's set before any reconnect starts and cleared only when connection is stable
        return _reconnectMode != ReconnectMode.None;
    }

    public ConnectionOptions config { get; private set; }

    public RequestManager requestManager = new();

    public ConnectionManager connectionManager = new();

    public Connection(string server, ConnectionOptions? options = null)
    {
        url = server;
        config = options ?? new ConnectionOptions();

        ValidateConfig();
    }

    public async Task ChangeServer(
        string server,
        ConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        SetConnectionState(XrpConnectionState.Connecting, message: $"ChangeServer: Switching to {server}...");

        // =====================================================
        // FAST CHANGE SERVER with PER-SESSION ISOLATION
        // =====================================================
        // Old session is marked as retiring and cleaned up in background.
        // New session is created immediately without waiting.
        // Callbacks check session ID to ignore retiring sessions.

        // 1. Quick state cleanup - stop reconnect loop
        StopReconnectLoop();
        
        // 2. Cancel ping timer and the message processor (but don't wait yet). Stopping the
        // processor is explicit since StopPingTimerSync no longer does it as a side effect - this
        // session's queue goes with the session.
        StopPingTimerSync();

        // 3. Mark old session as retiring (callbacks will be ignored) and clear ws - BEFORE
        // rejecting the pending requests, on purpose.
        // The rejection sweep resumes consumer continuations - inline on this thread when there
        // is no synchronization context - and a consumer that issues its next request from there
        // must already see no usable connection. Cleared afterwards, that request passes the
        // connectivity check on the old socket and is written into it after the sweep that would
        // have rejected it; nothing completes it before RequestTimeout (issue #177).
        ConnectionSession? oldSession;
        lock (_sessionLock)
        {
            oldSession = _activeSession;
            oldSession?.MarkAsRetiring();
        }

        WebSocketClient? oldSocket;
        lock (_disconnectLock)
        {
            oldSocket = ws;
            ws = null;
        }

        // 4. Reject all pending requests BEFORE waiting for ping
        // This allows the ping handler to receive OperationCanceledException and exit quickly
        requestManager.RejectAllWithCancellation();
        connectionManager.RejectAllAwaitingWithCancellation();

        // 5. The message processor goes with the session, and its reader is let go of after the
        // sweep, not before: consumers are released first, and this is the first yield of the
        // switch - what a single-threaded host runs their continuations on.
        await StopMessageProcessorAsync();

        // 6. Now wait for ping to finish (should be very fast since requests were rejected)
        await WaitForPingToFinishAsync();

        // 7. Mark old socket for intentional disconnect (per-socket tracking only)
        // CRITICAL: Do NOT set global _isIntentionalDisconnect = true here - same rule as the ping/network
        // recovery path. The global flag was only reset in OnceOpen, so if the NEW server never came up it
        // stayed set forever: OnConnectionFailed then read the failure of the new socket as a user disconnect,
        // reported "Connection closed permanently." and started no reconnect loop, leaving the client dead
        // with the misleading "No connection attempt in progress. Call Connect() first."
        // Per-socket tracking (_userInitiatedSockets + the socket's own flag, set in RetireOldSessionAsync)
        // already filters late callbacks from the old socket, and keeps global state clean for the new one.
        if (oldSocket != null)
        {
            Interlocked.Exchange(ref _userInitiatedSocket, oldSocket);
            MarkSocketAsUserInitiated(oldSocket);

            // 6. Fire-and-forget GRACEFUL disposal - no blocking
            _ = RetireOldSessionAsync(oldSession, oldSocket);
        }

        // The consumer's subscriptions belonged to the session just retired and do not follow the
        // client to the new server. Nothing else on this path says so - the socket's own close
        // callback is filtered out as a retiring session, and the status notification above reads
        // Connecting, which is what a first connection reports too. Announced before the new
        // connection is opened, so a consumer cannot see OnConnected for the new session and only
        // afterwards learn that the old one is gone.
        await NotifySessionEndedAsync(
            oldSession,
            SessionEndReason.ServerChanged,
            $"Switched to {server}. Subscriptions from the previous connection are no longer in effect.");

        // 7. Update config for new server
        url = server;
        if (options != null)
        {
            config = options;
        }

        ValidateConfig();
        _reconnectAttempts = 0;
        Interlocked.Exchange(ref _connectHandlerFailures, value: 0);

        // 8. Reset permanentlyDisconnected for new connection
        _permanentlyDisconnected = false;

        // Clear the global intentional-disconnect flag explicitly: it may still be set from an earlier
        // user Disconnect() (it is only ever reset in OnceOpen), and leaving it set would make a failure
        // of the NEW connection look intentional and suppress reconnection.
        _isIntentionalDisconnect = false;

        // 9. Immediately connect to new server (new session created in Connect)
        await Connect(cancellationToken);
    }

    /// <summary>
    /// Retires an old session in background. Does not block ChangeServer.
    /// </summary>
    private async Task RetireOldSessionAsync(ConnectionSession? session, WebSocketClient oldSocket)
    {
        try
        {
            oldSocket.SetIntentionalDisconnect();
            await oldSocket.InitiateGracefulCloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // Swallow - fire-and-forget cleanup
        }
        finally
        {
            session?.CompleteSession();
        }
    }

    /// <summary>
    /// Retires current session and reconnects immediately (same flow as ChangeServer).
    /// Used for ping timeout and network drop to avoid slow reconnect with exponential backoff.
    /// </summary>
    private async Task RetireCurrentSessionAndReconnectAsync(string reason)
    {
        // =====================================================
        // CRITICAL: Set reconnect state FIRST so IsReconnectActive() returns true
        // throughout the entire operation, including if Connect() fails.
        // =====================================================
        
        // 1. Set reconnect mode FIRST - this is the authoritative state
        // It will be cleared only when connection is stable (in OnceOpen)
        _reconnectMode = ReconnectMode.FastReconnect;
        _isFastReconnectActive = true; // Keep for backward compatibility
        
        // 2-3. Retire the previous reconnect session and install this one as a single transaction,
        // so a concurrent stop/start cannot dispose the source created here. Cancellation and
        // disposal of the old source happen after the lock is released.
        CancellationTokenSource oldCts;
        CancellationTokenSource ownCts;
        lock (_reconnectStateLock)
        {
            oldCts = _reconnectCts;
            _reconnectLoop = null; // Clear old loop reference so StartReconnectLoop can start a new one
            _reconnectAttempts = 1;
            ownCts = new CancellationTokenSource();
            _reconnectCts = ownCts;
        }

        oldCts?.Cancel();
        oldCts?.Dispose();
        
        // 4. Now send first notification - IsReconnectActive() will return true
        // Consumer handler exceptions are contained inside SetConnectionState - an escaping throw
        // here would leave the source installed above with no loop and nobody to dispose it.
        SetConnectionState(
            XrpConnectionState.RestoringConnection,
            message: $"{reason} Reconnecting immediately...",
            ConnectionCloseSeverity.Warning,
            reconnect: BuildReconnectInfo());

        // =====================================================
        // FAST RECONNECT with PER-SESSION ISOLATION (same as ChangeServer)
        // =====================================================
        // Old session is marked as retiring and cleaned up in background.
        // New session is created immediately without waiting.
        // Callbacks check session ID to ignore retiring sessions.

        // 4. Stop ping timer and the message processor (but don't wait yet) - the queue belongs
        // to the session being retired.
        StopPingTimerSync();

        // 5. Mark old session as retiring (callbacks will be ignored) and clear ws - BEFORE
        // rejecting the pending requests, on purpose.
        // The rejection sweep resumes consumer continuations - inline on this thread when there
        // is no synchronization context - and a consumer that issues its next request from there
        // must already see no usable connection. Cleared afterwards, that request passes the
        // connectivity check on the old socket and is written into it after the sweep that would
        // have rejected it; nothing completes it before RequestTimeout (issue #177).
        ConnectionSession? oldSession;
        lock (_sessionLock)
        {
            oldSession = _activeSession;
            oldSession?.MarkAsRetiring();
        }

        WebSocketClient? oldSocket;
        lock (_disconnectLock)
        {
            oldSocket = ws;
            ws = null;
        }

        // 6. Reject all pending requests BEFORE waiting for ping
        // This allows the ping handler to receive OperationCanceledException and exit quickly
        requestManager.RejectAllWithCancellation();
        connectionManager.RejectAllAwaitingWithCancellation();

        // 7. The message processor goes with the session (see ChangeServer for the ordering).
        await StopMessageProcessorAsync().ConfigureAwait(false);

        // 8. Now wait for the ping to finish. This method runs inside the ping check itself, so
        // the wait returns at once - see WaitForPingToFinishAsync.
        await WaitForPingToFinishAsync().ConfigureAwait(false);

        // 9. Mark old socket for intentional disconnect (per-socket tracking only)
        // CRITICAL: Do NOT set global _isIntentionalDisconnect = true for ping/network recoveries!
        // The global flag would block OnConnectionFailed from processing new connection failures.
        // Instead, rely solely on per-socket tracking (_userInitiatedSockets HashSet) to filter
        // late callbacks from the old socket while keeping global state clean for the new connection.
        if (oldSocket != null)
        {
            // Per-socket tracking - filters late callbacks from this specific socket
            Interlocked.Exchange(ref _userInitiatedSocket, oldSocket);
            MarkSocketAsUserInitiated(oldSocket);
            oldSocket.SetIntentionalDisconnect(); // Suppresses Critical logging in receive loop

            // 9. Fire-and-forget GRACEFUL disposal - no blocking
            _ = RetireOldSessionAsync(oldSession, oldSocket);
        }

        // Same as ChangeServer: the session being retired took the subscriptions with it, and the
        // socket's own close callback will be filtered out as retiring. The RestoringConnection
        // status above reports that the connection is being rebuilt, not that everything bound to
        // the old one is gone - a consumer had to infer the second from the first.
        await NotifySessionEndedAsync(oldSession, SessionEndReason.ConnectionLost, reason)
            .ConfigureAwait(false);

        // 10. Clear ping/network drop socket tracking (old socket is retired)
        // CRITICAL: If not cleared, these stale references would cause OnConnectionFailed
        // to filter callbacks from the NEW socket if Connect() fails, blocking reconnection.
        _pingTimeoutSocket = null;
        _networkDropSocket = null;

        // 11. Reset permanentlyDisconnected for new connection - unless the user asked to disconnect
        // while the awaits above were running. Disconnect() sets the flag, clears the reconnect
        // state and then waits on the ping task this method runs inside, so it is still blocked
        // here and cannot have finished its teardown. Clearing its flag and reconnecting anyway
        // would resurrect a client the consumer explicitly took down - and Disconnect() would
        // return reporting success while a fresh session was being built behind it.
        if (_permanentlyDisconnected)
        {
            CancellationTokenSource abandoned = null;
            lock (_reconnectStateLock)
            {
                if (ReferenceEquals(_reconnectCts, ownCts))
                {
                    abandoned = ownCts;
                    _reconnectCts = null;
                    _reconnectAttempts = 0;
                    _reconnectLoop = null;
                }
            }

            abandoned?.Cancel();
            abandoned?.Dispose();
            _isFastReconnectActive = false;

            Debug.WriteLine($"{DateTime.Now}Fast reconnect abandoned before connecting - the client was disconnected by the user");
            return;
        }

        _permanentlyDisconnected = false;

        // Note: _reconnectAttempts and _reconnectCts already set at the start of this method
        // Global _isIntentionalDisconnect stays false - allows new connection failures to be processed

        // 12. Immediately connect (bypass Connect() which calls StopReconnectLoop)
        // Note: Don't emit Connecting state here - we already emitted RestoringConnection
        // and Connecting would overwrite ReconnectInfo, confusing consuming apps
        try
        {
            // Pass the token of the session this method owns: a user Disconnect() cancels it, so the
            // attempt below stops instead of opening a socket behind a client that was taken down.
            // Disconnect() waits only briefly for the ping task, while acquisition can run much
            // longer, so the flag check above cannot cover this window on its own.
            await ConnectInternalAsync(ownCts.Token).ConfigureAwait(false);
            await WaitForConnectionAsync(config.ConnectionAcquisitionTimeout, ownCts.Token).ConfigureAwait(false);
            
            // Connect succeeded - cleanup reconnect state
            // Note: _reconnectMode will be cleared in OnceOpen when connection is fully established
            _isFastReconnectActive = false;

            // Only tear down the source this method installed. The awaits above give a concurrent
            // path (RestartReconnectLoop from a failing OnConnected handler, say) room to install a
            // newer one; cancelling and disposing that would strand the sequence it belongs to,
            // which is the same wedge the ownership checks in ReconnectLoopAsync guard against.
            // When ownership is lost, ownCts needs no cleanup here: whoever evicted it from the
            // field cancelled and disposed it as part of doing so.
            CancellationTokenSource settled = null;
            lock (_reconnectStateLock)
            {
                if (ReferenceEquals(_reconnectCts, ownCts))
                {
                    settled = ownCts;
                    _reconnectCts = null;
                    _reconnectAttempts = 0;

                    // Drop the task reference in the same transaction, for the same reason
                    // StopReconnectLoop does: a loop may have been started on this very source
                    // while the awaits above were running (OnConnectionFailed sees no live loop -
                    // the entry above cleared the reference - and StartReconnectLoop reuses a
                    // still-valid source). Cancelling that source without clearing the reference
                    // leaves every loopIsRunning check looking at a task that is exiting, so
                    // nobody starts a replacement and nobody reconnects.
                    _reconnectLoop = null;
                }
            }

            settled?.Cancel();
            settled?.Dispose();
        }
        catch (Exception ex)
        {
            // If Connect fails, transition to loop reconnect mode
            // Keep _reconnectMode set (will be LoopReconnect after StartReconnectLoop)
            
            // A user Disconnect() can land while the awaits above are running - and it will wait on
            // the very ping task this method runs inside, so it cannot have finished yet. Handing
            // the client back to a reconnect loop then would undo an explicit disconnect. The flag
            // is the authority: leave the state alone and let Disconnect() finish its teardown.
            if (_permanentlyDisconnected)
            {
                Debug.WriteLine($"{DateTime.Now}Fast reconnect abandoned - the client was disconnected by the user: {ex.Message}");
                return;
            }

            // The wait above ends in cancellation when its own source is retired, and on success the
            // path that retires it is OnceOpen: the attempt above failed at the socket, the failure
            // callback started the reconnect loop on this same source, and that loop connected
            // first. A client that is connected has nothing to reconnect. Treating this as a failure
            // started a second loop, whose first attempt retired the live socket and opened another -
            // one reconnect became two, with a RestoringConnection reported on a healthy client.
            if (IsConnected())
            {
                _isFastReconnectActive = false;
                Debug.WriteLine($"{DateTime.Now}Fast reconnect settled by the reconnect loop: {ex.Message}");
                return;
            }

            // Start the loop BEFORE notifying: SetConnectionState calls into consumer code, and an
            // exception from a handler must not cost us the reconnect loop. Ordering matters more
            // than the message here - without the loop the client never comes back.
            //
            // StartReconnectLoop reuses the source installed above when it is still there. It may
            // not be: the awaits could have let another path replace or clear it, the same way the
            // success branch above can no longer assume it still owns ownCts. Either outcome is
            // survivable here - a live foreign loop makes the call return early, a cleared source
            // makes it start a fresh sequence (losing only the seeded first delay) - so this path
            // does not need an ownership check of its own.
            StartReconnectLoop();

            SetConnectionState(
                XrpConnectionState.RestoringConnection,
                message: $"Reconnection failed: {ex.Message}. Retrying...",
                ConnectionCloseSeverity.Warning,
                reconnect: BuildReconnectInfo());
        }
    }

    public bool IsConnected() => State() == WebSocketState.Open;

    public async Task WaitForConnectionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (IsConnected())
        {
            return;
        }

        CheckIfNotConnected();

        var waitTimeout = timeout ?? config.ConnectionAcquisitionTimeout;

        if (waitTimeout != Timeout.InfiniteTimeSpan && waitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(timeout),
                message:
                $"Timeout must be positive or Timeout.InfiniteTimeSpan, but was {waitTimeout.TotalSeconds:F1}s");
        }

        var startTime = DateTime.UtcNow;
        var checkInterval = TimeSpan.FromMilliseconds(100);
        var hasTimeout = waitTimeout != Timeout.InfiniteTimeSpan;

        while (!IsConnected())
        {
            // Re-checked on every iteration, not only on entry: the client can be disconnected while a
            // caller is already waiting here (user Disconnect(), or the client giving up on a permanently
            // failing OnConnected handler). Without this the caller would sit out the whole acquisition
            // timeout and get a generic TimeoutException instead of the actual reason.
            if (_permanentlyDisconnected)
            {
                throw new NotConnectedException("Client has been disconnected. Call Connect() to reconnect.");
            }

            if (config.StopAfterMaxAttempts &&
                _reconnectAttempts >= config.MaxReconnectAttempts &&
                _reconnectCts == null)
            {
                throw new NotConnectedException(
                    $"Connection failed permanently after {config.MaxReconnectAttempts} attempts. " +
                    "Reconnection has been stopped.");
            }

            if (hasTimeout && DateTime.UtcNow - startTime > waitTimeout)
            {
                throw new System.TimeoutException(
                    $"Connection was not established within {waitTimeout.TotalSeconds:F1} seconds");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(message: "Connection wait was cancelled", cancellationToken);
            }

            try
            {
                await Task.Delay(checkInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException(message: "Connection wait was cancelled", cancellationToken);
            }
        }
    }

    public async Task<bool> HasConnectionAsync(TimeSpan? timeout = null)
    {
        try
        {
            await WaitForConnectionAsync(timeout, CancellationToken.None);
            return true;
        }
        catch (System.TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public Timer timer;

    public async Task Connect(CancellationToken cancellationToken)
    {
        if (IsConnected())
        {
            SetConnectionState(XrpConnectionState.Connected, message: $"Already connected to {url}");
            return;
        }

        StopReconnectLoop();
        Interlocked.Exchange(ref _connectHandlerFailures, value: 0);
        SetConnectionState(XrpConnectionState.Connecting, message: $"Connecting to {url}...");
        await ConnectInternalAsync();
        await WaitForConnectionAsync(config.ConnectionAcquisitionTimeout, cancellationToken);
    }

    private async Task ConnectInternalAsync(CancellationToken ct = default)
    {
        _permanentlyDisconnected = false;
        await _connectLock.WaitAsync(ct);
        try
        {
            // Check cancellation before proceeding
            ct.ThrowIfCancellationRequested();
            
            if (IsConnected())
            {
                return;
            }

            if (State() == WebSocketState.Connecting)
            {
                await connectionManager.AwaitConnection();
                return;
            }

            if (url == null)
            {
                throw new ConnectionException("Cannot connect because no server was specified");
            }

            if (this.ws != null)
            {
                throw new XrplException("Websocket connection never cleaned up.");
            }

            // Check cancellation again before creating WebSocket
            ct.ThrowIfCancellationRequested();
            
            this.ws = CreateWebSocket(url, config);
            _lastActiveSocket = this.ws;
            var capturedSocket = this.ws;

            // Check cancellation AFTER creating WebSocket - if cancelled, close the socket and exit
            if (ct.IsCancellationRequested)
            {
                try
                {
                    capturedSocket?.SetIntentionalDisconnect();
                    _ = capturedSocket?.InitiateGracefulCloseAsync();
                }
                catch { /* swallow */ }
                finally
                {
                    this.ws = null;
                }
                ct.ThrowIfCancellationRequested();
            }

            // Create session for this connection
            var newSession = new ConnectionSession(this.ws);
            lock (_sessionLock)
            {
                _activeSession = newSession;
            }

            var capturedSession = newSession;

            timer = new Timer(config.ConnectionAttemptTimeout.TotalMilliseconds);
            timer.Elapsed += async (sender, e) =>
            {
                try
                {
                    await OnConnectionFailed(
                        error: new ConnectionException(
                            $"Error: connect() timed out after {config.ConnectionAttemptTimeout.TotalSeconds:F1} seconds. If your internet connection is working, the rippled server may be blocked or inaccessible. You can also try setting the 'ConnectionAttemptTimeout' option in the Client constructor."),
                        capturedSocket,
                        capturedSession.SessionId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now}Connection timer error: {ex.Message}");
                }
            };
            timer.Start();
            if (this.ws == null)
            {
                throw new XrplException("Connect: created null websocket");
            }

            ws.OnConnect(async (connectedSocket) =>
            {
                try
                {
                    await OnceOpen(connectedSocket, capturedSession.SessionId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now}OnConnect callback error: {ex.Message}");
                }
            });

            var capturedTimer = timer;
            ws.OnConnectionError(async (e, errorSocket) =>
            {
                try
                {
                    // Only stop timer if this is the socket that owns it
                    if (errorSocket == capturedSocket)
                    {
                        capturedTimer?.Stop();
                    }

                    await OnConnectionFailed(e, errorSocket, capturedSession.SessionId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now}OnConnectionError callback error: {ex.Message}");
                }
            });

            ws.OnError(async (e, errorSocket) =>
            {
                try
                {
                    // Report-only: a failed send does not by itself mean the connection is gone, so this
                    // path never triggers a reconnect. Without it a fire-and-forget send failure would be
                    // invisible and the request would simply sit until its RequestTimeout expires.
                    var errorHandler = OnError;
                    if (errorHandler is not null)
                    {
                        await errorHandler.Invoke(
                            error: "error",
                            errorMessage: "socketSendError",
                            e.Message,
                            data: e);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now}OnError callback error: {ex.Message}");
                }
            });

            // Bound to the binary callback rather than the string one: the frame is already UTF-8
            // and that is what the JSON reader wants, so the UTF-16 copy of every message - twice
            // the byte length, on the large object heap for a big response - is never made.
            ws.OnBinaryMessage(async (m, ws) =>
            {
                try
                {
                    // Use fast-path processing to prioritize ping/pong responses
                    // and prevent head-of-line blocking from high-volume stream data.
                    // The session travels with the frame so a late arrival from a socket being
                    // retired can be told apart from one on the live connection - see
                    // EnqueueStreamMessage.
                    await IOnMessageFastPath(m, capturedSession.SessionId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now}OnBinaryMessage callback error: {ex.Message}");
                }
            });
            ws.OnDisconnect(async (closeStatus, closeDescription, closingSocket) =>
            {
                try
                {
                    // Only stop timer if this is the socket that owns it
                    if (closingSocket == capturedSocket)
                    {
                        capturedTimer?.Stop();
                    }

                    var code = (int?)closeStatus;
                    await OnceClose(code, closeDescription, closingSocket, capturedSession.SessionId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now}OnDisconnect callback error: {ex.Message}");
                }
            });

            await this.ws.Connect();

            connectionManager.AwaitConnection();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<int> Disconnect()
    {
        _isIntentionalDisconnect = true;
        _permanentlyDisconnected = true;

        // Capture the socket and clear ws BEFORE rejecting the pending requests - see ChangeServer
        // for why: the sweep runs consumer continuations, and a request issued from one of them
        // must not find the socket being closed still installed as the connection (issue #177).
        WebSocketClient? socketToClose;
        lock (_disconnectLock)
        {
            socketToClose = ws;
            ws = null;

            if (socketToClose != null)
            {
                MarkSocketAsUserInitiated(socketToClose);
                socketToClose.SetIntentionalDisconnect();

                if (_disconnectTcs == null || _disconnectTcs.Task.IsCompleted)
                {
                    _disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        ClearReconnectState(); // Clear all reconnect state on user disconnect
        StopPingTimerSync();

        // Reject pending requests so ping handler can exit quickly
        requestManager.RejectAllWithCancellation();
        connectionManager.RejectAllAwaitingWithCancellation();

        await StopMessageProcessorAsync();
        await WaitForPingToFinishAsync();

        if (socketToClose == null)
        {
            SetConnectionState(XrpConnectionState.Disconnected, message: "Already disconnected.");
            return 0;
        }

        Interlocked.Exchange(ref _userInitiatedSocket, socketToClose);
        CloseSocketIntentionally(socketToClose);

        SetConnectionState(XrpConnectionState.Disconnected, message: "Disconnected by user request.");

        return 0;
    }

    /// <summary>
    /// Disconnects and waits for the WebSocket to be fully closed and cleaned up.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for cleanup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DisconnectAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _isIntentionalDisconnect = true;
        _permanentlyDisconnected = true;

        // Same ordering as Disconnect(): the socket leaves ws before the sweep runs (issue #177).
        TaskCompletionSource<bool>? tcs = null;
        WebSocketClient? socketToClose;
        lock (_disconnectLock)
        {
            socketToClose = ws;
            ws = null;

            if (socketToClose != null)
            {
                MarkSocketAsUserInitiated(socketToClose);
                socketToClose.SetIntentionalDisconnect();

                if (_disconnectTcs == null || _disconnectTcs.Task.IsCompleted)
                {
                    _disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                tcs = _disconnectTcs;
            }
        }

        ClearReconnectState(); // Clear all reconnect state on user disconnect
        StopPingTimerSync();

        // Reject pending requests so ping handler can exit quickly
        requestManager.RejectAllWithCancellation();
        connectionManager.RejectAllAwaitingWithCancellation();

        await StopMessageProcessorAsync();
        await WaitForPingToFinishAsync();

        if (socketToClose == null || tcs == null)
        {
            SetConnectionState(XrpConnectionState.Disconnected, message: "Already disconnected.");
            return;
        }

        Interlocked.Exchange(ref _userInitiatedSocket, socketToClose);

        SetConnectionState(XrpConnectionState.Disconnected, message: "Disconnected by user request.");

        // Start disconnect async - it waits for receive loop which calls OnceClose
        // OnceClose will complete tcs, so both should complete around the same time
        var disconnectTask = CloseSocketIntentionallyAsync(socketToClose);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            // Wait for disconnectTask to complete (or timeout)
            // disconnectTask awaits receive loop, which awaits CallOnDisconnectedAsync(OnceClose)
            // OnceClose calls CompleteDisconnectTcs(), so tcs is completed before disconnectTask finishes
            var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);

            // Wait for disconnectTask or timeout
            var completedTask = await Task.WhenAny(disconnectTask, timeoutTask);

            if (completedTask != disconnectTask)
            {
                // Timeout - force complete TCS
                CompleteDisconnectTcs();
            }

            // If disconnectTask completed, OnceClose already called CompleteDisconnectTcs
            // No need to call it again
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            CompleteDisconnectTcs();
        }
        catch
        {
            CompleteDisconnectTcs();
        }
    }

    private void CompleteDisconnectTcs()
    {
        lock (_disconnectLock)
        {
            _disconnectTcs?.TrySetResult(true);
            _disconnectTcs = null;
        }
    }

    private void MarkSocketAsUserInitiated(WebSocketClient socket)
    {
        lock (_userInitiatedSocketsLock)
        {
            _userInitiatedSockets.Add(socket);
        }
    }

    private bool IsSocketUserInitiated(WebSocketClient? socket)
    {
        if (socket == null)
        {
            return false;
        }

        lock (_userInitiatedSocketsLock)
        {
            return _userInitiatedSockets.Contains(socket);
        }
    }

    private void RemoveFromUserInitiatedSockets(WebSocketClient? socket)
    {
        if (socket == null)
        {
            return;
        }

        lock (_userInitiatedSocketsLock)
        {
            _userInitiatedSockets.Remove(socket);
        }
    }

    /// <summary>
    /// Closes the socket with intentional disconnect flag set.
    /// This ensures the WebSocketClient receive loop won't call error callbacks.
    /// Use this for user-initiated disconnects (Disconnect, ChangeServer, Dispose).
    /// </summary>
    private void CloseSocketIntentionally(WebSocketClient socket)
    {
        socket.CancelIntentionally();
        socket.Disconnect();
    }

    /// <summary>
    /// Closes the socket with intentional disconnect flag set and waits for completion.
    /// This ensures the receive loop has fully exited before returning.
    /// Use this when you need to guarantee socket cleanup before proceeding.
    /// </summary>
    private async Task CloseSocketIntentionallyAsync(WebSocketClient socket)
    {
        socket.CancelIntentionally();
        await socket.DisconnectAsync().ConfigureAwait(false);
    }

    private static bool IsNetworkDropException(Exception error)
    {
        // Only classify transport-layer exceptions as network drops
        // TLS/auth/certificate errors should NOT be classified as network drops
        
        if (error is ObjectDisposedException)
            return true;

        // SocketException = transport-level issue (DNS, connection refused, timeout)
        if (error is System.Net.Sockets.SocketException)
            return true;
        
        // IOException - check for transport messages or SocketException inner
        if (error is IOException ioEx)
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
                
            return false;
        }

        // System.TimeoutException - always network-related
        if (error is System.TimeoutException)
            return true;
        
        // Xrpl.Client.Exceptions.TimeoutException - ping timeout from RequestManager
        // This indicates network stall, not a server error
        if (error is Xrpl.Client.Exceptions.TimeoutException)
            return true;

        if (error is TaskCanceledException tce && tce.InnerException != null)
            return IsNetworkDropException(tce.InnerException);
        
        if (error is OperationCanceledException oce && oce.InnerException != null)
            return IsNetworkDropException(oce.InnerException);
        
        // WebSocketException - check inner chain and message patterns
        if (error is System.Net.WebSockets.WebSocketException wsEx)
        {
            // WebSocketException wrapping any transport exception in chain
            if (wsEx.InnerException != null && IsNetworkDropException(wsEx.InnerException))
                return true;
            
            // Check message for common network error patterns
            var msg = wsEx.Message;
            if (msg.Contains("Unable to connect") ||
                msg.Contains("connect to the remote server") ||
                msg.Contains("connection was closed") ||
                msg.Contains("Connection reset"))
                return true;
                
            return false;
        }
        
        // HttpRequestException - check inner chain and message patterns
        if (error is System.Net.Http.HttpRequestException httpEx)
        {
            // HttpRequestException wrapping any transport exception in chain
            if (httpEx.InnerException != null && IsNetworkDropException(httpEx.InnerException))
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
                
            return false;
        }

        // Check for platform-specific HRESULTs on any exception type
        var hresult = error.HResult;
        if (hresult == unchecked((int)0x80072EE2) || // ERROR_WINHTTP_TIMEOUT
            hresult == unchecked((int)0x80072EFD) || // ERROR_WINHTTP_CANNOT_CONNECT
            hresult == unchecked((int)0x80072EE7) || // ERROR_WINHTTP_NAME_NOT_RESOLVED  
            hresult == unchecked((int)0x80072EFE) || // ERROR_WINHTTP_CONNECTION_ERROR
            hresult == unchecked((int)0x80072F78) || // ERROR_WINHTTP_CONNECTION_RESET
            hresult == unchecked((int)0x80004005) || // E_FAIL - generic failure, often wraps network errors
            hresult == unchecked((int)0xFFFDFFFF))   // iOS/macOS DNS failure
        {
            // For E_FAIL (0x80004005), only treat as network if message matches
            if (hresult == unchecked((int)0x80004005))
            {
                var msg = error.Message;
                if (msg.Contains("Unable to connect") ||
                    msg.Contains("connect to the remote server"))
                    return true;
                // E_FAIL with other messages might be TLS/auth - check inner
                if (error.InnerException != null)
                    return IsNetworkDropException(error.InnerException);
                return false;
            }
            return true;
        }
        
        // Check message patterns on any exception type as last resort
        var exMsg = error.Message;
        if (exMsg.Contains("nodename nor servname") ||  // iOS/macOS DNS failure
            exMsg.Contains("Name or service not known") || // Linux DNS failure
            exMsg.Contains("No such host is known"))  // Windows DNS failure
            return true;

        // Check inner exception for wrapped transport errors
        if (error.InnerException != null)
            return IsNetworkDropException(error.InnerException);

        return false;
    }

    private async Task OnConnectionFailed(
        Exception error,
        WebSocketClient? errorSocket = null,
        long sessionId = 0,
        bool isPingTimeoutReconnect = false,
        bool isNetworkDropReconnect = false)
    {
        // If this is a late callback from the socket closed due to ping timeout, ignore it
        // (but not the initial call from ping handler which has isPingTimeoutReconnect=true)
        if (_pingTimeoutSocket != null && _pingTimeoutSocket == errorSocket && !isPingTimeoutReconnect)
        {
            return;
        }

        // If this is a late callback from the socket closed due to network drop, ignore it
        // (but not the initial call which has isNetworkDropReconnect=true)
        if (_networkDropSocket != null && _networkDropSocket == errorSocket && !isNetworkDropReconnect)
        {
            return;
        }

        // Detect network drop via socket's FailureReason or exception type
        var isNetworkDrop = isNetworkDropReconnect || 
                            IsNetworkDropException(error) ||
                            (errorSocket?.FailureReason == SocketFailureReason.NetworkDrop);

        var currentUserInitiatedSocket = Volatile.Read(ref _userInitiatedSocket);
        bool userInitiated;
        bool intentionalDisconnect;
        bool wasOpen;
        bool isCurrentSocket;
        var isRetiringSession = false;

        if (errorSocket != null)
        {
            // Check if this callback is from a retiring session
            lock (_sessionLock)
            {
                if (sessionId > 0)
                {
                    if (_activeSession != null)
                    {
                        if (_activeSession.SessionId == sessionId)
                        {
                            // Same session - check if marked as retiring
                            isRetiringSession = _activeSession.IsRetiring;
                        }
                        else
                        {
                            // Different session - old callback
                            isRetiringSession = true;
                        }
                    }
                }
                else
                {
                    // Fallback for callbacks without session ID (timer timeout)
                    var activeSession = _activeSession;
                    if (activeSession != null)
                    {
                        if (activeSession.Socket != errorSocket)
                        {
                            isRetiringSession = true;
                        }
                        else if (activeSession.IsRetiring)
                        {
                            isRetiringSession = true;
                        }
                    }
                }
            }

            isCurrentSocket = ws == errorSocket;
            userInitiated = currentUserInitiatedSocket == errorSocket || IsSocketUserInitiated(errorSocket);
            intentionalDisconnect = _isIntentionalDisconnect || userInitiated || isRetiringSession;
            wasOpen = errorSocket.State == WebSocketState.Open;

            // Clean up HashSet tracking for this socket (prevent memory leak)
            RemoveFromUserInitiatedSockets(errorSocket);

            // Clear _userInitiatedSocket if it matches this socket
            Interlocked.CompareExchange(ref _userInitiatedSocket, value: null, errorSocket);

            // For stale sockets (not current) or retiring sessions, do minimal cleanup
            if ((!isCurrentSocket && ws != null) || isRetiringSession)
            {
                // This is a late callback from an old socket - don't touch current connection
                if (intentionalDisconnect)
                {
                    CloseSocketIntentionally(errorSocket);
                    CompleteDisconnectTcs();
                }
                else
                {
                    errorSocket.Cancel();
                    errorSocket.Disconnect();
                }

                return;
            }

            // Only stop timer for current socket
            timer?.Stop();
            timer?.Dispose();
            timer = null;

            // Use CloseSocketIntentionally for intentional disconnect, ping timeout, or network drop
            // to suppress Critical error logging in WebSocketClient receive loop
            if (intentionalDisconnect || isPingTimeoutReconnect || isNetworkDrop)
            {
                // Track network drop socket for filtering late callbacks
                if (isNetworkDrop && !isPingTimeoutReconnect && !intentionalDisconnect)
                {
                    _networkDropSocket = errorSocket;
                }

                CloseSocketIntentionally(errorSocket);
            }
            else
            {
                errorSocket.Cancel();
                errorSocket.Disconnect();
            }

            if (isCurrentSocket)
            {
                ws = null;
            }
        }
        else
        {
            isCurrentSocket = true; // null errorSocket means operate on current ws
            intentionalDisconnect = _isIntentionalDisconnect || currentUserInitiatedSocket != null;
            wasOpen = false;

            // Only stop timer when operating on current connection
            timer?.Stop();
            timer?.Dispose();
            timer = null;

            // For null errorSocket with intentional disconnect, still need to clean up ws reference
            if (intentionalDisconnect && ws != null)
            {
                CloseSocketIntentionally(ws);
                ws = null;
            }
        }

        CompleteDisconnectTcs();

        if (intentionalDisconnect)
        {
            connectionManager.RejectAllAwaitingWithCancellation();
            SetConnectionState(XrpConnectionState.Disconnected, message: "Connection closed permanently.");
            return;
        }

        // Reject awaiting connection requests and pending requests
        // For ping timeout and network drop, use cancellation (no Critical logging in consuming apps)
        // For other failures, use exception with message
        if (isPingTimeoutReconnect || isNetworkDrop)
        {
            requestManager.RejectAllWithCancellation();
            connectionManager.RejectAllAwaitingWithCancellation();
        }
        else
        {
            connectionManager.RejectAllAwaiting(new NotConnectedException(error.Message));
        }

        // For ping timeout or network drop, use Warning severity and RestoringConnection state
        // For other failures, use Error severity and Disconnected state
        if (isPingTimeoutReconnect)
        {
            SetConnectionState(
                XrpConnectionState.RestoringConnection,
                message: "Ping failed. Reconnecting...",
                ConnectionCloseSeverity.Warning,
                reconnect: BuildReconnectInfo());
        }
        else if (isNetworkDrop)
        {
            SetConnectionState(
                XrpConnectionState.RestoringConnection,
                message: "Network connection lost. Reconnecting...",
                ConnectionCloseSeverity.Warning,
                reconnect: BuildReconnectInfo());
        }
        else
        {
            // Check if we're in a reconnect flow using the authoritative _reconnectMode flag
            // This is set before any reconnect starts and cleared only when connection is stable
            if (IsReconnectActive())
            {
                // During reconnect, use RestoringConnection with ReconnectInfo and Warning severity
                var reconnectErrorMessage = $"Connection attempt failed: {error.Message}";
                SetConnectionState(
                    XrpConnectionState.RestoringConnection,
                    reconnectErrorMessage,
                    ConnectionCloseSeverity.Warning,
                    reconnect: BuildReconnectInfo());
            }
            else
            {
                // True initial connection failure - no reconnect in progress
                var errorMessage = $"Initial connection failed: {error.Message}";
                SetConnectionState(XrpConnectionState.Disconnected, errorMessage, ConnectionCloseSeverity.Error);
            }
        }

        // Start reconnect for initial connection failures, ping timeout, or network drop
        // For ping timeout/network drop, wasOpen=true but we still need to reconnect
        if (!wasOpen || isPingTimeoutReconnect || isNetworkDrop)
        {
            if (OnDisconnect is not null)
            {
                // For ping timeout and network drop, use neutral message to avoid Critical logging
                // in consuming apps that log OnDisconnect messages as errors
                var disconnectMessage = isPingTimeoutReconnect
                    ? "Connection lost, reconnecting..."
                    : isNetworkDrop
                        ? "Network connection lost, reconnecting..."
                        : error.Message;
                await OnDisconnect?.Invoke(code: null, disconnectMessage)!;
            }

            // Only start reconnect loop if not already running
            // This prevents _reconnectAttempts from being reset when OnConnectionFailed
            // is called from within the reconnect loop (each failed attempt triggers this callback)
            // Check both _reconnectMode AND actual loop task status for accuracy
            var loopIsRunning = _reconnectLoop != null && !_reconnectLoop.IsCompleted;
            if (!loopIsRunning)
            {
                StartReconnectLoop();
            }
        }
    }

    /// <summary>
    /// Sends a message through the WebSocket connection.
    /// </summary>
    /// <param name="ws">The WebSocket client to send through.</param>
    /// <param name="message">The message to send.</param>
    /// <exception cref="DisconnectedException">Thrown when the WebSocket connection is null or closed.</exception>
    public void WebsocketSendAsync(WebSocketClient ws, string message)
    {
        if (ws == null)
            throw new DisconnectedException("WebSocket connection was closed before request could be sent");
        ws.SendMessage(message);
    }

    private async Task EnsureConnectionForRequest(RequestFailurePolicy? policyOverride = null, CancellationToken cancellationToken = default)
    {
        if (ShouldBeConnected())
        {
            return;
        }

        CheckIfNotConnected();

        var policy = policyOverride ?? config.RequestPolicy;

        switch (policy)
        {
            case RequestFailurePolicy.ImmediateFail:
                throw new NotConnectedException();

            case RequestFailurePolicy.WaitForConnection:
                await WaitForConnectionAsync(cancellationToken: cancellationToken);
                if (!ShouldBeConnected())
                {
                    throw new NotConnectedException("Failed to establish connection within timeout period");
                }

                break;

            default:
                throw new NotConnectedException();
        }
    }

    private void CheckIfNotConnected()
    {
        if (_permanentlyDisconnected)
        {
            throw new NotConnectedException("Client has been disconnected. Call Connect() to reconnect.");
        }

        // Connecting or RestoringConnection states indicate an active attempt even if ws is null
        var isActiveState = _currentConnectionState == XrpConnectionState.Connecting ||
                            _currentConnectionState == XrpConnectionState.RestoringConnection;
        var noConnectionAttemptActive = ws == null && _reconnectCts == null && !isActiveState;
        if (noConnectionAttemptActive)
        {
            throw new NotConnectedException("No connection attempt in progress. Call Connect() first.");
        }
    }

    /// <summary>
    /// rippled requires both <c>admin_user</c> and <c>admin_password</c>; a half-configured pair sends neither.
    /// </summary>
    private AdminCredentials? GetAdminCredentials() =>
        string.IsNullOrEmpty(config.AdminUser) || string.IsNullOrEmpty(config.AdminPassword)
            ? null
            : new AdminCredentials(config.AdminUser, config.AdminPassword);

    public async Task<XrplResponse<Dictionary<string, object>>> Request(
        Dictionary<string, object> request,
        TimeSpan? timeout = null,
        RequestFailurePolicy? policyOverride = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectionForRequest(policyOverride, cancellationToken);

        var _request = requestManager.CreateRequest(request, timeout: timeout ?? config.RequestTimeout, adminCredentials: GetAdminCredentials(), cancellationToken: cancellationToken);
        try
        {
            WebsocketSendAsync(ws, _request.Message);
        }
        catch (Exception error) when (error is EncodingFormatException or DisconnectedException)
        {
            // The connection can be retired between the check above and this send. The request
            // never left, so it must not stay pending until RequestTimeout: the rejection is what
            // the await below surfaces to the caller.
            requestManager.Reject(_request.Id, error);
        }

        object resolved = await _request.Promise;
        return XrplResponse.From<Dictionary<string, object>>(resolved);
    }

    public async Task<XrplResponse<T>> GRequest<T, R>(
        R request,
        TimeSpan? timeout = null,
        RequestFailurePolicy? policyOverride = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectionForRequest(policyOverride, cancellationToken);

        var _request = requestManager.CreateGRequest<T, R>(request, timeout: timeout ?? config.RequestTimeout, adminCredentials: GetAdminCredentials(), cancellationToken: cancellationToken);
        try
        {
            WebsocketSendAsync(ws, _request.Message);
        }
        catch (Exception error) when (error is EncodingFormatException or DisconnectedException)
        {
            // The connection can be retired between the check above and this send. The request
            // never left, so it must not stay pending until RequestTimeout: the rejection is what
            // the await below surfaces to the caller.
            requestManager.Reject(_request.Id, error);
        }

        object resolved = await _request.Promise;
        return XrplResponse.From<T>(resolved);
    }

    public string GetUrl() => url;

    public WebSocketState State() => ws?.State ?? WebSocketState.Closed;

    private bool ShouldBeConnected() => ws is { State: WebSocketState.Open, };

    private async Task OnceOpen(WebSocketClient connectedSocket, long sessionId)
    {
        // Check if this callback is from the active session (not retiring)
        bool isActiveSession;
        ConnectionSession? openedSession = null;
        lock (_sessionLock)
        {
            isActiveSession = _activeSession != null &&
                              _activeSession.SessionId == sessionId &&
                              !_activeSession.IsRetiring;

            if (isActiveSession)
            {
                openedSession = _activeSession;
            }
        }

        if (!isActiveSession) // Callback from a retired session - ignore silently
        {
            return;
        }

        // Verify the connected socket matches current ws, or update ws if it was cleared
        if (ws == null)
        {
            // Restore ws reference from the connected socket
            ws = connectedSocket;
        }
        else if (ws != connectedSocket)
        {
            // This is a stale callback from an old socket, ignore it silently
            // Don't touch the timer - it belongs to the new connection
            return;
        }

        // Only stop timer for current socket's callback
        timer?.Stop();
        timer?.Dispose();
        timer = null;

        // Clear all reconnect state - connection is now stable
        ClearReconnectState();

        // Reset all intentional disconnect tracking now that new connection succeeded
        // This is the safe place to clear these - old socket callbacks will have already 
        // seen _isIntentionalDisconnect = true (set by ChangeServer/Disconnect before this point)
        _isIntentionalDisconnect = false;
        _pingTimeoutSocket = null; // Clear ping timeout socket tracking
        _networkDropSocket = null; // Clear network drop socket tracking
        Interlocked.Exchange(ref _userInitiatedSocket, value: null);
        lock (_userInitiatedSocketsLock)
        {
            _userInitiatedSockets.Clear();
        }

        connectedSocket.ResetIntentionalDisconnect();

        // The session has a socket that connected, and only from here can it hold subscriptions -
        // which is what makes its end worth announcing. Sessions are created before the connect
        // attempt, so without this a server that is down would announce one ended session per
        // retry, each of them a connection that never was.
        openedSession.MarkAsOpened();

        // Before ResolveAllAwaiting and before the OnConnected callback, because both hand control
        // to consumer code that subscribes - and the node can answer that subscription while the
        // handler is still running. Frames arriving with no channel take the fallback: outside the
        // capacity, uncounted by DroppedStreamMessages and dispatched concurrently, so the first
        // events after connecting were exactly the ones that could arrive out of order.
        StartMessageProcessor();

        try
        {
            connectionManager.ResolveAllAwaiting();
            if (OnConnected is not null)
            {
                await OnConnected?.Invoke();
            }

            Interlocked.Exchange(ref _connectHandlerFailures, value: 0);
            SetConnectionState(XrpConnectionState.Connected, message: $"Connected {url}");
        }
        catch (Exception error)
        {
            connectionManager.RejectAllAwaiting(error);
            await OnConnectHandlerFailedAsync(connectedSocket, error);
            return; // Don't start ping timer if connection failed
        }

        // Start ping timer AFTER connection is fully established and all callbacks completed
        // This is outside try/catch to ensure it always runs on successful connection
        StartPingTimer();
    }

    /// <summary>
    /// Handles an exception thrown by a consumer <see cref="OnConnected"/> handler.
    /// <para>
    /// A failing handler is a CONNECTION failure, not a user disconnect. Calling <see cref="Disconnect"/> here
    /// would set the permanent-disconnect flag and clear the reconnect state, stranding the client forever:
    /// no reconnect loop is restarted, no new socket is ever opened and every later request fails with
    /// <see cref="NotConnectedException"/>. This is a very reachable scenario - restoring subscriptions in
    /// <see cref="OnConnected"/> fails whenever the node accepts TCP before it starts serving requests.
    /// </para>
    /// <para>
    /// Instead the socket is torn down as a transport failure so the regular reconnect loop (with exponential
    /// backoff) brings the client back. A handler that keeps failing is bounded by
    /// <see cref="ConnectionOptions.MaxReconnectAttempts"/> when
    /// <see cref="ConnectionOptions.StopAfterMaxAttempts"/> is set, so a broken consumer cannot spin forever.
    /// </para>
    /// </summary>
    /// <param name="failedSocket">The socket whose <see cref="OnConnected"/> handler threw.</param>
    /// <param name="error">The exception thrown by the handler.</param>
    private async Task OnConnectHandlerFailedAsync(WebSocketClient failedSocket, Exception error)
    {
        int failures = Interlocked.Increment(ref _connectHandlerFailures);

        Debug.WriteLine($"{DateTime.Now}OnConnected handler failed ({failures}): {error.Message}");

        var errorHandler = OnError;
        if (errorHandler is not null)
        {
            try
            {
                await errorHandler
                    .Invoke(error: "error", errorMessage: "connectHandlerError", error.Message, data: error)
                    .ConfigureAwait(false);
            }
            catch (Exception notifyError)
            {
                Debug.WriteLine($"{DateTime.Now}OnError handler threw while reporting OnConnected failure: {notifyError.Message}");
            }
        }

        bool giveUp = config.StopAfterMaxAttempts && failures >= config.MaxReconnectAttempts;
        if (giveUp)
        {
            // Terminal state on purpose: the handler is broken, not the connection. Disconnect() gives the
            // consumer an immediate, actionable NotConnectedException instead of a silent 5-minute wait,
            // and Connect() resets the counter so recovery stays possible.
            // The detailed reason has to be notified BEFORE Disconnect(): Disconnect() moves the state to
            // Disconnected itself, and SetConnectionState only notifies on a state change, so a call after it
            // would be swallowed and the consumer would see "Disconnected by user request." instead.
            SetConnectionState(
                XrpConnectionState.Disconnected,
                message:
                $"OnConnected handler failed {failures} time(s) in a row: {error.Message}. Giving up after {config.MaxReconnectAttempts} attempts. Call Connect() to retry.",
                ConnectionCloseSeverity.Error);

            // Rejected here, before Disconnect(), and with the reason that is actually true. The
            // requests in flight are being stopped because this client gave up connecting, not
            // because anyone cancelled them - and Disconnect() rejects with cancellation, which is
            // right for a close the caller asked for and wrong for a failure. Connect() is where
            // that difference shows: it is two operations, the connection and the server_info that
            // SetNetworkId sends straight after, and the socket really does open for a moment
            // before a failing handler brings it down. A caller that got as far as the second
            // operation was told its own request had been cancelled, having cancelled nothing.
            requestManager.RejectAll(new NotConnectedException(
                $"Gave up connecting to {url}: the OnConnected handler failed {failures} time(s) in a row. " +
                $"Call Connect() to retry."));

            await Disconnect();
            return;
        }

        SetConnectionState(
            XrpConnectionState.RestoringConnection,
            message: $"OnConnected handler failed: {error.Message}. Reconnecting...",
            ConnectionCloseSeverity.Warning,
            reconnect: BuildReconnectInfo(failures));

        StopPingTimerSync();

        // Always tear down the socket the handler actually ran for. WebSocketClient.Connect invokes its
        // OnConnect callback without awaiting it, so the connect lock can be released while this method is
        // still running: by now `ws` may already point at a newer socket that must not be touched.
        // Cleared before the sweep below, for the reason given in ChangeServer (issue #177): the
        // socket is open, and a request issued from a rejected continuation would otherwise go into it.
        bool wasCurrentSocket;
        lock (_disconnectLock)
        {
            wasCurrentSocket = ReferenceEquals(ws, failedSocket);
            if (wasCurrentSocket)
            {
                ws = null;
            }
        }

        requestManager.RejectAllWithCancellation();
        await StopMessageProcessorAsync();
        await WaitForPingToFinishAsync();

        // The socket is deliberately NOT marked as user-initiated: OnceClose must treat this as a real
        // close so the standard reconnect path runs instead of the "closed permanently" branch.
        failedSocket.Cancel();
        failedSocket.Disconnect();

        if (!wasCurrentSocket)
        {
            // A newer connection already replaced this socket - it owns the reconnect state now.
            return;
        }

        // Take ownership of the reconnect state instead of asking "is a loop already running?".
        // This method can run inside the reconnect loop's own attempt: that loop breaks as soon as the
        // socket reports Open, which happens before the handler has even finished failing. Both this check
        // and the one in OnceClose would then race with the loop's exit, and losing the race leaves nobody
        // reconnecting - the very wedge this path exists to prevent. Cancel whatever is there, start fresh;
        // the later OnceClose sees a live loop and correctly stands down.
        // Seed the attempt counter with the consecutive-failure count. StopReconnectLoop zeroes
        // _reconnectAttempts and a fresh sequence would zero it again, and CalcBackoff derives the
        // delay from that counter alone — so without the seed every handler failure would restart
        // the backoff at ReconnectBaseDelay. With StopAfterMaxAttempts = false (no give-up branch)
        // that means connect -> handler failure -> teardown forever at a constant 2s, a sustained
        // connection load on a node that accepts TCP but cannot serve requests yet.
        RestartReconnectLoop(initialAttempts: failures);
    }

    /// <summary>
    /// Retires the current reconnect session and installs a fresh one in a single transaction,
    /// seeding the attempt counter with <paramref name="initialAttempts"/>.
    /// </summary>
    /// <remarks>
    /// Doing this as <c>StopReconnectLoop(); _reconnectLoop = null; StartReconnectLoop(seed);</c>
    /// took the lock twice with a bare write in between, so a concurrent start (from OnceClose or
    /// OnConnectionFailed) could slip in and install its own loop; the seeded start would then see
    /// a live loop, return without applying the seed, and the backoff would silently stop growing
    /// across consecutive handler failures — the very regression the seed exists to prevent.
    /// </remarks>
    private void RestartReconnectLoop(int initialAttempts)
    {
        CancellationTokenSource retired;
        lock (_reconnectStateLock)
        {
            retired = _reconnectCts;
            _reconnectMode = ReconnectMode.LoopReconnect;
            _isFastReconnectActive = false;
            _reconnectAttempts = initialAttempts;
            _reconnectCts = new CancellationTokenSource();

            // Safe to start under the lock: ReconnectLoopAsync reads its token and yields before
            // anything else, so this only schedules the loop - no consumer notification runs inline.
            _reconnectLoop = ReconnectLoopAsync(_reconnectCts);
        }

        retired?.Cancel();
        retired?.Dispose();
    }

    private async Task OnceClose(int? code, string? description, WebSocketClient closingSocket, long sessionId)
    {
        var (severity, userMessage) = DescribeClose(code, description);

        // Check if this callback is from a retiring session using session ID
        bool isActiveSession;
        var isRetiringSession = false;

        // The session object itself, not just the verdict about it: the end-of-session
        // announcement below is guarded per session, and only the object carries that guard.
        ConnectionSession? closingSession = null;
        lock (_sessionLock)
        {
            if (_activeSession != null)
            {
                if (_activeSession.SessionId == sessionId)
                {
                    closingSession = _activeSession;
                    // Same session - but check if it's marked as retiring
                    isActiveSession = !_activeSession.IsRetiring;
                    isRetiringSession = _activeSession.IsRetiring;
                }
                else
                {
                    // Different session - this callback is from an old session
                    isActiveSession = false;
                    isRetiringSession = true;
                }
            }
            else
            {
                isActiveSession = false;
            }
        }

        // Check if this is the current socket or a stale callback from an old socket
        var isCurrentSocket = ws == closingSocket;
        var wsWasNull = ws == null;

        var isUserInitiated = Interlocked.CompareExchange(
            ref _userInitiatedSocket,
            value: null,
            closingSocket
        ) == closingSocket;

        var isFromUserInitiatedSet = IsSocketUserInitiated(closingSocket);
        RemoveFromUserInitiatedSockets(closingSocket);

        var intentionalDisconnect =
            _isIntentionalDisconnect || isUserInitiated || isFromUserInitiatedSet || isRetiringSession;

        // For stale sockets (not current) or retiring sessions, only do minimal cleanup
        if ((!isCurrentSocket && !wsWasNull) || isRetiringSession)
        {
            // This is a late callback from an old socket - don't touch current connection state
            // Just complete the TCS if this was an intentional disconnect
            if (intentionalDisconnect)
            {
                CompleteDisconnectTcs();
            }

            return;
        }

        // Only for the current socket - and the message processor goes with it, this connection
        // is over.
        StopPingTimerSync();
        await StopMessageProcessorAsync();

        // Check if this is a network drop (FailureReason set by WebSocketClient)
        var isNetworkDrop = closingSocket.FailureReason == SocketFailureReason.NetworkDrop;
        
        // Track network drop socket for immediate reconnect
        if (isNetworkDrop && !intentionalDisconnect)
        {
            _networkDropSocket = closingSocket;
        }

        // For intentional disconnect or network drop, use cancellation (no Critical logging)
        if (intentionalDisconnect || isNetworkDrop)
        {
            requestManager.RejectAllWithCancellation();
        }
        else
        {
            requestManager.RejectAll(
                new DisconnectedException($"websocket was closed, code: {code}, reason: {userMessage}"));
        }

        // Clear ws reference
        if (isCurrentSocket)
        {
            ws = null;
        }

        CompleteDisconnectTcs();

        if (code == null)
        {
            if (OnDisconnect is not null)
            {
                await OnDisconnect?.Invoke(code: 1011, description: "Internal error - disconnect code was undefined")!;
            }
        }
        else
        {
            if (OnDisconnect is not null)
            {
                await OnDisconnect?.Invoke(code, userMessage)!;
            }
        }

        // The socket that carried this session has closed for real, so the session is over too.
        // OnDisconnect above says a socket closed; this says what the consumer actually has to act
        // on - that the subscriptions held against it are gone. Raised here as well as on the two
        // deliberate retirement paths so that one subscription is enough to cover every way a
        // session can end.
        await NotifySessionEndedAsync(
            closingSession,
            intentionalDisconnect ? SessionEndReason.UserDisconnected : SessionEndReason.ConnectionLost,
            userMessage);

        if (intentionalDisconnect)
        {
            _reconnectAttempts = 0;
            var noReconnectMessage = $"Connection closed permanently. {userMessage}";
            SetConnectionState(XrpConnectionState.Disconnected, noReconnectMessage, ConnectionCloseSeverity.Warning);
            return;
        }

        if (ShouldReconnect(code) || code == 1000)
        {
            // Check if reconnect loop is already running - don't reset counter or start new loop
            var loopIsRunning = _reconnectLoop != null && !_reconnectLoop.IsCompleted;
            if (!loopIsRunning)
            {
                // Set _reconnectAttempts = 1 before notification so BuildReconnectInfo returns correct value
                _reconnectAttempts = 1;
                SetConnectionState(
                    XrpConnectionState.RestoringConnection,
                    userMessage,
                    severity,
                    reconnect: BuildReconnectInfo());
                StartReconnectLoop();
            }
            // else: loop is already running and will handle reconnection, don't reset _reconnectAttempts
        }
        else
        {
            _reconnectAttempts = 0;
            var noReconnectMessage = $"Connection closed permanently. {userMessage}";
            SetConnectionState(XrpConnectionState.Disconnected, noReconnectMessage, ConnectionCloseSeverity.Warning);
        }
    }

    private void StopReconnectLoop()
    {
        // Detach under the lock, then cancel/dispose outside it: a start racing with this stop can
        // no longer have its fresh source torn down, and cancellation callbacks never run while the
        // lock is held.
        CancellationTokenSource retired;
        lock (_reconnectStateLock)
        {
            retired = _reconnectCts;
            _reconnectCts = null;
            _reconnectAttempts = 0;

            // Drop the task reference too, in the same transaction. The retired loop exits
            // asynchronously - it only notices it lost ownership on its next check - so leaving the
            // reference behind makes StartReconnectLoop see `!IsCompleted` and return without
            // starting anything, while the retired loop then stands down on its ownership check.
            // Nobody would be reconnecting. Reachable whenever Connect or ChangeServer stops a live
            // loop and the new connection fails.
            _reconnectLoop = null;
        }

        retired?.Cancel();
        retired?.Dispose();
        // Note: Do NOT clear _reconnectMode here!
        // _reconnectMode is cleared only by:
        // - OnceOpen (connection succeeded)
        // - End of ReconnectLoopAsync (loop terminated)
        // - ClearReconnectState (user-initiated disconnect)
        // This prevents race conditions where StopReconnectLoop is called during
        // fast reconnect transitions (RetireCurrentSessionAndReconnectAsync)
        _isFastReconnectActive = false; // Legacy flag for backward compatibility
    }
    
    /// <summary>
    /// Clears all reconnect state. Only called when connection is stable or user disconnects.
    /// </summary>
    private void ClearReconnectState()
    {
        StopReconnectLoop();
        _reconnectMode = ReconnectMode.None;
    }

    /// <summary>
    /// Starts a reconnect loop unless one is already running, reusing a pre-created cancellation
    /// source when there is one. A fresh sequence starts its attempt counter at zero, so the first
    /// delay is <c>CalcBackoff(1)</c> — twice <c>ReconnectBaseDelay</c> — except on the
    /// ping-timeout and network-drop paths, where the first attempt skips the delay entirely. The
    /// OnConnected-handler path needs a seeded counter instead and uses
    /// <see cref="RestartReconnectLoop"/>.
    /// </summary>
    private void StartReconnectLoop()
    {
        // Set reconnect mode to LoopReconnect (upgrades from FastReconnect or sets from None)
        _reconnectMode = ReconnectMode.LoopReconnect;

        // The whole decision — is a loop already running, is the current source reusable, install a
        // fresh one, hand it to the new loop — is one transaction. Split across the lock it would
        // race with StopReconnectLoop and with another start: two loops could end up running, or a
        // loop could be handed a source that a concurrent stop has already disposed.
        CancellationTokenSource retired = null;
        lock (_reconnectStateLock)
        {
            // CRITICAL: If a loop is already running, don't start another or reset the counter
            // This prevents _reconnectAttempts from being reset mid-loop when callbacks trigger
            // reconnect logic (OnceClose, OnConnectionFailed, etc.)
            var loopIsRunning = _reconnectLoop != null && !_reconnectLoop.IsCompleted;
            if (loopIsRunning)
            {
                // Loop is already running - let it continue, don't reset _reconnectAttempts
                return;
            }

            // If we have a valid pre-created CTS (from RetireCurrentSessionAndReconnectAsync),
            // we should reuse it. Check for this case first.
            var existingCts = _reconnectCts;
            var hasValidPreCreatedCts = existingCts != null && !existingCts.IsCancellationRequested;

            // If no valid pre-created CTS, create a new one
            // Only reset _reconnectAttempts when creating a FRESH CTS (new reconnect sequence)
            if (!hasValidPreCreatedCts)
            {
                // Retire the old CTS after the lock is released - see _reconnectStateLock
                retired = existingCts;
                _reconnectCts = new CancellationTokenSource();
                _reconnectAttempts = 0;
            }
            // else: Reuse existing valid CTS (pre-created for fast reconnect)
            // Don't reset _reconnectAttempts - this is continuation of existing reconnect sequence
            // Note: _reconnectLoop was already cleared by RetireCurrentSessionAndReconnectAsync

            // Safe to start under the lock: ReconnectLoopAsync yields before touching anything, so
            // this call only schedules the loop and returns - no consumer notification runs inline.
            _reconnectLoop = ReconnectLoopAsync(_reconnectCts);
        }

        retired?.Cancel();
        retired?.Dispose();
    }

    private async Task ReconnectLoopAsync(CancellationTokenSource ownCts)
    {
        // The CTS this loop owns. StopReconnectLoop cancels without awaiting the loop, so a retired loop
        // can still be running - or reach its tail - after a replacement has been installed. Everything
        // this loop writes to shared reconnect state is therefore guarded by an ownership check.
        //
        // Read BEFORE the yield below, and deliberately so: the caller still holds
        // _reconnectStateLock here, so this source cannot yet have been retired. After the yield a
        // concurrent stop may already have disposed it - Cancel/Dispose of a retired source run
        // outside the lock - and CancellationTokenSource.Token throws ObjectDisposedException once
        // disposed. Taken after the yield, that throw would land outside every try below, faulting
        // the loop before its first attempt and vanishing as an unobserved task exception.
        CancellationToken ct = ownCts.Token;

        // Yield so nothing beyond that read runs inline on the caller: StartReconnectLoop starts the
        // loop while holding _reconnectStateLock, and a consumer notification executing under that
        // lock could deadlock against any path that takes it (Disconnect from a handler, say).
        await Task.Yield();

        // Don't reset _reconnectAttempts here - it may be pre-set to 1 by fast reconnect path
        // StartReconnectLoop() sets it to 0 when creating a new CTS
        
        // Clear fast reconnect flag - reconnect loop has taken ownership
        // This must happen AFTER _reconnectCts is valid (which StartReconnectLoop ensures)
        // so any pending OnConnectionFailed callbacks still see IsReconnectActive()=true via CTS
        _isFastReconnectActive = false;
        
        // For ping timeout or network drop, first attempt should be immediate (no delay)
        var isImmediateReconnect = _pingTimeoutSocket != null || _networkDropSocket != null;

        while (!ct.IsCancellationRequested)
        {
            if (!ReferenceEquals(_reconnectCts, ownCts))
            {
                // Retired: a newer loop owns the reconnect sequence now.
                break;
            }

            _reconnectAttempts++;

            // Skip delay for first attempt if this is immediate reconnect (ping timeout or network drop)
            var skipDelay = isImmediateReconnect && _reconnectAttempts == 1;
            isImmediateReconnect = false; // Only affects first attempt
            
            var delay = skipDelay ? TimeSpan.Zero : CalcBackoff(_reconnectAttempts);
            var reconnectMessage = skipDelay 
                ? "Reconnecting immediately..."
                : $"Reconnecting in {delay.TotalSeconds:F1} seconds... (attempt #{_reconnectAttempts})";
            var type = ConnectionCloseSeverity.Info;
            if (_reconnectAttempts > config.MaxReconnectAttempts)
            {
                if (config.StopAfterMaxAttempts)
                {
                    SetConnectionState(
                        XrpConnectionState.Disconnected,
                        message: $"Reconnection stopped after {config.MaxReconnectAttempts} attempts.",
                        ConnectionCloseSeverity.Error);
                    break;
                }

                reconnectMessage =
                    $"Reconnection in {delay.TotalSeconds:F1} seconds... attempt #{_reconnectAttempts} (exceeded max {config.MaxReconnectAttempts}). Will keep trying, but this may indicate a persistent issue.";
                type = ConnectionCloseSeverity.Warning;
            }

            SetConnectionState(
                XrpConnectionState.RestoringConnection,
                reconnectMessage,
                type,
                reconnect: BuildReconnectInfo(delay: delay));

            if (!skipDelay)
            {
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // The source this loop owns was retired and disposed while the delay was being
                    // set up: registering a callback on a token whose source is gone throws instead
                    // of cancelling. Same meaning as cancellation - a newer sequence owns the
                    // reconnect state now - so leave quietly rather than fault the task.
                    break;
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // =====================================================
                // SESSION ISOLATION (same as ChangeServer)
                // =====================================================
                // Somebody else connected while this loop was waiting out its delay - another
                // attempt on the same source, or the fast-reconnect path. Whatever is in ws is live,
                // and retiring it below would trade a healthy connection for a reconnect nobody
                // needed. Every path that starts this loop does so because the connection is gone,
                // so an open socket here always belongs to someone who got there first.
                if (IsConnected())
                {
                    lock (_reconnectStateLock)
                    {
                        if (ReferenceEquals(_reconnectCts, ownCts))
                        {
                            _reconnectAttempts = 0;
                        }
                    }

                    break;
                }

                // Mark old session as retiring before creating new connection
                // so late callbacks from old socket are properly ignored.
                ConnectionSession? oldSession;
                WebSocketClient? oldSocket;
                lock (_sessionLock)
                {
                    oldSession = _activeSession;
                    oldSession?.MarkAsRetiring();
                }
                lock (_disconnectLock)
                {
                    oldSocket = ws;
                    ws = null;
                }
                
                // Mark old socket for intentional disconnect (per-socket tracking)
                if (oldSocket != null)
                {
                    MarkSocketAsUserInitiated(oldSocket);
                    oldSocket.SetIntentionalDisconnect();
                    // Fire-and-forget graceful disposal
                    _ = RetireOldSessionAsync(oldSession, oldSocket);
                }

                await ConnectInternalAsync(ct);

                if (IsConnected())
                {
                    // Ownership check and the write it guards belong together: checked outside the
                    // lock, this loop could be retired in between and reset a live sequence's counter.
                    lock (_reconnectStateLock)
                    {
                        if (ReferenceEquals(_reconnectCts, ownCts))
                        {
                            _reconnectAttempts = 0;
                        }
                    }

                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // Reconnect loop was cancelled (e.g., by ChangeServer or StopReconnectLoop)
                // Exit the loop quietly without logging an error
                Debug.WriteLine($"{DateTime.Now}Reconnect loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                // For network exceptions, use Warning severity to avoid Critical logging in consuming apps
                var isNetworkError = IsNetworkDropException(ex);
                var severity = isNetworkError ? ConnectionCloseSeverity.Warning : ConnectionCloseSeverity.Error;
                var errorMessage = isNetworkError 
                    ? $"Reconnection attempt #{_reconnectAttempts}: network unavailable"
                    : $"Reconnection attempt #{_reconnectAttempts} failed: {ex.Message}";
                SetConnectionState(
                    XrpConnectionState.RestoringConnection,
                    errorMessage,
                    severity,
                    reconnect: BuildReconnectInfo());
            }
        }

        // Note: _pingTimeoutSocket is cleared only in OnceOpen when new connection succeeds
        // This ensures late callbacks from ping-timeout socket are still filtered
        // even if reconnect attempts fail

        // A newer loop may already have taken over (this one was retired by StopReconnectLoop, which does
        // not await it). Its state belongs to that loop: clearing the mode or disposing the CTS here would
        // strand the live reconnect sequence.
        if (!ReferenceEquals(_reconnectCts, ownCts))
        {
            return;
        }

        // When loop exits (cancelled, max attempts, or success) and connection is not established,
        // clear the reconnect mode. If connected, OnceOpen already cleared it.
        if (!IsConnected())
        {
            _reconnectMode = ReconnectMode.None;
        }

        if (config.StopAfterMaxAttempts && _reconnectAttempts >= config.MaxReconnectAttempts)
        {
            // Re-check ownership inside the lock: between the check above and here a new sequence
            // could have installed its own source, and disposing that one would strand it.
            CancellationTokenSource finished = null;
            lock (_reconnectStateLock)
            {
                if (ReferenceEquals(_reconnectCts, ownCts))
                {
                    finished = _reconnectCts;
                    _reconnectCts = null;
                }
            }

            finished?.Dispose();
        }
    }

    private volatile int _pingRunning = 0;

    /// <summary>
    /// True inside this connection's ping check and everything it awaits. The fast-reconnect path
    /// is awaited from there, and it must be able to tell that the ping it would wait for is the
    /// one it is running in.
    /// </summary>
    /// <remarks>
    /// Per instance, not static: the value follows the execution context, so a consumer's
    /// <c>OnPing</c> handler that awaits another connection would carry a static flag into that
    /// connection and let it skip waiting for its own ping.
    /// </remarks>
    private readonly AsyncLocal<bool> _insidePingCheck = new AsyncLocal<bool>();

    private Task? _pingLoopTask = null;

    private System.Threading.Timer? _wasmPingTimer;

    private void StartWasmPingTimer(CancellationTokenSource cts)
    {
        _wasmPingTimer = new System.Threading.Timer(
            callback: state =>
            {
                var innerCts = (CancellationTokenSource)state!;
                if (innerCts.IsCancellationRequested) return;

                if (Interlocked.CompareExchange(ref _pingRunning, value: 1, comparand: 0) != 0)
                    return;

                Debug.WriteLine($"{DateTime.Now}[PING-WASM] Timer fired, executing ping check...");

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _currentPingTask, tcs.Task);

                _ = ExecutePingCheckAndReleaseAsync(innerCts, tcs);
            },
            state: cts,
            dueTime: (int)config.HealthCheckInterval.TotalMilliseconds,
            period: (int)config.HealthCheckInterval.TotalMilliseconds);
    }

    private async Task ExecutePingCheckAndReleaseAsync(CancellationTokenSource cts, TaskCompletionSource<bool> tcs)
    {
        try
        {
            await ExecutePingCheckAsync(cts);
            Debug.WriteLine($"{DateTime.Now}[PING-WASM] Ping check completed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{DateTime.Now}[PING-WASM] Ping check error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _pingRunning, value: 0);
            tcs.TrySetResult(true);
        }
    }

    private async Task ExecutePingCheckAsync(CancellationTokenSource cts)
    {
        _insidePingCheck.Value = true;

        try
        {
            if (cts.IsCancellationRequested)
            {
                Debug.WriteLine($"{DateTime.Now}[PING-CHECK] Early exit: CTS cancelled");
                return;
            }

            WebSocketClient? currentSocket;
            lock (_disconnectLock)
            {
                currentSocket = ws;
            }

            if (currentSocket == null || cts.IsCancellationRequested)
            {
                Debug.WriteLine($"{DateTime.Now}[PING-CHECK] Early exit: socket={currentSocket != null}, cts={cts.IsCancellationRequested}");
                return;
            }

            var now = DateTime.UtcNow;
            var timeSinceLastActivity = lastActivityTime.HasValue
                ? (now - lastActivityTime.Value).TotalSeconds
                : double.MaxValue;

            Debug.WriteLine($"{DateTime.Now}[PING-CHECK] timeSinceLastActivity={timeSinceLastActivity:F1}s, IsConnected={IsConnected()}, State={State()}");

            if (!IsConnected())
            {
                Debug.WriteLine($"{DateTime.Now}[PING-CHECK] Not connected (State={State()}), triggering reconnect");
                _pingTimeoutSocket = ws;
                await RetireCurrentSessionAndReconnectAsync($"Ping detected disconnected state ({State()}).");
                return;
            }

            if (!config.UseCustomPing)
            {
                return;
            }

            if (cts.IsCancellationRequested)
            {
                Debug.WriteLine($"{DateTime.Now}[PING-CHECK] Early exit: CTS cancelled before connect check");
                return;
            }

            double inactivityLimit = config.InactivityTimeout.TotalSeconds;
            if (timeSinceLastActivity > inactivityLimit)
            {
                _pingTimeoutSocket = ws;

                await RetireCurrentSessionAndReconnectAsync(
                    $"Connection timeout (no activity for {inactivityLimit:F0}+ seconds).");
                return;
            }

            if (timeSinceLastActivity < 30)
            {
                try
                {
                    Debug.WriteLine($"{DateTime.Now}[PING-CHECK] Fire-and-forget keepalive ping (active connection)");

                    // Raw send: this bypasses RequestManager, so AdminUser/AdminPassword are NOT attached.
                    // Safe for ping specifically — rippled resolves the role per command, and a guest-level
                    // command is answered normally even on a port that sets admin_user/admin_password
                    // (only commands requiring Role::ADMIN get "forbidden / Bad credentials."). Anything
                    // needing admin must go through Request/GRequest instead of being added here.
                    currentSocket?.SendMessage("{\"command\":\"ping\",\"id\":\"00000000-0000-0000-0000-000000000000\"}");
                    if (OnPing != null)
                    {
                        await OnPing.Invoke("Ping/Pong");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now}[PING-CHECK] Keepalive send failed: {ex.Message}");
                }
                return;
            }

            try
            {
                Debug.WriteLine($"{DateTime.Now}[PING-CHECK] Sending actual server ping...");
                if (OnPing != null)
                {
                    await OnPing.Invoke("Ping");
                }

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                await Request(
                    request: new Dictionary<string, object>
                    {
                        { "command", "ping" },
                    },
                    timeout: TimeSpan.FromSeconds(45),
                    RequestFailurePolicy.ImmediateFail);

                Debug.WriteLine($"{DateTime.Now}[PING-CHECK] Server pong received");
                if (OnPing != null && !cts.IsCancellationRequested)
                {
                    await OnPing.Invoke("Pong");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (NotConnectedException)
            {
            }
            catch (Exception pingEx)
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                Debug.WriteLine($"{DateTime.Now}Ping request error: {pingEx.Message}");

                _pingTimeoutSocket = ws;

                await RetireCurrentSessionAndReconnectAsync("Ping failed.");
                return;
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{DateTime.Now}Ping timer error: {ex.Message}");
        }
    }

    private void StartPingTimer()
    {
        if (!config.UseCustomPing && !config.UseCheckHealth)
        {
            return;
        }

        StopPingTimerSync();

        lastActivityTime = DateTime.UtcNow;

        var cts = new CancellationTokenSource();
        _pingCts = cts;

        if (OperatingSystem.IsBrowser())
        {
            StartWasmPingTimer(cts);
        }
        else
        {
            pingTimer = new Timer(config.HealthCheckInterval.TotalMilliseconds);
            pingTimer.Elapsed += (sender, e) =>
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _pingRunning, value: 1, comparand: 0) != 0)
                {
                    return;
                }

                if (cts.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _pingRunning, value: 0);
                    return;
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _currentPingTask, tcs.Task);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecutePingCheckAsync(cts).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _pingRunning, value: 0);
                        tcs.TrySetResult(true);
                    }
                });
            };

            pingTimer.AutoReset = true;
            pingTimer.Start();
        }
    }

    /// <summary>
    /// Stops the ping timer, and nothing else.
    /// </summary>
    /// <remarks>
    /// It used to stop the message processor too, which tied two unrelated lifecycles together:
    /// <see cref="StartPingTimer"/> begins by calling this, so starting the processor before the
    /// ping timer had it torn down again moments later - and that is what forced
    /// <see cref="StartMessageProcessor"/> to the very end of <c>OnceOpen</c>, after the
    /// <c>OnConnected</c> callback, leaving every frame answered during that callback to the
    /// fallback path. The processor is now stopped explicitly wherever a connection genuinely
    /// ends: <see cref="Disconnect"/>, <see cref="DisconnectAndWaitAsync"/>, <c>OnceClose</c>,
    /// <c>OnConnectHandlerFailedAsync</c>, <see cref="ChangeServer"/> and
    /// <c>RetireCurrentSessionAndReconnectAsync</c> - the same six places the side effect used to
    /// fire, so when the processor stops is unchanged; only the spurious stop inside
    /// <see cref="StartPingTimer"/> is gone.
    /// <para>
    /// <c>ReconnectLoopAsync</c> retires a session without stopping the processor, and deliberately
    /// so: it did not stop it before this change either, <c>OnceClose</c> has already run by the
    /// time it retries, and <see cref="StartMessageProcessor"/> tears down any leftover when the
    /// next connection opens. Adding a stop there would discard frames still queued from before the
    /// drop, on a path where nothing shows that is wanted.
    /// </para>
    /// </remarks>
    private void StopPingTimerSync()
    {
        var cts = _pingCts;
        var timer = pingTimer;
        var loopTask = _pingLoopTask;
        var wasmTimer = _wasmPingTimer;
        _pingCts = null;
        pingTimer = null;
        _pingLoopTask = null;
        _wasmPingTimer = null;

        cts?.Cancel();

        if (timer != null)
        {
            timer.Stop();
            timer.Dispose();
        }

        wasmTimer?.Dispose();

        cts?.Dispose();
    }

    /// <summary>
    /// Waits for the ping task to finish. Should be called AFTER rejecting pending requests
    /// so the ping handler receives OperationCanceledException and exits quickly.
    /// </summary>
    private async Task WaitForPingToFinishAsync()
    {
        // Clear the task reference
        Interlocked.Exchange(ref _currentPingTask, value: null);

        // Called from inside the ping check - RetireCurrentSessionAndReconnectAsync is awaited from
        // there, and only from there. The flag polled below is this very check's, and it cannot
        // clear before the check returns, which is after this method: the wait could only ever run
        // out its timeout, and did, on every ping-triggered reconnect. The ping is not running
        // alongside the retirement here; it is the retirement.
        if (_insidePingCheck.Value)
        {
            return;
        }

        // Wait for _pingRunning to become 0 (ping task's finally block will reset it)
        // Since we already rejected pending requests, the ping should exit very quickly
        var startTime = DateTime.UtcNow;
        var maxWait = TimeSpan.FromSeconds(3); // Short timeout - ping should exit quickly after request rejection
        
        while (Interlocked.CompareExchange(ref _pingRunning, value: 0, comparand: 0) != 0)
        {
            if (DateTime.UtcNow - startTime > maxWait)
            {
                // Timeout - force reset _pingRunning so we don't block the fast reconnect
                Interlocked.Exchange(ref _pingRunning, value: 0);
                break;
            }
            
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static (ConnectionCloseSeverity severity, string message) DescribeClose(int? code, string? reason)
    {
        var suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Reason: {reason}";

        return code switch
        {
            1000 => (ConnectionCloseSeverity.Info, "Connection closed normally (1000)." + suffix),
            1001 => (ConnectionCloseSeverity.Warning,
                "Server unavailable or intentionally closed the connection (1001)." + suffix),
            1002 => (ConnectionCloseSeverity.Error, "Protocol error occurred (1002)." + suffix),
            1003 => (ConnectionCloseSeverity.Error, "Invalid message type received (1003)." + suffix),
            1005 => (ConnectionCloseSeverity.Warning, "Connection was closed without a close frame (1005)." + suffix),
            1006 => (ConnectionCloseSeverity.Warning,
                "Connection interrupted abnormally (1006). Network issue, server restart, or timeout." + suffix),
            1007 => (ConnectionCloseSeverity.Error, "Invalid payload data in the WebSocket frame (1007)." + suffix),
            1008 => (ConnectionCloseSeverity.Warning,
                "Policy violation (1008). Possibly due to rate limits or access rules." + suffix),
            1009 => (ConnectionCloseSeverity.Warning, "Message too large (1009)." + suffix),
            1010 => (ConnectionCloseSeverity.Error, "Mandatory WebSocket extension is missing (1010)." + suffix),
            1011 => (ConnectionCloseSeverity.Error, "Internal server error (1011)." + suffix),
            _ => (ConnectionCloseSeverity.Warning, $"Connection closed with code {code}." + suffix),
        };
    }

    private static bool ShouldReconnect(int? code) =>
        code switch
        {
            null => true,
            1000 => false,
            1002 => false,
            1003 => false,
            1007 => false,
            1010 => false,

            1001 => true,
            1005 => true,
            1008 => true,
            1009 => true,
            1011 => true,

            _ => true,
        };

    private TimeSpan CalcBackoff(int attempts)
    {
        var exponentialDelay = config.ReconnectBaseDelay.TotalSeconds * Math.Pow(x: 2, attempts);
        var cappedDelay = Math.Min(exponentialDelay, config.ReconnectMaxDelay.TotalSeconds);

        var jitterPercent = 0.25;
        var jitter = cappedDelay * jitterPercent * (2 * _random.NextDouble() - 1);

        var finalDelay = cappedDelay + jitter;
        return TimeSpan.FromSeconds(Math.Max(val1: 0, finalDelay));
    }

    public async Task OnMessage(string message)
    {
        await IOnMessageFastPath(message);
    }

    /// <summary>
    /// Reliably detects if message is a response by scanning for top-level "id" field.
    /// 
    /// XRPL protocol observation:
    /// - Response messages always have "id" as one of the FIRST properties (typically first)
    /// - Stream messages have "type" as first property (never have top-level "id")
    /// 
    /// Optimization: Use fast string scan first, then confirm with JsonTextReader if needed.
    /// This is critical for performance under high stream load.
    /// 
    /// IMPORTANT: This method uses ONLY string scanning, no JSON parsing.
    /// In single-threaded WebAssembly, any JSON parsing overhead causes
    /// WebSocket receive delays that lead to ping timeouts.
    /// </summary>
    private bool IsLikelyResponse(string message)
    {
        if (string.IsNullOrEmpty(message) || message.Length < 10)
            return false;
        
        // PURE STRING SCAN - no JSON parsing for maximum performance
        // Response format: {"id":"...", ...} - ALWAYS has "id" property
        // Stream format: {"type":"transaction|ledgerClosed|...", ...} - never has "id"
        //
        // Note: Response messages also have "type":"response", but they ALWAYS have "id".
        // Stream messages have "type":"transaction" etc but NEVER have "id".
        // So the reliable discriminator is presence of "id" field.
        
        // Find opening brace
        var firstBrace = message.IndexOf('{');
        if (firstBrace < 0 || firstBrace + 10 >= message.Length)
            return false;
        
        // Search ENTIRE message for "id" property
        // XRPL responses can have large "result" objects before the "id" field,
        // so we can't limit the search to just the first N characters.
        // Example response: {"result":{"info":{...large data...}},"id":"...","status":"success"}
        var pos = firstBrace + 1;
        
        // Look for "id" property - this is the ONLY reliable discriminator
        var idIndex = message.IndexOf("\"id\"", pos, StringComparison.Ordinal);
        if (idIndex >= 0)
        {
            // Verify it's followed by colon (confirming it's a property name)
            // Only need to check the next few characters after "id"
            var checkEnd = Math.Min(message.Length, idIndex + 10);
            for (var i = idIndex + 4; i < checkEnd; i++)
            {
                var c = message[i];
                if (c == ':') return true; // This is a response
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') break;
            }
        }
        
        // No "id" found - this is a stream message
        return false;
    }

    /// <summary>
    /// <see cref="IsLikelyResponse(string)"/> over the raw frame, so the discriminator scan does
    /// not force a UTF-16 copy of the message. Byte-wise scanning is equivalent here: the tokens
    /// looked for are ASCII, and UTF-8 never encodes them inside a multi-byte sequence.
    /// </summary>
    private bool IsLikelyResponse(ReadOnlySpan<byte> utf8Message)
    {
        if (utf8Message.Length < 10)
            return false;

        int firstBrace = utf8Message.IndexOf((byte)'{');
        if (firstBrace < 0 || firstBrace + 10 >= utf8Message.Length)
            return false;

        ReadOnlySpan<byte> rest = utf8Message.Slice(firstBrace + 1);
        int idIndex = rest.IndexOf("\"id\""u8);
        if (idIndex < 0)
            return false;

        int checkEnd = Math.Min(rest.Length, idIndex + 10);
        for (int i = idIndex + 4; i < checkEnd; i++)
        {
            byte c = rest[i];
            if (c == (byte)':') return true; // This is a response
            if (c != (byte)' ' && c != (byte)'\t' && c != (byte)'\n' && c != (byte)'\r') break;
        }

        return false;
    }

    /// <summary>
    /// Starts the background message processor for stream messages.
    /// Creates a new session-bound channel and processor task.
    /// Uses Channel&lt;T&gt; for true async support in WebAssembly single-threaded environment.
    /// </summary>
    private void StartMessageProcessor()
    {
        lock (_messageProcessorLock)
        {
            // Detach any leftover without waiting for it: its channel is completed and its source
            // cancelled, so it exits on its own, and its frames belong to a session that is already
            // retired. Waiting here used to block OnceOpen on a single-threaded host.
            (Task? leftoverTask, CancellationTokenSource? leftoverCts) = DetachMessageProcessor();
            _ = AwaitMessageProcessorExitAsync(leftoverTask, leftoverCts);
            
            // Create new session-bound channel and CTS
            // Using bounded channel to prevent memory issues under high load
            // itemDropped runs inside TryWrite, i.e. on the receive loop, so it does no more than
            // increment: raising an event or logging here would put consumer code back on the path
            // this channel exists to keep it off. Callers read DroppedStreamMessages instead.
            _streamMessageChannel = System.Threading.Channels.Channel.CreateBounded<SessionFrame>(
                new BoundedChannelOptions(Math.Max(1, config?.StreamMessageQueueCapacity ?? 10000))
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                },
                itemDropped: _ => Interlocked.Increment(ref _droppedStreamMessages));
            _messageProcessorCts = new CancellationTokenSource();

            var channel = _streamMessageChannel;
            var cts = _messageProcessorCts;

            // Use truly async reader - works correctly in WebAssembly single-threaded environment
            _messageProcessorTask = Task.Run(async () =>
            {
                try
                {
                    var reader = channel.Reader;
                    while (await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                    {
                        while (reader.TryRead(out SessionFrame item))
                        {
                            if (cts.Token.IsCancellationRequested)
                                return;

                            try
                            {
                                await ProcessSessionFrameAsync(item).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                await NotifyStreamProcessingErrorAsync(ex, item.Frame).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                }
                catch (ChannelClosedException)
                {
                    // Channel was completed - expected on session end
                }
            }, cts.Token);
        }
    }

    /// <summary>
    /// Stops the background message processor and disposes resources.
    /// </summary>
    private Task StopMessageProcessorAsync()
    {
        (Task? task, CancellationTokenSource? cts) detached;
        lock (_messageProcessorLock)
        {
            detached = DetachMessageProcessor();
        }

        return AwaitMessageProcessorExitAsync(detached.task, detached.cts);
    }

    /// <summary>
    /// Takes the processor's channel, source and task out of their fields, completes the channel
    /// and cancels the source, so the reader exits on its own. Must be called with
    /// <c>_messageProcessorLock</c> held. Does not wait for the reader: that is
    /// <see cref="AwaitMessageProcessorExitAsync"/>, outside the lock.
    /// </summary>
    private (Task? task, CancellationTokenSource? cts) DetachMessageProcessor()
    {
        var channel = _streamMessageChannel;
        var cts = _messageProcessorCts;
        var task = _messageProcessorTask;

        _streamMessageChannel = null;
        _messageProcessorCts = null;
        _messageProcessorTask = null;

        // Complete the channel first to unblock WaitToReadAsync
        if (channel != null)
        {
            try { channel.Writer.Complete(); } catch { }
        }

        // Then cancel the CTS
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }

        return (task, cts);
    }

    /// <summary>
    /// Waits up to two seconds for a detached reader to exit, then disposes its source.
    /// </summary>
    /// <remarks>
    /// This used to be a blocking <c>Task.Wait</c> with the same cap. On a single-threaded host
    /// (Blazor WebAssembly) the reader's continuation needs the very thread that was blocked in
    /// order to observe the completed channel, so the wait never returned early: every
    /// <c>ChangeServer</c>, fast reconnect and <c>Disconnect</c> stalled the UI for the full two
    /// seconds. Awaited, the reader runs and is gone in milliseconds. The cap is kept for a reader
    /// stuck inside a consumer handler, and the source is disposed regardless, as before.
    /// </remarks>
    private static async Task AwaitMessageProcessorExitAsync(Task? task, CancellationTokenSource? cts)
    {
        if (task != null)
        {
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        cts?.Dispose();
    }

    /// <summary>
    /// Processes a single stream message (transaction, ledger, etc.) in the background.
    /// This is the async version of stream handling, decoupled from the receive loop.
    /// </summary>
    /// <remarks>
    /// Takes the frame rather than text for the same reason the response path does: a stream
    /// message is not wrapped in a "result" envelope, so the frame IS the event, and each typed
    /// event pairs itself with it through <see cref="BaseStream.AttachFrame(byte[])"/> - the same
    /// mechanism <see cref="RequestManager.HandleResponse(byte[])"/> uses for <see cref="BaseResponse"/>
    /// - so a consumer's <see cref="BaseStream.Raw"/> is the exact bytes rippled sent, not a
    /// re-encode of a string that was itself decoded from them. Text is materialized only for
    /// <see cref="OnWarning"/>/<see cref="OnServerWarning"/>/<see cref="OnError"/>, which predate
    /// this change and still take a string, and only when something is listening.
    /// </remarks>
    private async Task ProcessStreamMessageAsync(byte[] frame)
    {
        lastActivityTime = DateTime.UtcNow;

        // Lazily materialized, and shared by every caller below: rippled can attach both warnings
        // to the same message, and a null frame - OnMessage(null), routed rather than raised at
        // the entry point - must not throw again here, out of the very report that is supposed to
        // surface it.
        string text = null;
        string Text() => text ??= (frame is null ? null : Encoding.UTF8.GetString(frame));

        BaseResponse data;
        try
        {
            data = JsonSerializer.Deserialize<BaseResponse>(frame, XrplJsonOptions.Default);
        }
        catch (Exception error)
        {
            if (OnError is not null)
            {
                await OnError?.Invoke(error: "error", errorMessage: "badMessage", error.Message, Text())!;
            }
            return;
        }

        if (data.Warning != null && OnWarning is not null)
        {
            await OnWarning.Invoke(data.Warning, Text());
        }

        if (data.Warnings is { Count: > 0, } && OnServerWarning is not null)
        {
            await OnServerWarning.Invoke(data.Warnings, Text());
        }

        // Process stream messages by type
        if (data.Type != null)
        {
            Enum.TryParse(value: data.Type.ToString(), result: out ResponseStreamType type);
            switch (type)
            {
                case ResponseStreamType.ledgerClosed:
                {
                    var response = JsonSerializer.Deserialize<LedgerStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnLedgerClosed is not null)
                    {
                        await OnLedgerClosed.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.validationReceived:
                {
                    var response = JsonSerializer.Deserialize<ValidationStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnValidationReceived is not null)
                    {
                        await OnValidationReceived.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.transaction:
                {
                    var response = JsonSerializer.Deserialize<TransactionStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnTransaction is not null)
                    {
                        await OnTransaction.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.peerStatusChange:
                {
                    var response = JsonSerializer.Deserialize<PeerStatusStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnPeerStatusChange is not null)
                    {
                        await OnPeerStatusChange.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.consensusPhase:
                {
                    var response = JsonSerializer.Deserialize<ConsensusStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnConsensusPhase is not null)
                    {
                        await OnConsensusPhase.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.path_find:
                {
                    var response = JsonSerializer.Deserialize<PathFindStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnPathFind is not null)
                    {
                        await OnPathFind.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.manifestReceived:
                {
                    var response = JsonSerializer.Deserialize<ManifestStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnManifestReceived is not null)
                    {
                        await OnManifestReceived.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.bookChanges:
                {
                    var response = JsonSerializer.Deserialize<BookChangesStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnBookChanges is not null)
                    {
                        await OnBookChanges.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.serverStatus:
                {
                    var response = JsonSerializer.Deserialize<ServerStatusStream>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnServerStatus is not null)
                    {
                        await OnServerStatus.Invoke(response)!;
                    }
                    break;
                }

                case ResponseStreamType.error:
                {
                    var response = JsonSerializer.Deserialize<ErrorResponse>(frame, XrplJsonOptions.Default);
                    response.AttachFrame(frame);
                    if (OnError is not null)
                    {
                        await OnError.Invoke(response.Error, response.ErrorMessage, response.ErrorCode?.ToString(), response);
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    /// Fast-path message handler that prioritizes request responses over stream data.
    /// This prevents ping timeouts by ensuring pong responses are processed immediately,
    /// while stream messages are queued for background processing.
    /// 
    /// Threading Model:
    /// - Response handling (requestManager.HandleResponse) is SYNCHRONOUS and immediate
    /// - Warning/error callbacks are dispatched via fire-and-forget Task.Run for performance
    /// - Stream messages are queued to a background processor
    /// 
    /// IMPORTANT: Event handlers (OnWarning, OnError, OnServerWarning) may be invoked
    /// concurrently from the ThreadPool. Handler implementations MUST be thread-safe
    /// or marshal to their own synchronization context (e.g., UI thread).
    /// </summary>
    private Task IOnMessageFastPath(string message)
    {
        return IOnMessageFastPath(message, null, sessionId: null);
    }

    /// <summary>
    /// Overload for a message still in its wire form, used by the socket callback. See
    /// <see cref="IOnMessageFastPath(string, byte[], long?)"/> for why the bytes are kept as they are.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can drive the actual production entry point - the
    /// one <see cref="WebSocketClient.OnBinaryMessage"/> calls with the frame the socket produced -
    /// instead of only <see cref="OnMessage(string)"/>, where <c>Frame()</c> always synthesizes a
    /// fresh byte array from the string rather than reusing one. <c>InternalsVisibleTo</c> to
    /// <c>Xrpl.Tests</c> is already declared in the project file for this reason.
    /// </remarks>
    internal Task IOnMessageFastPath(byte[] utf8Message)
    {
        return IOnMessageFastPath(null, utf8Message, sessionId: null);
    }

    /// <summary>
    /// As above, for a frame whose originating session is known.
    /// </summary>
    internal Task IOnMessageFastPath(byte[] utf8Message, long? sessionId)
    {
        return IOnMessageFastPath(null, utf8Message, sessionId);
    }

    /// <summary>
    /// Sent to <see cref="OnError"/> in place of a message that could not be turned into text.
    /// A literal, so reporting the failure needs no allocation of its own.
    /// </summary>
    private const string UnavailableMessageText = "<message could not be materialized: out of memory>";

    /// <summary>
    /// Exactly one of <paramref name="message"/> and <paramref name="utf8Message"/> carries the
    /// message; the other is null.
    /// </summary>
    /// <remarks>
    /// A response is parsed straight out of <paramref name="utf8Message"/> when it is the one
    /// present, so the UTF-16 copy of the message - twice its byte length - is never made for the
    /// common case. Stream messages are routed on through <c>Frame()</c>, which likewise reuses
    /// <paramref name="utf8Message"/> when present rather than encoding a fresh copy of
    /// <paramref name="message"/> - the frame stream events pair themselves with is exactly the
    /// bytes the socket produced. Only the warning and error callbacks, which still take a string,
    /// ask for text at all, through <c>Text()</c>, and materialize it once and only then.
    /// </remarks>
    /// <param name="sessionId">
    /// The session whose socket produced this frame, or <see langword="null"/> when the caller has
    /// no session to name - <see cref="OnMessage(string)"/>, which anyone may call directly.
    /// </param>
    private async Task IOnMessageFastPath(string message, byte[] utf8Message, long? sessionId)
    {
        lastActivityTime = DateTime.UtcNow;

        // Null in, null out: the string entry point is public, and a null message used to travel
        // down to the stream processor and be reported through OnError rather than throw here.
        string Text()
        {
            if (message is null && utf8Message is not null)
            {
                message = Encoding.UTF8.GetString(utf8Message);
            }

            return message;
        }

        // The stream path now runs on the frame, not on text: encodes only when the binary
        // callback did not already hand one over, mirroring RequestManager.HandleResponse(string)'s
        // own Encoding.UTF8.GetBytes fallback for the same reason - so OnMessage(string), still a
        // public entry point, keeps working without a frame of its own to reuse. A null message
        // stays null rather than throwing out of Encoding.UTF8.GetBytes here: OnMessage(null) used
        // to travel down to the stream processor and be reported through OnError as a bad message
        // rather than raised at the entry point, and that must keep being true now that the
        // pipeline carries bytes instead of text.
        byte[] Frame() => utf8Message ?? (message is null ? null : Encoding.UTF8.GetBytes(message));

        // Scan message for "id" property to detect response messages
        var isResponse = utf8Message is null ? IsLikelyResponse(message) : IsLikelyResponse(utf8Message);

        if (isResponse)
        {
            // This is a response (including ping/pong) - process immediately with full parsing
            // CRITICAL: Minimize async operations here to prevent blocking subsequent messages
            BaseResponse data;
            bool handled;
            try
            {
                // FIRST: Handle response immediately to unblock any waiting requests (like ping)
                // This is the most time-critical operation
                (data, handled) = utf8Message is null
                    ? requestManager.HandleResponse(message)
                    : requestManager.HandleResponse(utf8Message);
            }
            catch (Exception error)
            {
                var errInfo = XrplErrorClassifier.Classify(error);
                if (OnError is null)
                {
                    return;
                }

                // The report has to survive whatever produced it. A response that fails to parse
                // is most often a heap that has just run out, and a UTF-16 copy of the whole
                // message is the largest allocation left on this path - if it cannot be had, the
                // classification still goes out rather than the notification being lost to a
                // second failure inside the handler.
                string capturedText;
                try
                {
                    capturedText = Text();
                }
                catch (OutOfMemoryException)
                {
                    capturedText = UnavailableMessageText;
                }

                // Fire-and-forget for error callback - don't block
                _ = Task.Run(async () =>
                {
                    if (OnError is not null)
                    {
                        await OnError.Invoke(error: "error", errorMessage: "badMessage", errInfo.UserMessage, capturedText);
                    }
                });
                return;
            }

            if (!handled)
            {
                // Message has "id" but no matching pending request — this is an async
                // follow-up (e.g. path_find updates). Route to stream processing.
                EnqueueStreamMessage(Frame(), sessionId);
                return;
            }

            // THEN: Handle warnings and errors in background (fire-and-forget)
            // These are informational and should not delay response processing.
            // Materialize the text only when something is actually listening: rippled attaches a
            // warning to every response under load and on a reporting-mode server, so building a
            // UTF-16 copy for a callback nobody registered would put back, page after page,
            // exactly the allocation this path exists to avoid.
            bool warningNeedsText = (data.Warning != null && OnWarning is not null)
                                    || (data.Warnings is { Count: > 0 } && OnServerWarning is not null);

            if (warningNeedsText)
            {
                var capturedData = data;
                var capturedMessage = Text();
                _ = Task.Run(async () =>
                {
                    if (capturedData.Warning != null && OnWarning is not null)
                    {
                        await OnWarning.Invoke(capturedData.Warning, capturedMessage);
                    }

                    if (capturedData.Warnings is { Count: > 0 } && OnServerWarning is not null)
                    {
                        await OnServerWarning.Invoke(capturedData.Warnings, capturedMessage);
                    }
                });
            }
        }
        else
        {
            // This is a stream message (no "id") - process asynchronously
            // to avoid blocking the receive loop and causing ping timeouts
            EnqueueStreamMessage(Frame(), sessionId);
        }
    }

    /// <summary>
    /// A stream frame together with the session whose socket produced it.
    /// </summary>
    /// <remarks>
    /// The session has to ride along in the queue, not just be checked on the way in: the channel
    /// is rebuilt per session by <see cref="StartMessageProcessor"/>, and between the check and
    /// the write nothing holds the two together. Carrying the id lets the reader ask the question
    /// again at the only moment that decides anything - just before handlers run.
    /// </remarks>
    /// <param name="Frame">The raw UTF-8 frame.</param>
    /// <param name="SessionId">
    /// The session whose socket produced it, or <see langword="null"/> when the caller had none to
    /// name.
    /// </param>
    private readonly record struct SessionFrame(byte[] Frame, long? SessionId);

    /// <summary>
    /// Whether a frame carrying this session id belongs to the connection as it stands right now.
    /// </summary>
    /// <remarks>
    /// Matching the id is not enough: <c>ChangeServer</c> and the reconnect loop call
    /// <c>MarkAsRetiring()</c> on the session while it is still <c>_activeSession</c>, and only
    /// <c>ConnectInternalAsync</c> installs its replacement. Frames arriving in that window carry
    /// the id of the very session being retired, so the retiring flag is part of the test - as
    /// <c>OnceOpen</c> and the other lifecycle guards do it, and under the same lock, since
    /// <c>IsRetiring</c> is a plain bool published only by <c>_sessionLock</c>.
    /// <para>
    /// A <see langword="null"/> id means the caller cannot name a session
    /// (<see cref="OnMessage(string)"/>, which anyone may call): nothing to compare, so nothing is
    /// rejected.
    /// </para>
    /// </remarks>
    private bool IsFromLiveSession(long? sessionId)
    {
        if (sessionId is null)
        {
            return true;
        }

        lock (_sessionLock)
        {
            return _activeSession != null &&
                   _activeSession.SessionId == sessionId &&
                   !_activeSession.IsRetiring;
        }
    }

    /// <summary>
    /// Hands a stream message to the background processor.
    /// </summary>
    /// <remarks>
    /// Used for ordinary stream messages (no <c>id</c>) and for follow-ups carrying an <c>id</c>
    /// that matches no pending request, such as <c>path_find</c> updates.
    /// <para>
    /// Browsers used to take a separate path here - one fire-and-forget task per frame, bypassing
    /// the queue entirely, so <see cref="ConnectionOptions.StreamMessageQueueCapacity"/> did not
    /// apply, <see cref="DroppedStreamMessages"/> stayed at zero however far handlers fell behind,
    /// the backlog was bounded by nothing, and concurrent dispatch could hand handlers events out
    /// of the order the node sent them. The queue was built for this environment in the first
    /// place ("true async support in WebAssembly single-threaded environment" on
    /// <see cref="StartMessageProcessor"/>), and measurement confirmed it works there: running the
    /// Blazor demo against mainnet, the queue delivered 1 004 transactions over 52 s (19.2 tx/s,
    /// 13 ledgers) with no console errors and timestamps in order - against 462 over 33 s
    /// (13.9 tx/s) on the bypass. So the platforms no longer diverge: capacity, eviction counting
    /// and single-reader ordering hold on every target.
    /// </para>
    /// <para>
    /// The startup window is closed too: <see cref="StartMessageProcessor"/> used to run at the
    /// very end of <c>OnceOpen</c>, after the <c>OnConnected</c> callback, so a handler
    /// subscribing there saw its first frames answered before the channel existed and they took
    /// the fallback below - outside the capacity, the eviction count and the ordering. It now runs
    /// before the callback. That was not a one-line change: <see cref="StartPingTimer"/> begins
    /// with <c>StopPingTimerSync</c>, which used to stop the message processor as well, so an
    /// earlier start was torn down again moments later; see the remarks on
    /// <see cref="StopPingTimerSync"/>.
    /// </para>
    /// <para>
    /// The fallback stays, because it is still reachable: <see cref="OnMessage(string)"/> on a
    /// client that never connected, anything arriving after the processor is stopped, and a write
    /// the channel refuses because its writer is already completed.
    /// </para>
    /// </remarks>
    private void EnqueueStreamMessage(byte[] frame, long? sessionId = null)
    {
        // A retiring socket keeps delivering while InitiateGracefulCloseAsync completes, and that
        // close runs fire-and-forget alongside the new connection. Without this its last frames
        // reach handlers as if they were current - stale after a reconnect, and from an entirely
        // different chain after a ChangeServer between networks.
        //
        // Checking here only saves a queue slot. It cannot be the guarantee: the channel is
        // rebuilt per session and the swap is under _messageProcessorLock, not _sessionLock, so
        // between this answer and the write below the session can retire and its replacement
        // install a new channel - and the frame would land in that one. The answer that counts is
        // the one the reader asks in ProcessSessionFrameAsync, immediately before handlers run.
        if (!IsFromLiveSession(sessionId))
        {
            Interlocked.Increment(ref _staleSessionFramesDropped);
            return;
        }

        SessionFrame item = new SessionFrame(frame, sessionId);

        // The channel is bounded with DropOldest, so a full queue is not a refusal: TryWrite
        // evicts the oldest frame, counts it through itemDropped and reports success. It
        // refuses only a completed writer - and that happens on the ordinary path, not just in
        // some corner: DetachMessageProcessor completes the writer after clearing
        // _streamMessageChannel, so a reader that got the reference an instant earlier writes
        // into a channel that is already closed. StartPingTimer tears the processor down and
        // StartMessageProcessor builds it again on every connect, so the window recurs.
        //
        // A refused frame therefore goes down the fallback rather than disappearing. It still
        // faces the session check there - fallback and queue meet in ProcessSessionFrameAsync.
        Channel<SessionFrame>? channel = _streamMessageChannel;
        if (channel?.Writer.TryWrite(item) == true)
        {
            return;
        }

        Interlocked.Increment(ref _fallbackDispatchedStreamMessages);
        _ = ProcessStreamMessageFireAndForgetAsync(item);
    }

    /// <summary>
    /// Runs a queued frame through the stream handlers, unless its session stopped being the live
    /// one while it waited.
    /// </summary>
    /// <remarks>
    /// This is where the session check has to be final. A frame can be queued while its session is
    /// live and dequeued after <c>ChangeServer</c> has moved the client to another network
    /// entirely - and the queue holds up to
    /// <see cref="ConnectionOptions.StreamMessageQueueCapacity"/> frames, so the wait is not
    /// necessarily short. Asking again here costs one uncontended lock per frame, against a JSON
    /// parse and a handler call.
    /// </remarks>
    private Task ProcessSessionFrameAsync(SessionFrame item)
    {
        if (!IsFromLiveSession(item.SessionId))
        {
            Interlocked.Increment(ref _staleSessionFramesDropped);
            return Task.CompletedTask;
        }

        return ProcessStreamMessageAsync(item.Frame);
    }

    /// <summary>
    /// Processes a stream frame outside the queue, on its own task.
    /// </summary>
    /// <remarks>
    /// Three ways in, none of them platform-specific since browsers stopped taking a path of their
    /// own: before <see cref="StartMessageProcessor"/> has run, after the processor was stopped,
    /// and when the channel refuses the write because its writer is already completed. Ordering
    /// and the capacity bound do not hold here - that is the cost of not losing the frame.
    /// <c>ConfigureAwait(false)</c> throughout, to keep continuations off a captured context.
    /// <para>
    /// The yield is what makes "fire and forget" true. Without it an async method runs on the
    /// caller's thread up to its first real await, and the first real await inside
    /// <see cref="ProcessStreamMessageAsync"/> comes after
    /// <c>JsonSerializer.Deserialize</c> - so the receive loop would pay for parsing every frame
    /// that takes this path, plus whatever a handler does before its own first await. That is
    /// precisely the head-of-line blocking the queue exists to prevent, and the fallback would
    /// have reintroduced it for the startup window, for a stopped processor and for a refused
    /// write.
    /// </para>
    /// </remarks>
    private async Task ProcessStreamMessageFireAndForgetAsync(SessionFrame item)
    {
        await Task.Yield();

        try
        {
            await ProcessSessionFrameAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await NotifyStreamProcessingErrorAsync(ex, item.Frame).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Surfaces an exception raised while processing a stream message — including exceptions
    /// thrown by consumer stream handlers (e.g. <see cref="OnLedgerClosed"/>, <see cref="OnTransaction"/>) —
    /// through the <see cref="OnError"/> event instead of swallowing it into a debug trace, so consumer
    /// bugs are observable. The message loop is always kept alive: cancellation is ignored, and an
    /// exception thrown by the <see cref="OnError"/> handler itself is contained.
    /// </summary>
    private async Task NotifyStreamProcessingErrorAsync(Exception ex, byte[] frame)
    {
        Debug.WriteLine($"{DateTime.Now}Stream message processing error: {ex.Message}");

        if (ex is OperationCanceledException)
        {
            return;
        }

        var handler = OnError;
        if (handler is null)
        {
            return;
        }

        try
        {
            // Materialized only here, on the failure path: OnError's data parameter predates the
            // frame and still takes text, and nothing before this point needed a string at all.
            // Guarded against a null frame - OnMessage(null) reaches this path too - so the report
            // itself cannot throw and swallow the very failure it exists to surface.
            string text = frame is null ? null : Encoding.UTF8.GetString(frame);
            await handler.Invoke(error: "error", errorMessage: "streamHandlerError", message: ex.Message, data: text).ConfigureAwait(false);
        }
        catch (Exception notifyEx)
        {
            Debug.WriteLine($"{DateTime.Now}OnError handler threw while reporting stream processing error: {notifyEx.Message}");
        }
    }
}