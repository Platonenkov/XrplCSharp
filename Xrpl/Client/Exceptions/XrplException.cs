using System;
using System.Text.RegularExpressions;

using Xrpl.Models.Subscriptions;

namespace Xrpl.Client.Exceptions
{
    public class RippleException : Exception
    {
        public RippleException() { }

        public RippleException(string message) : base(message) { }
        public RippleException(string message, Exception? InnerException) : base(message, InnerException) { }
    }

    public class XrplException : Exception
    {
        //public readonly string message;
        //public readonly byte[]? data;

        //public XrplException(byte[]? data, string message = "")
        //{
        //    //this.name = this.constructor.name; 
        //    this.message = message;
        //    this.data = data;
        //}

        public XrplException() { }

        public XrplException(string message) : base(message) { }
        public XrplException(string message, Exception? InnerException) : base(message, InnerException) { }
    }

    /// <summary>
    /// Exception thrown when rippled responds with an Exception.
    /// </summary>
    public class RippledException : XrplException
    {
        public ErrorResponse Response { get; }

        public RippledException(string message, ErrorResponse response) : base(message)
        {
            Response = response;
        }
    }

    public static class RippledErrorParser
    {
        private static readonly Regex RippledLineRegex =
            new(@"^RippledError:\s*(?<msg>[^\r\n]+)",
                RegexOptions.Multiline | RegexOptions.Compiled);

        public static string? TryExtractRippledError(string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
                return null;

            var m = RippledLineRegex.Match(errorMessage);
            return m.Success ? m.Groups["msg"].Value.Trim() : errorMessage;
        }
    }

    /// <summary>
    /// Exception thrown when xrpl.js cannot specify Exception type.
    /// </summary>
    public class UnexpectedException : XrplException { }
    /// <summary>
    /// Exception thrown when xrpl.js has an Exception with connection to rippled.
    /// </summary>
    public class ConnectionException : XrplException
    {
        public ConnectionException(string message) : base(message)
        {
        }
    }
    /// <summary>
    /// Exception thrown when xrpl.js is not connected to rippled server.
    /// </summary>
    public class NotConnectedException : XrplException
    {
        /// <summary>
        /// The message used when none is given. A bare <c>throw new NotConnectedException()</c>
        /// used to carry the runtime's "Exception of type ... was thrown", which says nothing to a
        /// consumer that classifies failures by their text.
        /// </summary>
        public const string DefaultMessage =
            "The client is not connected to a server. Call Connect() first, or wait for the connection to be restored.";

        public NotConnectedException(string message = null) : base(message ?? DefaultMessage)
        {
        }
    }
    /// <summary>
    /// Exception thrown when xrpl.js has disconnected from rippled server.
    /// </summary>
    public class DisconnectedException : XrplException
    {
        public DisconnectedException(string message) : base(message)
        {
        }

        public DisconnectedException(string message, Exception? innerException) : base(message, innerException)
        {
        }
    }
    /// <summary>
    /// Exception thrown when rippled is not initialized.
    /// </summary>
    public class RippledNotInitializedException : XrplException { }
    /// <summary>
    /// Exception thrown when a request to rippled times out.
    /// <para>
    /// IMPORTANT: this is <c>Xrpl.Client.Exceptions.TimeoutException</c>, NOT
    /// <see cref="System.TimeoutException"/>. It derives from <see cref="XrplException"/>, so a
    /// <c>catch (System.TimeoutException)</c> will NOT catch it. Catch this type (or its base
    /// <see cref="XrplException"/>) explicitly; if both <c>using System;</c> and
    /// <c>using Xrpl.Client.Exceptions;</c> are in scope, fully-qualify the type to avoid catching
    /// the wrong one.
    /// </para>
    /// </summary>
    public class TimeoutException : XrplException
    {
        public TimeoutException(string message, object data = null) : base(message)
        {

        }
    }
    /// <summary>
    /// Exception thrown when xrpl.js sees a response in the wrong format.
    /// </summary>
    public class ResponseFormatException : XrplException
    {
        public ResponseFormatException(string message, object data = null) : base(message)
        {
        }
    }
    /// <summary>
    /// Exception thrown when xrpl.js sees a malformed transaction.
    /// </summary>
    public class ValidationException : XrplException
    {
        public ValidationException(string message = null) : base(message)
        {
        }
        public ValidationException(string message, Exception? InnerException) : base(message, InnerException) { }
    }
    /// <summary>
    /// Exception thrown when a client cannot generate a wallet from the testnet/devnet
    /// faucets, or when the client cannot infer the faucet URL(i.e.when the Client
    /// is connected to mainnet).
    /// </summary>
    public class XRPLFaucetException : XrplException
    {
        public XRPLFaucetException(string message = null) : base(message)
        {
        }

        /// <summary>
        /// Keeps the failure that caused this one. A faucet call fails through the network, the
        /// JSON or the node, and without the cause the caller is left with a sentence.
        /// </summary>
        public XRPLFaucetException(string message, Exception? InnerException) : base(message, InnerException)
        {
        }
    }
    /// <summary>
    /// Exception thrown when xrpl.js cannot retrieve a transaction, ledger, account, etc.
    /// From rippled.
    /// </summary>
    public class NotFoundException : XrplException
    {
        public NotFoundException(string message = "Not Found") : base(message) { }
    }
}