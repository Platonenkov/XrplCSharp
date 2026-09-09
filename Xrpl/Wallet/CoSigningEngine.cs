#nullable enable
using System;
using System.Text.Json.Nodes;

using Xrpl.BinaryCodec;
using Xrpl.Client.Exceptions;
using Xrpl.Keypairs;
using Xrpl.Models.Transactions;
using Xrpl.Utils.Hashes;

// Xrpl.Utils.Hashes declares a HashPrefix of its own; the codec's is the one that prefixes a signing preimage.
using HashPrefix = Xrpl.BinaryCodec.Hashing.HashPrefix;

namespace Xrpl.Wallet
{
    /// <summary>
    /// Shared engine behind the inner co-signature helpers. SponsorSignature
    /// (XLS-68) and CounterpartySignature (XLS-66) follow one protocol shape —
    /// an inner not-signing STObject over the same transaction body as the main
    /// signature — so the V1/V2/V3 flows differ only by the field name, the hash
    /// prefix of the role and the wording of their errors. SponsorSigningHelper
    /// and LoanSigningHelper are thin facades over this class.
    /// </summary>
    internal static class CoSigningEngine
    {
        /// <summary>
        /// Computes the signing preimage bytes for the transaction's own signature.
        /// </summary>
        /// <remarks>
        /// Inner signature objects are kNotSigning and never enter the preimage, so the bytes
        /// depend only on the transaction body and the prefix. Since fixCleanup3_4_0 the prefix
        /// differs by role, so the submitter and a co-signer no longer sign the same bytes.
        /// </remarks>
        internal static byte[] GetSigningPreimage(JsonObject txJson)
            => GetSigningPreimage(txJson, HashPrefix.TransactionSig);

        /// <summary>
        /// Computes the signing preimage bytes under an explicit role prefix.
        /// </summary>
        internal static byte[] GetSigningPreimage(JsonObject txJson, HashPrefix prefix)
        {
            string signingHex = XrplBinaryCodec.EncodeForSigning(txJson, prefix);
            return AddressCodec.Utils.FromHex(signingHex);
        }

        /// <summary>
        /// The prefix a co-signature field is signed under, mirroring rippled's
        /// <c>signatureRole(SField const&amp;)</c> and <c>signingPrefix</c>.
        /// </summary>
        internal static HashPrefix PrefixFor(string coSignatureField, bool multiSigning = false) => coSignatureField switch
        {
            "SponsorSignature" => multiSigning ? HashPrefix.SponsorTransactionMultiSig : HashPrefix.SponsorTransactionSig,
            "CounterpartySignature" => multiSigning ? HashPrefix.CounterpartyTransactionMultiSig : HashPrefix.CounterpartyTransactionSig,
            _ => throw new ValidationException($"{coSignatureField} is not a co-signature field."),
        };

        /// <summary>Removes the signature-bearing fields for body comparison.</summary>
        internal static JsonObject Canonicalize(JsonObject tx, string coSignatureField) =>
            tx.WithoutFields("TxnSignature", "SigningPubKey", coSignatureField);

        /// <summary>
        /// V1 — both keys local: the co-signer and the submitter sign the same
        /// preimage; the co-signature lands in <paramref name="coSignatureField"/>.
        /// </summary>
        internal static SignatureResult SignBoth(
            JsonObject preparedTx,
            XrplWallet submitterWallet,
            XrplWallet coSignerWallet,
            string coSignatureField)
        {
            JsonObject tx = preparedTx.DeepClone().AsObject();
            tx["SigningPubKey"] = submitterWallet.PublicKey;
            tx.Remove(coSignatureField);
            tx.Remove("TxnSignature");

            // Two preimages, not one: the co-signer signs under its role prefix and the
            // submitter under the transaction's own, and they have differed since fixCleanup3_4_0
            byte[] coSignerBytes = GetSigningPreimage(tx, PrefixFor(coSignatureField));
            byte[] submitterBytes = GetSigningPreimage(tx);

            string coSignature = XrplKeypairs.Sign(coSignerBytes, coSignerWallet.PrivateKey);
            tx[coSignatureField] = SignatureObject.Single(coSignerWallet.PublicKey, coSignature).ToJsonObject();

            tx["TxnSignature"] = XrplKeypairs.Sign(submitterBytes, submitterWallet.PrivateKey);

            return Encode(tx);
        }

        /// <summary>
        /// V2 — merges two independently signed blobs of the same transaction:
        /// the submitter's (TxnSignature) and the co-signer's
        /// (<paramref name="coSignatureField"/>).
        /// </summary>
        internal static SignatureResult Combine(
            string submitterSignedBlob,
            string coSignerSignedBlob,
            string coSignatureField,
            string coSignerLabel)
        {
            JsonObject submitterTx = XrplBinaryCodec.Decode(submitterSignedBlob).AsObject();
            JsonObject coSignerTx = XrplBinaryCodec.Decode(coSignerSignedBlob).AsObject();

            string? submitterPubKey = submitterTx["SigningPubKey"]?.GetValue<string>();
            string? coSignerSidePubKey = coSignerTx["SigningPubKey"]?.GetValue<string>();
            if (!string.Equals(submitterPubKey, coSignerSidePubKey, StringComparison.Ordinal))
                throw new ValidationException("Incompatible SigningPubKey values. Both blobs must use the submitter's SigningPubKey.");

            if (!JsonNode.DeepEquals(Canonicalize(submitterTx, coSignatureField), Canonicalize(coSignerTx, coSignatureField)))
                throw new ValidationException("Incompatible transaction bodies. Both inputs must have identical non-signing fields.");

            JsonObject combined = submitterTx.DeepClone().AsObject();

            JsonNode coSignature = coSignerTx[coSignatureField]
                ?? throw new ValidationException($"{coSignerLabel} blob is missing {coSignatureField}.");
            // Reject structurally unsigned material (an empty object would
            // otherwise flow through and produce an unusable combined blob)
            SignatureObject.FromJsonObject(coSignature.AsObject());
            combined[coSignatureField] = coSignature.DeepClone();

            if (string.IsNullOrEmpty(combined["TxnSignature"]?.GetValue<string>()))
                throw new ValidationException("Submitter blob is missing TxnSignature.");

            return Encode(combined);
        }

        /// <summary>
        /// V3 — the submitter finalizes a partially signed blob that already
        /// carries the co-signature in <paramref name="coSignatureField"/>.
        /// </summary>
        internal static SignatureResult FinalizeAsSubmitter(
            string partiallySignedBlob,
            XrplWallet submitterWallet,
            string coSignatureField)
        {
            JsonObject tx = XrplBinaryCodec.Decode(partiallySignedBlob).AsObject();

            JsonNode coSignature = tx[coSignatureField]?.DeepClone()
                ?? throw new ValidationException($"Partially signed blob is missing {coSignatureField}.");
            // Same structural gate as Combine: an unsigned or malformed
            // co-signature object must not be finalized into a "signed" blob
            SignatureObject.FromJsonObject(coSignature.AsObject());

            tx.Remove(coSignatureField);
            tx.Remove("TxnSignature");

            string? existingSigningPubKey = tx["SigningPubKey"]?.GetValue<string>();
            if (string.IsNullOrEmpty(existingSigningPubKey))
            {
                throw new ValidationException($"The {coSignatureField} was made over a multisig submitter form (empty SigningPubKey); a single main signature would invalidate it. Compose multisig parts instead.");
            }
            if (!string.Equals(existingSigningPubKey, submitterWallet.PublicKey, StringComparison.Ordinal))
            {
                throw new ValidationException("Partially signed blob SigningPubKey does not match submitter wallet.");
            }
            tx["SigningPubKey"] = submitterWallet.PublicKey;

            byte[] signingBytes = GetSigningPreimage(tx);
            tx["TxnSignature"] = XrplKeypairs.Sign(signingBytes, submitterWallet.PrivateKey);
            tx[coSignatureField] = coSignature;

            return Encode(tx);
        }

        private static SignatureResult Encode(JsonObject tx)
        {
            string txBlob = XrplBinaryCodec.Encode(tx);
            return new SignatureResult(txBlob, HashLedger.HashSignedTx(txBlob));
        }
    }
}
