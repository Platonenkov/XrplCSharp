using System;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xrpl.BinaryCodec;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Transactions;

namespace Xrpl.Wallet
{
    /// <summary>
    /// Helper for sponsored transaction signing (XLS-68).
    /// A sponsored transaction carries the common fields Sponsor and SponsorFlags
    /// (spfSponsorFee = 1, spfSponsorReserve = 2) and, when the sponsorship requires it,
    /// the sponsor's co-signature: SponsorSignature (inner STObject with
    /// SigningPubKey + TxnSignature over the same transaction, under the sponsor's own prefix).
    ///
    /// Signing patterns (analogous to LoanSet broker/counterparty):
    ///
    /// <b>V1 — Automatic (both keys available):</b>
    /// <code>
    /// var result = SponsorSigningHelper.SignSponsored(preparedTx, submitterWallet, sponsorWallet);
    /// await client.SubmitRequest(result.TxBlob);
    /// </code>
    ///
    /// <b>V2 — Parallel (keys on separate devices):</b>
    /// <code>
    /// var sponsorSig = sponsorWallet.SignAsSponsor(preparedTx);
    /// var submitterSig = submitterWallet.Sign(preparedTx);
    /// var combined = SponsorSigningHelper.CombineSponsorSignatures(submitterSig.TxBlob, sponsorSig.TxBlob);
    /// </code>
    ///
    /// <b>V3 — Sequential (sponsor signs first, passes to submitter):</b>
    /// <code>
    /// var withSponsor = sponsorWallet.SignAsSponsor(preparedTx);
    /// var final = SponsorSigningHelper.SubmitterSign(withSponsor.TxBlob, submitterWallet);
    /// </code>
    /// </summary>
    public static class SponsorSigningHelper
    {
        /// <summary>
        /// Prepares a transaction JSON for sponsored signing: verifies the Sponsor
        /// common field, sets the submitter's SigningPubKey and removes signature fields.
        /// </summary>
        /// <param name="transaction">Any transaction carrying Sponsor/SponsorFlags (autofilled).</param>
        /// <param name="submitterWallet">The submitting account's wallet (fee-payer is the sponsor per SponsorFlags).</param>
        public static JsonObject PrepareForSigning(ITransactionRequest transaction, XrplWallet submitterWallet)
        {
            string txJsonStr = JsonSerializer.Serialize(transaction, transaction.GetType(), XrplJsonOptions.Default);
            JsonObject txJson = JsonNode.Parse(txJsonStr)?.AsObject()
                ?? throw new ValidationException("Failed to serialize transaction to JSON");

            if (txJson["Sponsor"] == null)
                throw new ValidationException("Sponsored transaction must carry the Sponsor field.");

            txJson["SigningPubKey"] = submitterWallet.PublicKey;
            txJson.Remove("SponsorSignature");
            txJson.Remove("TxnSignature");
            return txJson;
        }

        /// <summary>
        /// V1 — Automatic signing: both the submitter and the sponsor sign locally.
        /// </summary>
        public static SignatureResult SignSponsored(
            JsonObject preparedTx,
            XrplWallet submitterWallet,
            XrplWallet sponsorWallet)
        {
            VerifySponsorMatches(preparedTx, sponsorWallet);
            return CoSigningEngine.SignBoth(preparedTx, submitterWallet, sponsorWallet, "SponsorSignature");
        }

        /// <summary>
        /// V2 — Combine independently signed submitter and sponsor blobs.
        /// The submitter blob has TxnSignature but no SponsorSignature;
        /// the sponsor blob has SponsorSignature but no TxnSignature.
        /// </summary>
        public static SignatureResult CombineSponsorSignatures(
            string submitterSignedBlob,
            string sponsorSignedBlob)
            => CoSigningEngine.Combine(submitterSignedBlob, sponsorSignedBlob, "SponsorSignature", "Sponsor");

        /// <summary>
        /// V3 — The submitter signs a partially signed blob that already has SponsorSignature.
        /// </summary>
        public static SignatureResult SubmitterSign(string partiallySignedBlob, XrplWallet submitterWallet)
            => CoSigningEngine.FinalizeAsSubmitter(partiallySignedBlob, submitterWallet, "SponsorSignature");

        /// <summary>
        /// Computes the bytes the sponsor signs into SponsorSignature: the transaction under the
        /// sponsor prefix, which since fixCleanup3_4_0 is not what the submitter signs.
        /// </summary>
        /// <remarks>
        /// Named for the role rather than kept as <c>GetSigningPreimage</c>: that name returned the
        /// bytes of both signatures while they were the same, and leaving it would have changed
        /// what a call means without changing how it compiles. The submitter's own bytes come from
        /// <see cref="XrplBinaryCodec.EncodeForSigning(object)"/>.
        /// </remarks>
        public static byte[] GetSponsorPreimage(JsonObject txJson)
            => CoSigningEngine.GetSigningPreimage(txJson, CoSigningEngine.PrefixFor("SponsorSignature"));


        internal static void VerifySponsorMatches(JsonObject tx, XrplWallet sponsorWallet)
        {
            string sponsor = tx["Sponsor"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sponsor))
                throw new ValidationException("Sponsored transaction must carry the Sponsor field.");
            if (!string.Equals(sponsor, sponsorWallet.ClassicAddress, StringComparison.Ordinal))
                throw new ValidationException($"Sponsor field ({sponsor}) does not match the sponsor wallet ({sponsorWallet.ClassicAddress}).");
        }

    }
}
