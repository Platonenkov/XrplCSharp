using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Xrpl.Client.Exceptions;
using Xrpl.Client.Json.Converters;
using Xrpl.Models.Common;

// https://xrpl.org/docs/references/protocol/transactions/types/vaultwithdraw

namespace Xrpl.Models.Transactions
{
    /// <summary>
    /// The VaultWithdraw transaction withdraws assets from a vault.
    /// </summary>
    /// <remarks>Requires the Vault amendment (XLS-65d). This feature is in draft and subject to change.</remarks>
    public interface IVaultWithdraw : ITransactionCommon
    {
        /// <summary>
        /// The ID of the vault to withdraw from.
        /// </summary>
        string VaultID { get; set; }

        /// <summary>
        /// The exact amount of vault asset to withdraw or vault share to redeem.
        /// </summary>
        Currency Amount { get; set; }

        /// <summary>
        /// An account to receive the assets. This account must be able to receive the vault asset
        /// or the transaction fails.
        /// </summary>
        string Destination { get; set; }

        /// <summary>
        /// Arbitrary tag identifying the reason for the withdrawal to the destination.
        /// </summary>
        uint? DestinationTag { get; set; }

        /// <summary>
        /// Credentials (object IDs, 64 hex characters each) authorizing the withdrawal when the
        /// <see cref="Destination"/> requires deposit authorization with credential-based preauth
        /// (XLS-70). Maximum 8 entries.
        /// </summary>
        List<string> CredentialIDs { get; set; }
    }

    /// <inheritdoc cref="IVaultWithdraw" />
    public class VaultWithdraw : TransactionRequest, IVaultWithdraw
    {
        public VaultWithdraw()
        {
            TransactionType = TransactionType.VaultWithdraw;
        }

        /// <inheritdoc />
        [JsonPropertyName("VaultID")]
        public string VaultID { get; set; }

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

    /// <inheritdoc cref="IVaultWithdraw" />
    public class VaultWithdrawResponse : TransactionResponse, IVaultWithdraw
    {
        /// <inheritdoc />
        [JsonPropertyName("VaultID")]
        public string VaultID { get; set; }

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
        public static async Task ValidateVaultWithdraw(Dictionary<string, object> tx)
        {
            await Common.ValidateBaseTransaction(tx);

            if (!tx.TryGetValue("VaultID", out var vaultId) || vaultId is not string vaultIdStr
                || !IsValidHash256(vaultIdStr))
                throw new ValidationException("VaultWithdraw: VaultID must be a valid Hash256 (64 hex characters)");

            if (!tx.TryGetValue("Amount", out var amount) || amount is null)
                throw new ValidationException("VaultWithdraw: missing field Amount");

            if (tx.TryGetValue("CredentialIDs", out var credentialIds) && credentialIds is not null)
            {
                CredentialsValidator.ValidateCredentialsList(credentialIds, "VaultWithdraw", "CredentialIDs", isStringID: true);
            }
        }

        /// <summary>
        /// Validates that a string is a valid Hash256: exactly 64 hex characters.
        /// </summary>
        internal static bool IsValidHash256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
                return false;
            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }
    }
}
