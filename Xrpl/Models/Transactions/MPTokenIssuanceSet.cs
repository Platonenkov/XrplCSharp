#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Xrpl.Client.Exceptions;
using Xrpl.Models.Utils;
using Xrpl.Utils;

namespace Xrpl.Models.Transactions
{
    /// <summary>
    /// Enum representing flags for MPTokenIssuanceSet transactions.
    /// </summary>
    [Flags]
    public enum MPTokenIssuanceSetFlags : uint
    {
        /// <summary>
        /// If set, indicates that all MPT balances for this asset should be locked.
        /// </summary>
        tfMPTLock = 0x00000001,

        /// <summary>
        /// If set, indicates that all MPT balances for this asset should be unlocked.
        /// </summary>
        tfMPTUnlock = 0x00000002,

        /// <summary>DynamicMPT: enable lsfMPTCanLock on the issuance.</summary>
        tfMPTSetCanLock = 0x00000004,

        /// <summary>DynamicMPT: enable lsfMPTRequireAuth on the issuance.</summary>
        tfMPTSetRequireAuth = 0x00000008,

        /// <summary>DynamicMPT: enable lsfMPTCanEscrow on the issuance.</summary>
        tfMPTSetCanEscrow = 0x00000010,

        /// <summary>DynamicMPT: enable lsfMPTCanTrade on the issuance.</summary>
        tfMPTSetCanTrade = 0x00000020,

        /// <summary>DynamicMPT: enable lsfMPTCanTransfer on the issuance.</summary>
        tfMPTSetCanTransfer = 0x00000040,

        /// <summary>DynamicMPT: enable lsfMPTCanClawback on the issuance.</summary>
        tfMPTSetCanClawback = 0x00000080,

        /// <summary>
        /// DynamicMPT: enable lsfMPTCanHoldConfidentialBalance on the issuance.
        /// Requires the ConfidentialTransfer amendment.
        /// </summary>
        tfMPTSetCanHoldConfidentialBalance = 0x00000100
    }

    /// <summary>
    /// The MPTokenIssuanceSet transaction is used to globally lock/unlock an MPTokenIssuance,
    /// or to lock/unlock a specific holder's MPToken balance for an MPTokenIssuance.
    /// </summary>
    public interface IMPTokenIssuanceSet : ITransactionCommon
    {
        /// <summary>
        /// The MPTokenIssuance identifier.
        /// </summary>
        public string MPTokenIssuanceID { get; set; }

        /// <summary>
        /// An optional XRPL Address of an individual token holder balance to lock/unlock.
        /// If omitted, this transaction will apply to all any accounts holding MPTs.
        /// </summary>
        public string? Holder { get; set; }
        public new MPTokenIssuanceSetFlags? Flags { get; set; }

        /// <summary>DynamicMPT: capabilities and fields to freeze on the issuance (one-way).</summary>
        public MPTokenIssuanceImmutableFlags? ImmutableFlags { get; set; }

        /// <summary>DynamicMPT: new transfer fee (rejected once tifMPTTransferFee froze the field).</summary>
        public ushort? TransferFee { get; set; }

        /// <summary>DynamicMPT: new metadata blob in hex (rejected once tifMPTMetadata froze the field).</summary>
        public string MPTokenMetadata { get; set; }

        /// <summary>PermissionedDomains: domain restricting who may hold this MPT.</summary>
        public string DomainID { get; set; }

        /// <summary>
        /// ConfidentialTransfer: issuer ElGamal encryption public key (hex). Registered once; with
        /// ConfidentialMPTKeyRotation active a different key replaces the current one and increments
        /// the issuance's <c>IssuerKeyEpoch</c>, while the current key is refused (tecDUPLICATE).
        /// </summary>
        public string IssuerEncryptionKey { get; set; }

        /// <summary>
        /// ConfidentialTransfer: auditor ElGamal encryption public key (hex). Rotates the same way as
        /// <see cref="IssuerEncryptionKey"/> (<c>AuditorKeyEpoch</c>). Before the amendment it could only
        /// be registered together with the issuer key; with the amendment active it can also be added later.
        /// </summary>
        public string AuditorEncryptionKey { get; set; }
    }

    /// <summary>
    /// The MPTokenIssuanceSet transaction is used to globally lock/unlock an MPTokenIssuance.
    /// </summary>
    public class MPTokenIssuanceSet : TransactionRequest, IMPTokenIssuanceSet
    {
        /// <summary>
        /// Initializes a new instance of the MPTokenIssuanceSet class.
        /// </summary>
        public MPTokenIssuanceSet()
        {
            TransactionType = TransactionType.MPTokenIssuanceSet;
        }

        /// <inheritdoc />
        [JsonPropertyName("MPTokenIssuanceID")]
        public string MPTokenIssuanceID { get; set; } = null!;

        /// <inheritdoc />
        [JsonPropertyName("Holder")]
        public string? Holder { get; set; }
        public new MPTokenIssuanceSetFlags? Flags
        {
            get => base.Flags.HasValue ? (MPTokenIssuanceSetFlags?)base.Flags.Value : null;
            set => base.Flags = (uint?)value;
        }

    
        /// <inheritdoc />
        [JsonPropertyName("ImmutableFlags")]
        public MPTokenIssuanceImmutableFlags? ImmutableFlags { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("TransferFee")]
        public ushort? TransferFee { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("MPTokenMetadata")]
        public string MPTokenMetadata { get; set; }

        /// <summary>Decoded (non-hex) representation of <see cref="MPTokenMetadata"/>.</summary>
        [JsonIgnore]
        public string? MPTokenMetadataRow => MPTokenMetadata?.FromHexString();

        /// <summary>
        /// Parsed metadata object conforming to the XLS-89 Multi-Purpose Token Metadata Schema.
        /// Setting this property automatically serializes the schema to the <see cref="MPTokenMetadata"/> hex field.
        /// </summary>
        [JsonIgnore]
        public MPTokenMetadataSchema? Metadata
        {
            get => MPTokenMetadataSchema.FromHex(MPTokenMetadata);
            set => MPTokenMetadata = value?.ToHex();
        }

        /// <inheritdoc />
        [JsonPropertyName("DomainID")]
        public string DomainID { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("IssuerEncryptionKey")]
        public string IssuerEncryptionKey { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("AuditorEncryptionKey")]
        public string AuditorEncryptionKey { get; set; }
}

    /// <inheritdoc cref="IMPTokenIssuanceSet" />
    public class MPTokenIssuanceSetResponse : TransactionResponse, IMPTokenIssuanceSet
    {
        #region Implementation of IMPTokenIssuanceSet

        /// <inheritdoc />
        [JsonPropertyName("MPTokenIssuanceID")]
        public string MPTokenIssuanceID { get; set; } = null!;

        /// <inheritdoc />
        [JsonPropertyName("Holder")]
        public string? Holder { get; set; }

        #endregion
        public new MPTokenIssuanceSetFlags? Flags
        {
            get => base.Flags.HasValue ? (MPTokenIssuanceSetFlags?)base.Flags.Value : null;
            set => base.Flags = (uint?)value;
        }

    
        /// <inheritdoc />
        [JsonPropertyName("ImmutableFlags")]
        public MPTokenIssuanceImmutableFlags? ImmutableFlags { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("TransferFee")]
        public ushort? TransferFee { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("MPTokenMetadata")]
        public string MPTokenMetadata { get; set; }

        /// <summary>Decoded (non-hex) representation of <see cref="MPTokenMetadata"/>.</summary>
        [JsonIgnore]
        public string? MPTokenMetadataRow => MPTokenMetadata?.FromHexString();

        /// <summary>
        /// Parsed metadata object conforming to the XLS-89 Multi-Purpose Token Metadata Schema.
        /// </summary>
        [JsonIgnore]
        public MPTokenMetadataSchema? Metadata => MPTokenMetadataSchema.FromHex(MPTokenMetadata);

        /// <inheritdoc />
        [JsonPropertyName("DomainID")]
        public string DomainID { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("IssuerEncryptionKey")]
        public string IssuerEncryptionKey { get; set; }

        /// <inheritdoc />
        [JsonPropertyName("AuditorEncryptionKey")]
        public string AuditorEncryptionKey { get; set; }
}

    public partial class Validation
    {
        /// <summary>
        /// Verify the form and type of an MPTokenIssuanceSet at runtime.
        /// </summary>
        /// <param name="tx">An MPTokenIssuanceSet Transaction.</param>
        /// <exception cref="ValidationException">When the MPTokenIssuanceSet is Malformed.</exception>
        public static async Task ValidateMPTokenIssuanceSet(Dictionary<string, object> tx)
        {
            await Common.ValidateBaseTransaction(tx);

            if (!tx.TryGetValue("MPTokenIssuanceID", out var issuanceId) || issuanceId is null)
            {
                throw new ValidationException("MPTokenIssuanceSet: missing field MPTokenIssuanceID");
            }

            if (issuanceId is not string)
            {
                throw new ValidationException("MPTokenIssuanceSet: MPTokenIssuanceID must be a string");
            }

            if (tx.TryGetValue("Holder", out var holder) && holder is not null)
            {
                if (holder is not string)
                {
                    throw new ValidationException("MPTokenIssuanceSet: Holder must be a string");
                }
            }

            uint flagValue = 0;
            if (tx.TryGetValue("Flags", out var flags) && flags is not null)
            {
                // Same reporting as the ImmutableFlags check below: a non-numeric value has to
                // surface as ValidationException, which is what callers of this method catch —
                // Convert.ToUInt32 would throw FormatException or InvalidCastException instead.
                if (!Common.TryGetUInt32(flags, out flagValue))
                {
                    throw new ValidationException("MPTokenIssuanceSet: Flags must be a number");
                }

                bool hasLock = (flagValue & (uint)MPTokenIssuanceSetFlags.tfMPTLock) != 0;
                bool hasUnlock = (flagValue & (uint)MPTokenIssuanceSetFlags.tfMPTUnlock) != 0;

                if (hasLock && hasUnlock)
                {
                    throw new ValidationException("MPTokenIssuanceSet: cannot set both tfMPTLock and tfMPTUnlock flags");
                }
            }

            if (tx.TryGetValue("ImmutableFlags", out var immutableFlags) && immutableFlags is not null)
            {
                if (!Common.TryGetUInt32(immutableFlags, out uint immutable))
                {
                    throw new ValidationException("MPTokenIssuanceSet: ImmutableFlags must be a number");
                }

                // rippled MPTokenIssuanceSet::preflight: at least one flag must be
                // set and only tif* bits are allowed (temINVALID_FLAG otherwise)
                Common.ValidateNonZeroFlagsMask<MPTokenIssuanceImmutableFlags>(immutable, "MPTokenIssuanceSet: invalid ImmutableFlags");
            }

            if (tx.TryGetValue("TransferFee", out var transferFee) && transferFee is not null)
            {
                if (!Common.TryGetUInt32(transferFee, out uint fee))
                {
                    throw new ValidationException("MPTokenIssuanceSet: TransferFee must be a number");
                }

                if (fee > MPT_MAX_TRANSFER_FEE)
                {
                    throw new ValidationException($"MPTokenIssuanceSet: TransferFee must be between 0 and {MPT_MAX_TRANSFER_FEE}");
                }

                // rippled MPTokenIssuanceSet::preflight: a non-zero TransferFee combined
                // with enabling confidential balances is temBAD_TRANSFER_FEE
                if (fee > 0 && (flagValue & (uint)MPTokenIssuanceSetFlags.tfMPTSetCanHoldConfidentialBalance) != 0)
                {
                    throw new ValidationException("MPTokenIssuanceSet: TransferFee must be 0 when tfMPTSetCanHoldConfidentialBalance is set");
                }
            }

            if (tx.TryGetValue("MPTokenMetadata", out var metadata) && metadata is not null)
            {
                if (metadata is not string metadataStr)
                {
                    throw new ValidationException("MPTokenIssuanceSet: MPTokenMetadata must be a string");
                }

                if (metadataStr.Length > MPT_MAX_METADATA_LENGTH * 2)
                {
                    throw new ValidationException($"MPTokenIssuanceSet: MPTokenMetadata must be at most {MPT_MAX_METADATA_LENGTH} bytes");
                }
            }

            if (tx.TryGetValue("DomainID", out var domainId) && domainId is not null)
            {
                if (domainId is not string domain)
                {
                    throw new ValidationException("MPTokenIssuanceSet: DomainID must be a string");
                }

                // Format only, zero allowed (clears the domain); whether the
                // issuance has RequireAuth is ledger state rippled checks in preclaim
                Common.ValidateDomainId(domain, "MPTokenIssuanceSet", allowZero: true);
            }

            if (tx.TryGetValue("IssuerEncryptionKey", out var issuerKey) && issuerKey is not null && issuerKey is not string)
            {
                throw new ValidationException("MPTokenIssuanceSet: IssuerEncryptionKey must be a string");
            }

            if (tx.TryGetValue("AuditorEncryptionKey", out var auditorKey) && auditorKey is not null && auditorKey is not string)
            {
                throw new ValidationException("MPTokenIssuanceSet: AuditorEncryptionKey must be a string");
            }
        }
    }
}