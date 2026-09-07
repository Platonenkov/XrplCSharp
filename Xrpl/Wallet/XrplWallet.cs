using NBitcoin;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Xrpl.AddressCodec;
using Xrpl.BinaryCodec;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Keypairs;
using Xrpl.Models.Transactions;
using Xrpl.Models.Utils;
using Xrpl.Utils.Hashes;

// Xrpl.Utils.Hashes declares a HashPrefix of its own; the codec's is the one that prefixes a signing preimage.
using HashPrefix = Xrpl.BinaryCodec.Hashing.HashPrefix;

// https://github.com/XRPLF/xrpl.js/blob/main/packages/xrpl/src/Wallet/index.ts

namespace Xrpl.Wallet
{
    public class SignatureResult
    {
        [JsonPropertyName("tx_blob")]
        public string TxBlob { get; set; }

        [JsonPropertyName("hash")]
        public string Hash { get; set; }

        public SignatureResult(string txBlob, string hash)
        {
            TxBlob = txBlob;
            Hash = hash;
        }

        public Dictionary<string, object> GetTxDictionary()
        {
            if (TxBlob == null)
            {
                throw new NullReferenceException(nameof(TxBlob));
            }

            var dic = XrplBinaryCodec.Decode(TxBlob);
            dic["hash"] = Hash; // add hash to the dictionary for convenience
            return JsonSerializer.Deserialize<Dictionary<string, object>>(dic.ToString(), XrplJsonOptions.Default);
        }

        /// <summary>
        /// The signed blob decoded back into a typed transaction.
        /// </summary>
        /// <exception cref="ValidationException">
        /// The blob carries a top-level field no model property claims, so the returned object
        /// would not represent it. Signing that object again produces a blob missing the field -
        /// which is how a co-signature could be dropped: <c>CounterpartySignature</c> and
        /// <c>SponsorSignature</c> exist in <c>definitions.json</c> and survive the codec, but no
        /// request model declares them, so a round trip through here used to discard them
        /// silently. Failing loudly is the point: the caller is told what would have been lost
        /// rather than submitting a transaction the node will reject for a missing signature.
        /// For those two flows use the blob-level helpers (<c>LoanSigningHelper.BrokerSign</c>,
        /// <c>SponsorSigningHelper.SubmitterSign</c>), which never leave the blob.
        /// </exception>
        public ITransactionRequest GetTx()
        {
            if (TxBlob == null)
            {
                throw new NullReferenceException(nameof(TxBlob));
            }

            JsonObject decoded = XrplBinaryCodec.Decode(TxBlob).AsObject();
            ITransactionRequest transaction = JsonSerializer.Deserialize<TransactionRequest>(
                decoded.ToString(), XrplJsonOptions.Default);

            string reemitted = JsonSerializer.Serialize(transaction, transaction.GetType(), XrplJsonOptions.Default);
            using JsonDocument roundTripped = JsonDocument.Parse(reemitted);

            List<string> dropped = new List<string>();
            foreach (KeyValuePair<string, JsonNode> member in decoded)
            {
                if (!roundTripped.RootElement.TryGetProperty(member.Key, out _))
                {
                    dropped.Add(member.Key);
                }
            }

            if (dropped.Count > 0)
            {
                throw new ValidationException(
                    "Decoding this blob into a typed transaction would drop "
                    + string.Join(", ", dropped)
                    + " - no model property carries it, so signing the result would produce a blob without it. "
                    + "Work from TxBlob instead (see LoanSigningHelper/SponsorSigningHelper for the co-signing flows).");
            }

            return transaction;
        }
    }
    public enum TextWalletKdf
    {
        Sha256 = 0,
        Pbkdf2 = 1,
    }

    public class XrplWallet
    {

        public static string DEFAULT_ALGORITHM = Ed25519;
        public const string Ed25519 = "ed25519";
        public const string Secp256k1 = "secp256k1";

        private static readonly Lazy<string[]> _bip39WordlistCache = new Lazy<string[]>(() =>
        {
            var words = new string[2048];
            for (int i = 0; i < 2048; i++)
                words[i] = Wordlist.English.GetWordAtIndex(i);
            return words;
        });

        public readonly string PublicKey;
        public readonly string PrivateKey;
        public readonly string ClassicAddress;
        public readonly string Seed;

        /// <summary>
        /// Creates a new Wallet.
        /// </summary>
        /// <param name="publicKey">The public key for the account.</param>
        /// <param name="privateKey">The private key used for signing transactions for the account.</param>
        /// <param name="masterAddress">Include if a Wallet uses a Regular Key Pair. It must be the master address of the account.</param>
        /// <param name="seed">The seed used to derive the account keys.</param>
        public XrplWallet(string publicKey, string privateKey, string? masterAddress = null, string? seed = null)
        {
            this.PublicKey = publicKey;
            this.PrivateKey = privateKey;
            this.ClassicAddress = masterAddress ?? XrplKeypairs.DeriveAddress(publicKey);
            this.Seed = seed;
        }

        /// <summary>
        /// Generates a new Wallet using a generated seed.
        /// </summary>
        /// <param name="algorithm">The digital signature algorithm to generate an address for.</param>
        /// <returns>A new Wallet derived from a generated seed.</returns>
        public static XrplWallet Generate(string algorithm = Ed25519)
        {
            string seed = XrplKeypairs.GenerateSeed(null, algorithm);
            return XrplWallet.FromSeed(seed, null, algorithm);
        }
        /// <summary>
        /// Derives a wallet from a seed.
        /// </summary>
        /// <param name="seed">A string used to generate a keypair (publicKey/privateKey) to derive a wallet.</param>
        /// <param name="algorithm">The digital signature algorithm to generate an address for.</param>
        /// <param name="masterAddress">Include if a Wallet uses a Regular Key Pair. It must be the master address of the account.</param>
        /// <returns>A Wallet derived from a seed.</returns>
        public static XrplWallet FromSeed(string seed, string? masterAddress = null, string? algorithm = null)
        {
            return XrplWallet.DeriveWallet(seed, masterAddress, algorithm);
        }

        /// <summary>
        /// Creates a new instance of the XrplWallet class using the specified private key and an optional master
        /// address.
        /// </summary>
        /// <remarks>The method derives the public key from the provided private key using XRPL keypair
        /// functionality. Supplying an invalid private key will result in an exception during wallet creation. Ensure
        /// that the private key is valid and securely managed.</remarks>
        /// <param name="privateKey">The private key used to derive the wallet's public key. Must be a valid XRPL private key format and should
        /// be kept secure.</param>
        /// <param name="masterAddress">An optional master address associated with the wallet. If provided, it is used as part of the wallet's
        /// initialization; otherwise, the wallet is initialized without a master address.</param>
        /// <returns>An XrplWallet instance containing the derived public key, the provided private key, and the optional master
        /// address.</returns>
        public static XrplWallet FromPrivateKey(string privateKey, string? masterAddress = null)
        {
            var publicKey = XrplKeypairs.DerivePublicKeyFromPrivateKey(privateKey);
            return new XrplWallet(publicKey, privateKey, masterAddress);
        }

        /// <summary>
        /// An array of random numbers to generate a seed used to derive a wallet.
        /// </summary>
        /// <param name="algorithm">The digital signature algorithm to generate an address for.</param>
        /// <param name="masterAddress">Include if a Wallet uses a Regular Key Pair. It must be the master address of the account.</param>
        /// <returns>A Wallet derived from an entropy.</returns>
        public static XrplWallet FromEntropy(byte[] entropy, string? masterAddress = null, string? algorithm = null)
        {
            string falgorithm = algorithm ?? XrplWallet.DEFAULT_ALGORITHM;
            string seed = XrplKeypairs.GenerateSeed(entropy, falgorithm);
            return XrplWallet.DeriveWallet(seed, masterAddress, falgorithm);
        }

        public static XrplWallet FromMnemonic(string mnemonic,
            string? masterAddress = null,
            string? derivationPath = null,
            string? encoding = null,
            string? algorithm = null,
            string? passphrase = null)
        {

            if (encoding == "rfc1751")
            {
                return FromRFC1751Mnemonic(mnemonic, masterAddress, algorithm);
            }

            if (!IsValidBip39Mnemonic(mnemonic))
            {
                throw new ValidationException("Unable to parse the given mnemonic using bip39 encoding");
            }

            var masterNode = new Mnemonic(mnemonic).DeriveExtKey(passphrase);
            //var masterNode = new ExtKey(seed);
            var node = masterNode.Derive(new KeyPath(derivationPath ?? "m/44'/144'/0'/0/0"));

            var publicKey = node.PrivateKey.PubKey.ToHex().ToUpper();
            var privateKey = node.PrivateKey.ToHex().ToUpper();
            return new XrplWallet(publicKey, privateKey, masterAddress);
        }
        private static XrplWallet FromRFC1751Mnemonic(string mnemonic, string? masterAddress = null, string? algorithm = null)
        {
            var seed = RFC1751.RFC1751MnemonicToKey(mnemonic);
            var encodeAlgorithm = algorithm == Ed25519 ? Ed25519 : Secp256k1;
            var encodedSeed = XrplCodec.EncodeSeed(seed, encodeAlgorithm);
            return FromSeed(encodedSeed, masterAddress, algorithm);
        }

        private static bool IsValidBip39Mnemonic(string mnemonic)
        {
            try
            {
                var mnemo = new Mnemonic(mnemonic);
                return mnemo.IsValidChecksum;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generates a random BIP-39 mnemonic phrase.
        /// <para>
        /// BIP-39 defines a standard for mnemonic phrases - human-readable word sequences
        /// that encode cryptographic entropy. The words are selected from a standardized
        /// 2048-word English wordlist.
        /// </para>
        /// <para>
        /// The number of words determines the entropy strength:
        /// <list type="bullet">
        ///   <item><description>12 words = 128 bits of entropy (standard)</description></item>
        ///   <item><description>15 words = 160 bits of entropy</description></item>
        ///   <item><description>18 words = 192 bits of entropy</description></item>
        ///   <item><description>21 words = 224 bits of entropy</description></item>
        ///   <item><description>24 words = 256 bits of entropy (maximum)</description></item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="wordCount">The number of words to generate (12, 15, 18, 21, or 24). Default is 12.</param>
        /// <returns>An array of mnemonic words.</returns>
        /// <exception cref="ArgumentException">Thrown when wordCount is not 12, 15, 18, 21, or 24.</exception>
        /// <remarks>
        /// Reference: https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki
        /// </remarks>
        /// <example>
        /// <code>
        /// // Generate 12-word mnemonic (default)
        /// string[] words12 = XrplWallet.GenerateMnemonic();
        /// 
        /// // Generate 24-word mnemonic for maximum security
        /// string[] words24 = XrplWallet.GenerateMnemonic(24);
        /// 
        /// // Create wallet from mnemonic
        /// var wallet = XrplWallet.FromMnemonic(string.Join(" ", words24));
        /// </code>
        /// </example>
        public static string[] GenerateMnemonic(int wordCount = 12)
        {
            WordCount nbWordCount = wordCount switch
            {
                12 => WordCount.Twelve,
                15 => WordCount.Fifteen,
                18 => WordCount.Eighteen,
                21 => WordCount.TwentyOne,
                24 => WordCount.TwentyFour,
                _ => throw new ArgumentException(
                    $"Invalid word count: {wordCount}. Must be one of: 12, 15, 18, 21, 24.",
                    nameof(wordCount))
            };

            var mnemonic = new Mnemonic(Wordlist.English, nbWordCount);
            return mnemonic.Words;
        }

        /// <summary>
        /// Validates whether a word exists in the BIP-39 English wordlist.
        /// <para>
        /// The BIP-39 standard defines a fixed set of 2048 English words used for mnemonic phrases.
        /// This method checks if a given word is present in that wordlist.
        /// Use this for real-time validation as the user types each word.
        /// </para>
        /// </summary>
        /// <param name="word">The word to validate (case-insensitive).</param>
        /// <returns><c>true</c> if the word exists in the BIP-39 English wordlist; otherwise, <c>false</c>.</returns>
        /// <remarks>
        /// Note: In BIP-39, any valid word can appear at any position in the mnemonic.
        /// Position-level correctness can only be verified via checksum validation
        /// after all words have been entered (see <see cref="ValidateMnemonicChecksum"/>).
        /// <para>Reference: https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// bool valid = XrplWallet.ValidateMnemonicWord("abandon"); // true
        /// bool invalid = XrplWallet.ValidateMnemonicWord("xyz123"); // false
        /// </code>
        /// </example>
        public static bool ValidateMnemonicWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return false;
            return Wordlist.English.WordExists(word.Trim().ToLowerInvariant(), out _);
        }

        /// <summary>
        /// Suggests BIP-39 words similar to the given input for autocomplete and typo correction.
        /// <para>
        /// Returns matching words in priority order: exact prefix matches first (sorted alphabetically),
        /// then fuzzy matches by Levenshtein distance (for typo correction).
        /// Duplicates are removed so prefix matches are not repeated in fuzzy results.
        /// </para>
        /// </summary>
        /// <param name="input">The partial or misspelled word to find suggestions for.</param>
        /// <param name="maxSuggestions">Maximum number of suggestions to return. Default is 5.</param>
        /// <returns>
        /// An array of suggested words from the BIP-39 English wordlist, ordered by relevance.
        /// Returns an empty array if input is null or empty.
        /// </returns>
        /// <remarks>
        /// The algorithm uses two strategies:
        /// <list type="number">
        ///   <item><description>Prefix matching: words that start with the input string.</description></item>
        ///   <item><description>Levenshtein distance: words within edit distance 2 of the input (for typo correction).</description></item>
        /// </list>
        /// <para>Reference: https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// // Prefix matching
        /// string[] suggestions = XrplWallet.SuggestMnemonicWords("aban");
        /// // Returns: ["abandon", "ability", ...] — words starting with "aban"
        ///
        /// // Typo correction
        /// string[] typoFix = XrplWallet.SuggestMnemonicWords("abandonn");
        /// // Returns: ["abandon"] — corrects the typo
        /// </code>
        /// </example>
        public static string[] SuggestMnemonicWords(string input, int maxSuggestions = 5)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Array.Empty<string>();

            string normalized = input.Trim().ToLowerInvariant();
            var allWords = _bip39WordlistCache.Value;

            var prefixMatches = allWords
                .Where(w => w.StartsWith(normalized, StringComparison.Ordinal))
                .OrderBy(w => w)
                .ToList();

            if (prefixMatches.Count >= maxSuggestions)
                return prefixMatches.Take(maxSuggestions).ToArray();

            var prefixSet = new HashSet<string>(prefixMatches);
            var fuzzyMatches = allWords
                .Where(w => !prefixSet.Contains(w))
                .Select(w => new { Word = w, Distance = LevenshteinDistance(normalized, w) })
                .Where(x => x.Distance <= 2)
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Word)
                .Select(x => x.Word)
                .ToList();

            var result = new List<string>(prefixMatches);
            result.AddRange(fuzzyMatches);
            return result.Take(maxSuggestions).ToArray();
        }

        private static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;

            int sourceLength = source.Length;
            int targetLength = target.Length;
            var distance = new int[sourceLength + 1, targetLength + 1];

            for (int i = 0; i <= sourceLength; i++) distance[i, 0] = i;
            for (int j = 0; j <= targetLength; j++) distance[0, j] = j;

            for (int i = 1; i <= sourceLength; i++)
            {
                for (int j = 1; j <= targetLength; j++)
                {
                    int cost = source[i - 1] == target[j - 1] ? 0 : 1;
                    distance[i, j] = Math.Min(
                        Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                        distance[i - 1, j - 1] + cost);
                }
            }

            return distance[sourceLength, targetLength];
        }

        /// <summary>
        /// Validates the checksum of a complete BIP-39 mnemonic phrase.
        /// <para>
        /// In BIP-39, the last word of a mnemonic contains checksum bits derived from the
        /// SHA-256 hash of the entropy. This method verifies that the checksum is correct,
        /// which confirms that all words are valid and in the correct order.
        /// </para>
        /// </summary>
        /// <param name="words">The complete mnemonic phrase as an array of words.</param>
        /// <returns><c>true</c> if the mnemonic has a valid checksum; otherwise, <c>false</c>.</returns>
        /// <remarks>
        /// This method performs three levels of validation:
        /// <list type="number">
        ///   <item><description>Word count: must be 12, 15, 18, 21, or 24.</description></item>
        ///   <item><description>Word validity: all words must exist in the BIP-39 English wordlist.</description></item>
        ///   <item><description>Checksum: the last word's checksum bits must match the SHA-256 hash of the entropy.</description></item>
        /// </list>
        /// <para>
        /// Call this method after the user has entered all mnemonic words.
        /// For per-word validation during input, use <see cref="ValidateMnemonicWord"/>.
        /// </para>
        /// <para>Reference: https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// string[] words = { "assault", "rare", "scout", "seed", "design", "extend",
        ///                     "noble", "drink", "talk", "control", "guitar", "quote" };
        /// bool valid = XrplWallet.ValidateMnemonicChecksum(words); // true
        ///
        /// words[11] = "abandon"; // corrupt last word
        /// bool invalid = XrplWallet.ValidateMnemonicChecksum(words); // false
        /// </code>
        /// </example>
        public static bool ValidateMnemonicChecksum(string[] words)
        {
            if (words == null || words.Length == 0)
                return false;

            int count = words.Length;
            if (count != 12 && count != 15 && count != 18 && count != 21 && count != 24)
                return false;

            string sentence = string.Join(" ", words);
            try
            {
                var mnemo = new Mnemonic(sentence);
                return mnemo.IsValidChecksum;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Derive a Wallet from a seed.
        /// </summary>
        /// <param name="seed">The seed used to derive the wallet.</param>
        /// <param name="algorithm">The digital signature algorithm to generate an address for.</param>
        /// <param name="masterAddress">Include if a Wallet uses a Regular Key Pair. It must be the master address of the account.</param>
        /// <returns>A Wallet derived from the seed.</returns>
        private static XrplWallet DeriveWallet(string seed, string? masterAddress = null, string? algorithm = null)
        {
            IXrplKeyPair keypair = XrplKeypairs.DeriveKeypair(seed, algorithm);
            return new XrplWallet(keypair.Id(), keypair.Pk(), masterAddress, seed);
        }
        /// <summary>
        /// Creates a Wallet from xumm numbers.
        /// </summary>
        /// <returns>A Wallet from xumm numbers.</returns>
        public static XrplWallet FromXummNumbers(string[] numbers, string algorithm = Secp256k1, string? masterAddress = null)
        {
            byte[] entropy = XummExtension.EntropyFromXummNumbers(numbers);
            return FromEntropy(entropy, masterAddress, algorithm);
        }

        /// <summary>
        /// Creates a Wallet from a space-separated secret numbers string.
        /// Accepts formats like "554872 394230 209376 323698 140250 387423 652803 258676".
        /// </summary>
        /// <param name="secretString">Space-separated secret numbers string (8 groups of 6 digits)</param>
        /// <param name="algorithm">The digital signature algorithm to use. Default is secp256k1.</param>
        /// <returns>A Wallet created from the secret numbers.</returns>
        public static XrplWallet FromSecretString(string secretString, string algorithm = Secp256k1)
        {
            string[] numbers = XummExtension.ParseSecretString(secretString);
            return FromXummNumbers(numbers, algorithm);
        }

        /// <summary>
        /// Gets the Secret Numbers representation of this wallet's seed.
        /// Returns 8 groups of 6 digits each, where 5 digits are entropy and 1 digit is checksum.
        /// </summary>
        /// <returns>Array of 8 secret number strings, or null if the wallet was not created from a seed.</returns>
        public string[] GetSecretNumbers()
        {
            if (string.IsNullOrEmpty(Seed))
                return null;

            var decoded = XrplCodec.DecodeSeed(Seed);
            return XummExtension.EntropyToSecretNumbers(decoded.Bytes);
        }

        /// <summary>
        /// Gets the Secret Numbers as a formatted string with spaces between groups.
        /// </summary>
        /// <returns>Space-separated secret numbers string, or null if the wallet was not created from a seed.</returns>
        public string GetSecretString()
        {
            var numbers = GetSecretNumbers();
            return numbers != null ? string.Join(" ", numbers) : null;
        }

        /// <summary>
        /// Creates a Wallet from any text.
        /// </summary>
        /// <param name="text">any text to generate wallet</param>
        /// <param name="algorithm">The digital signature algorithm to generate an address for.</param>
        /// <param name="salt">user salt as a password</param>
        /// <param name="caseInsensitive">is case-insensitive</param>
        /// <param name="masterAddress">account master address, will use as account</param>
        /// <param name="kdf">Key Derivation Function</param>
        /// <returns>generated wallet</returns>
        public static XrplWallet FromNormalizedText(
            string text,
            string? salt = null,
            bool caseInsensitive = true,
            string algorithm = null,
            string masterAddress = null,
            TextWalletKdf kdf = TextWalletKdf.Sha256)
        {
            var normalized = NormalizeText(text, caseInsensitive);

            var seedBytes = kdf switch
            {
                TextWalletKdf.Sha256 => DeriveSeedWithSha256(normalized, salt),
                TextWalletKdf.Pbkdf2 => DeriveSeedWithPbkdf2(normalized, salt),
                _ => throw new ArgumentOutOfRangeException(nameof(kdf), kdf, "Unsupported KDF")
            };

            return XrplWallet.FromEntropy(seedBytes, masterAddress, algorithm ?? XrplWallet.DEFAULT_ALGORITHM);
        }
        private static byte[] DeriveSeedWithSha256(string text, string? salt, int seedLength = 16)
        {
            if (!string.IsNullOrWhiteSpace(salt))
                text += "::" + salt.Trim();

            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(text));

            return entropy.Take(seedLength).ToArray(); // 16 bytes = 128 bits
        }
        private static byte[] DeriveSeedWithPbkdf2(
            string normalized,
            string? salt,
            int iterations = 100_000,
            int seedLength = 16)
        {
            var passwordBytes = Encoding.UTF8.GetBytes(normalized);

            byte[] saltBytes = null;
            if (!string.IsNullOrWhiteSpace(salt))
            {
                // salt as is, but with Trim
                saltBytes = Encoding.UTF8.GetBytes(salt.Trim());
            }

            using var pbkdf2 = new Rfc2898DeriveBytes(
                passwordBytes,
                saltBytes ?? [],
                iterations,
                HashAlgorithmName.SHA256);

            return pbkdf2.GetBytes(seedLength); // 16 bytes of entropy for seed
        }

        private static string NormalizeText(string input, bool caseInsensitive)
        {
            // We remove extra spaces, convert to lowercase, and normalize characters.
            var normalized = input
                .Trim()
                .Replace("\r\n", "\n") // Windows → Unix
                .Replace("\r", "\n");    // Mac → Unix
            if (caseInsensitive)
            {
                normalized = normalized    // Mac → Unix
                    .ToLowerInvariant();     // if it's important to be case-insensitive
            }
            // Compressing multiple spaces and line breaks into a single space
            normalized = string.Join(" ", normalized
                .Split([' ', '\n', '\t',], StringSplitOptions.RemoveEmptyEntries));

            return normalized;
        }


        /// <summary>
        /// Refuses a transaction whose memos a node would refuse locally, before it is signed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A memo past the limit fails rippled's <c>passesLocalChecks</c>: the transaction is not
        /// relayed, reaches no ledger and costs no fee - but the consumer has by then built,
        /// autofilled and signed it, and the node's answer does not say which field was at fault.
        /// This is the last point where the refusal is still free.
        /// </para>
        /// <para>
        /// Called from every public entry that signs a transaction dictionary rather than from
        /// <see cref="Sign(Dictionary{string, object}, bool, string?)"/> alone, which would not have
        /// been enough: <c>SignAsBatchPart</c>, <c>SignAsSponsor</c> and
        /// <c>SignAsLoanCounterparty</c> each sign on their own, and the SDK's own multi-batch
        /// submission calls the first of them directly. The typed overloads convert and delegate to
        /// these, so guarding the four dictionary ones covers every way in.
        /// </para>
        /// </remarks>
        private static void GuardMemos(Dictionary<string, object> transaction)
        {
            transaction.TryGetValue("Memos", out object memos);
            MemoRules.Validate(memos);
        }

        /// <summary>
        /// Signs a transaction offline in a stated role, for the one shape where the transaction
        /// does not say which signature this is: a multi-signature entry on a transaction whose
        /// main signature is multi-signed too, or which names both a Sponsor and a Counterparty.
        /// </summary>
        /// <remarks>
        /// Everywhere else the role follows from the transaction and this overload is not needed;
        /// see <see cref="SignatureRole"/>. An overload rather than a fourth optional parameter,
        /// which would be source-compatible but not binary-compatible.
        /// </remarks>
        /// <param name="transaction">A transaction to be signed offline.</param>
        /// <param name="multisign">True to produce a multi-signature entry rather than a single signature.</param>
        /// <param name="signingFor">The signing account (classic or X-address); this wallet's own by default.</param>
        /// <param name="role">Which signature of the transaction this key is producing.</param>
        public SignatureResult Sign(
            Dictionary<string, object> transaction,
            bool multisign,
            string? signingFor,
            SignatureRole role)
        {
            GuardMemos(transaction);

            if (!multisign)
            {
                return role switch
                {
                    SignatureRole.Sponsor => SignAsSponsor(transaction),
                    SignatureRole.Counterparty => SignAsLoanCounterparty(transaction),
                    _ => Sign(transaction, false, signingFor),
                };
            }

            return SignMulti(transaction, NormalizeClassic(signingFor), role);
        }

        /// <summary>
        /// Signs a transaction offline.
        /// </summary>
        /// <param name="transaction">A transaction to be signed offline.</param>
        /// <param name="multisign">Specify true/false to use multisign or actual address (classic/x-address) to make multisign tx request.</param>
        /// <param name="signingFor"></param>
        /// <returns>A Wallet derived from the seed.</returns>
        /// <exception cref="ValidationException">
        /// When the transaction carries <c>Memos</c> a node would refuse locally - see
        /// <see cref="MemoRules"/>.
        /// </exception>
        public SignatureResult Sign(Dictionary<string, object> transaction, bool multisign = false, string? signingFor = null)
        {
            GuardMemos(transaction);

            // 1) special case: Batch inner part
            if (string.Equals($"{transaction[nameof(ITransactionCommon.TransactionType)]}", "Batch", StringComparison.OrdinalIgnoreCase))
            {
                var accounts = transaction.GetBatchSignerAccounts();
                var myAccount = signingFor ?? this.ClassicAddress;
                if (!myAccount.Equals(accounts.Root, StringComparison.OrdinalIgnoreCase)
                    && accounts.Raw.Contains(myAccount, StringComparer.OrdinalIgnoreCase))
                {
                    return SignAsBatchPart(transaction, multisign, signingFor);
                }

                // The sponsor of the OUTER batch (spfSponsorFee) co-signs the batch
                // itself with a regular SponsorSignature - it is not a batch signer
                if (!multisign
                    && transaction.TryGetValue("Sponsor", out var outerSponsorObj)
                    && outerSponsorObj is string outerSponsor
                    && string.Equals(outerSponsor, myAccount, StringComparison.OrdinalIgnoreCase))
                {
                    return SignAsSponsor(transaction);
                }

                if (!multisign)
                {
                    VerifyBatchSubmitter(transaction, signingFor, true);
                }
            }
            // 2) XLS-68: when this wallet is the transaction's Sponsor, route to the
            // sponsor co-signature path automatically (same pattern as the Batch
            // inner-signer routing above). Multisig signing is exempt here because
            // the sponsor's own key is not what signs then: the entries come from the
            // sponsor's SignerList, and SignMulti works their role out for itself.
            if (!multisign
                && transaction.TryGetValue("Sponsor", out var sponsorField)
                && sponsorField is string sponsorAddress
                && string.Equals(sponsorAddress, this.ClassicAddress, StringComparison.Ordinal))
            {
                return SignAsSponsor(transaction);
            }

            // 3) XLS-66: when this wallet is a LoanSet's Counterparty (the borrower),
            // route to the counterparty co-signature path automatically.
            if (!multisign
                && string.Equals($"{transaction[nameof(ITransactionCommon.TransactionType)]}", "LoanSet", StringComparison.OrdinalIgnoreCase)
                && transaction.TryGetValue("Counterparty", out var counterpartyField)
                && counterpartyField is string counterpartyAddress
                && string.Equals(counterpartyAddress, this.ClassicAddress, StringComparison.Ordinal))
            {
                return SignAsLoanCounterparty(transaction);
            }

            if (multisign)
            {
                // The SIGNER's address, not the owner's. Convert an X-address if one arrived.
                var signerAccount = NormalizeClassic(signingFor);
                return SignMulti(transaction, signerAccount);
            }
            else
            {
                Dictionary<string, object> tx = transaction;

                if (tx.ContainsKey("TxnSignature") || tx.ContainsKey("Signers"))
                {
                    throw new ValidationException("txJSON must not contain `TxnSignature` or `Signers` properties");
                }

                JsonObject txToSignAndEncode = JsonNode.Parse(JsonSerializer.Serialize(transaction, XrplJsonOptions.Default))?.AsObject();

                // A present inner co-signature (SponsorSignature / CounterpartySignature)
                // was computed over a preimage that already carried the submitter's
                // SigningPubKey — refuse to silently invalidate it
                if (txToSignAndEncode.ContainsKey("SponsorSignature") || txToSignAndEncode.ContainsKey("CounterpartySignature"))
                {
                    string existingPubKey = txToSignAndEncode["SigningPubKey"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(existingPubKey))
                    {
                        throw new ValidationException("The co-signature was made over a multisig submitter form (empty SigningPubKey); a single main signature would invalidate it. Sign with multisign: true instead.");
                    }
                    if (!string.Equals(existingPubKey, this.PublicKey, StringComparison.Ordinal))
                    {
                        throw new ValidationException("Transaction SigningPubKey does not match this wallet; the co-signer signed a different submitter's preimage.");
                    }
                }

                txToSignAndEncode["SigningPubKey"] = this.PublicKey;

                string signature = ComputeSignature(JsonSerializer.Deserialize<Dictionary<string, object>>(txToSignAndEncode.ToJsonString(), XrplJsonOptions.Default), this.PrivateKey);
                txToSignAndEncode["TxnSignature"] = signature;

                string serialized = XrplBinaryCodec.Encode(txToSignAndEncode);
                return new SignatureResult(serialized, HashLedger.HashSignedTx(serialized));
            }
        }

        private string NormalizeClassic(string? signingFor)
        {
            string signerAccount = signingFor ?? this.ClassicAddress;
            return SignerUtilities.NormalizeClassicAddress(signerAccount);
        }


        private SignatureResult SignMulti(Dictionary<string, object> transaction, string signerAccount)
            => SignMulti(transaction, signerAccount, null);

        /// <summary>
        /// The prefix a multi-signature entry is signed under.
        /// </summary>
        /// <remarks>
        /// Until fixCleanup3_4_0 an entry was section-agnostic: <c>tx.Signers</c>,
        /// <c>SponsorSignature.Signers</c> and <c>CounterpartySignature.Signers</c> all covered
        /// the same bytes, and which section an entry belonged to was settled when the parts were
        /// composed. Since the amendment the section is part of what is signed, so it has to be
        /// known here.
        /// <para>
        /// It is worked out from the transaction wherever the transaction says it. A main
        /// signature that is itself multi-signed leaves <c>SigningPubKey</c> empty, so a
        /// non-empty one on a transaction naming a Sponsor or a Counterparty means the entry can
        /// only belong to that co-signing side. When both a Sponsor and a Counterparty are named
        /// on such a transaction, or when the main signature is multi-signed as well, the
        /// transaction does not say, and the caller has to pass
        /// <paramref name="role"/> through the <see cref="Sign(Dictionary{string, object}, bool, string?, SignatureRole)"/>
        /// overload.
        /// </para>
        /// </remarks>
        private static HashPrefix MultiSigningPrefix(JsonObject txBase, bool coSigningSide, SignatureRole? role)
        {
            bool hasSponsor = txBase["Sponsor"] is not null;
            bool hasCounterparty = txBase["Counterparty"] is not null;

            if (role is SignatureRole.Sponsor)
            {
                if (!hasSponsor)
                    throw new ValidationException("Cannot sign as the sponsor: the transaction names no Sponsor.");
                return HashPrefix.SponsorTransactionMultiSig;
            }

            if (role is SignatureRole.Counterparty)
            {
                if (!hasCounterparty)
                    throw new ValidationException("Cannot sign as the counterparty: the transaction names no Counterparty.");
                return HashPrefix.CounterpartyTransactionMultiSig;
            }

            if (role is SignatureRole.Transaction || !coSigningSide)
                return HashPrefix.TransactionMultiSig;

            if (hasSponsor && hasCounterparty)
            {
                throw new ValidationException(
                    "The transaction names both a Sponsor and a Counterparty, so a multi-signature entry could belong to either. " +
                    "Pass SignatureRole.Sponsor or SignatureRole.Counterparty to Sign.");
            }

            return hasSponsor ? HashPrefix.SponsorTransactionMultiSig : HashPrefix.CounterpartyTransactionMultiSig;
        }

        private SignatureResult SignMulti(Dictionary<string, object> transaction, string signerAccount, SignatureRole? role)
        {
            // txBase is what finally goes out; it accumulates Signers.
            var txBase = JsonNode.Parse(JsonSerializer.Serialize(transaction, XrplJsonOptions.Default))?.AsObject();

            // txForSign is the copy used for the preimage: without Signers or TxnSignature.
            // SigningPubKey is part of the multi-signing preimage (startMultiSigningData
            // serialises the outer transaction whole): for a multi-signed primary signature
            // it must be "", but for a sponsored transaction carrying a SINGLE primary
            // signature the sponsor-side signers sign over the sender's pubkey - so there
            // it is kept as it is.
            var txForSign = txBase.DeepClone().AsObject();
            // Sponsor (XLS-68) and LoanSet Counterparty (XLS-66) share the inner
            // co-signature protocol - both co-sign over the submitter's pubkey
            bool sponsoredSingleMain = (txBase["Sponsor"] is not null || txBase["Counterparty"] is not null) &&
                !string.IsNullOrEmpty(txBase["SigningPubKey"]?.GetValue<string>());
            txForSign["SigningPubKey"] = sponsoredSingleMain
                ? txBase["SigningPubKey"]!.GetValue<string>()
                : "";
            txForSign.Remove("TxnSignature");
            txForSign.Remove("Signers");

            string preimageHex = XrplBinaryCodec.EncodeForMultiSigning(
                txForSign,
                signerAccount,
                MultiSigningPrefix(txBase, sponsoredSingleMain, role));
            var preimage = Xrpl.AddressCodec.Utils.FromHex(preimageHex);

            string sig = Xrpl.Keypairs.XrplKeypairs.Sign(preimage, this.PrivateKey);

            var existingSigners = txBase["Signers"] as JsonArray;
            var signers = existingSigners != null
                ? JsonNode.Parse(existingSigners.ToJsonString())?.AsArray() ?? new JsonArray()
                : new JsonArray();
            signers.Add(new JsonObject
            {
                ["Signer"] = new JsonObject
                {
                    ["Account"] = signerAccount,
                    ["SigningPubKey"] = this.PublicKey,
                    ["TxnSignature"] = sig
                }
            });

            // CRITICAL: sort Signers by the bytes of Account (shared helper)
            txBase["Signers"] = SignerUtilities.DedupeAndSortSigners(signers);
            // Preserve the submitter's pubkey on sponsored single-main parts so the
            // composed transaction keeps the exact serialization the entries signed
            txBase["SigningPubKey"] = sponsoredSingleMain
                ? txBase["SigningPubKey"]!.GetValue<string>()
                : "";
            txBase.Remove("TxnSignature");

            string blob = XrplBinaryCodec.Encode(txBase);
            return new SignatureResult(blob, HashLedger.HashSignedTx(blob));
        }

        public SignatureResult SignAsBatchPart(IBatch transaction, bool multisign, string? signingFor)
        {
            var json = transaction.ToJson();
            var tx = JsonSerializer.Deserialize<Dictionary<string, object>>(json, XrplJsonOptions.Default)
                         ?? throw new ValidationException("Failed to deserialize tx json");
            return SignAsBatchPart(tx, multisign, signingFor);
        }
        public SignatureResult SignAsBatchPart(Dictionary<string, object> transaction, bool multisign, string? signingFor)
        {
            GuardMemos(transaction);
            VerifyBatchSubmitter(transaction, signingFor, false);

            // 1) Normalise the input into a JsonObject
            var outer = JsonNode.Parse(JsonSerializer.Serialize(transaction, XrplJsonOptions.Default))?.AsObject()
                ?? throw new ArgumentException("tx is null");

            // 2) Basic "Batch" checks
            var txType = outer["TransactionType"]?.GetValue<string>();
            if (!string.Equals(txType, "Batch", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("TransactionType must be 'Batch'.");

            var innerTransactions = outer["RawTransactions"]?.AsArray()
                ?? throw new ValidationException("Batch transaction must have RawTransactions (array).");

            if (innerTransactions.Count == 0 || innerTransactions.Count > 8)
                throw new ValidationException("Batch.RawTransactions length must be between 1 and 8.");

            var normalizedInners = new List<JsonObject>(innerTransactions.Count);
            // 3) Walk the inner transactions and validate them against XLS-56.
            // The loop is indexed rather than filtered on `n is JsonObject`. Such an element cannot
            // reach here today - VerifyBatchSubmitter runs GetBatchSignerAccounts first, which
            // refuses it - so this cannot fire; it is kept because a silent skip is the wrong thing
            // to leave behind should that order ever change, and because every other malformed
            // input in this loop is refused rather than dropped.
            for (int i = 0; i < innerTransactions.Count; i++)
            {
                JsonObject item = innerTransactions[i] as JsonObject
                    ?? throw new ValidationException($"Batch.RawTransactions[{i}] must be an object.");

                var innerTx = item["RawTransaction"]?.AsObject()
                              ?? throw new ValidationException("RawTransaction must be an object.");
                // TransactionType is required, and must not be Batch
                var innerType = innerTx["TransactionType"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(innerType))
                    throw new ValidationException("Inner RawTransaction.TransactionType is required.");
                if (string.Equals(innerType, "Batch", StringComparison.OrdinalIgnoreCase))
                    throw new ValidationException("Nested Batch is not allowed.");

                // Forbidden fields
                if (innerTx["TxnSignature"] != null || innerTx["Signers"] != null || innerTx["LastLedgerSequence"] != null)
                    throw new ValidationException("Inner tx must NOT contain TxnSignature, Signers or LastLedgerSequence.");

                // Fee, if present, must be exactly "0"
                if (innerTx["Fee"] != null && innerTx["Fee"]?.GetValue<string>() != "0")
                    throw new ValidationException("Inner tx Fee must be string \"0\" when present.");

                // SigningPubKey, if present, must be exactly ""
                if (innerTx["SigningPubKey"] != null && innerTx["SigningPubKey"]?.GetValue<string>() != "")
                    throw new ValidationException("Inner tx SigningPubKey must be empty string when present.");

                // Normalise for the txid computation (Fee="0", SigningPubKey="", + tfInnerBatchTxn).
                // The normalised form has to go back into `outer`: the blob is encoded from it
                // further down, while the batch preimage commits to the txIDs computed here. Were
                // the two to diverge, the signature would attest to transactions the blob does not
                // carry.
                JsonObject normalizedInner = innerTx.NormalizeInnerTransaction();
                item["RawTransaction"] = normalizedInner;
                normalizedInners.Add(normalizedInner);
            }


            // 4) Compute the txIDs of the normalised inner transactions
            var txIds = normalizedInners.Select(BatchNormalizer.ComputeInnerTxId).ToList();


            // 5) Flags of the outer batch
            uint flags = 0;
            var fTok = outer["Flags"];
            if (fTok != null)
            {
                if (fTok is JsonValue fVal && fVal.TryGetValue<long>(out var fLong)) flags = (uint)fLong;
                else if (fTok is JsonValue fStr && fStr.TryGetValue<string>(out var fStrVal) && uint.TryParse(fStrVal, out var u)) flags = u;
                outer["Flags"] = flags;
            }

            // 5.1) The outer batch's Account and Sequence are part of the batch preimage (BatchV1_1)
            var outerAccount = outer["Account"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(outerAccount))
                throw new ValidationException("Batch transaction must have Account.");

            uint outerSequence;
            var seqTok = outer["Sequence"];
            if (seqTok is JsonValue seqVal && seqVal.TryGetValue<long>(out var seqLong))
            {
                outerSequence = (uint)seqLong;
            }
            else if (seqTok is JsonValue seqStr && seqStr.TryGetValue<string>(out var seqStrVal) && uint.TryParse(seqStrVal, out var s))
            {
                outerSequence = s;
            }
            else if (seqTok == null && outer["TicketSequence"] != null)
            {
                // With tickets, Sequence is required and equals 0 - in the preimage and in the
                // serialised blob alike, or the resulting transaction is malformed.
                outerSequence = 0;
                outer["Sequence"] = 0u;
            }
            else
            {
                throw new ValidationException("Batch transaction must have Sequence (run Autofill first) or TicketSequence.");
            }

            // 6) Signing (both modes build the same batch preimage)
            // batch-preimage = BCH\0 || outerAccount(20) || outerSequence(4) || Flags(4) || Count(4) || txID[0..N-1]
            byte[] preimage = XrplBinaryCodec.EncodeForSigningBatch(outerAccount, outerSequence, flags, txIds);
            if (!multisign)
            {
                var accountFor = NormalizeClassic(signingFor);

                // MULTI-ACCOUNT: the participant's signature over the batch preimage plus that
                // BatchSigner's AccountID (rippled's finishMultiSigningData(batchSigner.Account, msg)).
                var batchSignerAccountId = Xrpl.AddressCodec.XrplCodec.DecodeAccountID(accountFor);
                var signData = new byte[preimage.Length + batchSignerAccountId.Length];
                Buffer.BlockCopy(preimage, 0, signData, 0, preimage.Length);
                Buffer.BlockCopy(batchSignerAccountId, 0, signData, preimage.Length, batchSignerAccountId.Length);

                string signature = XrplKeypairs.Sign(signData, this.PrivateKey);

                var existingBatchSigners = outer["BatchSigners"] as JsonArray;
                var batchSigners = existingBatchSigners != null
                    ? JsonNode.Parse(existingBatchSigners.ToJsonString())?.AsArray() ?? new JsonArray()
                    : new JsonArray();

                var signerObj = new JsonObject
                {
                    ["Account"] = accountFor,
                    ["SigningPubKey"] = this.PublicKey,
                    ["TxnSignature"] = signature
                    // For a multi-signature under THIS SAME account, replace the pair above with "Signers": [ { Signer{Account,SigningPubKey,TxnSignature} }, ... ]
                    // A nested Signer signs a longer preimage than the single form above:
                    // batch-preimage + BatchSigner.Account(20) + that signer's own account ID(20).
                };
                batchSigners.Add(new JsonObject { ["BatchSigner"] = signerObj });

                // Sort BatchSigners and the nested Signers by account id, as XRPL does
                outer["BatchSigners"] = BatchSigningHelper.SortBatchSigners(batchSigners);

                // An outer Batch that carries BatchSigners takes an empty SigningPubKey and NO TxnSignature
                //outer["SigningPubKey"] = "";
                //outer.Remove("TxnSignature");
            }
            else
            {
                // === MULTI-SIG under one BatchSigner.Account via Signers[] ===

                if (string.IsNullOrWhiteSpace(signingFor))
                {
                    throw new ValidationException("Batch inner multisign requires signingFor = owner account (RawTransaction.Account).");
                }

                var ownerAccount = Xrpl.AddressCodec.XrplCodec.IsValidClassicAddress(signingFor)
                    ? signingFor
                    : XrplAddressCodec.XAddressToClassicAddress(signingFor).ClassicAddress;

                // For inner multi-signing (BatchSigner.Signers[]) under XLS-56 / BatchV1_1:
                // data = batch-preimage + BatchSigner.Account(20) + signer's account ID(20)
                // (in rippled: serializeBatch -> addBitString(batchSignerAccount) -> finishMultiSigningData(signerAccount))
                var ownerAccountId = Xrpl.AddressCodec.XrplCodec.DecodeAccountID(ownerAccount);
                var signerAccountId = Xrpl.AddressCodec.XrplCodec.DecodeAccountID(this.ClassicAddress);
                var fullPreimage = new byte[preimage.Length + ownerAccountId.Length + signerAccountId.Length];
                Buffer.BlockCopy(preimage, 0, fullPreimage, 0, preimage.Length);
                Buffer.BlockCopy(ownerAccountId, 0, fullPreimage, preimage.Length, ownerAccountId.Length);
                Buffer.BlockCopy(signerAccountId, 0, fullPreimage, preimage.Length + ownerAccountId.Length, signerAccountId.Length);

                var sig = Xrpl.Keypairs.XrplKeypairs.Sign(fullPreimage, this.PrivateKey);

                // Fetch or create the BatchSigner for ownerAccount
                var existingBatchSigners = outer["BatchSigners"] as JsonArray;
                var batchSigners = existingBatchSigners != null
                    ? JsonNode.Parse(existingBatchSigners.ToJsonString())?.AsArray() ?? new JsonArray()
                    : new JsonArray();
                var bs = BatchSigningHelper.FindOrCreateBatchSigner(batchSigners, ownerAccount);

                // Convert the single form into the multi-signature form, if needed
                if (bs["Signers"] == null)
                {
                    bs.Remove("SigningPubKey");
                    bs.Remove("TxnSignature");
                    bs["Signers"] = new JsonArray();
                }

                // Add the current signer
                var signersArr = bs["Signers"]!.AsArray();
                var signerEntry = new JsonObject
                {
                    ["Signer"] = new JsonObject
                    {
                        ["Account"] = this.ClassicAddress,   // the SIGNER's account specifically, from the local wallet
                        ["SigningPubKey"] = this.PublicKey,
                        ["TxnSignature"] = sig
                    }
                };

                // Guard against duplicates, by the triple Account|SigningPubKey|TxnSignature
                static string KeyOf(JsonObject se)
                {
                    var so = se["Signer"]!.AsObject();
                    return $"{so["Account"]?.GetValue<string>()}|{so["SigningPubKey"]?.GetValue<string>()}|{so["TxnSignature"]?.GetValue<string>()}";
                }
                var seen = new HashSet<string>(
                    signersArr.Where(n => n is JsonObject).Select(n => KeyOf(n!.AsObject())),
                    StringComparer.Ordinal);
                if (seen.Add(KeyOf(signerEntry)))
                    signersArr.Add(signerEntry);

                // Canonical sort of both Signers and BatchSigners
                outer["BatchSigners"] = BatchSigningHelper.SortBatchSigners(batchSigners);

                // Root left unsigned
                //outer["SigningPubKey"] = "";
                //outer.Remove("TxnSignature");
            }
            // 9) Serialisation and hash
            string signedHex = XrplBinaryCodec.Encode(outer);
            string txHash = HashLedger.HashSignedTx(signedHex);
            var txRes = XrplBinaryCodec.Decode(signedHex);

            return new SignatureResult(signedHex, txHash);
        }

        private void VerifyBatchSubmitter(Dictionary<string, object> transaction, string? signingFor, bool allowRoot)
        {
            var status = transaction.GetBatchSignStatus();
            var me = NormalizeClassic(signingFor);

            // 3. Check whether this account is supposed to sign at all
            bool isRoot = status.Root.Equals(me, StringComparison.OrdinalIgnoreCase);
            bool isInner = status.InnerRequired.Contains(me, StringComparer.OrdinalIgnoreCase);

            if (isRoot && !allowRoot)
            {
                // My account is not one of the owners in Batch/RawTransactions
                throw new UnauthorizedAccessException($"root account must submit top level of this batch tx");
            }
            if (!isInner && !isRoot)
            {
                // My account is not one of the owners in Batch/RawTransactions
                throw new UnauthorizedAccessException($"{me} account has no access to submit this batch tx");
            }
            if (isInner)
            {
                // An inner OWNER account already being "signed" does NOT mean another multi-signer cannot be added.
                // Only a repeat of the same signer (this.ClassicAddress) for that owner is refused.
                if (!status.InnerMissing.Contains(me))
                {
                    try
                    {
                        var outer = JsonNode.Parse(JsonSerializer.Serialize(transaction, XrplJsonOptions.Default))?.AsObject();
                        var batchSigners = outer?["BatchSigners"] as JsonArray;

                        if (batchSigners != null)
                        {
                            // Find the BatchSigner for owner = me
                            foreach (var w in batchSigners.Where(n => n is JsonObject).Select(n => n!.AsObject()))
                            {
                                var bs = w["BatchSigner"]?.AsObject() ?? w;
                                var acc = bs["Account"]?.GetValue<string>();
                                if (!string.Equals(acc, me, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var signersArr = bs["Signers"] as JsonArray;
                                if (signersArr == null)
                                    break; // single-sig BatchSigner: a repeat is refused

                                // Check whether this signer has already signed for that owner
                                var signerMe = NormalizeClassic(this.ClassicAddress);
                                var already = signersArr
                                    .Where(n => n is JsonObject).Select(n => n!.AsObject())
                                    .Select(x => x["Signer"]?["Account"]?.GetValue<string>())
                                    .Where(x => !string.IsNullOrWhiteSpace(x))
                                    .Any(x => string.Equals(NormalizeClassic(x!), signerMe, StringComparison.OrdinalIgnoreCase));

                                if (!already)
                                {
                                    // The owner has a BatchSigner already, but this signer has not taken part - allow it to continue
                                    return;
                                }

                                // this signer has already signed for that owner
                                throw new UnauthorizedAccessException($"{me} account already submit this batch tx");
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw;
                    }
                    catch
                    {
                        // If the JSON turns out malformed, failing as before is the better answer
                    }

                    // Old behaviour, when it could not be shown that this is merely a second multi-signer
                    throw new UnauthorizedAccessException($"{me} account already submit this batch tx");
                }
            }

        }

        /// <summary>
        /// Signs a transaction offline.
        /// </summary>
        /// <param name="tx">A transaction to be signed offline.</param>
        /// <param name="multisign">Specify true/false to use multisign or actual address (classic/x-address) to make multisign tx request.</param>
        /// <param name="signingFor"></param>
        /// <returns>A Wallet derived from the seed.</returns>
        public SignatureResult Sign(ITransactionRequest tx, bool multisign = false, string? signingFor = null)
        {
            Dictionary<string, object> txJson = JsonSerializer.Deserialize<Dictionary<string, object>>(tx.ToJson(), XrplJsonOptions.Default);
            return Sign(txJson, multisign, signingFor);
        }

        /// <summary>
        /// Signs a transaction offline in a stated role: the typed counterpart of
        /// <see cref="Sign(Dictionary{string, object}, bool, string?, SignatureRole)"/>, for the one
        /// shape where the transaction does not say which of its signatures this is.
        /// </summary>
        /// <param name="tx">A transaction to be signed offline.</param>
        /// <param name="multisign">True to produce a multi-signature entry rather than a single signature.</param>
        /// <param name="signingFor">The signing account (classic or X-address); this wallet's own by default.</param>
        /// <param name="role">Which signature of the transaction this key is producing.</param>
        public SignatureResult Sign(ITransactionRequest tx, bool multisign, string? signingFor, SignatureRole role)
        {
            Dictionary<string, object> txJson = JsonSerializer.Deserialize<Dictionary<string, object>>(tx.ToJson(), XrplJsonOptions.Default);
            return Sign(txJson, multisign, signingFor, role);
        }

        /// <summary>
        /// Verifies a signed transaction offline.
        /// </summary>
        /// <param name="signedTransaction">A signed transaction (hex string of signTransaction result) to be verified offline.</param>
        /// <returns>Returns true if a signedTransaction is valid.</returns>
        public bool VerifyTransaction(string signedTransaction)
        {
            JsonNode txNode = XrplBinaryCodec.Decode(signedTransaction);
            Dictionary<string, object> txDict = JsonSerializer.Deserialize<Dictionary<string, object>>(txNode.ToJsonString(), XrplJsonOptions.Default);
            string messageHex = XrplBinaryCodec.EncodeForSigning(txDict);
            string signature = txNode["TxnSignature"]?.GetValue<string>();
            return XrplKeypairs.Verify(messageHex.FromHex(), signature, this.PublicKey);
        }

        public string GetXAddress(uint tag, bool isTestnet = false)
        {
            return XrplAddressCodec.ClassicAddressToXAddress(this.ClassicAddress, tag, isTestnet);
        }

        public string ComputeSignature(Dictionary<string, object> transaction, string privateKey, string? signAs = null)
        {
            string encoded = XrplBinaryCodec.EncodeForSigning(transaction);
            return XrplKeypairs.Sign(AddressCodec.Utils.FromHex(encoded), privateKey);
        }

        /// <summary>
        /// Signs a LoanSet transaction as the borrower (Counterparty).
        /// Computes the signing preimage and adds CounterpartySignature (inner STObject
        /// with this wallet's SigningPubKey and TxnSignature).
        ///
        /// Used in V2 (parallel) and V3 (sequential) signing flows:
        ///
        /// <b>V3 (sequential) — borrower signs first, passes to broker:</b>
        /// <code>
        /// var withCounterparty = borrowerWallet.SignAsLoanCounterparty(preparedTx);
        /// var final = LoanSigningHelper.BrokerSign(withCounterparty.TxBlob, brokerWallet);
        /// await client.SubmitRequest(final.TxBlob);
        /// </code>
        /// Note the blob, not <c>GetTx()</c>: no request model declares
        /// <c>CounterpartySignature</c>, so decoding into a typed transaction and signing that
        /// would produce a blob without the co-signature. <c>BrokerSign</c> stays at the blob
        /// level, stripping the co-signature to compute the preimage and restoring it afterwards.
        /// <see cref="SignatureResult.GetTx"/> now refuses such a blob rather than losing it.
        ///
        /// <b>V2 (parallel) — both sign independently, then combine:</b>
        /// <code>
        /// var counterpartySig = borrowerWallet.SignAsLoanCounterparty(preparedTx);
        /// var brokerSig = brokerWallet.Sign(preparedTx);
        /// var combined = LoanSigningHelper.CombineLoanSignatures(brokerSig.TxBlob, counterpartySig.TxBlob);
        /// </code>
        /// </summary>
        /// <param name="transaction">Prepared LoanSet transaction (must have SigningPubKey set to broker's key).</param>
        /// <returns>SignatureResult with CounterpartySignature added (no TxnSignature yet).</returns>
        public SignatureResult SignAsLoanCounterparty(ITransactionRequest transaction)
        {
            Dictionary<string, object> txDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                transaction.ToJson(), XrplJsonOptions.Default);
            return SignAsLoanCounterparty(txDict);
        }

        /// <summary>
        /// Signs a LoanSet transaction as the borrower (Counterparty).
        /// </summary>
        public SignatureResult SignAsLoanCounterparty(Dictionary<string, object> transaction)
        {
            GuardMemos(transaction);

            JsonObject tx = JsonNode.Parse(JsonSerializer.Serialize(transaction, XrplJsonOptions.Default))?.AsObject()
                ?? throw new ValidationException("Failed to serialize transaction to JSON");

            string txType = tx["TransactionType"]?.GetValue<string>();
            if (!string.Equals(txType, "LoanSet", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException($"SignAsLoanCounterparty requires TransactionType=LoanSet, got: {txType}");

            // Verify broker's SigningPubKey is present - it is part of what the counterparty signs
            string brokerSigningPubKey = tx["SigningPubKey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(brokerSigningPubKey))
                throw new ValidationException("LoanSet must include broker SigningPubKey before counterparty signing.");

            // Remove existing signatures but keep SigningPubKey (broker's)
            tx.Remove("CounterpartySignature");
            tx.Remove("TxnSignature");

            // The counterparty's preimage: the same transaction as the broker's, under its own prefix
            byte[] signingBytes = LoanSigningHelper.GetSigningPreimage(tx);

            // Sign the preimage with this wallet's key
            string sig = XrplKeypairs.Sign(signingBytes, this.PrivateKey);

            // Add CounterpartySignature
            tx["CounterpartySignature"] = SignatureObject.Single(this.PublicKey, sig).ToJsonObject();

            // Encode (without broker's TxnSignature — partially signed)
            string txBlob = XrplBinaryCodec.Encode(tx);
            string txHash = HashLedger.HashSignedTx(txBlob);
            return new SignatureResult(txBlob, txHash);
        }

        /// <summary>
        /// Signs a sponsored transaction as the sponsor (XLS-68).
        /// Computes the signing preimage and adds SponsorSignature (inner STObject
        /// with this wallet's SigningPubKey and TxnSignature over the sponsor preimage, which
        /// since fixCleanup3_4_0 is not the one the submitter signs). The transaction must carry
        /// Sponsor = this wallet's address.
        ///
        /// <b>V3 (sequential) — sponsor signs first, passes to submitter:</b>
        /// <code>
        /// var withSponsor = sponsorWallet.SignAsSponsor(preparedTx);
        /// var final = SponsorSigningHelper.SubmitterSign(withSponsor.TxBlob, submitterWallet);
        /// </code>
        ///
        /// <b>V2 (parallel) — both sign independently, then combine:</b>
        /// <code>
        /// var sponsorSig = sponsorWallet.SignAsSponsor(preparedTx);
        /// var submitterSig = submitterWallet.Sign(preparedTx);
        /// var combined = SponsorSigningHelper.CombineSponsorSignatures(submitterSig.TxBlob, sponsorSig.TxBlob);
        /// </code>
        /// </summary>
        /// <param name="transaction">Prepared transaction with Sponsor/SponsorFlags and the submitter's SigningPubKey set.</param>
        /// <returns>SignatureResult with SponsorSignature added (no TxnSignature yet).</returns>
        public SignatureResult SignAsSponsor(ITransactionRequest transaction)
        {
            Dictionary<string, object> txDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                transaction.ToJson(), XrplJsonOptions.Default);
            return SignAsSponsor(txDict);
        }

        /// <summary>
        /// Signs a sponsored transaction as the sponsor (XLS-68).
        /// </summary>
        public SignatureResult SignAsSponsor(Dictionary<string, object> transaction)
        {
            GuardMemos(transaction);

            JsonObject tx = JsonNode.Parse(JsonSerializer.Serialize(transaction, XrplJsonOptions.Default))?.AsObject()
                ?? throw new ValidationException("Failed to serialize transaction to JSON");

            SponsorSigningHelper.VerifySponsorMatches(tx, this);

            // The submitter's SigningPubKey must be present — the sponsor signs the same
            // preimage. An empty value is legal: it is the protocol marker of a
            // multisig main signature (tx.Signers), and the sponsor co-signs over it.
            if (tx["SigningPubKey"] is null)
                throw new ValidationException("Sponsored transaction must include the submitter SigningPubKey (empty for a multisig submitter) before sponsor signing.");

            tx.Remove("SponsorSignature");
            tx.Remove("TxnSignature");

            byte[] signingBytes = SponsorSigningHelper.GetSigningPreimage(tx);
            string sig = XrplKeypairs.Sign(signingBytes, this.PrivateKey);

            tx["SponsorSignature"] = SignatureObject.Single(this.PublicKey, sig).ToJsonObject();

            string txBlob = XrplBinaryCodec.Encode(tx);
            string txHash = HashLedger.HashSignedTx(txBlob);
            return new SignatureResult(txBlob, txHash);
        }

        /// <summary>
        /// Merges several partially signed Batch transactions (txBlob in hex) into one final blob.
        /// Conditions:
        ///  - Every input blob must be a Batch and carry an IDENTICAL body, apart from SigningPubKey/TxnSignature/BatchSigners/Signers.
        ///  - BatchSigners and root Signers are both merged. Where the inputs carry no root Signers, an identical outer signature is carried over, whether or not there are BatchSigners.
        ///  - BatchSigners are sorted by Account; the nested Signers by Signer.Account.
        /// </summary>
        public static SignatureResult CombineBatchSigners(params string[] txBlobs)
        {
            if (txBlobs == null || txBlobs.Length == 0)
                throw new ArgumentException("No tx blobs provided.");
            if (txBlobs.Length == 1)
            {
                var single = txBlobs[0];
                return new SignatureResult(single, HashLedger.HashSignedTx(single));
            }

            // Canonicalise the body: drop *every* signature (outer + inner + multisign)
            static JsonObject Canonicalize(JsonObject x)
            {
                var c = x.DeepClone().AsObject();
                c.Remove("TxnSignature");
                c.Remove("SigningPubKey");
                c.Remove("BatchSigners");
                c.Remove("Signers");
                return c;
            }

            // ---------- 1) decode + sanity ----------

            var decoded = txBlobs.Select(DecodeToObject).ToList();
            foreach (var o in decoded)
            {
                var tt = o["TransactionType"]?.GetValue<string>();
                if (!string.Equals(tt, "Batch", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("All blobs must be Batch transactions.");
            }

            // ---------- 2) check that the bodies are identical, signatures aside ----------

            var baseCanon = Canonicalize(decoded[0]);
            for (int i = 1; i < decoded.Count; i++)
            {
                if (!JsonNode.DeepEquals(baseCanon, Canonicalize(decoded[i])))
                    throw new InvalidOperationException("Incompatible Batch bodies. All inputs must have identical non-signing fields.");
            }

            // ---------- 3) base for the result ----------

            var combined = decoded[0].DeepClone().AsObject();
            combined.Remove("BatchSigners");
            combined.Remove("Signers");
            combined.Remove("TxnSignature");
            combined.Remove("SigningPubKey");

            // ---------- 4) collect and merge BatchSigners (inner signatures) ----------

            var byAccount = new Dictionary<string, JsonObject>(StringComparer.Ordinal); // Account -> BatchSigner object

            foreach (var outer in decoded)
            {
                var arr = outer["BatchSigners"] as JsonArray;
                if (arr == null) continue;

                foreach (var w in arr.Where(n => n is JsonObject).Select(n => n!.AsObject()))
                {
                    var bs = w["BatchSigner"]?.AsObject() ?? w;
                    var accRaw = bs["Account"]?.GetValue<string>() ?? throw new InvalidOperationException("BatchSigner missing Account.");

                    var acc = SignerUtilities.NormalizeClassicAddress(accRaw);
                    bs["Account"] = acc; // normalise

                    if (!byAccount.TryGetValue(acc, out var existing))
                    {
                        byAccount[acc] = bs.DeepClone().AsObject();
                    }
                    else
                    {
                        // A BatchSigner for this account exists already -> merge
                        BatchSigningHelper.MergeBatchSigner(existing, bs);
                    }
                }
            }

            // Each BatchSigner may itself carry a multi-signature (Signers[])
            // Dedupe those inner Signers and sort them by AccountID
            foreach (var kvp in byAccount.ToList())
            {
                var bs = kvp.Value;
                var signersArr = bs["Signers"] as JsonArray;
                if (signersArr == null || signersArr.Count == 0)
                    continue;

                bs["Signers"] = SignerUtilities.DedupeAndSortSigners(signersArr);
            }

            // Gather into the wrapping array
            var mergedBatchSignersArr = new JsonArray(byAccount.Values.Select(v => (JsonNode)new JsonObject { ["BatchSigner"] = v }).ToArray());
            combined["BatchSigners"] = BatchSigningHelper.SortBatchSigners(mergedBatchSignersArr);

            // ---------- 5) collect and merge the root Signers (top-level multisign) ----------

            var allRootSigners = new List<JsonNode>();

            foreach (var outer in decoded)
            {
                if (outer["Signers"] is not JsonArray arr) continue;
                foreach (var it in arr)
                {
                    if (it is JsonObject itObj)
                        allRootSigners.Add(itObj.DeepClone());
                }
            }

            if (allRootSigners.Count > 0)
            {
                // dedupe and sort root Signers using helper
                var sortedRootSigners = SignerUtilities.DedupeAndSortSigners(new JsonArray(allRootSigners.ToArray()));
                combined["Signers"] = sortedRootSigners;

                // The XRPL rule for multisign: SigningPubKey = "", no TxnSignature
                combined["SigningPubKey"] = "";
                combined.Remove("TxnSignature");
            }
            else
            {
                // 6) Outer signature: blobs carrying neither TxnSignature nor SigningPubKey take
                // no part; the first pair found is kept, and a later pair that differs from it is
                // a conflict. Applies whether or not BatchSigners are present.
                string? outSig = null, outPub = null;
                bool gotOuter = false;

                foreach (var o in decoded)
                {
                    var s = o["TxnSignature"]?.GetValue<string>();
                    var p = o["SigningPubKey"]?.GetValue<string>();

                    if (string.IsNullOrEmpty(s) && string.IsNullOrEmpty(p))
                        continue;

                    if (!gotOuter)
                    {
                        gotOuter = true;
                        outSig = s;
                        outPub = p;
                        continue;
                    }

                    if (!string.Equals(outSig, s, StringComparison.Ordinal) ||
                        !string.Equals(outPub, p, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Conflicting outer signatures across inputs.");
                    }
                }

                if (gotOuter)
                {
                    if (!string.IsNullOrEmpty(outPub))
                        combined["SigningPubKey"] = outPub!;
                    if (!string.IsNullOrEmpty(outSig))
                        combined["TxnSignature"] = outSig!;
                }
                else
                {
                    combined.Remove("SigningPubKey");
                    combined.Remove("TxnSignature");
                }
            }

            // ---------- 7) encode + hash ----------

            string signedHex = XrplBinaryCodec.Encode(combined);
            string txHash = HashLedger.HashSignedTx(signedHex);
            return new SignatureResult(signedHex, txHash);
        }

        /// <summary>Decodes a hex blob into a JsonObject.</summary>
        private static JsonObject DecodeToObject(string blobHex)
        {
            JsonNode dec = XrplBinaryCodec.Decode(blobHex);
            return dec.AsObject();
        }
    }
}