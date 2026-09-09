using System.Collections.Generic;
using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Xrpl.Client.Json.Converters;
using Xrpl.Models.Common;

using static Xrpl.Models.Common.Common;

namespace Xrpl.Models.Ledger;

/// <summary>
/// Flags for the Vault ledger object.
/// </summary>
[Flags]
public enum VaultLedgerFlags : uint
{
    /// <summary>
    /// If set, indicates that the vault is private.
    /// This flag can only be set when creating the vault.
    /// </summary>
    lsfVaultPrivate = 0x00010000,
}

/// <summary>
/// Values of the Vault ledger entry's <c>LEVersion</c> field (rippled <c>VaultVersion</c>).
/// </summary>
/// <remarks>
/// <see cref="LOVault.LEVersion"/> stays a plain <c>uint?</c>, matching the other UInt8
/// fields of this object; these constants name the values the protocol defines so far.
/// </remarks>
public enum VaultVersion : uint
{
    /// <summary>
    /// Accrual-basis accounting. Vaults created before cash-basis accounting was activated
    /// carry no LEVersion at all and are treated as this version implicitly.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// Cash-basis accounting (rippled #7817).
    /// </summary>
    CashBasis = 1,
}

/// <summary>
/// Values of the <c>VaultKind</c> field (rippled <c>VaultKind</c>, LendingProtocolV1_1, rippled #7921).
/// </summary>
/// <remarks>
/// <see cref="LOVault.VaultKind"/> and <see cref="Xrpl.Models.Transactions.VaultCreate.VaultKind"/> stay
/// plain <c>uint?</c>, matching the other UInt8 fields; these constants name the values the protocol
/// defines so far.
/// </remarks>
public enum VaultKind : uint
{
    /// <summary>
    /// Deposits and withdrawals at any time. A vault with no VaultKind at all is open-ended, whether it
    /// was created before the amendment or after it.
    /// </summary>
    OpenEnded = 0,

    /// <summary>
    /// Three phases fixed at creation: subscription up to <see cref="LOVault.SubscriptionDate"/>,
    /// investment up to <see cref="LOVault.RedemptionDate"/>, redemption afterwards. Deposits are
    /// accepted in the subscription phase only (tecEXPIRED later); withdrawals are refused during
    /// the investment phase (tecTOO_SOON) and accepted in the other two.
    /// </summary>
    ClosedEnded = 1,
}

/// <summary>
/// Recommended structure for the Vault Data field.
/// The JSON is whitespace-removed and hex-encoded (max 256 bytes).
/// </summary>
public class VaultDataFormat
{
    /// <summary>
    /// Members the node sent that no declared property here claims. Mirrors
    /// <see cref="BaseLedgerEntry.UnknownFields"/> for ledger entries and
    /// <see cref="Xrpl.Models.Methods.BaseMethodResult.UnknownFields"/> for command results:
    /// without it, anything this model does not yet know about is dropped between the node and
    /// the caller instead of surviving the round trip.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownFields { get; set; }

    /// <summary>
    /// Human-readable vault identifier reflecting its strategy (short key: "n").
    /// </summary>
    [JsonPropertyName("n")]
    public string Name { get; set; }

    /// <summary>
    /// Associated website URL (omit protocol prefix and www, short key: "w").
    /// </summary>
    [JsonPropertyName("w")]
    public string Website { get; set; }

    /// <summary>
    /// Built once rather than per call. Colder than the codec's signing path, but the same
    /// mistake, and an options instance is not the place to express "compact" one call at a time.
    /// </summary>
    private static readonly JsonSerializerOptions CompactOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serializes to compact JSON, then hex-encodes for the Data field.
    /// </summary>
    public string ToHex()
    {
        string json = JsonSerializer.Serialize(this, CompactOptions);
        return Convert.ToHexString(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Attempts to deserialize a VaultDataFormat from a hex-encoded Data field.
    /// </summary>
    /// <param name="hex">The hex string from the Data field.</param>
    /// <returns>Deserialized <see cref="VaultDataFormat"/> or null if parsing fails.</returns>
    public static VaultDataFormat FromHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            string json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<VaultDataFormat>(json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// A Vault ledger object represents a pooled asset vault.
/// </summary>
/// <remarks>Requires the Vault amendment (XLS-65d). This feature is in draft and subject to change.</remarks>
public class LOVault : BaseLedgerEntry
{
    /// <summary>
    /// The address of the vault's pseudo-account.
    /// </summary>
    [JsonPropertyName("Account")]
    public string Account { get; init; }

    /// <summary>
    /// The account address of the Vault Owner.
    /// </summary>
    [JsonPropertyName("Owner")]
    public string Owner { get; init; }

    /// <summary>
    /// The primary asset held by the vault. Supports XRP, trust line tokens, and MPTs.
    /// </summary>
    [JsonPropertyName("Asset")]
    [JsonConverter(typeof(IssuedCurrencyConverter))]
    public IssuedCurrency Asset { get; init; }

    /// <summary>
    /// The total value of the vault (STNumber type, serialized as string in JSON).
    /// </summary>
    [JsonPropertyName("AssetsTotal")]
    public string AssetsTotal { get; init; }

    /// <summary>
    /// The asset amount that is available in the vault (STNumber type, serialized as string in JSON).
    /// </summary>
    [JsonPropertyName("AssetsAvailable")]
    public string AssetsAvailable { get; init; }

    /// <summary>
    /// The maximum amount of assets the vault can hold, or 0 for no limit (STNumber type).
    /// </summary>
    [JsonPropertyName("AssetsMaximum")]
    public string AssetsMaximum { get; init; }

    /// <summary>
    /// The potential loss amount that is not yet realized, expressed as the vault's asset (STNumber type).
    /// </summary>
    [JsonPropertyName("LossUnrealized")]
    public string LossUnrealized { get; init; }

    /// <summary>
    /// The identifier of the share MPTokenIssuance object.
    /// </summary>
    [JsonPropertyName("ShareMPTID")]
    public string ShareMPTID { get; init; }

    /// <summary>
    /// The withdrawal policy for the vault (UInt8).
    /// </summary>
    [JsonPropertyName("WithdrawalPolicy")]
    public uint? WithdrawalPolicy { get; init; }

    /// <summary>
    /// The scale (decimal precision) for share calculations.
    /// 0-18 for trust line tokens; fixed at 0 for XRP/MPTs.
    /// </summary>
    [JsonPropertyName("Scale")]
    public uint? Scale { get; init; }

    /// <summary>
    /// Schema version of this ledger entry (UInt8), see <see cref="VaultVersion"/>.
    /// Absent on vaults created before cash-basis accounting was activated, which
    /// rippled resolves as <see cref="VaultVersion.Legacy"/> (0) rather than an error.
    /// </summary>
    [JsonPropertyName("LEVersion")]
    public uint? LEVersion { get; init; }

    /// <summary>
    /// LendingProtocolV1_1: the kind of vault (UInt8), see <see cref="Xrpl.Models.Ledger.VaultKind"/>.
    /// Absent on open-ended vaults, which rippled resolves as
    /// <see cref="Xrpl.Models.Ledger.VaultKind.OpenEnded"/> (0).
    /// </summary>
    [JsonPropertyName("VaultKind")]
    public uint? VaultKind { get; init; }

    /// <summary>
    /// LendingProtocolV1_1: the end of a closed-ended vault's subscription phase. Absent on open-ended vaults.
    /// </summary>
    [JsonPropertyName("SubscriptionDate")]
    [JsonConverter(typeof(RippleDateTimeConverter))]
    public DateTime? SubscriptionDate { get; init; }

    /// <summary>
    /// LendingProtocolV1_1: the start of a closed-ended vault's redemption phase. Absent on open-ended vaults.
    /// </summary>
    [JsonPropertyName("RedemptionDate")]
    [JsonConverter(typeof(RippleDateTimeConverter))]
    public DateTime? RedemptionDate { get; init; }

    /// <summary>
    /// Arbitrary hex-encoded data associated with the vault, limited to 256 bytes.
    /// Use <see cref="DataParsed"/> for a human-readable representation.
    /// </summary>
    [JsonPropertyName("Data")]
    public string Data { get; init; }

    /// <summary>
    /// Decoded human-readable value of the Data field.
    /// Follows the recommended format: {"n":"name","w":"website"}.
    /// Returns null if parsing fails or Data is empty.
    /// </summary>
    [JsonIgnore]
    public VaultDataFormat DataParsed => VaultDataFormat.FromHex(Data);

    /// <summary>
    /// Decoded human-readable UTF-8 string of the Data field.
    /// </summary>
    [JsonIgnore]
    public string DataRaw
    {
        get
        {
            if (string.IsNullOrEmpty(Data)) return null;
            try { return Encoding.UTF8.GetString(Convert.FromHexString(Data)); }
            catch { return null; }
        }
    }

    /// <summary>
    /// The transaction sequence number that created the vault.
    /// </summary>
    [JsonPropertyName("Sequence")]
    public uint? Sequence { get; init; }

    /// <summary>
    /// A hint indicating which page of the owner's directory links to this object (UInt64).
    /// </summary>
    [JsonPropertyName("OwnerNode")]
    public string OwnerNode { get; init; }

    /// <summary>
    /// The identifying hash of the transaction that most recently modified this object.
    /// </summary>
    [JsonPropertyName("PreviousTxnID")]
    public string PreviousTxnID { get; init; }

    /// <summary>
    /// The index of the ledger that contains the transaction that most recently modified this object.
    /// </summary>
    [JsonPropertyName("PreviousTxnLgrSeq")]
    public uint? PreviousTxnLgrSeq { get; init; }
}
