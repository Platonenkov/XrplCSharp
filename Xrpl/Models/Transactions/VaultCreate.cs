using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Xrpl.Client.Exceptions;
using Xrpl.Client.Json.Converters;
using Xrpl.Models.Common;

using static Xrpl.Models.Common.Common;

// https://xrpl.org/docs/references/protocol/transactions/types/vaultcreate

namespace Xrpl.Models.Transactions
{
    /// <summary>
    /// Flags for VaultCreate transactions.
    /// </summary>
    [Flags]
    public enum VaultCreateFlags : uint
    {
        /// <summary>
        /// Designates the vault as private, restricting access to credentialed accounts
        /// within a specified Permissioned Domain. Set only at vault creation.
        /// </summary>
        tfVaultPrivate = 0x00010000,

        /// <summary>
        /// Makes vault shares non-transferable between accounts. Set only at vault creation.
        /// </summary>
        tfVaultShareNonTransferable = 0x00020000,
    }

    /// <summary>
    /// The VaultCreate transaction creates a new Vault ledger object for holding pooled assets.
    /// </summary>
    /// <remarks>Requires the Vault amendment (XLS-65d). This feature is in draft and subject to change.</remarks>
    public interface IVaultCreate : ITransactionCommon
    {
        /// <summary>
        /// The asset held by the vault.
        /// </summary>
        IssuedCurrency Asset { get; set; }

        /// <summary>
        /// The initial deposit amount.
        /// </summary>
        Currency Amount { get; set; }

        /// <summary>
        /// The maximum asset amount that can be held in the vault.
        /// STNumber type (12 bytes: int64 mantissa + int32 exponent), serialized as string in JSON.
        /// </summary>
        string AssetsMaximum { get; set; }

        /// <summary>
        /// Arbitrary metadata for the vault shares (MPToken), limited in size. Hex-encoded string.
        /// </summary>
        string MPTokenMetadata { get; set; }

        /// <summary>
        /// The withdrawal policy for the vault. Defines how withdrawals are handled.
        /// </summary>
        uint? WithdrawalPolicy { get; set; }

        /// <summary>
        /// The scale (decimal precision) for the vault shares.
        /// </summary>
        uint? Scale { get; set; }

        /// <summary>
        /// Arbitrary hex-encoded data associated with the vault, limited to 256 bytes.
        /// </summary>
        string Data { get; set; }

        /// <summary>
        /// The ID of a permissioned domain to associate with the vault.
        /// </summary>
        string DomainID { get; set; }

        /// <summary>
        /// LendingProtocolV1_1: the kind of vault, see <see cref="Xrpl.Models.Ledger.VaultKind"/>.
        /// Absent means open-ended. <see cref="Xrpl.Models.Ledger.VaultKind.ClosedEnded"/> requires
        /// both <see cref="SubscriptionDate"/> and <see cref="RedemptionDate"/>; an open-ended vault
        /// may carry neither.
        /// </summary>
        uint? VaultKind { get; set; }

        /// <summary>
        /// LendingProtocolV1_1: the end of a closed-ended vault's subscription phase, after which
        /// its investment phase begins. Fixed at creation. Serialized as seconds since the Ripple Epoch.
        /// </summary>
        DateTime? SubscriptionDate { get; set; }

        /// <summary>
        /// LendingProtocolV1_1: the start of a closed-ended vault's redemption phase. Fixed at creation,
        /// and must lie at least one minute and less than thirty years after <see cref="SubscriptionDate"/>.
        /// Serialized as seconds since the Ripple Epoch.
        /// </summary>
        DateTime? RedemptionDate { get; set; }
    }

    /// <inheritdoc cref="IVaultCreate" />
    public class VaultCreate : TransactionRequest, IVaultCreate
    {
        public VaultCreate()
        {
            TransactionType = TransactionType.VaultCreate;
        }

        /// <inheritdoc />
        [JsonPropertyName("Asset")]
        [JsonConverter(typeof(IssuedCurrencyConverter))]
        public IssuedCurrency Asset { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Amount")]
        public Currency Amount { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("AssetsMaximum")]
        public string AssetsMaximum { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("MPTokenMetadata")]
        public string MPTokenMetadata { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("WithdrawalPolicy")]
        public uint? WithdrawalPolicy { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Scale")]
        public uint? Scale { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Data")]
        public string Data { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("DomainID")]
        public string DomainID { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("VaultKind")]
        public uint? VaultKind { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("SubscriptionDate")]
        [JsonConverter(typeof(RippleDateTimeConverter))]
        public DateTime? SubscriptionDate { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("RedemptionDate")]
        [JsonConverter(typeof(RippleDateTimeConverter))]
        public DateTime? RedemptionDate { get; set; }
    }

    /// <inheritdoc cref="IVaultCreate" />
    public class VaultCreateResponse : TransactionResponse, IVaultCreate
    {
        /// <inheritdoc />
        [JsonPropertyName("Asset")]
        [JsonConverter(typeof(IssuedCurrencyConverter))]
        public IssuedCurrency Asset { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Amount")]
        public Currency Amount { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("AssetsMaximum")]
        public string AssetsMaximum { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("MPTokenMetadata")]
        public string MPTokenMetadata { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("WithdrawalPolicy")]
        public uint? WithdrawalPolicy { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Scale")]
        public uint? Scale { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("Data")]
        public string Data { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("DomainID")]
        public string DomainID { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("VaultKind")]
        public uint? VaultKind { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("SubscriptionDate")]
        [JsonConverter(typeof(RippleDateTimeConverter))]
        public DateTime? SubscriptionDate { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("RedemptionDate")]
        [JsonConverter(typeof(RippleDateTimeConverter))]
        public DateTime? RedemptionDate { get; set; }
    }

    public partial class Validation
    {
        /// <summary>
        /// rippled <c>kMinInvestmentPeriod</c>: the smallest gap between SubscriptionDate and RedemptionDate.
        /// </summary>
        private const long MinInvestmentPeriodSeconds = 60;

        /// <summary>
        /// rippled <c>kMaxInvestmentPeriod</c>: thirty Gregorian years; the gap must stay below it.
        /// </summary>
        private const long MaxInvestmentPeriodSeconds = 946708560;

        public static async Task ValidateVaultCreate(Dictionary<string, object> tx)
        {
            await Common.ValidateBaseTransaction(tx);

            if (!tx.TryGetValue("Asset", out var asset) || asset is null)
                throw new ValidationException("VaultCreate: missing field Asset");

            // rippled VaultCreate::preflight, closed-ended vaults (LendingProtocolV1_1)
            bool closedEnded = false;
            if (tx.TryGetValue("VaultKind", out var vaultKind) && vaultKind is not null)
            {
                if (!Common.TryGetUInt32(vaultKind, out uint kind))
                    throw new ValidationException("VaultCreate: VaultKind must be a number");

                if (kind != (uint)Ledger.VaultKind.OpenEnded && kind != (uint)Ledger.VaultKind.ClosedEnded)
                    throw new ValidationException("VaultCreate: VaultKind must be 0 (open-ended) or 1 (closed-ended)");

                closedEnded = kind == (uint)Ledger.VaultKind.ClosedEnded;
            }

            bool hasSubscription = tx.TryGetValue("SubscriptionDate", out var subscription) && subscription is not null;
            bool hasRedemption = tx.TryGetValue("RedemptionDate", out var redemption) && redemption is not null;

            if (!closedEnded && (hasSubscription || hasRedemption))
                throw new ValidationException("VaultCreate: SubscriptionDate and RedemptionDate are only allowed on a closed-ended vault");

            if (!closedEnded)
                return;

            if (!hasSubscription || !hasRedemption)
                throw new ValidationException("VaultCreate: a closed-ended vault requires both SubscriptionDate and RedemptionDate");

            if (!Common.TryGetUInt32(subscription, out uint subscriptionDate) || !Common.TryGetUInt32(redemption, out uint redemptionDate))
                throw new ValidationException("VaultCreate: SubscriptionDate and RedemptionDate must be numbers (seconds since the Ripple Epoch)");

            long gap = (long)redemptionDate - subscriptionDate;
            if (gap < MinInvestmentPeriodSeconds || gap >= MaxInvestmentPeriodSeconds)
                throw new ValidationException("VaultCreate: RedemptionDate must be at least one minute and less than thirty years after SubscriptionDate");
        }
    }
}
