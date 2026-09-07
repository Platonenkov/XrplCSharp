using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Client.Json.Converters;
using Xrpl.Models.Ledger;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;
using Xrpl.Wallet;

using JsonSerializer = System.Text.Json.JsonSerializer;

using static Xrpl.Client.Connection;
using static Xrpl.Client.XrplClient;

using BookOffers = Xrpl.Models.Transactions.BookOffers;
using Submit = Xrpl.Models.Transactions.Submit;

// https://github.com/XRPLF/xrpl.js/blob/main/packages/xrpl/src/client/index.ts

// https://xrpl.org/public-api-methods.html
namespace Xrpl.Client
{

    public delegate Task OnError(string error, string errorMessage, string message, object data);
    public delegate Task OnWarning(string warning, string message);
    public delegate Task OnServerWarning(List<RippleResponseWarning> warning, string message);
    public delegate Task OnConnected();
    public delegate Task OnDisconnect(int? code, string? description);
    public delegate Task OnPing(string ping);
    public delegate Task OnLedgerClosed(LedgerStream response);
    public delegate Task OnTransaction(TransactionStream response);
    public delegate Task OnValidationReceived(ValidationStream response);
    public delegate Task OnManifestReceived(ManifestStream response);
    public delegate Task OnPeerStatusChange(PeerStatusStream response);
    public delegate Task OnConsensusPhase(ConsensusStream response);
    public delegate Task OnPathFind(PathFindStream response);
    public delegate Task OnBookChanges(BookChangesStream response);
    public delegate Task OnServerStatus(ServerStatusStream response);

    /// <summary>
    /// Why the connection session ended.
    /// </summary>
    public enum SessionEndReason
    {
        /// <summary>
        /// The connection dropped, or the SDK tore it down itself to reconnect after a ping
        /// timeout or a network drop.
        /// </summary>
        ConnectionLost,

        /// <summary>The caller moved to another server with <c>ChangeServer</c>.</summary>
        ServerChanged,

        /// <summary>The caller closed the connection with <c>Disconnect</c>.</summary>
        UserDisconnected,
    }

    /// <summary>
    /// A connection session has ended. Everything the node held against that connection - the
    /// subscriptions above all - went with it.
    /// </summary>
    /// <param name="reason">What ended the session.</param>
    /// <param name="description">Human-readable detail, for logs.</param>
    public delegate Task OnSessionEnded(SessionEndReason reason, string description);


    public interface IXrplClient : IDisposable
    {
        /// <summary>
        /// The socket this client speaks over.
        /// </summary>
        /// <remarks>
        /// Read-only: replacing it would leave every handler registered through the events below
        /// attached to the old object, and the stream would go quiet with nothing to show for it.
        /// <c>ChangeServer</c> is how a caller moves to another server - it swaps the session
        /// inside this object rather than the object itself, so subscriptions survive.
        /// </remarks>
        Connection connection { get; }

        /// <summary>
        /// How many stream messages were discarded because handlers fell behind.
        /// </summary>
        /// <remarks>
        /// Non-zero means events reached this client and never reached its handlers. The queue in
        /// front of them is bounded (<see cref="Connection.ConnectionOptions.StreamMessageQueueCapacity"/>)
        /// and drops the oldest when full, so a slow handler costs events instead of stalling the
        /// socket - silently, until something reads this. A consumer building state from the
        /// stream should treat any increase as a signal that its state has drifted.
        /// <para>
        /// Defaulted, like <see cref="SetNetworkId"/>: forwarding to <see cref="connection"/> is
        /// the only implementation that means anything, and an observational member is a poor
        /// reason to break every external implementer of this interface.
        /// </para>
        /// </remarks>
        long DroppedStreamMessages => connection.DroppedStreamMessages;

        /// <summary>
        /// How many stream frames were discarded because they came from a connection this client
        /// had already left.
        /// </summary>
        /// <remarks>
        /// A socket being retired keeps delivering until its graceful close finishes, so a few
        /// frames can arrive after a reconnect or <c>ChangeServer</c> has moved on. Delivering them
        /// would be wrong rather than merely late: after a change of network they describe a
        /// different chain entirely. A non-zero value here is therefore normal right after a
        /// reconnect and says nothing about handler speed - that is
        /// <see cref="DroppedStreamMessages"/>, and the two are counted apart on purpose.
        /// <para>
        /// Defaulted, like <see cref="SetNetworkId"/>: forwarding to <see cref="connection"/> is
        /// the only implementation that means anything, and an observational member is a poor
        /// reason to break every external implementer of this interface.
        /// </para>
        /// </remarks>
        long StaleSessionFramesDropped => connection.StaleSessionFramesDropped;

        /// <summary>
        /// How many stream frames were dispatched outside the queue, without its ordering or its
        /// capacity bound.
        /// </summary>
        /// <remarks>
        /// Frames take that path when the background processor is not up yet, when it has been
        /// stopped, or when the channel refuses a write. The first of those is a real window on
        /// every connect: the processor starts at the end of <c>OnceOpen</c>, after the
        /// <c>OnConnected</c> callback, so a handler subscribing there can be reached before the
        /// queue exists. This counts how often that happened.
        /// <para>
        /// Defaulted, like <see cref="SetNetworkId"/>: forwarding to <see cref="connection"/> is
        /// the only implementation that means anything, and an observational member is a poor
        /// reason to break every external implementer of this interface.
        /// </para>
        /// </remarks>
        long FallbackDispatchedStreamMessages => connection.FallbackDispatchedStreamMessages;

        /// <summary>Node error reported over the socket.</summary>
        event OnError OnError;

        /// <summary>Node warning reported over the socket.</summary>
        event OnWarning OnWarning;

        /// <summary>Server warnings attached to a response envelope.</summary>
        event OnServerWarning OnServerWarning;

        /// <summary>The socket finished connecting.</summary>
        event OnConnected OnConnected;

        /// <summary>The socket closed.</summary>
        event OnDisconnect OnDisconnect;

        /// <summary>
        /// The connection that carried the subscriptions has ended - by any route.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Subscriptions live on the node against one connection. When that connection goes, so do
        /// they, and a consumer has to resubscribe. Knowing when to is the hard part, because the
        /// connection can end in three different ways and until this event only the first was
        /// announced:
        /// </para>
        /// <list type="bullet">
        /// <item><description>the socket closes on its own, or the caller closes it with
        /// <c>Disconnect</c> - <see cref="OnDisconnect"/> fires;</description></item>
        /// <item><description>a ping timeout or a network drop makes the SDK retire the session
        /// and reconnect at once - only a <see cref="XrpConnectionState.RestoringConnection"/>
        /// status went out;</description></item>
        /// <item><description><c>ChangeServer</c> retires the session to move to another server -
        /// nothing went out at all, and the client reported <c>Connected</c> throughout, so a
        /// consumer had no way to notice its stream had gone quiet for good.</description></item>
        /// </list>
        /// <para>
        /// This event fires exactly once per session in all three cases, which makes it the single
        /// thing to subscribe to in order to know that a resubscribe is due.
        /// <see cref="OnDisconnect"/> keeps its own meaning - a socket closed - and still fires
        /// alongside it where it always did.
        /// </para>
        /// <para>
        /// It reports a loss, not a readiness: on a server switch and on a fast reconnect it fires
        /// before the replacement connection is open, so the handler cannot resubscribe from where
        /// it stands. Note that the subscriptions are gone and send them again from
        /// <see cref="OnConnected"/> - which is also why the loss is announced first, so a consumer
        /// is never told to resubscribe after it has already seen the new connection come up.
        /// </para>
        /// <para>
        /// Handlers are awaited before the SDK carries on, so a slow one holds up the very
        /// reconnect or server switch it is reporting. Keep the work short.
        /// </para>
        /// <para>
        /// It does not fire for a connection attempt that never succeeded: there was no session,
        /// and so no subscription, to lose. It does fire when the client is disposed, since
        /// <see cref="IDisposable.Dispose"/> closes the connection - and, because that close is not
        /// awaited, possibly after <c>Dispose</c> has returned. A handler that touches the client
        /// should be detached before disposing it.
        /// </para>
        /// </remarks>
        event OnSessionEnded OnSessionEnded;

        /// <summary>Keep-alive round trip completed.</summary>
        event OnPing OnPing;

        /// <summary><c>ledgerClosed</c> stream event.</summary>
        event OnLedgerClosed OnLedgerClosed;

        /// <summary><c>transaction</c> stream event - the one a wallet renders for signing.</summary>
        event OnTransaction OnTransaction;

        /// <summary><c>validationReceived</c> stream event.</summary>
        event OnValidationReceived OnValidationReceived;

        /// <summary><c>manifestReceived</c> stream event.</summary>
        event OnManifestReceived OnManifestReceived;

        /// <summary><c>peerStatusChange</c> stream event.</summary>
        event OnPeerStatusChange OnPeerStatusChange;

        /// <summary><c>consensusPhase</c> stream event.</summary>
        event OnConsensusPhase OnConsensusPhase;

        /// <summary><c>path_find</c> follow-up.</summary>
        event OnPathFind OnPathFind;

        /// <summary><c>bookChanges</c> stream event.</summary>
        event OnBookChanges OnBookChanges;

        /// <summary><c>serverStatus</c> stream event.</summary>
        event OnServerStatus OnServerStatus;

        /// <summary>Connection state transitions, for diagnostics.</summary>
        event Action<ConnectionStatusInfo> OnConnectionStatus;
        double feeCushion { get; set; }
        string maxFeeXRP { get; set; }
        uint? networkID { get; set; }

        /// <summary>
        /// Set network id for transactions, required in network where Id > 1024
        /// </summary>
        /// <param name="networkId">network id</param>
        public void SetNetworkId(uint? networkId)
        {
            this.networkID = networkId;
        }

        #region Server
        /// <summary> the url </summary>
        string Url();
        /// <summary>
        /// Connects to the server and reads the network id from it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Returns when the client is connected and that second step has completed. A consumer
        /// <see cref="OnConnected"/> handler that fails and works on the next attempt does not
        /// reach the caller: the client rebuilds the connection itself, and reporting that as a
        /// failed connect would be reporting something that did not happen.
        /// </para>
        /// <para>
        /// Throws <see cref="NotConnectedException"/> when the client gave up - a
        /// handler that fails every time, or a server that never comes up - and when a
        /// <see cref="Disconnect"/> issued while this call was connecting won: the client is down,
        /// and this call did not connect it. <see cref="OperationCanceledException"/> means what
        /// it says and nothing else: the caller's own <paramref name="cancellationToken"/> was
        /// cancelled. A <c>ChangeServer</c> issued meanwhile does not fail this call - it returns
        /// once the client is connected, wherever that switch took it.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">Cancels the wait for a connection.</param>
        Task Connect(System.Threading.CancellationToken cancellationToken = default);
        /// <summary> Disconnect from server </summary>
        Task Disconnect();
        /// <summary>
        /// Disconnects and waits for the WebSocket to be fully closed and cleaned up.
        /// </summary>
        /// <param name="timeout">Maximum time to wait for cleanup.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task DisconnectAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
        /// <summary> if the websocket is connected </summary>
        bool IsConnected();
        /// <summary> The subscribe method requests periodic notifications from the server when certain events happen. </summary>
        /// <param name="request">An <see cref="SubscribeRequest"/> request.</param>
        /// <returns></returns>
        Task<XrplResponse<object>> Subscribe(SubscribeRequest request, CancellationToken cancellationToken = default);
        /// <summary> The unsubscribe command tells the server to stop sending messages for a particular subscription or set of subscriptions.</summary>
        /// <param name="request">An <see cref="UnsubscribeRequest"/> request.</param>
        /// <returns></returns>
        Task<XrplResponse<object>> Unsubscribe(UnsubscribeRequest request, CancellationToken cancellationToken = default);
        /// <summary>
        /// The ping command returns an acknowledgement,
        /// so that clients can test the connection status and latency
        /// </summary>
        /// <returns></returns>
        Task<XrplResponse<object>> Ping(CancellationToken cancellationToken = default);
        /// <summary> The server_info command asks the server for a human-readable version of various information about the rippled server being queried. </summary>
        /// <param name="request">An <see cref="ServerInfoRequest"/> request.</param>
        /// <returns>A <see cref="ServerInfo"/> response.</returns>
        Task<XrplResponse<ServerInfo>> ServerInfo(ServerInfoRequest request, CancellationToken cancellationToken = default);
        /// <summary> The server_state command asks the server for a human-readable version of various information about the rippled server being queried. </summary>
        /// <param name="request">An <see cref="ServerStateRequest"/> request.</param>
        /// <returns>A <see cref="ServerState"/> response.</returns>
        Task<XrplResponse<ServerState>> ServerState(ServerStateRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The feature command returns information about amendments this server knows about,<br/>
        /// including whether they are enabled and if the server knows how to apply the amendments.<br/><br/>
        /// 
        /// This is the non-admin version of the feature admin command.<br/>
        /// It follows the same formatting as the admin command, but hides potentially sensitive data.
        /// </summary>
        /// <param name="feature">
        /// (Optional) The unique ID of an amendment, as hexadecimal;<br/>
        /// or the short name of the amendment.<br/>
        /// If provided, limits the response to one amendment. Otherwise, the response lists all amendments.
        /// </param>
        /// <returns>A <see cref="ServerFeatures"/> response. Feature and their states</returns>
        Task<XrplResponse<ServerFeatures>> ServerFeatures(string feature = null, CancellationToken cancellationToken = default);

        /// <summary> The fee command reports the current state of the open-ledger requirements for the transaction cost. </summary>
        /// <returns>An <see cref="Models.Methods.Fee"/> response.</returns>
        Task<XrplResponse<Fee>> Fee(CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>server_definitions</c> method retrieves the definition enums used by the server.
        /// </summary>
        /// <param name="request">A <see cref="ServerDefinitionsRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ServerDefinitionsResponse"/>.</returns>
        Task<XrplResponse<ServerDefinitionsResponse>> ServerDefinitions(ServerDefinitionsRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>vault_info</c> method retrieves information about a vault.
        /// </summary>
        /// <param name="request">A <see cref="VaultInfoRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="VaultInfoResponse"/>.</returns>
        Task<XrplResponse<VaultInfoResponse>> VaultInfo(VaultInfoRequest request, CancellationToken cancellationToken = default);

        #endregion

        #region Account
        //https://xrpl.org/account-methods.html
        /// <summary> The account_info command retrieves information about an account, its activity, and its XRP balance. </summary>
        /// <param name="request">An <see cref="AccountInfoRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.AccountInfo"/> response.</returns>
        Task<XrplResponse<AccountInfo>> AccountInfo(AccountInfoRequest request, CancellationToken cancellationToken = default);


        /// <summary> The account_offers method retrieves a list of offers made by a given account that are outstanding as of a particular ledger version </summary>
        /// <param name="request">An <see cref="AccountOffersRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.AccountOffers"/> response.</returns>
        Task<XrplResponse<AccountOffers>> AccountOffers(AccountOffersRequest request, CancellationToken cancellationToken = default);

        /// <summary> The account_currencies command retrieves a list of currencies that an account can send or receive, based on its trust lines. </summary>
        /// <param name="request">An <see cref="AccountCurrenciesRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.AccountCurrencies"/> response.</returns>
        Task<XrplResponse<AccountCurrencies>> AccountCurrencies(AccountCurrenciesRequest request, CancellationToken cancellationToken = default);


        /// <summary>
        /// The account_lines method returns information about an account's trust lines, including balances in all non-XRP currencies and assets.
        /// </summary>
        /// <param name="request">An <see cref="AccountLinesRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.AccountLines"/> response.</returns>
        Task<XrplResponse<AccountLines>> AccountLines(AccountLinesRequest request, CancellationToken cancellationToken = default);


        /// <summary>
        /// The AccountObjects command returns the raw ledger format for all objects owned by an account. For a higher-level view of an account's trust lines and balances, see <see cref="Models.Methods.AccountLines"/> instead.
        /// </summary>
        /// <param name="request">An <see cref="AccountObjectsRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.AccountObjects"/> response.</returns>
        Task<XrplResponse<AccountObjects>> AccountObjects(AccountObjectsRequest request, CancellationToken cancellationToken = default);


        /// <summary>
        /// The noripple_check command provides a quick way to check the status of the Default Ripple field
        /// for an account and the No Ripple flag of its trust lines, compared with the recommended settings
        /// </summary>
        /// <returns>An <see cref="NoRippleCheckRequest"/> response.</returns>
        /// <returns>An <see cref="Models.Methods.NoRippleCheck"/> response.</returns>
        Task<XrplResponse<NoRippleCheck>> NoRippleCheck(NoRippleCheckRequest request, CancellationToken cancellationToken = default);


        /// <summary> The gateway_balances command calculates the total balances issued by a given account,
        /// optionally excluding amounts held by operational addresses. </summary>
        /// <param name="request">An <see cref="GatewayBalancesRequest"/> request.</param>
        /// <returns>An <see cref="GatewayBalancesResponse"/> response.</returns>
        Task<XrplResponse<GatewayBalancesResponse>> GatewayBalances(GatewayBalancesRequest request, CancellationToken cancellationToken = default);


        /// <summary> The account_tx method retrieves a list of transactions that involved the specified account </summary>
        /// <param name="request">An <see cref="AccountTransactionsRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.AccountTransactions"/> response.</returns>
        Task<XrplResponse<AccountTransactions>> AccountTransactions(AccountTransactionsRequest request, CancellationToken cancellationToken = default);
        /// <summary> The account_channels method returns information about an account's Payment Channels.
        /// This includes only channels where the specified account is the channel's source, not the destination. </summary>
        /// <param name="request">An <see cref="AccountChannelsRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.AccountChannels"/> response.</returns>
        Task<XrplResponse<AccountChannels>> AccountChannels(AccountChannelsRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The simulate method executes a dry run of any transaction type,
        /// enabling you to preview the results and metadata of a transaction without committing them to the XRP Ledger.<br/>
        /// Since this command never submits a transaction to the network, it doesn't incur any fees.<br/>
        /// Expects a response in the form of a  <see cref="SimulateRequest"/> .
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        Task<XrplResponse<SimulateResponse>> Simulate(SimulateRequest request, CancellationToken cancellationToken = default);
        #endregion

        #region NFT


        /// <summary> The nft_buy_offers method returns a list of buy offers for a given NFToken object. </summary>
        /// <param name="request">An <see cref="NFTBuyOffersRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.NFTBuyOffers"/> response.</returns>
        Task<XrplResponse<NFTBuyOffers>> NFTBuyOffers(NFTBuyOffersRequest request, CancellationToken cancellationToken = default);

        /// <summary> The nft_sell_offers method returns a list of sell offers for a given NFToken object</summary>
        /// <param name="request">An <see cref="NFTSellOffersRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.NFTSellOffers"/> response.</returns>
        Task<XrplResponse<NFTSellOffers>> NFTSellOffers(NFTSellOffersRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The nft_info method says who owns an NFToken and what it was minted with.
        /// </summary>
        /// <remarks>
        /// A Clio method: a plain rippled node answers <c>unknownCmd</c>, which arrives as an
        /// ordinary node error so a caller can catch it and fall back. There is no substitute for
        /// it on rippled - an owner cannot be read out of <see cref="NFTSellOffers"/>, because a
        /// sale leaves the seller's offers in the ledger and the new owner usually has none.
        /// </remarks>
        /// <param name="request">An <see cref="NFTInfoRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.NFTInfo"/> response.</returns>
        Task<XrplResponse<NFTInfo>> NFTInfo(NFTInfoRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The nft_history method returns the transactions that touched an NFToken.
        /// </summary>
        /// <remarks>
        /// A Clio method, paginated the way <c>account_tx</c> is: keep passing
        /// <see cref="Models.Methods.NFTHistory.Marker"/> back until an answer comes without one.
        /// </remarks>
        /// <param name="request">An <see cref="NFTHistoryRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.NFTHistory"/> response.</returns>
        Task<XrplResponse<NFTHistory>> NFTHistory(NFTHistoryRequest request, CancellationToken cancellationToken = default);


        /// <summary> The account_nfts method returns a list of NFToken objects for the specified account.</summary>
        /// <param name="request">An <see cref="AccountNFTsRequest"/> request.</param>
        /// <returns>An <see cref="Models.Methods.AccountNFTs"/> response.</returns>
        Task<XrplResponse<AccountNFTs>> AccountNFTs(AccountNFTsRequest request, CancellationToken cancellationToken = default);


        #endregion

        #region Transactions
        ////https://xrpl.org/transaction-methods.html
        ///// <summary>
        ///// The submit method applies a transaction and sends it to the network to be confirmed and included in future ledgers.
        ///// </summary>
        ///// <param name="request">An <see cref="SubmitRequest"/> request.</param>
        ///// <returns>An <see cref="Models.Transaction.Submit"/> response.</returns>
        //Task<Submit> Submit(SubmitRequest request);
        /// <summary>
        /// Submits a transaction to the XRP Ledger for processing.
        /// </summary>
        /// <param name="tx">
        /// Transaction in JSON format with an array of Signers.<br/>
        /// To be successful, the weights of the signatures must be equal or higher than the quorum of the SignerList.
        /// </param>
        /// <param name="wallet">wallet</param>
        /// <param name="autoFill">use autofill for tx</param>
        /// <param name="failHard">yse fail hard</param>
        /// <returns>An <see cref="Models.Transactions.Submit"/> response.</returns>
        Task<Submit> Submit(Dictionary<string, object> tx, XrplWallet wallet, bool autoFill = true, bool failHard = false, CancellationToken cancellationToken = default);
        /// <summary>
        /// Submits a transaction to the XRP Ledger for processing.
        /// </summary>
        /// <param name="tx">
        /// Transaction.<br/>
        /// To be successful, the weights of the signatures must be equal or higher than the quorum of the SignerList.
        /// </param>
        /// <param name="wallet">wallet</param>
        /// <param name="autoFill">use autofill for tx</param>
        /// <param name="failHard">yse fail hard</param>
        /// <returns>An <see cref="Models.Transactions.Submit"/> response.</returns>
        Task<Submit> Submit(ITransactionRequest tx, XrplWallet wallet, bool autoFill = true, bool failHard = false, CancellationToken cancellationToken = default);
        /// <summary>
        /// The tx method retrieves information on a single transaction, by its identifying hash, using
        /// the API v1 wire shape: the transaction's own fields (Account, Amount, TransactionType, ...)
        /// sit at the top level of <see cref="TransactionResponse"/>, alongside meta.
        /// </summary>
        /// <remarks>
        /// Always requests API v1 regardless of <see cref="ClientOptions.ApiVersion"/> — this is the one
        /// place in the SDK where the choice of method, not a setting, decides the protocol version. It
        /// cannot honor <see cref="ClientOptions.ApiVersion"/> instead: <see cref="TransactionResponse"/>
        /// has no field for API v2's <c>tx_json</c>, so handing it a v2 payload would lose the transaction
        /// wholesale rather than just its field names. Use <see cref="TxV2"/> for the v2 shape.
        /// </remarks>
        /// <param name="request">An <see cref="TxRequest"/> request.</param>
        /// <returns>An <see cref="TransactionResponse"/> response.</returns>
        Task<XrplResponse<TransactionResponse>> TxV1(TxRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The tx method retrieves information on a single transaction, by its identifying hash, using
        /// the API v2 wire shape: <see cref="TransactionSummary.Transaction"/> (from <c>tx_json</c>) and
        /// <see cref="TransactionSummary.Meta"/> sit side by side, as rippled's v2 response actually sends
        /// them.
        /// </summary>
        /// <remarks>
        /// Always requests API v2 regardless of <see cref="ClientOptions.ApiVersion"/> — like
        /// <see cref="TxV1"/>, the choice of method decides the protocol version here, not the client
        /// setting. Use <see cref="TxV1"/> for the v1 shape.
        /// </remarks>
        Task<XrplResponse<TransactionSummary>> TxV2(TxRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>transaction_entry</c> method retrieves information on a single transaction
        /// from a specific ledger version.
        /// </summary>
        /// <param name="request">A <see cref="TransactionEntryRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="TransactionEntryResponse"/>.</returns>
        Task<XrplResponse<TransactionEntryResponse>> TransactionEntry(TransactionEntryRequest request, CancellationToken cancellationToken = default);
        #endregion

        #region Channels

        /// <summary>
        /// The <c>channel_authorize</c> method creates a signature that can be used to redeem
        /// a specific amount of XRP from a payment channel.
        /// </summary>
        /// <param name="request">A <see cref="ChannelAuthorizeRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ChannelAuthorizeResponse"/> containing the signature.</returns>
        Task<XrplResponse<ChannelAuthorizeResponse>> ChannelAuthorize(ChannelAuthorizeRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>channel_verify</c> method checks the validity of a signature that can be
        /// used to redeem a specific amount of XRP from a payment channel.
        /// </summary>
        /// <param name="request">A <see cref="ChannelVerifyRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ChannelVerifyResponse"/> indicating whether the signature is valid.</returns>
        Task<XrplResponse<ChannelVerifyResponse>> ChannelVerify(ChannelVerifyRequest request, CancellationToken cancellationToken = default);

        #endregion

        #region Ledger
        //https://xrpl.org/ledger-methods.html

        /// <summary>
        /// The ledger_request command tells server to fetch a specific ledger version from its connected peers.
        /// This only works if one of the server's immediately-connected peers has that ledger.
        /// You may need to run the command several times to completely fetch a ledger
        /// </summary>
        /// <param name="request">An <see cref="LedgerRequest"/> request.</param>
        /// <returns>An <see cref="LOLedger"/> response.</returns>
        Task<XrplResponse<LOLedger>> Ledger(LedgerRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The ledger_data method retrieves contents of the specified ledger.
        /// You can iterate through several calls to retrieve the entire contents of a single ledger version.
        /// </summary>
        /// <param name="request">An <see cref="LedgerDataRequest"/> request.</param>
        /// <returns>An <see cref="LOLedgerData"/> response.</returns>
        Task<XrplResponse<LOLedgerData>> LedgerData(LedgerDataRequest request, CancellationToken cancellationToken = default);
        /// <summary> The ledger_closed method returns the unique identifiers of the most recently closed ledger. </summary>
        /// <param name="request">An <see cref="LedgerClosedRequest"/> response.</param>
        /// <returns>An <see cref="LOBaseLedger"/> response.</returns>
        Task<XrplResponse<LOBaseLedger>> LedgerClosed(LedgerClosedRequest request, CancellationToken cancellationToken = default);
        /// <summary>
        /// The ledger_current method returns the unique identifiers of the current in-progress ledger.<br/>
        /// This command is mostly useful for testing, because the ledger returned is still in flux.
        /// </summary>
        /// <param name="request">An <see cref="LedgerCurrentRequest"/> response.</param>
        /// <returns>An <see cref="LOLedgerCurrentIndex"/> response.</returns>
        Task<XrplResponse<LOLedgerCurrentIndex>> LedgerCurrent(LedgerCurrentRequest request, CancellationToken cancellationToken = default);
        /// <summary>
        /// The ledger_entry method returns a single ledger object from the XRP Ledger in its raw format.<br/>
        /// See ledger format for information on the different types of objects you can retrieve.
        /// </summary>
        /// <param name="request">An <see cref="LedgerEntryRequest"/> response.</param>
        /// <returns>An <see cref="LedgerEntryResponse"/> response.</returns>
        Task<XrplResponse<LedgerEntryResponse>> LedgerEntry(LedgerEntryRequest request, CancellationToken cancellationToken = default);


        #endregion

        /// <summary>
        /// The amm_info method gets information about an Automated Market Maker (AMM) instance.
        /// </summary>
        /// <param name="request">An <see cref="AMMInfoRequest"/> request.</param>
        /// <returns>An <see cref="AMMInfoResponse"/> response.</returns>
        Task<XrplResponse<AMMInfoResponse>> AmmInfo(AMMInfoRequest request, CancellationToken cancellationToken = default);
        /// <summary>
        /// The book_offers method retrieves a list of offers, also known as the order book , between two currencies
        /// </summary>
        /// <param name="request">An <see cref="BookOffersRequest"/> request.</param>
        /// <returns>An <see cref="Models.Transactions.BookOffers"/> response.</returns>
        Task<XrplResponse<BookOffers>> BookOffers(BookOffersRequest request, CancellationToken cancellationToken = default);
        /// <summary>
        /// The random command provides a random number to be used as a source of entropy for random number generation by clients.<br/>
        /// https://xrpl.org/random.html#random
        /// </summary>
        /// <returns></returns>
        Task<XrplResponse<object>> Random(CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>deposit_authorized</c> command indicates whether one account is authorized to send payments
        /// directly to another. https://xrpl.org/deposit_authorized.html
        /// </summary>
        /// <param name="request">A <see cref="DepositAuthorizedRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="DepositAuthorized"/> response.</returns>
        Task<XrplResponse<DepositAuthorized>> DepositAuthorized(DepositAuthorizedRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>path_find</c> create sub-command creates an ongoing request to find possible paths
        /// along which a payment transaction could be made.<br/>
        /// WebSocket API only.<br/>
        /// After the initial response, the server sends asynchronous follow-ups via the <see cref="OnPathFind"/> event.
        /// </summary>
        /// <param name="request">A <see cref="PathFindCreateRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="PathFindResponse"/> with initial path alternatives.</returns>
        Task<XrplResponse<PathFindResponse>> PathFind(PathFindCreateRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>path_find</c> close sub-command instructs the server to stop sending information
        /// about the current open pathfinding request.
        /// </summary>
        /// <param name="request">A <see cref="PathFindCloseRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="PathFindResponse"/>.</returns>
        Task<XrplResponse<PathFindResponse>> PathFindClose(PathFindCloseRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>path_find</c> status sub-command requests an immediate update about the client's
        /// currently-open pathfinding request.
        /// </summary>
        /// <param name="request">A <see cref="PathFindStatusRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="PathFindResponse"/>.</returns>
        Task<XrplResponse<PathFindResponse>> PathFindStatus(PathFindStatusRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// The <c>ripple_path_find</c> method is a simplified version of the path_find method
        /// that provides a single response with a payment path you can use right away.<br/>
        /// Available in both WebSocket and JSON-RPC APIs.
        /// </summary>
        /// <param name="request">A <see cref="RipplePathFindRequest"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="RipplePathFindResponse"/>.</returns>
        Task<XrplResponse<RipplePathFindResponse>> RipplePathFind(RipplePathFindRequest request, CancellationToken cancellationToken = default);

        Task<XrplResponse<object>> AnyRequest(BaseRequest request, CancellationToken cancellationToken = default);

        Task<XrplResponse<Dictionary<string, object>>> Request(Dictionary<string, object> request, CancellationToken cancellationToken = default);
        Task<XrplResponse<T>> GRequest<T, R>(R request, CancellationToken cancellationToken = default) where R : BaseRequest;


        #region Sugars
        /// <summary>
        /// Autofills fields in a transaction. This will set `Sequence`, `Fee`,
        /// `lastLedgerSequence` according to the current state of the server this Client
        /// is connected to. It also converts all X-Addresses to classic addresses and
        /// flags interfaces into numbers.
        /// </summary>
        /// <param name="tx">A {@link Transaction} in JSON format</param>
        /// <param name="signersCount">The expected number of signers for this transaction. Only used for multisigned transactions.</param>
        /// <returns>The autofilled transaction.</returns>
        Task<Dictionary<string, object>> Autofill(Dictionary<string, object> tx, int? signersCount = null, CancellationToken cancellationToken = default);
        /// <summary>
        /// Autofills fields in a transaction. This will set `Sequence`, `Fee`,
        /// `lastLedgerSequence` according to the current state of the server this Client
        /// is connected to. It also converts all X-Addresses to classic addresses and
        /// flags interfaces into numbers.
        /// </summary>
        /// <param name="tx">A {@link Transaction} in JSON format</param>
        /// <param name="signersCount">The expected number of signers for this transaction. Only used for multisigned transactions.</param>
        /// <returns>The autofilled transaction.</returns>
        Task<T> Autofill<T>(T tx, int? signersCount = null, CancellationToken cancellationToken = default) where T : ITransactionRequest;
        Task<uint> GetLedgerIndex(CancellationToken cancellationToken = default);
        Task<string> GetXrpBalance(string address, CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves the client to another server: retires the current session, connects to
        /// <paramref name="server"/> and reads its network id.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The switch is announced through <see cref="OnSessionEnded"/> with
        /// <see cref="SessionEndReason.ServerChanged"/> before the new connection is opened; the
        /// subscriptions held against the old connection do not follow the client.
        /// </para>
        /// <para>
        /// A later <see cref="Disconnect"/>, <see cref="Connect"/> or <c>ChangeServer</c> - from
        /// another thread, or from a handler this call runs - takes the connection over, and this
        /// call stops where it is: <see cref="NotConnectedException"/> when a
        /// <see cref="Disconnect"/> won, because the client is down;
        /// <see cref="OperationCanceledException"/> when anything else won, because the client is
        /// connecting, or connected, somewhere this call did not ask for. The old session is
        /// retired either way.
        /// </para>
        /// </remarks>
        /// <param name="server">The WebSocket URL of the server to move to.</param>
        /// <param name="options">Options for the new connection; <c>null</c> keeps the current ones.</param>
        /// <param name="cancellationToken">Cancels the wait for the new connection.</param>
        Task ChangeServer(string server, ClientOptions? options = null, CancellationToken cancellationToken = default);

        string EnsureClassicAddress(string address);

        #endregion
    }

    public class XrplClient : IXrplClient
    {

        public class ClientOptions : ConnectionOptions
        {
            public uint? NetworkID { get; set; }
            public double? feeCushion { get; set; }
            public string? maxFeeXRP { get; set; }

            /// <summary>
            /// The API version to use when making requests.
            /// </summary>
            public uint? ApiVersion { get; set; }
        }

        // get-only, not `private set`: the one-assignment invariant the forwarding below depends on
        // is then checked by the compiler rather than by everyone who edits this 1100-line class.
        // A second assignment would strand every handler attached through these events on the old
        // object.
        public Connection connection { get; }

        /// <inheritdoc />
        public long DroppedStreamMessages => connection.DroppedStreamMessages;

        /// <inheritdoc />
        public long StaleSessionFramesDropped => connection.StaleSessionFramesDropped;

        /// <inheritdoc />
        public long FallbackDispatchedStreamMessages => connection.FallbackDispatchedStreamMessages;

        // Forwarded, not relayed: add/remove reach the same Connection a caller would have used
        // through the property, so this type holds no delegates and no subscription of its own. A
        // relaying version - a local event plus a subscription to the connection that re-raises it
        // - would add a second subscriber list, a subscription nothing removes, and a second place
        // to keep in sync. There is nothing to keep in sync here.
        //
        // Safe because the Connection outlives the client: it is assigned once, in the
        // constructor, and ChangeServer swaps the session inside it rather than the object. Were
        // that to change, handlers would silently stay on the old object - which is why the
        // property lost its public setter.

        public event OnError OnError
        {
            add => connection.OnError += value;
            remove => connection.OnError -= value;
        }

        public event OnWarning OnWarning
        {
            add => connection.OnWarning += value;
            remove => connection.OnWarning -= value;
        }

        public event OnServerWarning OnServerWarning
        {
            add => connection.OnServerWarning += value;
            remove => connection.OnServerWarning -= value;
        }

        public event OnConnected OnConnected
        {
            add => connection.OnConnected += value;
            remove => connection.OnConnected -= value;
        }

        public event OnDisconnect OnDisconnect
        {
            add => connection.OnDisconnect += value;
            remove => connection.OnDisconnect -= value;
        }

        public event OnSessionEnded OnSessionEnded
        {
            add => connection.OnSessionEnded += value;
            remove => connection.OnSessionEnded -= value;
        }

        public event OnPing OnPing
        {
            add => connection.OnPing += value;
            remove => connection.OnPing -= value;
        }

        public event OnLedgerClosed OnLedgerClosed
        {
            add => connection.OnLedgerClosed += value;
            remove => connection.OnLedgerClosed -= value;
        }

        public event OnTransaction OnTransaction
        {
            add => connection.OnTransaction += value;
            remove => connection.OnTransaction -= value;
        }

        public event OnValidationReceived OnValidationReceived
        {
            add => connection.OnValidationReceived += value;
            remove => connection.OnValidationReceived -= value;
        }

        public event OnManifestReceived OnManifestReceived
        {
            add => connection.OnManifestReceived += value;
            remove => connection.OnManifestReceived -= value;
        }

        public event OnPeerStatusChange OnPeerStatusChange
        {
            add => connection.OnPeerStatusChange += value;
            remove => connection.OnPeerStatusChange -= value;
        }

        public event OnConsensusPhase OnConsensusPhase
        {
            add => connection.OnConsensusPhase += value;
            remove => connection.OnConsensusPhase -= value;
        }

        public event OnPathFind OnPathFind
        {
            add => connection.OnPathFind += value;
            remove => connection.OnPathFind -= value;
        }

        public event OnBookChanges OnBookChanges
        {
            add => connection.OnBookChanges += value;
            remove => connection.OnBookChanges -= value;
        }

        public event OnServerStatus OnServerStatus
        {
            add => connection.OnServerStatus += value;
            remove => connection.OnServerStatus -= value;
        }

        public event Action<ConnectionStatusInfo> OnConnectionStatus
        {
            add => connection.OnConnectionStatus += value;
            remove => connection.OnConnectionStatus -= value;
        }
        public double feeCushion { get; set; }
        public string maxFeeXRP { get; set; }
        public uint? networkID { get; set; }

        /// <summary>
        /// rippled's name for the API version field. Typed requests get it from
        /// <see cref="BaseRequest.ApiVersion"/>'s <c>[JsonPropertyName]</c>; a dictionary request
        /// has to spell it out, since its keys reach the wire exactly as written.
        /// </summary>
        private const string ApiVersionField = "api_version";

        /// <summary>
        /// The API version to use when making requests.
        /// </summary>
        public uint ApiVersion { get; set; }

        ///// <summary> Current web socket client state </summary>
        //public WebSocketState SocketState => client.State;

        public XrplClient(string server, ClientOptions? options = null)
        {

            if (!IsValidWss(server))
            {
                throw new Exception("Invalid WSS Server Url");
            }
            SetSettings(options);
            connection = new Connection(server, options);
        }

        private void SetSettings(ClientOptions options)
        {
            if (feeCushion != 0 && options is null)
            {
                return;
            }

            feeCushion = options?.feeCushion ?? 1.2;
            maxFeeXRP = options?.maxFeeXRP;
            networkID = options?.NetworkID;
            ApiVersion = options?.ApiVersion ?? 2;
        }

        public async Task ChangeServer(string server, ClientOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!IsValidWss(server))
            {
                throw new Exception("Invalid WSS Server Url");
            }
            SetSettings(options);

            await connection.ChangeServer(server, options, cancellationToken);
            await SetNetworkId();
        }

        /// <inheritdoc />
        public string Url()
        {
            return this.connection.GetUrl();
        }

        public bool IsValidWss(string server)
        {
            return true;
        }

        /// <inheritdoc />
        public async Task Connect(System.Threading.CancellationToken cancellationToken = default)
        {
            await connection.Connect(cancellationToken);
            await SetNetworkIdWhileConnectingAsync(cancellationToken);
        }

        /// <summary>
        /// Reads the network id, surviving a connection the client is still settling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Connect()</c> is two operations, not one: the connection, and this. The socket really
        /// does open for a moment before a failing <c>OnConnected</c> handler brings it down, so the
        /// wait inside the first operation can return successfully while the second is still in
        /// flight - and the teardown then rejects it. Letting that reach the caller was wrong twice
        /// over: they cancelled nothing, and a handler that fails once and works on the next attempt
        /// is precisely what the reconnect path is for, so moments later the client was connected
        /// and <c>Connect()</c> had reported a failure that did not happen.
        /// </para>
        /// <para>
        /// Waiting rather than asking whether the client is connected right now: at the moment the
        /// request is rejected the socket has just been torn down, so the answer would be no even
        /// though the recovery is already under way. The wait ends when the connection is back, or
        /// throws <see cref="NotConnectedException"/> if the client gave up - the one case where the
        /// caller really did fail to connect.
        /// </para>
        /// <para>
        /// More than one attempt because the wait can also return during the teardown itself, while
        /// the old socket is still nominally open; the request then goes out on a socket that is
        /// already dying. The cap is the number of times the connection may be rebuilt: each retry
        /// here answers one teardown, and the client cannot tear down more often than that before it
        /// gives up.
        /// </para>
        /// </remarks>
        private async Task SetNetworkIdWhileConnectingAsync(CancellationToken cancellationToken)
        {
            int attempts = Math.Max(1, connection.config.MaxReconnectAttempts);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await SetNetworkId();
                    return;
                }
                catch (Exception error) when (
                    error is OperationCanceledException or DisconnectedException &&
                    !cancellationToken.IsCancellationRequested &&
                    attempt < attempts)
                {
                    await connection.WaitForConnectionAsync(cancellationToken: cancellationToken);
                }
            }
        }

        private async Task SetNetworkId()
        {
            var server = await ServerInfo(new ServerInfoRequest());
            if (server.Result?.Info?.NetworkID is { } id and > 1024)
            {
                SetNetworkId(id);
            }
            else
            {
                SetNetworkId(networkId: null);
            }
        }

        public void SetNetworkId(uint? networkId)
        {
            this.networkID = networkId;
        }

        /// <inheritdoc />
        public async Task Disconnect()
        {
            await connection.Disconnect();
        }

        /// <inheritdoc />
        public async Task DisconnectAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            await connection.DisconnectAndWaitAsync(timeout, cancellationToken);
        }

        /// <inheritdoc />
        public bool IsConnected()
        {
            return this.connection.IsConnected();
        }

        // SUGARS
        public Task<Dictionary<string, object>> Autofill(Dictionary<string, object> tx, int? signersCount = null, CancellationToken cancellationToken = default)
        {
            return AutofillSugar.Autofill(this, tx, signersCount, cancellationToken);
        }
        public async Task<T> Autofill<T>(T tx, int? signersCount = null, CancellationToken cancellationToken = default) where T : ITransactionRequest
        {
            var dic = tx.ToDictionary();
            var filled = await AutofillSugar.Autofill(this, dic, signersCount, cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(filled, XrplJsonOptions.Default);
            tx = (T)JsonSerializer.Deserialize(json, tx.GetType(), XrplJsonOptions.Default);

            return tx;
        }

        /// <inheritdoc />
        public Task<Submit> Submit(Dictionary<string, object> tx, XrplWallet wallet, bool autoFill = true, bool failHard = false, CancellationToken cancellationToken = default)
        {
            if (this.networkID is { } network)
            {
                tx["NetworkID"] = network;
            }

            return this.Submit(tx, autoFill, failHard, wallet, cancellationToken);
        }
        /// <inheritdoc />
        public Task<Submit> Submit(ITransactionRequest tx, XrplWallet wallet, bool autoFill = true, bool failHard = false, CancellationToken cancellationToken = default)
        {
            if (this.networkID is { } network)
            {
                tx.NetworkID = network;
            }

            var json = tx.ToJson();
            //var json = JsonConvert.SerializeObject(tx);
            Dictionary<string, object> txJson = JsonSerializer.Deserialize<Dictionary<string, object>>(json, XrplJsonOptions.Default);
            return this.Submit(txJson, autoFill, failHard, wallet, cancellationToken);
        }

        /// <inheritdoc />
        public Task<uint> GetLedgerIndex(CancellationToken cancellationToken = default)
        {
            return GetLedgerSugar.GetLedgerIndex(this, cancellationToken);
        }
        /// <inheritdoc />
        public Task<string> GetXrpBalance(string address, CancellationToken cancellationToken = default)
        {
            return BalancesSugar.GetXrpBalance(this, address, cancellationToken: cancellationToken);
        }

        // REQUESTS
        /// <inheritdoc />
        public Task<XrplResponse<AccountChannels>> AccountChannels(AccountChannelsRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AccountChannels, AccountChannelsRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<ChannelAuthorizeResponse>> ChannelAuthorize(ChannelAuthorizeRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<ChannelAuthorizeResponse, ChannelAuthorizeRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<ChannelVerifyResponse>> ChannelVerify(ChannelVerifyRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<ChannelVerifyResponse, ChannelVerifyRequest>(request, cancellationToken);
        }

        public Task<XrplResponse<SimulateResponse>> Simulate(SimulateRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<SimulateResponse, SimulateRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<AccountCurrencies>> AccountCurrencies(AccountCurrenciesRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AccountCurrencies, AccountCurrenciesRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<AccountInfo>> AccountInfo(AccountInfoRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AccountInfo, AccountInfoRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<AccountLines>> AccountLines(AccountLinesRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AccountLines, AccountLinesRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<AccountNFTs>> AccountNFTs(AccountNFTsRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AccountNFTs, AccountNFTsRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<AccountObjects>> AccountObjects(AccountObjectsRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AccountObjects, AccountObjectsRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<AccountOffers>> AccountOffers(AccountOffersRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AccountOffers, AccountOffersRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<AccountTransactions>> AccountTransactions(AccountTransactionsRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AccountTransactions, AccountTransactionsRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<AMMInfoResponse>> AmmInfo(AMMInfoRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<AMMInfoResponse, AMMInfoRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<BookOffers>> BookOffers(BookOffersRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<BookOffers, BookOffersRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<DepositAuthorized>> DepositAuthorized(DepositAuthorizedRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<DepositAuthorized, DepositAuthorizedRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<LOLedger>> Ledger(LedgerRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<LOLedger, LedgerRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<LOBaseLedger>> LedgerClosed(LedgerClosedRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<LOBaseLedger, LedgerClosedRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<LOLedgerCurrentIndex>> LedgerCurrent(LedgerCurrentRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<LOLedgerCurrentIndex, LedgerCurrentRequest>(request, cancellationToken);
        }
        /// <inheritdoc />
        public Task<XrplResponse<LOLedgerData>> LedgerData(LedgerDataRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<LOLedgerData, LedgerDataRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<LedgerEntryResponse>> LedgerEntry(LedgerEntryRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<LedgerEntryResponse, LedgerEntryRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<Fee>> Fee(CancellationToken cancellationToken = default)
        {
            FeeRequest request = new FeeRequest();
            return this.GRequest<Fee, FeeRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<GatewayBalancesResponse>> GatewayBalances(GatewayBalancesRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<GatewayBalancesResponse, GatewayBalancesRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<NFTBuyOffers>> NFTBuyOffers(NFTBuyOffersRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<NFTBuyOffers, NFTBuyOffersRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<NFTSellOffers>> NFTSellOffers(NFTSellOffersRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<NFTSellOffers, NFTSellOffersRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<NFTInfo>> NFTInfo(NFTInfoRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<NFTInfo, NFTInfoRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<NFTHistory>> NFTHistory(NFTHistoryRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<NFTHistory, NFTHistoryRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<NoRippleCheck>> NoRippleCheck(NoRippleCheckRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<NoRippleCheck, NoRippleCheckRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<PathFindResponse>> PathFind(PathFindCreateRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<PathFindResponse, PathFindCreateRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<PathFindResponse>> PathFindClose(PathFindCloseRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<PathFindResponse, PathFindCloseRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<PathFindResponse>> PathFindStatus(PathFindStatusRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<PathFindResponse, PathFindStatusRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<object>> Ping(CancellationToken cancellationToken = default)
        {
            PingRequest request = new PingRequest();
            return this.GRequest<object, PingRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<object>> Random(CancellationToken cancellationToken = default)
        {
            RandomRequest request = new RandomRequest();
            return this.GRequest<object, RandomRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<RipplePathFindResponse>> RipplePathFind(RipplePathFindRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<RipplePathFindResponse, RipplePathFindRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<ServerInfo>> ServerInfo(ServerInfoRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<ServerInfo, ServerInfoRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<ServerState>> ServerState(ServerStateRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<ServerState, ServerStateRequest>(request, cancellationToken);
        }
        /// <inheritdoc />
        public Task<XrplResponse<ServerFeatures>> ServerFeatures(string feature = null, CancellationToken cancellationToken = default)
        {
            var request = new ServerFeaturesRequest()
            {
                Feature = feature
            };
            return this.GRequest<ServerFeatures, ServerFeaturesRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<ServerDefinitionsResponse>> ServerDefinitions(ServerDefinitionsRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<ServerDefinitionsResponse, ServerDefinitionsRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<VaultInfoResponse>> VaultInfo(VaultInfoRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<VaultInfoResponse, VaultInfoRequest>(request, cancellationToken);
        }

        //public Task<Submit> Submit(SubmitRequest request)
        //{
        //    return this.GRequest<Submit, SubmitRequest>(request);
        //}

        //public Task<SubmitMultisign> SubmitMultisign(SubmitMultisignRequest request, Wallet wallet)
        //{
        //    return this.GRequest<SubmitMultisign, SubmitMultisignRequest>(request);
        //}

        /// <inheritdoc />
        public Task<XrplResponse<object>> Subscribe(SubscribeRequest request, CancellationToken cancellationToken = default)
        {

            return this.GRequest<object, SubscribeRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<object>> Unsubscribe(UnsubscribeRequest request, CancellationToken cancellationToken = default)
        {

            return this.GRequest<object, UnsubscribeRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<TransactionEntryResponse>> TransactionEntry(TransactionEntryRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<TransactionEntryResponse, TransactionEntryRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<TransactionResponse>> TxV1(TxRequest request, CancellationToken cancellationToken = default)
        {
            request.ApiVersion = 1;
            return this.GRequest<TransactionResponse, TxRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<TransactionSummary>> TxV2(TxRequest request, CancellationToken cancellationToken = default)
        {
            request.ApiVersion = 2;
            return this.GRequest<TransactionSummary, TxRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<object>> AnyRequest(BaseRequest request, CancellationToken cancellationToken = default)
        {
            return this.GRequest<object, BaseRequest>(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<Dictionary<string, object>>> Request(Dictionary<string, object> request, CancellationToken cancellationToken = default)
        {
            //string account = request["Account"] ? EnsureClassicAddress((string)request["account"]) : null;
            //request["Account"] = account;

            // The key has to be the wire name. A dictionary is serialized verbatim, and rippled
            // knows only `api_version` - it ignores anything else and answers on its default,
            // API v1. Stamping `nameof(ApiVersion)` here meant this path never delivered the
            // version at all, so the same client spoke v2 through its typed methods and v1
            // through this one.
            if (!request.ContainsKey(ApiVersionField))
            {
                request[ApiVersionField] = ApiVersion;
            }

            return this.connection.Request(request, cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public Task<XrplResponse<T>> GRequest<T, R>(R request, CancellationToken cancellationToken = default) where R : BaseRequest
        {
            request.ApiVersion ??= ApiVersion;
            return this.connection.GRequest<T, R>(request, cancellationToken: cancellationToken);
        }

        public string EnsureClassicAddress(string address)
        {
            return Xrpl.Sugar.Utils.EnsureClassicAddress(address);
        }

        #region IDisposable

        public void Dispose()
        {
            // todo: should check for ws...
            connection?.Disconnect();
        }

        #endregion
    }
}
