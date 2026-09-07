using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xrpl.BinaryCodec;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Transactions;

namespace Xrpl.Wallet
{
    /// <summary>
    /// Helper for LoanSet multi-party signing (XLS-66d).
    /// LoanSet requires two signatures: the broker (Account) signs as the submitter (TxnSignature),
    /// and the borrower (Counterparty) provides a CounterpartySignature (inner STObject with
    /// SigningPubKey + TxnSignature).
    ///
    /// Three signing patterns (analogous to Batch V1/V2/V3):
    ///
    /// <b>V1 — Automatic (both keys available):</b>
    /// <code>
    /// var result = LoanSigningHelper.SignLoanSet(loanTx, brokerWallet, borrowerWallet);
    /// await client.SubmitRequest(result.TxBlob);
    /// </code>
    ///
    /// <b>V2 — Parallel (keys on separate devices, sign independently):</b>
    /// <code>
    /// // Device A (borrower):
    /// var counterpartySig = borrowerWallet.SignAsLoanCounterparty(preparedTxJson);
    /// // Device B (broker):
    /// var brokerSig = brokerWallet.Sign(preparedTxJson);
    /// // Combiner:
    /// var combined = LoanSigningHelper.CombineLoanSignatures(brokerSig.TxBlob, counterpartySig.TxBlob);
    /// await client.SubmitRequest(combined);
    /// </code>
    ///
    /// <b>V3 — Sequential (borrower signs first, passes to broker):</b>
    /// <code>
    /// // Borrower signs, adds CounterpartySignature:
    /// var withCounterparty = borrowerWallet.SignAsLoanCounterparty(preparedTxJson);
    /// // Broker receives the partially signed blob, adds TxnSignature:
    /// var final = LoanSigningHelper.BrokerSign(withCounterparty.TxBlob, brokerWallet);
    /// await client.SubmitRequest(final.TxBlob);
    /// </code>
    ///
    /// <b>Multisig borrower (Counterparty with a SignerList):</b> each signer of the
    /// borrower's list produces a portable Signer entry with the standard multisign
    /// call, and the composer places them under <c>CounterpartySignature.Signers</c>
    /// (rippled verifies them against the counterparty's SignerList over the same
    /// multisign preimage as <c>tx.Signers</c>). Autofill with the signer count so the
    /// fee covers them (rippled <c>LoanSet::calculateBaseFee</c> charges one base fee
    /// per counterparty signer):
    /// <code>
    /// var prepared = LoanSigningHelper.PrepareForSigning(await client.Autofill(loanTx.ToDictionary(), signersCount: 2), brokerWallet);
    /// var brokerPart = brokerWallet.Sign(prepared);
    /// var part1 = signer1.Sign(prepared, multisign: true);
    /// var part2 = signer2.Sign(prepared, multisign: true);
    /// // ledger-driven routing (looks up the Counterparty's SignerList):
    /// var composed = await client.ComposeSignatures(new[] { brokerPart.TxBlob, part1.TxBlob, part2.TxBlob });
    /// // or offline, naming the borrower's signers:
    /// var offline = LoanSigningHelper.CombineLoanSignatures(new[] { brokerPart.TxBlob, part1.TxBlob, part2.TxBlob }, new[] { signer1.ClassicAddress, signer2.ClassicAddress });
    /// </code>
    /// </summary>
    public static class LoanSigningHelper
    {
        /// <summary>
        /// Prepares a LoanSet transaction JSON for signing.
        /// Sets SigningPubKey to the broker's public key and removes signature fields.
        /// Returns a JsonObject ready for both parties to sign.
        /// </summary>
        /// <remarks>
        /// Fee for CounterpartySignature overhead is handled by Autofill
        /// (see <c>CalculateBaseFeeForType</c> in <c>Autofill.cs</c>).
        /// </remarks>
        /// <param name="loanSetTx">The LoanSet transaction (autofilled with Sequence, Fee, LastLedgerSequence).</param>
        /// <param name="brokerWallet">The broker's wallet (submitting account).</param>
        /// <returns>JsonObject ready for signing by both parties.</returns>
        public static JsonObject PrepareForSigning(
            ITransactionRequest loanSetTx,
            XrplWallet brokerWallet)
        {
            string txJsonStr = JsonSerializer.Serialize(loanSetTx, XrplJsonOptions.Default);
            JsonObject txJson = JsonNode.Parse(txJsonStr)?.AsObject()
                ?? throw new ValidationException("Failed to serialize LoanSet to JSON");

            return PrepareForSigning(txJson, brokerWallet);
        }

        /// <summary>
        /// Prepares a LoanSet JSON object for signing.
        /// </summary>
        public static JsonObject PrepareForSigning(
            JsonObject txJson,
            XrplWallet brokerWallet)
        {
            string txType = txJson["TransactionType"]?.GetValue<string>();
            if (!string.Equals(txType, "LoanSet", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException($"TransactionType must be LoanSet, got: {txType}");

            // Set broker's signing pub key
            txJson["SigningPubKey"] = brokerWallet.PublicKey;

            // Remove signature fields
            txJson.Remove("CounterpartySignature");
            txJson.Remove("TxnSignature");

            return txJson;
        }

        /// <summary>
        /// V1 — Automatic signing: both broker and borrower wallets available locally.
        /// Computes the signing preimage, has both parties sign, and returns the fully signed tx blob.
        /// </summary>
        /// <param name="preparedTx">Prepared LoanSet JSON (from PrepareForSigning or already prepared).</param>
        /// <param name="brokerWallet">The broker's wallet (submitting account).</param>
        /// <param name="borrowerWallet">The borrower's wallet (counterparty).</param>
        /// <returns>Fully signed transaction blob and hash.</returns>
        public static SignatureResult SignLoanSet(
            JsonObject preparedTx,
            XrplWallet brokerWallet,
            XrplWallet borrowerWallet)
        {
            string txType = preparedTx["TransactionType"]?.GetValue<string>();
            if (!string.Equals(txType, "LoanSet", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException($"TransactionType must be LoanSet, got: {txType}");

            string counterparty = preparedTx["Counterparty"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(counterparty) &&
                !string.Equals(counterparty, borrowerWallet.ClassicAddress, StringComparison.Ordinal))
            {
                throw new ValidationException($"Counterparty field ({counterparty}) does not match the borrower wallet ({borrowerWallet.ClassicAddress}).");
            }

            return CoSigningEngine.SignBoth(preparedTx, brokerWallet, borrowerWallet, "CounterpartySignature");
        }

        /// <summary>
        /// V2 — Combine independently signed broker and counterparty blobs.
        /// The broker blob has TxnSignature but no CounterpartySignature.
        /// The counterparty blob has CounterpartySignature but no TxnSignature.
        /// </summary>
        /// <param name="brokerSignedBlob">Hex blob signed by the broker (has TxnSignature).</param>
        /// <param name="counterpartySignedBlob">Hex blob signed by the borrower (has CounterpartySignature).</param>
        /// <returns>Combined fully signed blob.</returns>
        public static SignatureResult CombineLoanSignatures(
            string brokerSignedBlob,
            string counterpartySignedBlob)
        {
            RequireLoanSet(brokerSignedBlob, "Broker");
            RequireLoanSet(counterpartySignedBlob, "Counterparty");
            return CoSigningEngine.Combine(brokerSignedBlob, counterpartySignedBlob, "CounterpartySignature", "Counterparty");
        }

        /// <summary>
        /// Combines a broker part with the portable Signer entries of a multisig
        /// borrower: entries from <paramref name="counterpartySignerAccounts"/> land in
        /// <c>CounterpartySignature.Signers</c>, any other entry in <c>tx.Signers</c>
        /// (a multisig broker). Offline; for ledger-driven routing use
        /// <c>IXrplClient.ComposeSignatures</c>.
        /// </summary>
        /// <param name="partBlobs">The broker's blob and the borrower-side multisign parts.</param>
        /// <param name="counterpartySignerAccounts">Accounts of the borrower's SignerList.</param>
        public static SignatureResult CombineLoanSignatures(
            IEnumerable<string> partBlobs,
            IReadOnlyCollection<string> counterpartySignerAccounts)
        {
            List<string> parts = partBlobs?.ToList()
                ?? throw new ValidationException("At least one partially signed blob is required.");
            foreach (string blob in parts)
                RequireLoanSet(blob, "Part");
            return SignatureComposer.ComposeSignatures(parts, null, counterpartySignerAccounts);
        }

        /// <summary>
        /// V3 — Broker signs a partially signed LoanSet blob (one that already has CounterpartySignature).
        /// Decodes the blob, strips CounterpartySignature to compute the correct preimage,
        /// adds the broker's TxnSignature, then restores CounterpartySignature for encoding.
        /// </summary>
        /// <param name="partiallySignedBlob">Hex blob from borrower's SignAsLoanCounterparty (has CounterpartySignature, no TxnSignature).</param>
        /// <param name="brokerWallet">The broker's wallet (submitting account).</param>
        /// <returns>Fully signed transaction blob and hash.</returns>
        public static SignatureResult BrokerSign(string partiallySignedBlob, XrplWallet brokerWallet)
        {
            RequireLoanSet(partiallySignedBlob, "Partially signed");
            return CoSigningEngine.FinalizeAsSubmitter(partiallySignedBlob, brokerWallet, "CounterpartySignature");
        }

        private static void RequireLoanSet(string blob, string label)
        {
            JsonObject tx = XrplBinaryCodec.Decode(blob).AsObject();
            string txType = tx["TransactionType"]?.GetValue<string>();
            if (!string.Equals(txType, "LoanSet", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException($"{label} blob TransactionType must be LoanSet, got: {txType}");
        }

        /// <summary>
        /// Computes the bytes the borrower signs into CounterpartySignature: the transaction
        /// under the counterparty prefix, which since fixCleanup3_4_0 is not what the broker signs.
        /// </summary>
        internal static byte[] GetSigningPreimage(JsonObject txJson)
            => CoSigningEngine.GetSigningPreimage(txJson, CoSigningEngine.PrefixFor("CounterpartySignature"));

    }
}
