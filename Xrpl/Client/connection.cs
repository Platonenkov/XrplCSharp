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
        /// When enabled, the local connection state is checked every <see cref="HealthCheckInterval"/>. If the WebSocket is
        /// detected as no longer Open, an automatic reconnection is triggered.<br/>
        /// This check does not send any network requests — it only inspects the local connection state.<br/>
        /// Automatically enabled when <see cref="UseCustomPing"/> is set to <see langword="true"/>.<br/>
        /// Default: <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// On its own this detects only a socket the runtime already knows is gone. A peer that vanished without
        /// closing leaves the socket Open, and the only signal for that is silence - which is a signal only when
        /// something is expected to arrive. That is what <see cref="UseCustomPing"/> adds: keepalive pings whose
        /// answers keep the activity clock moving, so <see cref="InactivityTimeout"/> can mean "the node stopped
        /// answering". Without pings an idle connection - no subscriptions, no requests - receives nothing at all,
        /// and silence would declare a healthy socket dead every <see cref="InactivityTimeout"/>; so the inactivity
        /// check runs only when <see cref="UseCustomPing"/> is enabled.
        /// </remarks>
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
        /// Applies only when <see cref="UseCustomPing"/> is enabled.<br/>
        /// Default: 60 seconds, the threshold this check has always used.
        /// </summary>
        /// <remarks>
        /// A socket whose peer vanished stays <c>Open</c> until the next I/O, so silence is the only
        /// signal that reaches the client - and it is a signal only while keepalive pings are being
        /// sent, because an idle connection with no subscriptions receives nothing by design. With
        /// <see cref="UseCustomPing"/> off this value is not consulted; see the remarks on
        /// <see cref="UseCheckHealth"/>. Exposed together with <see cref="HealthCheckInterval"/> so
        /// the fast-reconnect path is reachable from a test in under a second instead of over a minute.
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

    private void ValidateConfig() => ValidateOptions(config);

    /// <summary>
    /// Rejects an option set the client cannot run with. Static so <see cref="ChangeServer"/> can
    /// validate the options it was handed before it tears the current connection down.
    /// </summary>
    private static void ValidateOptions(ConnectionOptions options)
    {
        if (options.ConnectionAcquisitionTimeout < options.ConnectionAttemptTimeout)
        {
            throw new ArgumentException(
                $"ConnectionAcquisitionTimeout ({options.ConnectionAcquisitionTimeout.TotalSeconds}s) must be >= ConnectionAttemptTimeout ({options.ConnectionAttemptTimeout.TotalSeconds}s) to allow at least one full connection attempt.");
        }

        // The WASM timer takes this as an int of milliseconds: zero fires once and never repeats,
        // and anything past int.MaxValue or below zero is rejected outright by the timer itself.
        // Fail here instead, where the message can say which option is wrong.
        double healthCheckMs = options.HealthCheckInterval.TotalMilliseconds;
        if (healthCheckMs < 1 || healthCheckMs > int.MaxValue)
        {
            throw new ArgumentException(
                $"HealthCheckInterval ({options.HealthCheckInterval}) must be between 1ms and {int.MaxValue}ms.");
        }

        if (options.InactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"InactivityTimeout ({options.InactivityTimeout}) must be positive - a non-positive value would " +
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

    // Volatile: this is the connectivity gate. It is written only under _transitionLock - by the
    // takeover that begins a transition and by ConnectCoreAsync installing that transition's
    // socket - and read lock-free by ShouldBeConnected/State/CheckIfNotConnected.
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
    /// The lock every transition of the connection runs its synchronous part under. The
    /// generation counter (<see cref="_generation"/>), <see cref="ws"/>,
    /// <see cref="_permanentlyDisconnected"/> and the reconnect-loop state
    /// (<see cref="_reconnectCts"/>, <see cref="_reconnectLoopGeneration"/>,
    /// <see cref="_reconnectAttempts"/>) are written only while it is held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It absorbs what used to be two locks, <c>_disconnectLock</c> around <see cref="ws"/> and
    /// <c>_reconnectStateLock</c> around the reconnect session. Kept apart, the two let an
    /// operation take the socket under one lock and the reconnect state under the other with a gap
    /// in between - the shape of every race in issue #179. <see cref="_sessionLock"/> stays
    /// separate because the per-frame session check takes it, and nests inside this one; so does
    /// <see cref="_messageProcessorLock"/>. The order is fixed: transition, then session, then
    /// processor.
    /// </para>
    /// <para>
    /// Nothing that can call back into consumer code runs while the lock is held, and nothing
    /// awaits under it: a retired cancellation source is cancelled and disposed after the lock is
    /// released, the message processor's exit is awaited outside, and the reconnect loop starts
    /// with a yield so that starting it under the lock never runs a notification inline.
    /// </para>
    /// </remarks>
    private readonly object _transitionLock = new object();

    /// <summary>
    /// The transition that owns the connection right now. Every operation that moves the
    /// connection either begins a new one (<see cref="TakeOver"/>) or continues the current one,
    /// and re-validates after every await and every consumer callback with <see cref="Owns"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ChangeServer</c>, <c>Connect</c>, <c>Disconnect</c>, <c>DisconnectAndWaitAsync</c> and
    /// the health check's fast reconnect begin a generation: the consumer said something, or the
    /// connection was found dead, and whatever was in flight before is over. The socket callbacks
    /// (<c>OnceOpen</c>, <c>OnceClose</c>, <c>OnConnectionFailed</c>), the reconnect loop and the
    /// failed-<c>OnConnected</c>-handler path continue the generation of the socket they run for:
    /// a failed attempt hands the same transition to the loop, a successful one completes it. If
    /// that generation is no longer current, a later operation owns the connection, and the
    /// callback confines itself to what is unconditionally its own - closing its socket and
    /// announcing that its session ended.
    /// </para>
    /// <para>
    /// This replaces the ad-hoc <c>ReferenceEquals(ws, ...)</c> checks that used to reconcile two
    /// operations running at once. Those were placed after whichever await somebody had noticed;
    /// the generation is checked after all of them, and the socket read and the send happen under
    /// the same lock the takeover writes under, so there is no gap for a third operation to fit
    /// into.
    /// </para>
    /// </remarks>
    private long _generation;

    /// <summary>
    /// What kind of operation began the current generation. Only used to tell a superseded caller
    /// what superseded it.
    /// </summary>
    private TransitionKind _generationKind;

    /// <summary>
    /// The generation whose reconnect loop is running, or 0 when none is.
    /// </summary>
    /// <remarks>
    /// This is what <c>OnceClose</c> and <c>OnConnectionFailed</c> ask before starting a loop, and
    /// what the loop clears - under <see cref="_transitionLock"/>, together with a re-check that
    /// the socket is still open - when its attempt succeeded. It used to be a task reference and an
    /// <c>IsCompleted</c> check, which left a window between the loop's <c>break</c> and its task
    /// completing where a close saw a running loop that was about to exit and nobody reconnected.
    /// </remarks>
    private long _reconnectLoopGeneration;

    // Volatile so the lock-free readers (WaitForConnectionAsync, CheckIfNotConnected) see a
    // consistent reference; every write is under _transitionLock.
    private volatile CancellationTokenSource _reconnectCts;

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

    // Per-session isolation for ChangeServer
    private ConnectionSession? _activeSession = null;

    private readonly object _sessionLock = new();

    /// <summary>
    /// What began a generation. Carried for the message a superseded caller gets, nothing else.
    /// </summary>
    private enum TransitionKind
    {
        None,

        Connect,

        ChangeServer,

        Disconnect,

        FastReconnect,
    }

    /// <summary>
    /// What <see cref="TakeOver"/> hands the new owner: its generation, the session and socket it
    /// took from the previous one, and the exit of the message processor that went with them.
    /// </summary>
    private readonly record struct Takeover(
        long Generation,
        ConnectionSession? Session,
        WebSocketClient? Socket,
        Task ProcessorExit);

    /// <summary>
    /// Whether <paramref name="generation"/> is still the transition in charge of the connection.
    /// </summary>
    private bool Owns(long generation) => Volatile.Read(ref _generation) == generation;

    private long CurrentGeneration() => Volatile.Read(ref _generation);

    /// <summary>
    /// Begins a new transition: bumps the generation, takes the live session and socket out of
    /// their fields, stops the reconnect loop, the ping timer and the message processor - all in
    /// one critical section, so that no other operation can see the connection half taken.
    /// </summary>
    /// <remarks>
    /// The socket leaves <see cref="ws"/> here, before the caller sweeps the pending requests:
    /// the sweep resumes consumer continuations inline, and a request issued from one of them
    /// must already see no usable connection (issue #177). Closing the socket that came out is
    /// the caller's job - how it is closed depends on why it was taken.
    /// </remarks>
    /// <param name="kind">What is taking over.</param>
    /// <param name="retireSession">
    /// Whether to mark the session retiring, which silences its socket's close callback.
    /// <c>Disconnect</c> passes <see langword="false"/>: <c>OnceClose</c> is what announces a user
    /// disconnect.
    /// </param>
    private Takeover TakeOver(TransitionKind kind, bool retireSession)
    {
        CancellationTokenSource? retiredCts;
        Takeover takeover;
        lock (_transitionLock)
        {
            takeover = TakeOverLocked(kind, retireSession, out retiredCts);
        }

        retiredCts?.Cancel();
        retiredCts?.Dispose();
        return takeover;
    }

    /// <summary>
    /// <see cref="TakeOver"/> for the health check's fast reconnect, which may only take the
    /// connection over if the socket it found dead is still the one installed. A user
    /// <c>Disconnect()</c> that landed while the ping check was running has taken the socket
    /// already, and a reconnect after that would resurrect a client the consumer took down.
    /// </summary>
    /// <param name="expectedSocket">The socket the ping check found dead.</param>
    /// <param name="takeover">The takeover, when it happened.</param>
    /// <param name="ownCts">
    /// The cancellation source installed as <see cref="_reconnectCts"/> for this reconnect, so a
    /// later takeover can cancel the attempt in flight.
    /// </param>
    private bool TryTakeOverFrom(
        WebSocketClient expectedSocket,
        out Takeover takeover,
        out CancellationTokenSource ownCts)
    {
        CancellationTokenSource? retiredCts;
        lock (_transitionLock)
        {
            if (!ReferenceEquals(ws, expectedSocket))
            {
                takeover = default;
                ownCts = null;
                return false;
            }

            takeover = TakeOverLocked(TransitionKind.FastReconnect, retireSession: true, out retiredCts);
            ownCts = new CancellationTokenSource();
            _reconnectCts = ownCts;
            _reconnectAttempts = 1;
            _reconnectMode = ReconnectMode.FastReconnect;
            _isFastReconnectActive = true;
        }

        retiredCts?.Cancel();
        retiredCts?.Dispose();
        return true;
    }

    /// <summary>
    /// The body of <see cref="TakeOver"/>. Must be called with <see cref="_transitionLock"/> held;
    /// the retired reconnect source comes out for the caller to cancel and dispose outside it.
    /// </summary>
    private Takeover TakeOverLocked(
        TransitionKind kind,
        bool retireSession,
        out CancellationTokenSource? retiredCts)
    {
        long generation = ++_generation;
        _generationKind = kind;
        _permanentlyDisconnected = kind == TransitionKind.Disconnect;

        // The global intentional-disconnect flag follows the generation: set by a disconnect,
        // cleared by anything that connects. It used to be cleared only by OnceOpen and by
        // ChangeServer, so a Connect() after a Disconnect() ran with it still set, and a failure of
        // the new handshake was read as a user disconnect - "closed permanently", no reconnect
        // loop, and the caller waiting out ConnectionAcquisitionTimeout for a TimeoutException
        // that named nothing. The sockets a disconnect closed stay recognisable through their own
        // per-socket marks, which this flag does not touch.
        _isIntentionalDisconnect = kind == TransitionKind.Disconnect;
        if (kind == TransitionKind.Disconnect)
        {
            _reconnectMode = ReconnectMode.None;
        }

        retiredCts = StopReconnectLoopLocked();
        DetachLocked(retireSession, out ConnectionSession? session, out WebSocketClient? socket);
        StopPingTimerSync();

        (Task? processorTask, CancellationTokenSource? processorCts) detached;
        lock (_messageProcessorLock)
        {
            detached = DetachMessageProcessor();
        }

        return new Takeover(
            generation,
            session,
            socket,
            AwaitMessageProcessorExitAsync(detached.processorTask, detached.processorCts));
    }

    /// <summary>
    /// Takes the session and socket out of their fields. Must be called with
    /// <see cref="_transitionLock"/> held.
    /// </summary>
    /// <remarks>
    /// Nothing is taken when there is no socket: the session then belongs to whoever took the
    /// socket before - a <c>Disconnect()</c> whose close callback is about to announce
    /// <see cref="SessionEndReason.UserDisconnected"/> - or to a close that has already been
    /// announced. Retiring it here would silence that callback and hand the session to a caller
    /// that would announce a reason of its own for an end that was somebody else's.
    /// </remarks>
    private void DetachLocked(bool retireSession, out ConnectionSession? session, out WebSocketClient? socket)
    {
        socket = ws;
        ws = null;

        if (socket == null)
        {
            session = null;
            return;
        }

        lock (_sessionLock)
        {
            session = _activeSession;
            if (retireSession)
            {
                session?.MarkAsRetiring();
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="socket"/> will run its close callback once closed - which is only
    /// true of a socket whose receive loop exists. A handshake that is cancelled reports nothing,
    /// and a socket that is already closed has reported already.
    /// </summary>
    private static bool WillReportClose(WebSocketClient socket) =>
        socket.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived;

    /// <summary>
    /// Retires the reconnect loop's state. Must be called with <see cref="_transitionLock"/>
    /// held; the source comes out for the caller to cancel and dispose outside it. A loop that
    /// is running notices on its next ownership check and stands down.
    /// </summary>
    private CancellationTokenSource? StopReconnectLoopLocked()
    {
        CancellationTokenSource? retired = _reconnectCts;
        _reconnectCts = null;
        _reconnectAttempts = 0;
        _reconnectLoopGeneration = 0;
        _isFastReconnectActive = false;
        return retired;
    }

    private void ThrowIfSuperseded(long generation)
    {
        lock (_transitionLock)
        {
            ThrowIfSupersededLocked(generation);
        }
    }

    private void ThrowIfSupersededLocked(long generation)
    {
        if (_generation != generation)
        {
            throw SupersededLocked();
        }
    }

    /// <summary>
    /// The exception a superseded caller gets. A <c>Disconnect()</c> that won leaves the client
    /// disconnected, and <see cref="NotConnectedException"/> is what every other path says about
    /// that; anything else that won is connecting, or connected, somewhere the caller did not ask
    /// for, and a cancellation says so without claiming the client is down.
    /// </summary>
    private Exception SupersededLocked() =>
        _generationKind switch
        {
            TransitionKind.Disconnect => new NotConnectedException("Client has been disconnected. Call Connect() to reconnect."),
            TransitionKind.ChangeServer => new OperationCanceledException($"Superseded by a later ChangeServer to {url}."),
            TransitionKind.Connect => new OperationCanceledException("Superseded by a later Connect()."),
            _ => new OperationCanceledException("Superseded by a reconnect the health check started."),
        };

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

    /// <summary>
    /// Moves the client to another server: retires the current session and connects to
    /// <paramref name="server"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a transition of the connection and it can be superseded by a later one - a
    /// <c>Disconnect()</c>, another <c>ChangeServer</c>, a <c>Connect()</c> - issued while it is
    /// still under way, from another thread or from a consumer callback it runs. It then stops
    /// where it is, leaves the rest to the operation that took over, and tells the caller:
    /// <see cref="NotConnectedException"/> when a <c>Disconnect()</c> won, because the client is
    /// down; <see cref="OperationCanceledException"/> when anything else won, because the client
    /// is connecting, or connected, somewhere this call did not ask for. It used to reset the
    /// disconnect and connect anyway - the client online after the consumer took it down.
    /// </para>
    /// <para>
    /// The old session is retired in the background whatever happens, and the switch is
    /// announced through <see cref="OnSessionEnded"/> before the new connection is opened.
    /// </para>
    /// </remarks>
    public async Task ChangeServer(
        string server,
        ConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Validated before anything is torn down: a bad option set used to be reported only
        // after the old connection was gone.
        ValidateOptions(options ?? config);

        Takeover takeover = TakeOver(TransitionKind.ChangeServer, retireSession: true);
        long generation = takeover.Generation;

        // The old socket is closed in the background whatever happens below. A call superseded at
        // one of the checks still owes the server it left a close frame - nobody else holds the
        // socket any more.
        //
        // Per-socket tracking only. The global _isIntentionalDisconnect flag was only reset in
        // OnceOpen, so if the NEW server never came up it stayed set forever: OnConnectionFailed
        // then read the failure of the new socket as a user disconnect and started no reconnect
        // loop, leaving the client dead with "No connection attempt in progress."
        if (takeover.Socket != null)
        {
            Interlocked.Exchange(ref _userInitiatedSocket, takeover.Socket);
            MarkSocketAsUserInitiated(takeover.Socket);
            _ = RetireOldSessionAsync(takeover.Session, takeover.Socket);
        }
        else
        {
            takeover.Session?.CompleteSession();
        }

        string sessionEnded = $"Switched to {server}. Subscriptions from the previous connection are no longer in effect.";

        try
        {
            // Notified after the takeover, not before it: a handler that answers this with
            // Disconnect() has to win, and it can only win against a transition that has begun.
            SetConnectionState(XrpConnectionState.Connecting, message: $"ChangeServer: Switching to {server}...");
            ThrowIfSuperseded(generation);

            // The takeover cleared ws before this sweep, on purpose: the sweep resumes consumer
            // continuations - inline on this thread when there is no synchronization context - and
            // a request issued from one of them must already see no usable connection (issue #177).
            requestManager.RejectAllWithCancellation();
            connectionManager.RejectAllAwaitingWithCancellation();
            ThrowIfSuperseded(generation);

            // The message processor went with the session, and its reader is let go of after the
            // sweep, not before: consumers are released first, and this is the first yield of the
            // switch - what a single-threaded host runs their continuations on.
            await takeover.ProcessorExit;
            ThrowIfSuperseded(generation);

            await WaitForPingToFinishAsync();
            ThrowIfSuperseded(generation);
        }
        catch
        {
            // Superseded before the switch was announced. The session was retired by the takeover,
            // which silences its own close callback, and the operation that took over does not
            // know it - so the announcement the consumer is owed goes out here, or never.
            await NotifySessionEndedAsync(takeover.Session, SessionEndReason.ServerChanged, sessionEnded);
            throw;
        }

        // The consumer's subscriptions belonged to the session just retired and do not follow the
        // client to the new server. Nothing else on this path says so - the socket's own close
        // callback is filtered out as a retiring session, and the status notification above reads
        // Connecting, which is what a first connection reports too. Announced before the new
        // connection is opened, so a consumer cannot see OnConnected for the new session and only
        // afterwards learn that the old one is gone.
        await NotifySessionEndedAsync(takeover.Session, SessionEndReason.ServerChanged, sessionEnded);

        // The handler above may have started something of its own; the target is written only by
        // the transition that still owns the connection.
        lock (_transitionLock)
        {
            ThrowIfSupersededLocked(generation);
            url = server;
            if (options != null)
            {
                config = options;
            }
        }

        Interlocked.Exchange(ref _connectHandlerFailures, value: 0);

        await ConnectCoreAsync(generation, cancellationToken);
        await WaitForConnectionAsync(config.ConnectionAcquisitionTimeout, cancellationToken);

        // Connected - but a later ChangeServer that took over during the wait connected to its own
        // server, and this call's is not where the client is.
        string connectedTo = GetUrl();
        if (!string.Equals(connectedTo, server, StringComparison.Ordinal))
        {
            throw new OperationCanceledException(
                $"ChangeServer to {server} was superseded by a later ChangeServer to {connectedTo}.");
        }
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
    /// Retires the current session and reconnects immediately (same flow as ChangeServer).
    /// Used for ping timeout and network drop to avoid slow reconnect with exponential backoff.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs inside the ping check that found <paramref name="deadSocket"/> dead. It takes the
    /// connection over only if that socket is still the one installed: a user
    /// <c>Disconnect()</c> that landed during the check has taken it already, and reconnecting
    /// after that would resurrect a client the consumer took down. It used to check
    /// <c>_permanentlyDisconnected</c> at two points along the way instead, and the review of
    /// #178 found the point in between.
    /// </para>
    /// <para>
    /// The session and socket are captured by the takeover, before the
    /// <see cref="XrpConnectionState.RestoringConnection"/> notification - not after it. A
    /// handler that answers that notification with a <c>ChangeServer</c> and blocks on it has
    /// installed a replacement session by the time the callback returns; captured then, the
    /// replacement would be the one marked retiring. Now the handler's switch takes the
    /// connection over, and this path stands down at the check that follows the callback.
    /// </para>
    /// </remarks>
    private async Task RetireCurrentSessionAndReconnectAsync(string reason, WebSocketClient deadSocket)
    {
        if (!TryTakeOverFrom(deadSocket, out Takeover takeover, out CancellationTokenSource ownCts))
        {
            Debug.WriteLine($"{DateTime.Now}Fast reconnect not started - the socket found dead is no longer the connection");
            return;
        }

        long generation = takeover.Generation;

        // Per-socket tracking only - see ChangeServer for why the global flag stays clear. Closed
        // in the background before anything else, so a stand-down below leaves nothing open.
        if (takeover.Socket != null)
        {
            Interlocked.Exchange(ref _userInitiatedSocket, takeover.Socket);
            MarkSocketAsUserInitiated(takeover.Socket);
            takeover.Socket.SetIntentionalDisconnect(); // Suppresses Critical logging in receive loop
            _ = RetireOldSessionAsync(takeover.Session, takeover.Socket);
        }
        else
        {
            takeover.Session?.CompleteSession();
        }

        // Consumer handler exceptions are contained inside SetConnectionState - an escaping throw
        // here would leave the source installed by the takeover with no loop and nobody to
        // dispose it.
        SetConnectionState(
            XrpConnectionState.RestoringConnection,
            message: $"{reason} Reconnecting immediately...",
            ConnectionCloseSeverity.Warning,
            reconnect: BuildReconnectInfo());

        // Standing down before the session end is announced still owes that announcement: the
        // takeover retired the session, which silences its own close callback, and whoever took
        // over does not know the session - see the same catch in ChangeServer.
        if (!Owns(generation))
        {
            Debug.WriteLine($"{DateTime.Now}Fast reconnect superseded from the status handler");
            await NotifySessionEndedAsync(takeover.Session, SessionEndReason.ConnectionLost, reason).ConfigureAwait(false);
            return;
        }

        // ws is already null, so a request issued from a rejected continuation sees no usable
        // connection (issue #177). The rejection also lets the ping handler exit quickly.
        requestManager.RejectAllWithCancellation();
        connectionManager.RejectAllAwaitingWithCancellation();

        if (!Owns(generation))
        {
            await NotifySessionEndedAsync(takeover.Session, SessionEndReason.ConnectionLost, reason).ConfigureAwait(false);
            return;
        }

        // The message processor went with the session (see ChangeServer for the ordering).
        await takeover.ProcessorExit.ConfigureAwait(false);

        // This method runs inside the ping check itself, so the wait returns at once - see
        // WaitForPingToFinishAsync.
        await WaitForPingToFinishAsync().ConfigureAwait(false);

        // Same as ChangeServer: the session being retired took the subscriptions with it, and the
        // socket's own close callback will be filtered out as retiring. The RestoringConnection
        // status above reports that the connection is being rebuilt, not that everything bound to
        // the old one is gone - a consumer had to infer the second from the first. Announced
        // whether or not this path still owns the connection, for the reason given above.
        await NotifySessionEndedAsync(takeover.Session, SessionEndReason.ConnectionLost, reason)
            .ConfigureAwait(false);

        if (!Owns(generation))
        {
            return;
        }

        // Clear ping/network drop socket tracking (old socket is retired). If not cleared, these
        // stale references would cause OnConnectionFailed to filter callbacks from the NEW socket
        // if the attempt fails, blocking reconnection.
        _pingTimeoutSocket = null;
        _networkDropSocket = null;

        // Don't emit Connecting state here - RestoringConnection has been emitted already, and
        // Connecting would overwrite ReconnectInfo, confusing consuming apps.
        try
        {
            // The token of the source the takeover installed: a later takeover - a user
            // Disconnect(), say - cancels it, so the attempt below stops instead of opening a
            // socket behind a client that was taken down.
            await ConnectCoreAsync(generation, ownCts.Token).ConfigureAwait(false);
            await WaitForConnectionAsync(config.ConnectionAcquisitionTimeout, ownCts.Token).ConfigureAwait(false);

            _isFastReconnectActive = false;

            // Only tear down the source this path installed, and only while it still owns the
            // connection and no loop is running on the source: the attempt above may have failed
            // at the socket, the failure callback started the loop on this same source, and the
            // loop is the one that connected - it releases the source itself.
            CancellationTokenSource settled = null;
            lock (_transitionLock)
            {
                if (Owns(generation) &&
                    _reconnectLoopGeneration != generation &&
                    ReferenceEquals(_reconnectCts, ownCts))
                {
                    settled = ownCts;
                    _reconnectCts = null;
                    _reconnectAttempts = 0;
                }
            }

            settled?.Dispose();
        }
        catch (Exception ex)
        {
            // A later transition owns the connection - a user Disconnect(), a ChangeServer from a
            // handler. Handing the client to a reconnect loop now would undo what it did.
            if (!Owns(generation))
            {
                Debug.WriteLine($"{DateTime.Now}Fast reconnect superseded: {ex.Message}");
                return;
            }

            // The wait above ends in cancellation when its source is released, and on success the
            // path that releases it is the reconnect loop: the attempt above failed at the socket,
            // the failure callback started the loop on this same source, and that loop connected
            // first. A client that is connected has nothing to reconnect. Treating this as a
            // failure started a second loop, whose first attempt retired the live socket and
            // opened another - one reconnect became two, with a RestoringConnection reported on a
            // healthy client.
            if (IsConnected())
            {
                _isFastReconnectActive = false;
                Debug.WriteLine($"{DateTime.Now}Fast reconnect settled by the reconnect loop: {ex.Message}");
                return;
            }

            // The wait says the client gave up: the loop the failure callback started on this
            // source ran out of attempts, reported Disconnected and released the source. Starting
            // another loop here would run a second full series behind a state that said the first
            // was the last, and StopAfterMaxAttempts would mean nothing.
            if (ex is NotConnectedException)
            {
                _isFastReconnectActive = false;
                Debug.WriteLine($"{DateTime.Now}Fast reconnect gave up with the reconnect loop: {ex.Message}");
                return;
            }

            // Start the loop BEFORE notifying: SetConnectionState calls into consumer code, and an
            // exception from a handler must not cost us the reconnect loop. Ordering matters more
            // than the message here - without the loop the client never comes back.
            StartReconnectLoop(generation);

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

        // Connect() is the consumer saying "connect now", and that wins over whatever the client
        // was doing on its own: the reconnect loop stops, and a handshake another transition still
        // has in flight is closed - the takeover took it out of ws, so it is this call's to close.
        Takeover takeover = TakeOver(TransitionKind.Connect, retireSession: true);
        if (takeover.Socket != null)
        {
            MarkSocketAsUserInitiated(takeover.Socket);
            CloseSocketIntentionally(takeover.Socket);
        }

        takeover.Session?.CompleteSession();

        // Whatever was written to that socket is not going to be answered. Its close callback
        // would have swept these, but a close that is still being processed when this takeover
        // lands finds the connection owned by someone else and leaves the sweep to that owner -
        // and that owner is this call.
        requestManager.RejectAllWithCancellation();

        // The previous session's reader may still be inside a consumer handler; the new
        // connection's reader must not run alongside it. Completed at once on a client that had
        // no processor, bounded on one that did - see AwaitMessageProcessorExitAsync.
        await takeover.ProcessorExit;

        Interlocked.Exchange(ref _connectHandlerFailures, value: 0);
        SetConnectionState(XrpConnectionState.Connecting, message: $"Connecting to {url}...");

        // A session that had opened held the consumer's subscriptions, and retiring it above
        // silences the close callback that would otherwise have announced their loss - the same
        // reason ChangeServer announces for itself. A session that never opened announces
        // nothing.
        await NotifySessionEndedAsync(
            takeover.Session,
            SessionEndReason.ConnectionLost,
            "Connect() replaced a connection that was no longer open. Subscriptions from the previous connection are no longer in effect.");

        try
        {
            await ConnectCoreAsync(takeover.Generation);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later ChangeServer or Connect(): that operation is connecting now,
            // and the wait below reports on its outcome. OperationCanceledException from this
            // method means the caller's own token and nothing else - see IXrplClient.Connect. A
            // Disconnect() that won comes out of ConnectCoreAsync as NotConnectedException and
            // propagates.
        }

        await WaitForConnectionAsync(config.ConnectionAcquisitionTimeout, cancellationToken);
    }

    /// <summary>
    /// Opens the socket for <paramref name="generation"/>. The caller has taken the connection
    /// over (or continues a transition that did) and <see cref="ws"/> is null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership is checked under <see cref="_transitionLock"/> at three points: after the connect
    /// lock is acquired, together with the installation of the socket, and after the handshake.
    /// The middle one is what makes a socket created during a race impossible: the takeover and
    /// the installation run under the same lock, so a takeover either finds <see cref="ws"/>
    /// empty - and this method, seeing the generation move on, never creates the socket - or
    /// finds the socket installed and takes it. The last one covers a takeover that landed while
    /// the handshake ran: the socket is this method's to close, and it closes it.
    /// </para>
    /// <para>
    /// A takeover before the socket exists is reported as an exception
    /// (<see cref="SupersededLocked"/>); one after the handshake is not - the socket is closed and
    /// the method returns, leaving the caller's wait to report on whatever the new owner does.
    /// </para>
    /// </remarks>
    /// <param name="generation">The transition this attempt belongs to.</param>
    /// <param name="ct">
    /// Cancels the attempt: the reconnect loop's and the fast reconnect's source, which a later
    /// takeover cancels.
    /// </param>
    private async Task ConnectCoreAsync(long generation, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            WebSocketClient capturedSocket;
            ConnectionSession capturedSession;
            lock (_transitionLock)
            {
                ThrowIfSupersededLocked(generation);

                if (ShouldBeConnected())
                {
                    return;
                }

                if (url == null)
                {
                    throw new ConnectionException("Cannot connect because no server was specified");
                }

                if (ws != null)
                {
                    throw new XrplException("Websocket connection never cleaned up.");
                }

                capturedSocket = CreateWebSocket(url, config);
                ws = capturedSocket;
                _lastActiveSocket = capturedSocket;

                capturedSession = new ConnectionSession(capturedSocket, generation);
                lock (_sessionLock)
                {
                    _activeSession = capturedSession;
                }
            }

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
            Timer capturedTimer = timer;

            capturedSocket.OnConnect(async (connectedSocket) =>
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

            capturedSocket.OnConnectionError(async (e, errorSocket) =>
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

            capturedSocket.OnError(async (e, errorSocket) =>
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
            capturedSocket.OnBinaryMessage(async (m, _) =>
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
            capturedSocket.OnDisconnect(async (closeStatus, closeDescription, closingSocket) =>
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

            await capturedSocket.Connect();

            // The handshake is over, one way or another, and the attempt timer has nothing left
            // to time. On success and on failure the socket's callbacks stopped it already; a
            // handshake that a takeover cancelled reports nothing at all - WebSocketClient
            // swallows the cancellation - and the timer would go on firing OnConnectionFailed
            // for this dead socket at every ConnectionAttemptTimeout, completing whatever
            // disconnect source a later DisconnectAndWaitAsync had installed.
            capturedTimer.Stop();
            capturedTimer.Dispose();

            // A takeover during the handshake took ws - or found it empty, if the handshake had
            // already failed and its callback cleared it. A socket that is open is nobody's now
            // but this method's, and it must not be left open behind the new owner. One that is
            // not open has been dealt with by its callback, or by the takeover that cancelled it;
            // marking it again here would re-add it to the user-initiated set after that callback
            // removed it, with no close left to take it out.
            bool superseded;
            lock (_transitionLock)
            {
                superseded = !Owns(generation);
                if (superseded && ReferenceEquals(ws, capturedSocket))
                {
                    ws = null;
                }
            }

            if (superseded && capturedSocket.State == WebSocketState.Open)
            {
                MarkSocketAsUserInitiated(capturedSocket);
                CloseSocketIntentionally(capturedSocket);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Takes the connection over on behalf of a user disconnect and marks the socket that came
    /// out - the part <see cref="Disconnect"/> and <see cref="DisconnectAndWaitAsync"/> share.
    /// </summary>
    /// <remarks>
    /// A disconnect wins against anything in flight: the takeover bumps the generation, so a
    /// <c>ChangeServer</c> or a reconnect that was mid-way stands down at its next check and a
    /// handshake it had running is closed by the attempt that started it. Nothing resets the
    /// disconnect afterwards except the consumer's own <c>Connect()</c> or <c>ChangeServer</c>.
    /// </remarks>
    /// <returns>
    /// The takeover and the completion source the socket's close callback completes, when there
    /// was a socket to close.
    /// </returns>
    private (Takeover Takeover, TaskCompletionSource<bool>? Tcs) TakeOverForDisconnect()
    {
        // The socket is marked and the completion source installed in the same critical section
        // that takes the socket: its close callback completes the source, and a peer closing the
        // socket in the instant between would otherwise find no source to complete and leave
        // DisconnectAndWaitAsync waiting out its timeout.
        Takeover takeover;
        TaskCompletionSource<bool>? tcs = null;
        CancellationTokenSource? retiredCts;
        lock (_transitionLock)
        {
            takeover = TakeOverLocked(TransitionKind.Disconnect, retireSession: false, out retiredCts);

            WebSocketClient? socketToClose = takeover.Socket;
            if (socketToClose != null)
            {
                MarkSocketAsUserInitiated(socketToClose);
                socketToClose.SetIntentionalDisconnect();

                // Only for a socket whose close will be reported. A source installed for a
                // handshake in flight is completed by nobody - the cancelled handshake reports
                // nothing - and the next DisconnectAndWaitAsync would wait out its timeout on it.
                if (WillReportClose(socketToClose))
                {
                    if (_disconnectTcs == null || _disconnectTcs.Task.IsCompleted)
                    {
                        _disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    tcs = _disconnectTcs;
                }
            }
        }

        retiredCts?.Cancel();
        retiredCts?.Dispose();

        return (takeover, tcs);
    }

    public async Task<int> Disconnect()
    {
        (Takeover takeover, _) = TakeOverForDisconnect();
        long generation = takeover.Generation;
        WebSocketClient? socketToClose = takeover.Socket;

        // ws left the field in the takeover, before this sweep, so a request issued from a
        // rejected continuation finds no socket to go into (issue #177). The rejection also lets
        // the ping handler exit quickly.
        requestManager.RejectAllWithCancellation();
        connectionManager.RejectAllAwaitingWithCancellation();

        await takeover.ProcessorExit;
        await WaitForPingToFinishAsync();

        if (socketToClose == null)
        {
            // Reported only while this disconnect still owns the connection: a Connect() or
            // ChangeServer that took over during the awaits above is reporting its own state now.
            if (Owns(generation))
            {
                SetConnectionState(XrpConnectionState.Disconnected, message: "Already disconnected.");
            }

            return 0;
        }

        Interlocked.Exchange(ref _userInitiatedSocket, socketToClose);
        CloseSocketIntentionally(socketToClose);

        if (Owns(generation))
        {
            SetConnectionState(XrpConnectionState.Disconnected, message: "Disconnected by user request.");
        }

        // Announced here as well as from the socket's close callback, which dedups. The callback
        // alone is not enough: a Connect() issued right after this call installs a new session
        // before the old socket's close is processed, and the callback then files it as a stale
        // session and announces nothing - the consumer bounced the client and never heard that
        // its subscriptions went with the old connection.
        await NotifySessionEndedAsync(
            takeover.Session,
            SessionEndReason.UserDisconnected,
            "Disconnected by user request. Subscriptions from this connection are no longer in effect.");

        return 0;
    }

    /// <summary>
    /// Disconnects and waits for the WebSocket to be fully closed and cleaned up.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for cleanup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DisconnectAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        (Takeover takeover, TaskCompletionSource<bool>? tcs) = TakeOverForDisconnect();
        long generation = takeover.Generation;
        WebSocketClient? socketToClose = takeover.Socket;

        // Same ordering as Disconnect(): the socket left ws before the sweep runs (issue #177).
        requestManager.RejectAllWithCancellation();
        connectionManager.RejectAllAwaitingWithCancellation();

        await takeover.ProcessorExit;
        await WaitForPingToFinishAsync();

        if (socketToClose == null)
        {
            // Nothing here to close - but another DisconnectAndWaitAsync may be mid-way, having
            // taken the socket already. This call promised to return once the socket is gone, so
            // it waits on that one's completion source rather than reporting a disconnect that
            // has not finished.
            TaskCompletionSource<bool>? inProgress;
            lock (_transitionLock)
            {
                inProgress = _disconnectTcs;
            }

            if (inProgress is { Task.IsCompleted: false })
            {
                await Task.WhenAny(inProgress.Task, Task.Delay(timeout, cancellationToken));
            }

            if (Owns(generation))
            {
                SetConnectionState(XrpConnectionState.Disconnected, message: "Already disconnected.");
            }

            return;
        }

        Interlocked.Exchange(ref _userInitiatedSocket, socketToClose);

        if (Owns(generation))
        {
            SetConnectionState(XrpConnectionState.Disconnected, message: "Disconnected by user request.");
        }

        // See Disconnect() for why this is announced here and not left to the close callback.
        await NotifySessionEndedAsync(
            takeover.Session,
            SessionEndReason.UserDisconnected,
            "Disconnected by user request. Subscriptions from this connection are no longer in effect.");

        if (tcs == null)
        {
            // A handshake in flight: closing it reports nothing, so there is nothing to wait for
            // once the cancellation is issued.
            CloseSocketIntentionally(socketToClose);
            return;
        }

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
        lock (_transitionLock)
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

    /// <summary>
    /// The socket reported that its handshake failed, or the connect-attempt timer fired.
    /// </summary>
    /// <remarks>
    /// The failure belongs to the transition that opened the socket. If that transition still
    /// owns the connection, this is where it continues: the reconnect loop is started under it,
    /// unless it is already running - each failed attempt of the loop reaches here too. If a later
    /// transition owns the connection, that operation is handling the connection now; this
    /// callback closes its socket, clears what was its own, and does not sweep, report or
    /// reconnect against a connection that is no longer this socket's.
    /// </remarks>
    private async Task OnConnectionFailed(
        Exception error,
        WebSocketClient? errorSocket = null,
        long sessionId = 0)
    {
        // A late callback from a socket the ping check or a network drop already retired.
        if (_pingTimeoutSocket != null && _pingTimeoutSocket == errorSocket)
        {
            return;
        }

        if (_networkDropSocket != null && _networkDropSocket == errorSocket)
        {
            return;
        }

        // Detect network drop via socket's FailureReason or exception type
        bool isNetworkDrop = IsNetworkDropException(error) ||
                             errorSocket?.FailureReason == SocketFailureReason.NetworkDrop;

        WebSocketClient? currentUserInitiatedSocket = Volatile.Read(ref _userInitiatedSocket);
        bool userInitiated;
        bool intentionalDisconnect;
        bool wasOpen;
        bool isCurrentSocket;
        bool isRetiringSession = false;
        ConnectionSession? failedSession = null;

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
                            failedSession = _activeSession;
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
                    // Fallback for callbacks without session ID
                    ConnectionSession? activeSession = _activeSession;
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
                        else
                        {
                            failedSession = activeSession;
                        }
                    }
                }
            }

            bool wsIsNull;
            lock (_transitionLock)
            {
                isCurrentSocket = ReferenceEquals(ws, errorSocket);
                wsIsNull = ws == null;
            }

            userInitiated = currentUserInitiatedSocket == errorSocket || IsSocketUserInitiated(errorSocket);
            intentionalDisconnect = _isIntentionalDisconnect || userInitiated || isRetiringSession;
            wasOpen = errorSocket.State == WebSocketState.Open;

            // Clean up HashSet tracking for this socket (prevent memory leak)
            RemoveFromUserInitiatedSockets(errorSocket);

            // Clear _userInitiatedSocket if it matches this socket
            Interlocked.CompareExchange(ref _userInitiatedSocket, value: null, errorSocket);

            // For stale sockets (not current) or retiring sessions, do minimal cleanup
            if ((!isCurrentSocket && !wsIsNull) || isRetiringSession)
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

            // Use CloseSocketIntentionally for intentional disconnect or network drop to suppress
            // Critical error logging in WebSocketClient receive loop
            if (intentionalDisconnect || isNetworkDrop)
            {
                // Track network drop socket for filtering late callbacks
                if (isNetworkDrop && !intentionalDisconnect)
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

            // Conditional and under the lock: a takeover during the calls above may have
            // installed a socket of its own, and that one is not this callback's to clear.
            lock (_transitionLock)
            {
                if (ReferenceEquals(ws, errorSocket))
                {
                    ws = null;
                }
            }
        }
        else
        {
            intentionalDisconnect = _isIntentionalDisconnect || currentUserInitiatedSocket != null;
            wasOpen = false;

            // Only stop timer when operating on current connection
            timer?.Stop();
            timer?.Dispose();
            timer = null;

            // For null errorSocket with intentional disconnect, still need to clean up ws reference
            if (intentionalDisconnect)
            {
                WebSocketClient? current;
                lock (_transitionLock)
                {
                    current = ws;
                    ws = null;
                }

                if (current != null)
                {
                    CloseSocketIntentionally(current);
                }
            }
        }

        CompleteDisconnectTcs();

        if (intentionalDisconnect)
        {
            connectionManager.RejectAllAwaitingWithCancellation();
            SetConnectionState(XrpConnectionState.Disconnected, message: "Connection closed permanently.");
            return;
        }

        // From here on everything is about the connection as a whole - the sweep, the state, the
        // loop - and that belongs to whoever owns it. A callback without a session (no caller in
        // this class produces one; the parameter defaults exist for a null socket) is taken to be
        // about the current transition.
        long generation = failedSession?.Generation ?? CurrentGeneration();
        if (!Owns(generation))
        {
            return;
        }

        // Reject awaiting connection requests and pending requests. For a network drop, use
        // cancellation (no Critical logging in consuming apps); for other failures, use an
        // exception with the message.
        if (isNetworkDrop)
        {
            requestManager.RejectAllWithCancellation();
            connectionManager.RejectAllAwaitingWithCancellation();
        }
        else
        {
            connectionManager.RejectAllAwaiting(new NotConnectedException(error.Message));
        }

        if (isNetworkDrop)
        {
            SetConnectionState(
                XrpConnectionState.RestoringConnection,
                message: "Network connection lost. Reconnecting...",
                ConnectionCloseSeverity.Warning,
                reconnect: BuildReconnectInfo());
        }
        else if (IsReconnectActive())
        {
            // During reconnect, use RestoringConnection with ReconnectInfo and Warning severity
            SetConnectionState(
                XrpConnectionState.RestoringConnection,
                $"Connection attempt failed: {error.Message}",
                ConnectionCloseSeverity.Warning,
                reconnect: BuildReconnectInfo());
        }
        else
        {
            // True initial connection failure - no reconnect in progress
            SetConnectionState(
                XrpConnectionState.Disconnected,
                $"Initial connection failed: {error.Message}",
                ConnectionCloseSeverity.Error);
        }

        // Start reconnect for initial connection failures and network drops. For a network drop
        // wasOpen is true, and the client still needs to reconnect.
        if (!wasOpen || isNetworkDrop)
        {
            if (OnDisconnect is not null)
            {
                // For a network drop, use a neutral message to avoid Critical logging in
                // consuming apps that log OnDisconnect messages as errors
                string disconnectMessage = isNetworkDrop
                    ? "Network connection lost, reconnecting..."
                    : error.Message;
                await OnDisconnect?.Invoke(code: null, disconnectMessage)!;
            }

            // Under the transition that opened the failed socket, unless a loop is already running
            // for it - this callback runs for each failed attempt of that loop too, and restarting
            // it would reset its counter - or a later transition took over during the callback.
            StartReconnectLoop(generation);
        }
    }

    /// <summary>
    /// Sends a message through the WebSocket connection, fire-and-forget.
    /// </summary>
    /// <remarks>
    /// Kept for callers outside this class. <see cref="Request"/> and <see cref="GRequest{T, R}"/>
    /// use <see cref="SendRequestAsync"/> instead, which pairs the socket read with the send under
    /// the retirement lock and observes the send.
    /// </remarks>
    /// <param name="ws">The WebSocket client to send through.</param>
    /// <param name="message">The message to send.</param>
    /// <exception cref="DisconnectedException">Thrown when the WebSocket connection is null or closed.</exception>
    public void WebsocketSendAsync(WebSocketClient ws, string message)
    {
        if (ws == null)
            throw new DisconnectedException("WebSocket connection was closed before request could be sent");
        ws.SendMessage(message);
    }

    /// <summary>
    /// Writes a request into the connection as it stands right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The socket is read and the send started under <see cref="_transitionLock"/>, the lock
    /// every retirement takes the socket out under. Reading the socket first and sending after,
    /// with nothing in between, still left the few instructions of that "nothing" for a
    /// retirement to land in: the sweep had rejected the request, and the request went out to a
    /// server the client had left. Under the lock a retirement finds the request either not yet
    /// sent - and the cleared socket refuses it - or already handed to the socket.
    /// </para>
    /// <para>
    /// What runs under the lock is the send's synchronous prefix: up to the write being issued
    /// to <see cref="ClientWebSocket"/>, or up to the wait for the socket's send lock when
    /// another message holds it - see <see cref="WebSocketClient.SendMessageAsync"/> for the
    /// residue that case leaves. No consumer code and no await.
    /// </para>
    /// </remarks>
    /// <returns>The send, which faults if the message could not be written.</returns>
    /// <exception cref="DisconnectedException">There is no open connection to send into.</exception>
    private Task SendRequestAsync(string message)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message);

        lock (_transitionLock)
        {
            WebSocketClient? socket = ws;
            if (socket is not { State: WebSocketState.Open })
            {
                throw new DisconnectedException("WebSocket connection was closed before request could be sent");
            }

            return socket.SendMessageAsync(payload);
        }
    }

    /// <summary>
    /// Starts sending <paramref name="message"/> for the request <paramref name="requestId"/>,
    /// rejecting the request instead of leaving it pending if the message could not be written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection can be retired between the connectivity check and this send, and the
    /// socket can refuse the write. Either way the request never left, so it must not stay
    /// pending until RequestTimeout: the rejection is what the caller's await of the promise
    /// surfaces. A request the retirement sweep rejected first is left as the sweep left it -
    /// <see cref="RequestManager.Reject{T}"/> ignores a promise that is already gone.
    /// </para>
    /// <para>
    /// The send is observed, not awaited, by the request. The promise is what honours the
    /// request's timeout and the caller's token; a send that stalls - a half-open connection
    /// whose send buffer has filled - would otherwise hold the caller past both, and the health
    /// check's own ping with it, so the dead socket would never be noticed.
    /// </para>
    /// </remarks>
    private void SendOrReject(Guid requestId, string message)
    {
        Task send;
        try
        {
            send = SendRequestAsync(message);
        }
        catch (Exception error)
        {
            // Nothing left the client - a cleared socket, or the socket refusing to start the
            // send at all - so the request is rejected with what stopped it rather than thrown
            // past the promise the caller is about to await.
            requestManager.Reject(requestId, error);
            return;
        }

        if (send.IsCompletedSuccessfully)
        {
            return;
        }

        _ = RejectOnSendFailureAsync(requestId, send);
    }

    private async Task RejectOnSendFailureAsync(Guid requestId, Task send)
    {
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            requestManager.Reject(
                requestId,
                new DisconnectedException($"The request could not be written to the WebSocket: {error.Message}", error));
        }
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
                // Said in words: since #178 this is the exception a request issued during a
                // server switch gets at once, where it used to get a TimeoutException with
                // "Timeout" in it, and a consumer classifying failures by message text needs
                // something to recognise.
                throw new NotConnectedException(
                    "The client is not connected to a server and the request was refused at once " +
                    "(RequestFailurePolicy.ImmediateFail). Call Connect() first, or use " +
                    "RequestFailurePolicy.WaitForConnection to have requests wait for the connection.");

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

        // Connecting or RestoringConnection say an attempt is under way even with ws null. So
        // does Connected with ws null: a close is being processed - OnceClose takes the socket out
        // before its first await and reports the state, and starts the loop, after its callbacks -
        // and every path out of that reports either RestoringConnection or Disconnected. Only
        // Disconnected means nothing is in progress.
        var isActiveState = _currentConnectionState != XrpConnectionState.Disconnected;
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
        SendOrReject(_request.Id, _request.Message);

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
        SendOrReject(_request.Id, _request.Message);

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

        lock (_transitionLock)
        {
            // The transition that opened this socket has been superseded: a later operation owns
            // the connection, and it either took this socket already or - for a socket that
            // finished its handshake after the takeover - leaves it to ConnectCoreAsync to close.
            // Installing it here would undo that operation. The socket's own marks cover the
            // same case for a user Disconnect(), which does not retire the session (OnceClose is
            // what announces a user disconnect) but does mark the socket it takes.
            if (openedSession.Generation != _generation ||
                _permanentlyDisconnected ||
                IsSocketUserInitiated(connectedSocket))
            {
                return;
            }

            // Verify the connected socket matches current ws, or update ws if it was cleared
            if (ws == null)
            {
                // Restore ws reference from the connected socket
                ws = connectedSocket;
            }
            else if (!ReferenceEquals(ws, connectedSocket))
            {
                // This is a stale callback from an old socket, ignore it silently
                // Don't touch the timer - it belongs to the new connection
                return;
            }
        }

        // Only stop timer for current socket's callback
        timer?.Stop();
        timer?.Dispose();
        timer = null;

        // The connection is up. The reconnect loop, if this socket is its attempt, releases its
        // own state once ConnectCoreAsync returns to it - under the lock, with a re-check that
        // the socket is still open - so nothing here touches it. Only the mode flags are cleared.
        _reconnectMode = ReconnectMode.None;
        _isFastReconnectActive = false;

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

            // The handler is consumer code, and a Disconnect() or ChangeServer from inside it has
            // taken the connection over by now: ws is theirs (or nobody's), and reporting
            // Connected on top of the Disconnected they reported - then starting a ping timer that
            // nothing would ever stop - would be this path speaking for a connection it no longer
            // owns.
            if (!Owns(openedSession.Generation))
            {
                return;
            }

            Interlocked.Exchange(ref _connectHandlerFailures, value: 0);
            SetConnectionState(XrpConnectionState.Connected, message: $"Connected {url}");
        }
        catch (Exception error)
        {
            connectionManager.RejectAllAwaiting(error);
            await OnConnectHandlerFailedAsync(connectedSocket, openedSession, error);
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
    /// <param name="failedSession">The session that socket serves; its generation is the transition this path continues.</param>
    /// <param name="error">The exception thrown by the handler.</param>
    private async Task OnConnectHandlerFailedAsync(WebSocketClient failedSocket, ConnectionSession failedSession, Exception error)
    {
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

        // Ownership first, before anything below counts or tears down. WebSocketClient.Connect invokes
        // its OnConnect callback without awaiting it, so this can run after a newer socket has replaced
        // the one whose handler failed. That socket's failure is not a failure of the current
        // connection: it must not count towards giving up, and the give-up branch - RejectAll and
        // Disconnect() - would take the live connection down for a callback that belongs to a dead one.
        // Read-only here; the clear under the same lock happens below, once this path owns the teardown.
        if (!IsCurrentSocket(failedSocket))
        {
            failedSocket.Cancel();
            failedSocket.Disconnect();
            return;
        }

        int failures = Interlocked.Increment(ref _connectHandlerFailures);

        Debug.WriteLine($"{DateTime.Now}OnConnected handler failed ({failures}): {error.Message}");

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

            // The notification above ran consumer code. A handler that answered "gave up" with a
            // ChangeServer has already taken this socket out of ws and is opening another; the
            // teardown below would then reject that connection's requests and close its socket.
            if (!IsCurrentSocket(failedSocket))
            {
                failedSocket.Cancel();
                failedSocket.Disconnect();
                return;
            }

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

        // Always tear down the socket the handler actually ran for. WebSocketClient.Connect invokes its
        // OnConnect callback without awaiting it, so the connect lock can be released while this method is
        // still running: by now `ws` may already point at a newer socket that must not be touched.
        // Cleared before the sweep below, for the reason given in ChangeServer (issue #177): the
        // socket is open, and a request issued from a rejected continuation would otherwise go into it.
        // Taken after the notification above on purpose: the check that comes with the clear is the
        // one that sees what the consumer's handler did. The ping timer and the message processor
        // go in the same critical section - they are this connection's, and a takeover that lands
        // between the clear and their stop would otherwise have its own torn down.
        bool wasCurrentSocket;
        (Task? task, CancellationTokenSource? cts) detachedProcessor = default;
        lock (_transitionLock)
        {
            wasCurrentSocket = ReferenceEquals(ws, failedSocket);
            if (wasCurrentSocket)
            {
                ws = null;
                StopPingTimerSync();
                lock (_messageProcessorLock)
                {
                    detachedProcessor = DetachMessageProcessor();
                }
            }
        }

        if (!wasCurrentSocket)
        {
            // Replaced since the ownership check at the top - the OnError notification, the give-up
            // branch and the RestoringConnection notification in between all hand control to consumer
            // code, and a ChangeServer from any of them retires this socket itself. A newer connection
            // owns the ping timer, the pending requests, the message processor and the reconnect state
            // now; this callback closes the socket its handler ran for and steps aside.
            failedSocket.Cancel();
            failedSocket.Disconnect();
            return;
        }

        requestManager.RejectAllWithCancellation();
        await AwaitMessageProcessorExitAsync(detachedProcessor.task, detachedProcessor.cts);
        await WaitForPingToFinishAsync();

        // The socket is deliberately NOT marked as user-initiated: OnceClose must treat this as a real
        // close so the standard reconnect path runs instead of the "closed permanently" branch.
        failedSocket.Cancel();
        failedSocket.Disconnect();

        // Continue the transition this socket belongs to. If its loop is running - this method can
        // run inside the loop's own attempt, and OnceClose for the socket will ask the same
        // question - the loop carries on with its own counter, which grows per attempt; the
        // decision is one lock, so there is no exit to race with. Otherwise a loop starts here,
        // seeded with the consecutive-failure count: a fresh sequence starts its counter at zero,
        // and CalcBackoff derives the delay from that counter alone - so without the seed every
        // handler failure would restart the backoff at ReconnectBaseDelay. With
        // StopAfterMaxAttempts = false (no give-up branch) that means connect -> handler failure ->
        // teardown forever at a constant 2s, a sustained connection load on a node that accepts
        // TCP but cannot serve requests yet.
        StartReconnectLoop(failedSession.Generation, initialAttempts: failures);
    }

    /// <summary>
    /// Whether <paramref name="socket"/> is the one installed as the connection right now. Read
    /// under <see cref="_transitionLock"/>, the lock every retirement path clears <c>ws</c> under.
    /// </summary>
    private bool IsCurrentSocket(WebSocketClient socket)
    {
        lock (_transitionLock)
        {
            return ReferenceEquals(ws, socket);
        }
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
        bool isCurrentSocket;
        bool wsWasNull;
        lock (_transitionLock)
        {
            isCurrentSocket = ReferenceEquals(ws, closingSocket);
            wsWasNull = ws == null;
        }

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

        // Only for the current socket - and the ping timer and the message processor go with it,
        // this connection is over. Taken in one critical section with the clear of ws: a takeover
        // that lands between them would otherwise have its own timer and processor torn down. The
        // clear is conditional for the same reason - a takeover may already have installed a
        // socket of its own, and that one is not this callback's to clear.
        (Task? task, CancellationTokenSource? cts) detachedProcessor;
        lock (_transitionLock)
        {
            StopPingTimerSync();
            lock (_messageProcessorLock)
            {
                detachedProcessor = DetachMessageProcessor();
            }

            if (ReferenceEquals(ws, closingSocket))
            {
                ws = null;
            }
        }

        await AwaitMessageProcessorExitAsync(detachedProcessor.task, detachedProcessor.cts);

        // Check if this is a network drop (FailureReason set by WebSocketClient)
        var isNetworkDrop = closingSocket.FailureReason == SocketFailureReason.NetworkDrop;

        // Track network drop socket for immediate reconnect
        if (isNetworkDrop && !intentionalDisconnect)
        {
            _networkDropSocket = closingSocket;
        }

        // The sweep, the state and the loop belong to whoever owns the connection. This socket's
        // transition owns it unless a later one took over - a user Disconnect() that closed this
        // socket, a Connect() or ChangeServer issued while it was closing - and that operation is
        // sweeping and reporting for itself now; rejecting its requests here would reject the
        // requests of the connection it is building. A callback with no session to name (none
        // in this class) is taken to be about the current transition.
        long closingGeneration = closingSession?.Generation ?? CurrentGeneration();
        if (Owns(closingGeneration))
        {
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

        // Asked again after the two callbacks above: a handler may have moved the client on, and
        // the state it reports is its own.
        if (!Owns(closingGeneration))
        {
            return;
        }

        if (intentionalDisconnect)
        {
            var noReconnectMessage = $"Connection closed permanently. {userMessage}";
            SetConnectionState(XrpConnectionState.Disconnected, noReconnectMessage, ConnectionCloseSeverity.Warning);
            return;
        }

        if (ShouldReconnect(code) || code == 1000)
        {
            // The loop is started before the notification, so an escaping handler cannot cost it,
            // and only when none is running for this transition - the decision is one lock, so
            // there is no loop exit to race with. A loop that is running handles the close itself:
            // it re-checks the socket under the same lock before it releases.
            ReconnectInfo firstAttempt = BuildReconnectInfo(explicitAttempt: 1);
            if (StartReconnectLoop(closingGeneration))
            {
                SetConnectionState(
                    XrpConnectionState.RestoringConnection,
                    userMessage,
                    severity,
                    reconnect: firstAttempt);
            }
        }
        else
        {
            lock (_transitionLock)
            {
                if (Owns(closingGeneration))
                {
                    _reconnectAttempts = 0;
                }
            }

            var noReconnectMessage = $"Connection closed permanently. {userMessage}";
            SetConnectionState(XrpConnectionState.Disconnected, noReconnectMessage, ConnectionCloseSeverity.Warning);
        }
    }

    /// <summary>
    /// Starts the reconnect loop for <paramref name="generation"/>, unless that transition no
    /// longer owns the connection or its loop is already running. Reuses the cancellation source
    /// the fast reconnect installed when there is one; a fresh sequence starts its attempt counter
    /// at <paramref name="initialAttempts"/>, so the first delay of a fresh sequence is
    /// <c>CalcBackoff(1)</c> - twice <c>ReconnectBaseDelay</c> - except on the ping-timeout and
    /// network-drop paths, where the first attempt skips the delay entirely.
    /// </summary>
    /// <remarks>
    /// The whole decision - does the transition still own the connection, is its loop already
    /// running, is the current source reusable, install a fresh one, hand it to the new loop - is
    /// one critical section under <see cref="_transitionLock"/>, the same lock the loop releases
    /// itself under. Split, it would race with that release and with another start: two loops
    /// could end up running, or none.
    /// </remarks>
    /// <returns>Whether a loop was started.</returns>
    private bool StartReconnectLoop(long generation, int initialAttempts = 0)
    {
        CancellationTokenSource retired = null;
        lock (_transitionLock)
        {
            if (_generation != generation || _reconnectLoopGeneration == generation)
            {
                return false;
            }

            // Set reconnect mode to LoopReconnect (upgrades from FastReconnect or sets from None)
            _reconnectMode = ReconnectMode.LoopReconnect;

            // A valid pre-created source (from the fast reconnect) is reused, and the sequence it
            // belongs to continues with its counter - the seed only ever raises it. Otherwise a
            // fresh sequence starts on a fresh source.
            CancellationTokenSource existingCts = _reconnectCts;
            bool hasValidPreCreatedCts = existingCts != null && !existingCts.IsCancellationRequested;
            if (hasValidPreCreatedCts)
            {
                _reconnectAttempts = Math.Max(_reconnectAttempts, initialAttempts);
            }
            else
            {
                // Retire the old source after the lock is released - see _transitionLock
                retired = existingCts;
                _reconnectCts = new CancellationTokenSource();
                _reconnectAttempts = initialAttempts;
            }

            _reconnectLoopGeneration = generation;

            // Safe to start under the lock: ReconnectLoopAsync reads its token and yields before
            // anything else, so this only schedules the loop - no consumer notification runs inline.
            _ = ReconnectLoopAsync(generation, _reconnectCts);
        }

        retired?.Cancel();
        retired?.Dispose();
        return true;
    }

    /// <summary>
    /// Releases the loop's claim on the reconnect state, for a loop that connected. Must be called
    /// with <see cref="_transitionLock"/> held, by a loop that still owns its generation. The
    /// source comes out for the caller to dispose outside the lock.
    /// </summary>
    private CancellationTokenSource? ReleaseReconnectLoopLocked(CancellationTokenSource ownCts)
    {
        _reconnectLoopGeneration = 0;
        _reconnectAttempts = 0;

        if (!ReferenceEquals(_reconnectCts, ownCts))
        {
            return null;
        }

        _reconnectCts = null;
        return ownCts;
    }

    private async Task ReconnectLoopAsync(long generation, CancellationTokenSource ownCts)
    {
        // The source this loop runs on. A takeover cancels it without awaiting the loop, so a
        // retired loop can still be running - or reach its tail - after another transition has
        // begun. Everything this loop writes to shared state is therefore guarded by an ownership
        // check on its generation, under the lock.
        //
        // Read BEFORE the yield below, and deliberately so: the caller still holds
        // _transitionLock here, so this source cannot yet have been retired. After the yield a
        // concurrent takeover may already have disposed it - Cancel/Dispose of a retired source run
        // outside the lock - and CancellationTokenSource.Token throws ObjectDisposedException once
        // disposed. Taken after the yield, that throw would land outside every try below, faulting
        // the loop before its first attempt and vanishing as an unobserved task exception.
        CancellationToken ct = ownCts.Token;

        // Yield so nothing beyond that read runs inline on the caller: StartReconnectLoop starts the
        // loop while holding _transitionLock, and a consumer notification executing under that
        // lock could deadlock against any path that takes it (Disconnect from a handler, say).
        await Task.Yield();

        // Clear fast reconnect flag - reconnect loop has taken ownership
        _isFastReconnectActive = false;

        // For ping timeout or network drop, first attempt should be immediate (no delay)
        var isImmediateReconnect = _pingTimeoutSocket != null || _networkDropSocket != null;

        while (!ct.IsCancellationRequested)
        {
            if (!Owns(generation))
            {
                // Superseded: a later transition owns the connection now.
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
                    // The source this loop runs on was retired and disposed while the delay was
                    // being set up: registering a callback on a token whose source is gone throws
                    // instead of cancelling. Same meaning as cancellation - a later transition owns
                    // the connection now - so leave quietly rather than fault the task.
                    break;
                }
            }

            if (ct.IsCancellationRequested || !Owns(generation))
            {
                break;
            }

            try
            {
                // Retire the previous attempt's session and socket - under the lock, without a
                // takeover: this is the same transition, one attempt further on. An open socket
                // here means the transition connected by some other means while this loop was
                // waiting out its delay; the loop is done then, and retiring a healthy connection
                // would trade it for a reconnect nobody needed.
                ConnectionSession? oldSession = null;
                WebSocketClient? oldSocket = null;
                CancellationTokenSource? releasedBeforeAttempt = null;
                bool alreadyConnected;
                lock (_transitionLock)
                {
                    if (!Owns(generation))
                    {
                        break;
                    }

                    alreadyConnected = ShouldBeConnected();
                    if (alreadyConnected)
                    {
                        releasedBeforeAttempt = ReleaseReconnectLoopLocked(ownCts);
                    }
                    else
                    {
                        DetachLocked(retireSession: true, out oldSession, out oldSocket);
                    }
                }

                if (alreadyConnected)
                {
                    releasedBeforeAttempt?.Dispose();
                    return;
                }

                // Mark old socket for intentional disconnect (per-socket tracking)
                if (oldSocket != null)
                {
                    MarkSocketAsUserInitiated(oldSocket);
                    oldSocket.SetIntentionalDisconnect();
                    // Fire-and-forget graceful disposal
                    _ = RetireOldSessionAsync(oldSession, oldSocket);
                }

                await ConnectCoreAsync(generation, ct);

                // Release under the lock, with the socket re-checked under the same lock. This is
                // the window OnceClose used to fall into: the loop saw an open socket, broke out,
                // and a close processed before its task completed saw a running loop that was
                // about to exit and started nothing. Now a close either sees the loop released
                // (and starts a new one) or sees it running - and the loop, taking the lock next,
                // sees the socket closed and goes round again.
                CancellationTokenSource? released = null;
                bool settled;
                lock (_transitionLock)
                {
                    if (!Owns(generation))
                    {
                        break;
                    }

                    settled = ShouldBeConnected();
                    if (settled)
                    {
                        released = ReleaseReconnectLoopLocked(ownCts);
                    }
                }

                if (settled)
                {
                    released?.Dispose();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled by a takeover - ChangeServer, Connect, Disconnect. Exit quietly.
                Debug.WriteLine($"{DateTime.Now}Reconnect loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                // A later transition owns the connection: its state is its own to report.
                if (!Owns(generation))
                {
                    break;
                }

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

        // Exited without connecting: cancelled, or out of attempts. If a later transition owns the
        // connection, its state is its own - the takeover that superseded this loop cleared the
        // loop's claim as part of taking over. Otherwise the claim is released here, and the
        // source is disposed when the sequence is over for good.
        CancellationTokenSource finished = null;
        lock (_transitionLock)
        {
            if (!Owns(generation) || _reconnectLoopGeneration != generation)
            {
                return;
            }

            _reconnectLoopGeneration = 0;

            if (!ShouldBeConnected())
            {
                _reconnectMode = ReconnectMode.None;
            }

            if (config.StopAfterMaxAttempts &&
                _reconnectAttempts >= config.MaxReconnectAttempts &&
                ReferenceEquals(_reconnectCts, ownCts))
            {
                finished = _reconnectCts;
                _reconnectCts = null;
            }
        }

        finished?.Dispose();
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
            lock (_transitionLock)
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
                _pingTimeoutSocket = currentSocket;
                await RetireCurrentSessionAndReconnectAsync($"Ping detected disconnected state ({State()}).", currentSocket);
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
                _pingTimeoutSocket = currentSocket;

                await RetireCurrentSessionAndReconnectAsync(
                    $"Connection timeout (no activity for {inactivityLimit:F0}+ seconds).",
                    currentSocket);
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

                _pingTimeoutSocket = currentSocket;

                await RetireCurrentSessionAndReconnectAsync("Ping failed.", currentSocket);
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
    /// <c>ConnectCoreAsync</c> installs its replacement. Frames arriving in that window carry
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