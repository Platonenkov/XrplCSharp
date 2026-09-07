using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Xrpl.Client.Exceptions;
using Xrpl.Client.Json.Converters;
using Xrpl.Models.Common;

namespace Xrpl.Models.Transactions
{
    /// <summary>
    /// The LoanBrokerCoverWithdraw transaction withdraws cover assets from a loan broker.
    /// </summary>
    /// <remarks>Requires the Loan amendment (XLS-66d). This feature is in draft and subject to change.</remarks>
    public interface ILoanBrokerCoverWithdraw : ITransactionCommon
    {
        /// <summary>
        /// The ID of the loan broker to withdraw cover assets from.
        /// </summary>
        string LoanBrokerID { get; set; }

        /// <summary>
        /// The amount of cover assets to withdraw.
        /// </summary>
        Currency Amount { get; set; }

        /// <summary>
        /// The destination account for the withdrawn assets. Optional.
        /// </summary>
        string Destination { get; set; }

        /// <summary>
        /// An arbitrary tag to identify the destination. Optional.
        /// </summary>
        uint? DestinationTag { get; set; }

        /// <summary>
        /// Credentials (object IDs, 64 hex characters each) authorizing the withdrawal when the
        /// <see cref="Destination"/> requires deposit authorization with credential-based preauth
        /// (XLS-70). Maximum 8 entries.
        /// </summary>
        List<string> CredentialIDs { get; set; }
    }

    /// <inheritdoc cref="ILoanBrokerCoverWithdraw" />
    public class LoanBrokerCoverWithdraw : TransactionRequest, ILoanBrokerCoverWithdraw
    {
        public LoanBrokerCoverWithdraw()
        {
            TransactionType = TransactionType.LoanBrokerCoverWithdraw;
        }

        /// <inheritdoc />
        [JsonPropertyName("LoanBrokerID")]
        public string LoanBrokerID { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Amount")]
        [JsonConverter(typeof(CurrencyConverter))]
        public Currency Amount { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Destination")]
        public string Destination { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("DestinationTag")]
        public uint? DestinationTag { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("CredentialIDs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> CredentialIDs { get; set; }
    }

    /// <inheritdoc cref="ILoanBrokerCoverWithdraw" />
    public class LoanBrokerCoverWithdrawResponse : TransactionResponse, ILoanBrokerCoverWithdraw
    {
        /// <inheritdoc />
        [JsonPropertyName("LoanBrokerID")]
        public string LoanBrokerID { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Amount")]
        [JsonConverter(typeof(CurrencyConverter))]
        public Currency Amount { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Destination")]
        public string Destination { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("DestinationTag")]
        public uint? DestinationTag { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("CredentialIDs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> CredentialIDs { get; set; }
    }

    public partial class Validation
    {
        public static async Task ValidateLoanBrokerCoverWithdraw(Dictionary<string, object> tx)
        {
            await Common.ValidateBaseTransaction(tx);

            if (!tx.TryGetValue("LoanBrokerID", out var id) || id is not string)
                throw new ValidationException("LoanBrokerCoverWithdraw: missing field LoanBrokerID");

            if (!tx.TryGetValue("Amount", out var amount) || amount is null)
                throw new ValidationException("LoanBrokerCoverWithdraw: missing field Amount");

            if (tx.TryGetValue("CredentialIDs", out var credentialIds) && credentialIds is not null)
            {
                CredentialsValidator.ValidateCredentialsList(credentialIds, "LoanBrokerCoverWithdraw", "CredentialIDs", isStringID: true);
            }
        }
    }
}
