#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Xrpl.BinaryCodec;
using Xrpl.Keypairs;
using Xrpl.Client.Exceptions;
using Xrpl.Models.Transactions;
using Xrpl.Utils.Hashes;

// Xrpl.Utils.Hashes declares a HashPrefix of its own; the codec's is the one that prefixes a signing preimage.
using HashPrefix = Xrpl.BinaryCodec.Hashing.HashPrefix;

namespace Xrpl.Wallet
{
    /// <summary>
    /// Composes a fully signed transaction from partially signed blobs (#43).
    /// Devices sign with whatever keys they hold — single main signature,
    /// sponsor or counterparty co-signature, or portable multisig Signer
    /// entries — and the composer routes everything into the right sections.
    /// Which section an entry belongs to is still decided here, by account, but it is no longer
    /// only the composer's business: since rippled's fixCleanup3_4_0 an entry in
    /// SponsorSignature.Signers or CounterpartySignature.Signers covers different bytes than one
    /// in tx.Signers, so the signer had to know its side already. It works that out from the
    /// transaction in every shape but one - see <see cref="SignatureRole"/> - and routing an entry
    /// into a section its signer did not sign for now produces a signature the node rejects
    /// rather than a portable one.
    /// </summary>
    public static class SignatureComposer
    {
        private const string SponsorField = "SponsorSignature";
        private const string CounterpartyField = "CounterpartySignature";

        // SigningPubKey is intentionally NOT stripped: it is part of every signing
        // preimage, so all parts of one transaction must agree on it - a mismatch
        // means the parts were signed over different submitter forms.
        private static readonly string[] SignatureFields = { "TxnSignature", "Signers", SponsorField, CounterpartyField };

        /// <summary>
        /// One inner co-signature section (SponsorSignature or CounterpartySignature)
        /// being assembled: either a single signature or a set of Signer entries.
        /// </summary>
        private sealed class InnerSection
        {
            public InnerSection(string field, string label, HashSet<string> signerAccounts)
            {
                Field = field;
                Label = label;
                SignerAccounts = signerAccounts;
            }

            public string Field { get; }
            public string Label { get; }
            public HashSet<string> SignerAccounts { get; }
            public SignatureObject? Single { get; set; }
            public JsonArray Entries { get; } = new JsonArray();
        }

        /// <summary>
        /// Offline composition. Signer entries from accounts listed in
        /// <paramref name="sponsorSignerAccounts"/> go into
        /// <c>SponsorSignature.Signers</c>, entries from
        /// <paramref name="counterpartySignerAccounts"/> into
        /// <c>CounterpartySignature.Signers</c> (XLS-66 LoanSet borrower with a
        /// SignerList); all other entries go into <c>tx.Signers</c>. For
        /// ledger-driven routing use the <c>IXrplClient.ComposeSignatures</c>
        /// extension instead.
        /// </summary>
        /// <param name="partBlobs">Partially signed blobs of the same transaction.</param>
        /// <param name="sponsorSignerAccounts">Accounts whose Signer entries belong to the sponsor's SignerList.</param>
        /// <param name="counterpartySignerAccounts">Accounts whose Signer entries belong to the LoanSet counterparty's SignerList.</param>
        public static SignatureResult ComposeSignatures(
            IEnumerable<string> partBlobs,
            IReadOnlyCollection<string>? sponsorSignerAccounts = null,
            IReadOnlyCollection<string>? counterpartySignerAccounts = null)
        {
            return Compose(partBlobs, sponsorSignerAccounts, counterpartySignerAccounts);
        }

        /// <summary>
        /// The two-argument form, for sponsor-side routing only.
        /// </summary>
        /// <remarks>
        /// Kept as its own overload rather than folded into the three-argument method above.
        /// Adding a parameter with a default is source-compatible but not binary-compatible: an
        /// assembly compiled against the two-argument signature emits a call to a method that
        /// would no longer exist, and fails at run time rather than at build.
        /// </remarks>
        /// <param name="partBlobs">Partially signed blobs of the same transaction.</param>
        /// <param name="sponsorSignerAccounts">Accounts whose Signer entries belong to the sponsor's SignerList.</param>
        public static SignatureResult ComposeSignatures(
            IEnumerable<string> partBlobs,
            IReadOnlyCollection<string>? sponsorSignerAccounts)
        {
            return Compose(partBlobs, sponsorSignerAccounts, null);
        }

        private static SignatureResult Compose(
            IEnumerable<string> partBlobs,
            IReadOnlyCollection<string>? sponsorSignerAccounts,
            IReadOnlyCollection<string>? counterpartySignerAccounts)
        {
            List<JsonObject> parts = partBlobs?.Select(b => XrplBinaryCodec.Decode(b).AsObject()).ToList()
                ?? throw new ValidationException("At least one partially signed blob is required.");
            if (parts.Count == 0)
                throw new ValidationException("At least one partially signed blob is required.");

            InnerSection sponsor = new InnerSection(SponsorField, "sponsor", ToSet(sponsorSignerAccounts));
            InnerSection counterparty = new InnerSection(CounterpartyField, "counterparty", ToSet(counterpartySignerAccounts));
            InnerSection[] sections = { sponsor, counterparty };

            foreach (string shared in sponsor.SignerAccounts.Intersect(counterparty.SignerAccounts, StringComparer.Ordinal))
                throw new ValidationException($"Ambiguous signer role for {shared}: listed as both a sponsor-side and a counterparty-side signer.");

            // All parts must agree on every non-signature field
            JsonObject canonical = parts[0].WithoutFields(SignatureFields);
            foreach (JsonObject part in parts.Skip(1))
            {
                if (!JsonNode.DeepEquals(canonical, part.WithoutFields(SignatureFields)))
                    throw new ValidationException("Incompatible transaction bodies. All parts must have identical non-signing fields.");
            }

            string? mainPubKey = null;
            string? mainSignature = null;
            JsonArray accountEntries = new JsonArray();

            void RouteEntry(JsonNode entry)
            {
                string account = entry?["Signer"]?["Account"]?.GetValue<string>()
                    ?? throw new ValidationException("Signer entry is missing the Account field.");
                string normalized = SignerUtilities.NormalizeClassicAddress(account);
                JsonArray target = sections.FirstOrDefault(s => s.SignerAccounts.Contains(normalized))?.Entries ?? accountEntries;
                target.Add(entry.DeepClone());
            }

            foreach (JsonObject part in parts)
            {
                string? pubKey = part["SigningPubKey"]?.GetValue<string>();
                string? signature = part["TxnSignature"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(pubKey) && !string.IsNullOrEmpty(signature))
                {
                    if (mainSignature is not null &&
                        (!string.Equals(mainSignature, signature, StringComparison.Ordinal) ||
                         !string.Equals(mainPubKey, pubKey, StringComparison.Ordinal)))
                        throw new ValidationException("Multiple conflicting main signatures supplied.");
                    mainPubKey = pubKey;
                    mainSignature = signature;
                }

                if (part["Signers"] is JsonArray entries)
                {
                    foreach (JsonNode? entry in entries)
                        RouteEntry(entry!);
                }

                foreach (InnerSection section in sections)
                {
                    if (part[section.Field] is JsonObject innerJson)
                        CollectInner(section, innerJson);
                }
            }

            if (mainSignature is not null && accountEntries.Count > 0)
                throw new ValidationException("Both a single main signature and main-side Signer entries were supplied; a transaction carries one or the other.");
            foreach (InnerSection section in sections)
            {
                if (section.Single is not null && section.Entries.Count > 0)
                    throw new ValidationException($"Both a single {section.Label} signature and {section.Label}-side Signer entries were supplied; {section.Field} carries one or the other.");
            }
            if (mainSignature is null && accountEntries.Count == 0)
                throw new ValidationException("No main signature material supplied. The transaction is not signed by all participants.");

            JsonObject result = canonical;
            if (mainSignature is not null)
            {
                result["SigningPubKey"] = mainPubKey;
                result["TxnSignature"] = mainSignature;
            }
            else
            {
                result["SigningPubKey"] = "";
                result["Signers"] = SignerUtilities.DedupeAndSortSigners(accountEntries);
            }

            foreach (InnerSection section in sections)
            {
                if (section.Single is not null)
                {
                    result[section.Field] = section.Single.ToJsonObject();
                }
                else if (section.Entries.Count > 0)
                {
                    result[section.Field] = new JsonObject
                    {
                        ["SigningPubKey"] = "",
                        ["Signers"] = SignerUtilities.DedupeAndSortSigners(section.Entries),
                    };
                }
            }

            VerifyEverySignature(result, sections);

            string txBlob = XrplBinaryCodec.Encode(result);
            return new SignatureResult(txBlob, HashLedger.HashSignedTx(txBlob));
        }

        /// <summary>
        /// Checks every signature in the composed transaction against the bytes that transaction
        /// actually ships, under the prefix of the section it ended up in.
        /// </summary>
        /// <remarks>
        /// Routing by account is a guess about what a signer meant, and since rippled's
        /// fixCleanup3_4_0 a wrong guess is no longer harmless: an entry made for the transaction
        /// itself covers different bytes than one made for a co-signing account, and only the node
        /// used to notice. Where the transaction cannot say which side a multi-signature entry
        /// belongs to - the main signature multi-signed as well - the signer chooses a default,
        /// and this is where choosing wrong stops being silent. It catches the other direction
        /// too: a part signed over a SigningPubKey the composed transaction does not ship.
        /// <para>
        /// The preimage is taken from <paramref name="result"/> itself. TxnSignature, Signers and
        /// both co-signature objects are not signing fields, so the codec drops them and what is
        /// left is exactly what each participant signed.
        /// </para>
        /// </remarks>
        private static void VerifyEverySignature(JsonObject result, IEnumerable<InnerSection> sections)
        {
            if (result["TxnSignature"]?.GetValue<string>() is { Length: > 0 } mainSignature)
            {
                RequireSingle(result, HashPrefix.TransactionSig, result["SigningPubKey"]?.GetValue<string>(), mainSignature,
                    "the transaction's own signature");
            }

            foreach (JsonNode? entry in result["Signers"] as JsonArray ?? new JsonArray())
                RequireEntry(result, entry, HashPrefix.TransactionMultiSig, "Signers");

            foreach (InnerSection section in sections)
            {
                if (result[section.Field] is not JsonObject inner)
                    continue;

                if (inner["Signers"] is JsonArray entries)
                {
                    foreach (JsonNode? entry in entries)
                        RequireEntry(result, entry, CoSigningEngine.PrefixFor(section.Field, multiSigning: true), $"{section.Field}.Signers");
                }
                else if (inner["TxnSignature"]?.GetValue<string>() is { Length: > 0 } coSignature)
                {
                    RequireSingle(result, CoSigningEngine.PrefixFor(section.Field), inner["SigningPubKey"]?.GetValue<string>(), coSignature, section.Field);
                }
            }
        }

        private static void RequireSingle(JsonObject result, HashPrefix prefix, string? publicKey, string signature, string what)
        {
            if (string.IsNullOrEmpty(publicKey))
                throw new ValidationException($"{what} carries a signature with no SigningPubKey.");

            byte[] preimage = AddressCodec.Utils.FromHex(XrplBinaryCodec.EncodeForSigning(result, prefix));
            if (!XrplKeypairs.Verify(preimage, signature, publicKey))
                throw new ValidationException(
                    $"{what} does not verify over the composed transaction. It was signed over different bytes - a different transaction body, a different SigningPubKey, or another signing role.");
        }

        private static void RequireEntry(JsonObject result, JsonNode? entry, HashPrefix prefix, string section)
        {
            JsonObject signer = entry?["Signer"]?.AsObject()
                ?? throw new ValidationException($"A {section} entry is missing its Signer object.");
            string account = signer["Account"]?.GetValue<string>()
                ?? throw new ValidationException($"A {section} entry is missing the Account field.");
            string? publicKey = signer["SigningPubKey"]?.GetValue<string>();
            string signature = signer["TxnSignature"]?.GetValue<string>()
                ?? throw new ValidationException($"The {section} entry of {account} is missing its TxnSignature.");

            if (string.IsNullOrEmpty(publicKey))
                throw new ValidationException($"The {section} entry of {account} carries no SigningPubKey.");

            byte[] preimage = AddressCodec.Utils.FromHex(XrplBinaryCodec.EncodeForMultiSigning(result, account, prefix));
            if (!XrplKeypairs.Verify(preimage, signature, publicKey))
                throw new ValidationException(
                    $"The {section} entry of {account} does not verify over the composed transaction. " +
                    "A multi-signature entry covers the section it belongs to, so an entry signed for one side cannot be routed into another; " +
                    "where the transaction does not say which side, pass the role to Sign.");
        }

        /// <summary>
        /// Folds one part's inner signature object into its section. Entries
        /// pre-placed under the section's Signers keep their explicit role: the
        /// producer asserted the side, so they are NOT re-routed by the account
        /// sets (offline compose may have none).
        /// </summary>
        private static void CollectInner(InnerSection section, JsonObject innerJson)
        {
            SignatureObject parsed = SignatureObject.FromJsonObject(innerJson);
            if (parsed.Signers is { Count: > 0 })
            {
                foreach (SignatureObject inner in parsed.Signers)
                {
                    if (string.IsNullOrEmpty(inner.Account))
                        throw new ValidationException($"A {section.Field} Signers entry is missing the Account field.");
                    if (string.IsNullOrEmpty(inner.SigningPubKey) || string.IsNullOrEmpty(inner.TxnSignature))
                        throw new ValidationException($"A {section.Field} Signers entry is missing SigningPubKey or TxnSignature.");
                    section.Entries.Add(new JsonObject
                    {
                        ["Signer"] = SignatureObject
                            .Single(inner.SigningPubKey, inner.TxnSignature, inner.Account)
                            .ToJsonObject(),
                    });
                }
            }
            else if (!string.IsNullOrEmpty(parsed.TxnSignature))
            {
                if (section.Single is not null &&
                    (!string.Equals(section.Single.TxnSignature, parsed.TxnSignature, StringComparison.Ordinal) ||
                     !string.Equals(section.Single.SigningPubKey, parsed.SigningPubKey, StringComparison.Ordinal)))
                    throw new ValidationException($"Multiple conflicting {section.Label} signatures supplied.");
                section.Single = parsed;
            }
        }

        private static HashSet<string> ToSet(IReadOnlyCollection<string>? accounts) =>
            new HashSet<string>(
                (accounts ?? Array.Empty<string>()).Select(SignerUtilities.NormalizeClassicAddress),
                StringComparer.Ordinal);
    }
}
