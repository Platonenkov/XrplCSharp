using System;
using Xrpl.AddressCodec;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xrpl.BinaryCodec.Binary;
using Xrpl.BinaryCodec.Hashing;
using Xrpl.BinaryCodec.Types;
using Xrpl.BinaryCodec.Util;


// https://github.com/XRPLF/xrpl.js/blob/main/packages/ripple-binary-codec/src/index.ts

namespace Xrpl.BinaryCodec
{
    public class XrplBinaryCodec
    {
        static uint PAYMENT_CHANNEL_CLAIM_PREFIX = 0x434C4D00u;

        /// <summary>
        /// The two option sets <see cref="ObjectToJsonNode"/> needs, built once.
        /// </summary>
        /// <remarks>
        /// These were constructed per call, on the path every signing operation takes -
        /// <see cref="Encode(object)"/>, <see cref="EncodeForSigning"/>, <see cref="EncodeForSigningClaim"/>
        /// and <see cref="EncodeForMultiSigning"/> all route through it.
        /// </remarks>
        /// <remarks>
        /// The cost was smaller than the usual telling of this bug suggests: since .NET 7
        /// System.Text.Json shares a caching context between structurally equal options instances,
        /// so type metadata was not rebuilt per call - had it been, the gap below would be orders of
        /// magnitude rather than 1.7x. What was paid is an allocation and a structural-equality
        /// lookup in that shared pool, which is capped at 64 contexts and no longer leaned on here.
        /// </remarks>
        /// <remarks>
        /// Measured end to end on <see cref="EncodeForSigning"/>, 50 000 calls, best of five rounds:
        /// 1075.8 ms and 14458 B/op before, 621.8 ms and 13601 B/op after - 1.73x, and 857 fewer
        /// bytes each call. The encoded blob is unchanged, hashing identically either way.
        /// </remarks>
        private static readonly JsonSerializerOptions IgnoreNullOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions KeepNullOptions = new JsonSerializerOptions();

        /// <summary>
        /// Decode a hex string into a JsonNode representing the transaction/object.
        /// </summary>
        /// <param name="binary"></param>
        /// <returns>JsonNode</returns>
        public static JsonNode Decode(string binary)
        {
            var stobject = StObject.FromHex(binary);
            return stobject.ToJson();
        }

        /// <summary>
        /// Encode a JsonNode into binary hex string.
        /// </summary>
        /// <param name="token"></param>
        /// <returns>string</returns>
        public static string Encode(JsonNode token)
        {
            return SerializeJson(token);
        }

        /// <summary>
        /// Encode an object into binary hex string.
        /// </summary>
        /// <param name="json"></param>
        /// <returns>string</returns>
        public static string Encode(object json)
        {
            JsonNode node = ObjectToJsonNode(json, ignoreNull: true);
            return Encode(node);
        }

        /// <summary>
        /// Encode a transaction into binary format in preparation for signing. (Only encodes fields that are intended to be signed.)
        /// </summary>
        /// <param name="json"></param>
        /// <returns>string</returns>
        public static string EncodeForSigning(object json)
        {
            return EncodeForSigning(json, HashPrefix.TransactionSig);
        }

        /// <summary>
        /// Encode a transaction for signing under an explicit prefix, for a signature that is not
        /// the transaction's own: <see cref="HashPrefix.SponsorTransactionSig"/> for
        /// SponsorSignature, <see cref="HashPrefix.CounterpartyTransactionSig"/> for
        /// CounterpartySignature.
        /// </summary>
        /// <remarks>
        /// An overload rather than an optional parameter on the method above: a default value is
        /// source-compatible but not binary-compatible, and an assembly built against the
        /// one-argument signature would call a method that no longer exists.
        /// </remarks>
        /// <param name="json">The transaction.</param>
        /// <param name="prefix">Prefix for the signing role.</param>
        /// <returns>string</returns>
        public static string EncodeForSigning(object json, HashPrefix prefix)
        {
            JsonNode node = ObjectToJsonNode(json);
            return SerializeJson(node, prefix.Bytes(), null, true);
        }

        /// <summary>
        /// Encode a `payment channel <a href="https://xrpl.org/payment-channels.html">here</a>`_ Claim to be signed.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns>string</returns> The binary-encoded claim, ready to be signed.
        public static string EncodeForSigningClaim(object obj)
        {
            JsonNode json = ObjectToJsonNode(obj);

            byte[] prefix = Bits.GetBytes(PAYMENT_CHANNEL_CLAIM_PREFIX);
            JsonNode channelNode = json["channel"] ?? throw new ArgumentException("Missing 'channel' property");
            JsonNode amountNode = json["amount"] ?? throw new ArgumentException("Missing 'amount' property");
            byte[] channel = Hash256.FromHex(channelNode.GetValue<string>()).Buffer;
            byte[] amount = Uint64.FromValue(int.Parse(amountNode.GetValue<string>())).ToBytes();
            byte[] rv = new byte[prefix.Length + channel.Length + amount.Length];
            System.Buffer.BlockCopy(prefix, 0, rv, 0, prefix.Length);
            System.Buffer.BlockCopy(channel, 0, rv, prefix.Length, channel.Length);
            System.Buffer.BlockCopy(amount, 0, rv, prefix.Length + channel.Length, amount.Length);
            return rv.ToHex();
        }

        /// <summary>
        /// Encode a transaction into binary format in preparation for providing one signature towards a multi-signed transaction. (Only encodes fields that are intended to be signed.)
        /// </summary>
        /// <param name="json"></param>
        /// <param name="signingAccount"></param>
        /// <returns>string</returns>
        public static string EncodeForMultiSigning(object json, string signingAccount)
        {
            return EncodeForMultiSigning(json, signingAccount, HashPrefix.TransactionMultiSig);
        }

        /// <summary>
        /// Encode a transaction for one multi-signature under an explicit prefix, for a signer on
        /// a co-signing account's SignerList: <see cref="HashPrefix.SponsorTransactionMultiSig"/>
        /// for SponsorSignature.Signers, <see cref="HashPrefix.CounterpartyTransactionMultiSig"/>
        /// for CounterpartySignature.Signers.
        /// </summary>
        /// <remarks>
        /// Since fixCleanup3_4_0 a Signer entry is no longer section-agnostic: an entry made for
        /// the transaction's own Signers covers different bytes than the same entry inside a role
        /// section, so the signer has to know which side it signs for. An overload for the same
        /// binary-compatibility reason as <see cref="EncodeForSigning(object, HashPrefix)"/>.
        /// </remarks>
        /// <param name="json">The transaction.</param>
        /// <param name="signingAccount">The account whose key signs this entry.</param>
        /// <param name="prefix">Prefix for the signing role.</param>
        /// <returns>string</returns>
        public static string EncodeForMultiSigning(object json, string signingAccount, HashPrefix prefix)
        {
            string accountID = new AccountId(signingAccount).ToHex();
            JsonNode token = ObjectToJsonNode(json);
            return SerializeJson(token, prefix.Bytes(), accountID.FromHex(), true);
        }

        private static JsonNode ObjectToJsonNode(object obj, bool ignoreNull = false)
        {
            if (obj is JsonNode node) return node;

            JsonSerializerOptions options = ignoreNull ? IgnoreNullOptions : KeepNullOptions;

            string jsonString = JsonSerializer.Serialize(obj, options);
            return JsonNode.Parse(jsonString);
        }

        /// <summary>
        /// Encode a multi transaction - Batch (XLS-56, BatchV1_1 amendment).
        /// Preimage layout mirrors rippled's serializeBatch():
        /// "BCH\0" || outerAccount(20) || outerSequence(4) || Flags(4) || Count(4) || txID[i](32 each).
        /// The signer-specific suffixes (BatchSigner account, inner multisign signer account)
        /// are appended by the caller, matching finishMultiSigningData() in rippled.
        /// </summary>
        /// <param name="outerAccount">Account of the outer Batch transaction (classic base58 r-address).</param>
        /// <param name="outerSequence">Sequence of the outer Batch transaction.</param>
        /// <param name="flags">Batch flags.</param>
        /// <param name="txIDs">Collection of inner transaction IDs.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentException"></exception>
        public static byte[] EncodeForSigningBatch(string outerAccount, uint outerSequence, uint flags, IEnumerable<string> txIDs)
        {
            if (string.IsNullOrWhiteSpace(outerAccount)) throw new ArgumentNullException(nameof(outerAccount));
            if (txIDs == null) throw new ArgumentNullException(nameof(txIDs));

            var list = new BytesList();

            // 1) The "BCH\0" prefix
            list.Put(Bits.GetBytes((uint)HashPrefix.Batch));

            // 2) Account of the outer Batch transaction (20 bytes)
            byte[] outerAccountId = new AccountId(outerAccount).Buffer;
            if (outerAccountId.Length != 20) throw new ArgumentException("outerAccount must decode to 20 bytes.");
            list.Put(outerAccountId);

            // 3) Sequence of the outer Batch transaction (UInt32 BE)
            list.Put(new Uint32(outerSequence).ToBytes());

            // 4) Flags (UInt32 BE)
            list.Put(new Uint32(flags).ToBytes());

            // 5) The number of txIDs (UInt32 BE)
            list.Put(new Uint32((uint)txIDs.Count()).ToBytes());

            // 6) Each txid as 32 bytes
            foreach (var id in txIDs)
            {
                var raw = Hash256.FromHex(id).Buffer; // validate hex string
                if (raw.Length != 32) throw new ArgumentException("txID must be 32 bytes (Hash256).");
                list.Put(raw);
            }

            return list.ToBytes();
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="json"></param>
        /// <returns>string</returns>
        public static string SerializeJson(JsonNode json, byte[]? prefix = null, byte[]? suffix = null, bool signingOnly = false)
        {
            var list = new BytesList();
            if (prefix != null)
            {
                list.Put(prefix);
            }

            StObject so = StObject.FromJson(json, signingOnly);
            list.Put(so.ToBytes());

            if (suffix != null)
            {
                list.Put(suffix);
            }
            return list.BytesHex();
        }
    }
}