using System.Text.Json.Serialization;

using System;

using Xrpl.Client.Json.Converters;
using Xrpl.Models.Utils;
using Xrpl.Utils;

// https://github.com/XRPLF/xrpl.js/blob/main/packages/xrpl/src/models/ledger/MPTokenIssuance.ts

namespace Xrpl.Models.Ledger
{
    [Flags]
    public enum MPTokenIssuanceFlags : uint
    {
        None = 0,

        /// <summary>
        /// All balances of this MPT are locked.
        /// </summary>
        MPTLocked = 0x00000001,

        /// <summary>
        /// Issuer can lock individual balances or all balances.
        /// </summary>
        MPTCanLock = 0x00000002,

        /// <summary>
        /// Holders must be authorized by the issuer.
        /// </summary>
        MPTRequireAuth = 0x00000004,

        /// <summary>
        /// Holders can place balances into escrow.
        /// Requires TokenEscrow amendment.
        /// </summary>
        MPTCanEscrow = 0x00000008,

        /// <summary>
        /// Holders can trade balances via DEX or AMM.
        /// </summary>
        MPTCanTrade = 0x00000010,

        /// <summary>
        /// Tokens held by non-issuers can be transferred to other accounts.
        /// </summary>
        MPTCanTransfer = 0x00000020,

        /// <summary>
        /// Issuer can claw back balances from holders.
        /// </summary>
        MPTCanClawback = 0x00000040,

        /// <summary>
        /// Holders can hold confidential (encrypted) balances of this MPT.
        /// Requires ConfidentialTransfer amendment.
        /// </summary>
        MPTCanHoldConfidentialBalance = 0x00000080,
    }

    /// <summary>
    /// The MPTokenIssuance object represents a Multi-Purpose Token (MPT) issuance.
    /// </summary>
    public class LOMPTokenIssuance : BaseLedgerEntry
    {
        [JsonPropertyName("Flags")]
        public MPTokenIssuanceFlags? Flags { get; init; }

        /// <summary>
        /// The address of the account that controls the issuance.
        /// AccountID
        /// </summary>
        [JsonPropertyName("Issuer")]
        public string Issuer { get; init; } = default!;

        /// <summary>
        /// Asset scale (decimal places).
        /// UInt8
        /// </summary>
        [JsonPropertyName("AssetScale")]
        public byte? AssetScale { get; init; }

        /// <summary>
        /// Maximum number of tokens that can exist.
        /// UInt64 (0 .. 2^63-1)
        /// Optional
        /// </summary>
        [JsonPropertyName("MaximumAmount")]
        [JsonConverter(typeof(UInt64StringJsonConverter))]
        public ulong? MaximumAmount { get; init; }

        /// <summary>
        /// Total amount of tokens currently in circulation.
        /// UInt64 (0 .. 2^63-1)
        /// </summary>
        [JsonPropertyName("OutstandingAmount")]
        [JsonConverter(typeof(UInt64StringJsonConverter))]
        public ulong? OutstandingAmount { get; init; }

        /// <summary>
        /// Amount of tokens currently locked (included in OutstandingAmount).
        /// UInt64 (0 .. 2^63-1)
        /// Requires TokenEscrow amendment.
        /// Optional
        /// </summary>
        [JsonPropertyName("LockedAmount")]
        [JsonConverter(typeof(UInt64StringJsonConverter))]
        public ulong? LockedAmount { get; init; }

        /// <summary>
        /// Transfer fee in tenths of a basis point.
        /// UInt16 (0 .. 50000)
        /// </summary>
        [JsonPropertyName("TransferFee")]
        public ushort? TransferFee { get; init; }

        /// <summary>
        /// Arbitrary metadata in hex format (max 1024 bytes).
        /// Blob
        /// </summary>
        [JsonPropertyName("MPTokenMetadata")]
        public string? MPTokenMetadata { get; init; }

        [JsonIgnore]
        public string? MPTokenMetadataRow => MPTokenMetadata?.FromHexString();


        /// <summary>
        /// Parsed metadata object conforming to the XLS-89 Multi-Purpose Token Metadata Schema.
        /// Lazily deserialized from the <see cref="MPTokenMetadata"/> hex field.
        /// </summary>
        [JsonIgnore]
        public MPTokenMetadataSchema? Metadata=> MPTokenMetadataSchema.FromHex(MPTokenMetadata);

        /// <summary>
        /// Owner directory page hint.
        /// UInt64
        /// </summary>
        [JsonPropertyName("OwnerNode")]
        [JsonConverter(typeof(UInt64HexJsonConverter))]
        public ulong? OwnerNode { get; init; }

        /// <summary>
        /// Hash of the transaction that last modified this entry.
        /// UInt256
        /// </summary>
        [JsonPropertyName("PreviousTxnID")]
        public string PreviousTxnID { get; init; } = default!;

        /// <summary>
        /// Ledger index of the previous modifying transaction.
        /// UInt32
        /// </summary>
        [JsonPropertyName("PreviousTxnLgrSeq")]
        public uint? PreviousTxnLgrSeq { get; init; }

        /// <summary>
        /// Sequence or Ticket number that created this issuance.
        /// UInt32
        /// </summary>
        [JsonPropertyName("Sequence")]
        public uint? Sequence { get; init; }

        /// <summary>
        /// PermissionedDomain restricting who may hold this MPT.
        /// </summary>
        [JsonPropertyName("DomainID")]
        public string? DomainID { get; init; }

        /// <summary>
        /// DynamicMPT: which issuance capabilities and fields are frozen.
        /// </summary>
        [JsonPropertyName("ImmutableFlags")]
        public uint? ImmutableFlags { get; init; }

        /// <summary>
        /// MPTokensV2: the reference holding object for DEX trading.
        /// </summary>
        [JsonPropertyName("ReferenceHolding")]
        public string? ReferenceHolding { get; init; }

        /// <summary>
        /// ConfidentialTransfer: the issuer's ElGamal encryption public key (hex blob).
        /// </summary>
        [JsonPropertyName("IssuerEncryptionKey")]
        public string? IssuerEncryptionKey { get; init; }

        /// <summary>
        /// ConfidentialTransfer: the auditor's ElGamal encryption public key (hex blob).
        /// </summary>
        [JsonPropertyName("AuditorEncryptionKey")]
        public string? AuditorEncryptionKey { get; init; }

        /// <summary>
        /// ConfidentialMPTKeyRotation: how many times <see cref="IssuerEncryptionKey"/> has been replaced
        /// through MPTokenIssuanceSet. Absent (0) until the first rotation, including on issuances whose
        /// key was registered before the amendment.
        /// </summary>
        [JsonPropertyName("IssuerKeyEpoch")]
        public uint? IssuerKeyEpoch { get; init; }

        /// <summary>
        /// ConfidentialMPTKeyRotation: how many times <see cref="AuditorEncryptionKey"/> has been replaced
        /// through MPTokenIssuanceSet. Absent (0) until the first rotation.
        /// </summary>
        [JsonPropertyName("AuditorKeyEpoch")]
        public uint? AuditorKeyEpoch { get; init; }

        /// <summary>
        /// ConfidentialTransfer: total amount held in confidential balances.
        /// UInt64 (0 .. 2^63-1)
        /// </summary>
        [JsonPropertyName("ConfidentialOutstandingAmount")]
        [JsonConverter(typeof(UInt64StringJsonConverter))]
        public ulong? ConfidentialOutstandingAmount { get; init; }

        /// <summary>
        /// Computed 192-bit MPTokenIssuanceID (48 hex chars, uppercase).
        /// Derived from <see cref="Sequence"/> and <see cref="Issuer"/> per XLS-33.
        /// </summary>
        // Null when Sequence is absent (e.g. this model represents PreviousFields content):
        // the deterministic ID formula requires the creating Sequence and would be wrong to fabricate.
        [JsonIgnore]
        public string? MPTokenIssuanceID =>
            Sequence.HasValue ? ParseMPTID.GenerateMPTokenIssuanceID(Sequence.Value, Issuer) : null;
    }
}