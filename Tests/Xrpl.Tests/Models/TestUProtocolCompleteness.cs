using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.BinaryCodec;
using Xrpl.Client.Json;
using Xrpl.Models.Common;
using Xrpl.Models.Ledger;
using Xrpl.Models.Transactions;

using static Xrpl.Models.Common.Common;

using TxFormat = Xrpl.Models.Transaction.TxFormat;
using Xrpl.Wallet;

namespace Xrpl.Tests.Models.Tests
{
    /// <summary>
    /// Pins the protocol-completeness pass driven by rippled server_definitions:
    /// newly added transaction/ledger-object fields round-trip, and the
    /// NFTokenModify validation dispatch bug stays fixed.
    /// </summary>
    [TestClass]
    public class TestUProtocolCompleteness
    {
        private static string Account1;
        private static string Account2;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            Account1 = XrplWallet.Generate().ClassicAddress;
            Account2 = XrplWallet.Generate().ClassicAddress;
        }

        [TestMethod]
        public async Task TestUNFTokenModify_DispatchesToOwnValidator()
        {
            // Pre-fix the dispatcher routed NFTokenModify to ValidateNFTokenMint,
            // which rejects a valid Modify (no NFTokenTaxon present)
            Dictionary<string, object> tx = new()
            {
                ["TransactionType"] = "NFTokenModify",
                ["Account"] = Account1,
                ["NFTokenID"] = new string('A', 64),
            };
            await Validation.Validate(tx);
        }

        [TestMethod]
        public void TestUNFTokenMint_MintOfferFields_RoundTrip()
        {
            System.DateTime rippleEpoch = new System.DateTime(2000, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            NFTokenMint mint = new NFTokenMint
            {
                Account = Account1,
                NFTokenTaxon = 7,
                Amount = new Currency { ValueAsXrp = 2m },
                Destination = Account2,
                Expiration = rippleEpoch.AddSeconds(800000000),
                Sequence = 1,
                Fee = new Currency { Value = "12" },
                SigningPublicKey = "",
            };
            JsonObject json = JsonNode.Parse(mint.ToJson())!.AsObject();
            string blob = XrplBinaryCodec.Encode(json);
            JsonObject decoded = XrplBinaryCodec.Decode(blob).AsObject();

            Assert.AreEqual("2000000", decoded["Amount"]!.GetValue<string>());
            Assert.AreEqual(Account2, decoded["Destination"]!.GetValue<string>());
            Assert.AreEqual(800000000u, decoded["Expiration"]!.GetValue<uint>());
        }

        [TestMethod]
        public void TestUMPTokenIssuanceSet_DynamicFields_RoundTrip()
        {
            MPTokenIssuanceSet set = new MPTokenIssuanceSet
            {
                Account = Account1,
                MPTokenIssuanceID = "00000001A407AF5856CCF3C42619DAA925813FC955C72983",
                Flags = MPTokenIssuanceSetFlags.tfMPTSetCanLock | MPTokenIssuanceSetFlags.tfMPTSetRequireAuth,
                ImmutableFlags = MPTokenIssuanceImmutableFlags.tifMPTCanTrade | MPTokenIssuanceImmutableFlags.tifMPTMetadata,
                TransferFee = 250,
                MPTokenMetadata = "DEADBEEF",
                DomainID = new string('B', 64),
                IssuerEncryptionKey = new string('C', 66),
                AuditorEncryptionKey = new string('D', 66),
                Sequence = 1,
                Fee = new Currency { Value = "12" },
                SigningPublicKey = "",
            };
            JsonObject json = JsonNode.Parse(set.ToJson())!.AsObject();
            string blob = XrplBinaryCodec.Encode(json);
            JsonObject decoded = XrplBinaryCodec.Decode(blob).AsObject();

            Assert.AreEqual(
                (uint)(MPTokenIssuanceSetFlags.tfMPTSetCanLock | MPTokenIssuanceSetFlags.tfMPTSetRequireAuth),
                decoded["Flags"]!.GetValue<uint>());
            Assert.AreEqual(
                (uint)(MPTokenIssuanceImmutableFlags.tifMPTCanTrade | MPTokenIssuanceImmutableFlags.tifMPTMetadata),
                decoded["ImmutableFlags"]!.GetValue<uint>());
            Assert.AreEqual(250u, decoded["TransferFee"]!.GetValue<uint>());
            Assert.AreEqual("DEADBEEF", decoded["MPTokenMetadata"]!.GetValue<string>());
            Assert.AreEqual(new string('B', 64), decoded["DomainID"]!.GetValue<string>());
            Assert.AreEqual(new string('C', 66), decoded["IssuerEncryptionKey"]!.GetValue<string>());
            Assert.AreEqual(new string('D', 66), decoded["AuditorEncryptionKey"]!.GetValue<string>());
        }

        [TestMethod]
        public void TestUSetFee_XRPFeesFields_RoundTrip()
        {
            JsonObject json = JsonNode.Parse($@"{{
                ""TransactionType"": ""SetFee"",
                ""Account"": ""rrrrrrrrrrrrrrrrrrrrrhoLvTp"",
                ""Fee"": ""0"",
                ""Sequence"": 0,
                ""SigningPubKey"": """",
                ""LedgerSequence"": 123456,
                ""BaseFeeDrops"": ""10"",
                ""ReserveBaseDrops"": ""1000000"",
                ""ReserveIncrementDrops"": ""200000""
            }}")!.AsObject();
            string blob = XrplBinaryCodec.Encode(json);
            JsonObject decoded = XrplBinaryCodec.Decode(blob).AsObject();

            Assert.AreEqual("10", decoded["BaseFeeDrops"]!.GetValue<string>());
            Assert.AreEqual("1000000", decoded["ReserveBaseDrops"]!.GetValue<string>());
            Assert.AreEqual("200000", decoded["ReserveIncrementDrops"]!.GetValue<string>());

            TxFormat setFeeFormat = TxFormat.Formats[BinaryCodec.Types.TransactionType.SetFee];
            Assert.IsTrue(setFeeFormat.ContainsKey(BinaryCodec.Enums.Field.BaseFeeDrops));
            Assert.IsTrue(setFeeFormat.ContainsKey(BinaryCodec.Enums.Field.ReserveBaseDrops));
            Assert.IsTrue(setFeeFormat.ContainsKey(BinaryCodec.Enums.Field.ReserveIncrementDrops));
            Assert.IsTrue(TxFormat.Formats[BinaryCodec.Types.TransactionType.UNLModify]
                .ContainsKey(BinaryCodec.Enums.Field.UNLModifyDisabling));
        }

        [TestMethod]
        public void TestUAMMDeposit_TradingFee_RoundTrip()
        {
            JsonObject json = JsonNode.Parse($@"{{
                ""TransactionType"": ""AMMDeposit"",
                ""Account"": ""{Account1}"",
                ""Fee"": ""12"",
                ""Sequence"": 1,
                ""SigningPubKey"": """",
                ""Asset"": {{ ""currency"": ""XRP"" }},
                ""Asset2"": {{ ""currency"": ""USD"", ""issuer"": ""{Account2}"" }},
                ""TradingFee"": 600
            }}")!.AsObject();
            string blob = XrplBinaryCodec.Encode(json);
            JsonObject decoded = XrplBinaryCodec.Decode(blob).AsObject();

            Assert.AreEqual(600u, decoded["TradingFee"]!.GetValue<uint>());
            Assert.IsTrue(TxFormat.Formats[BinaryCodec.Types.TransactionType.AMMDeposit]
                .ContainsKey(BinaryCodec.Enums.Field.TradingFee));
        }

        [TestMethod]
        public void TestUVaultDelete_MemoData_RoundTrip()
        {
            JsonObject json = JsonNode.Parse($@"{{
                ""TransactionType"": ""VaultDelete"",
                ""Account"": ""{Account1}"",
                ""Fee"": ""12"",
                ""Sequence"": 1,
                ""SigningPubKey"": """",
                ""VaultID"": ""{new string('E', 64)}"",
                ""MemoData"": ""CAFEBABE""
            }}")!.AsObject();
            string blob = XrplBinaryCodec.Encode(json);
            JsonObject decoded = XrplBinaryCodec.Decode(blob).AsObject();

            Assert.AreEqual("CAFEBABE", decoded["MemoData"]!.GetValue<string>());
            Assert.IsTrue(TxFormat.Formats[BinaryCodec.Types.TransactionType.VaultDelete]
                .ContainsKey(BinaryCodec.Enums.Field.MemoData));
        }

        [TestMethod]
        public void TestUUint64_FieldContext_RoundTrip()
        {
            // Digit-only value of a hex-semantics UInt64 field must be parsed as hex:
            // pre-fix "0000000000000012" was parsed as decimal 12 (0x0C) and the
            // round-trip silently corrupted the value.
            JsonObject hexField = JsonNode.Parse(@"{""OwnerNode"": ""0000000000000012""}")!.AsObject();
            JsonObject decodedHex = XrplBinaryCodec.Decode(XrplBinaryCodec.Encode(hexField)).AsObject();
            Assert.AreEqual("0000000000000012", decodedHex["OwnerNode"]!.GetValue<string>());

            // kSmdBaseTen fields keep decimal-string semantics in both directions
            JsonObject baseTenField = JsonNode.Parse(@"{""MaximumAmount"": ""18446744073709551615""}")!.AsObject();
            JsonObject decodedBaseTen = XrplBinaryCodec.Decode(XrplBinaryCodec.Encode(baseTenField)).AsObject();
            Assert.AreEqual("18446744073709551615", decodedBaseTen["MaximumAmount"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task TestUMPTokenIssuanceSet_PreflightRules()
        {
            // rippled MPTokenIssuanceSet::preflight rules pinned client-side
            Dictionary<string, object> tx = new()
            {
                ["TransactionType"] = "MPTokenIssuanceSet",
                ["Account"] = Account1,
                ["MPTokenIssuanceID"] = "00000001A407AF5856CCF3C42619DAA925813FC955C72983",
            };

            // A non-numeric Flags value must report as ValidationException like every other
            // malformed field here, not as a raw conversion exception callers do not catch.
            tx["Flags"] = "not-a-number";
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateMPTokenIssuanceSet(tx));
            tx.Remove("Flags");

            // ImmutableFlags: zero and out-of-mask values are temINVALID_FLAG
            tx["ImmutableFlags"] = 0u;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateMPTokenIssuanceSet(tx));
            tx["ImmutableFlags"] = 0x1u; // outside tif* mask (0x2..0x80, 0x10000, 0x20000)
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateMPTokenIssuanceSet(tx));

            tx["ImmutableFlags"] = (uint)MPTokenIssuanceImmutableFlags.tifMPTCanHoldConfidentialBalance;
            await Validation.ValidateMPTokenIssuanceSet(tx);

            // Non-zero TransferFee combined with enabling confidential balances is temBAD_TRANSFER_FEE.
            // Since 3.3.0 the capability is enabled through a tf* flag, not through a separate field.
            tx.Remove("ImmutableFlags");
            tx["Flags"] = (uint)MPTokenIssuanceSetFlags.tfMPTSetCanHoldConfidentialBalance;
            tx["TransferFee"] = 10u;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateMPTokenIssuanceSet(tx));

            tx["TransferFee"] = 0u;
            await Validation.ValidateMPTokenIssuanceSet(tx);
        }

        [TestMethod]
        public async Task TestUMPTokenIssuanceCreate_ImmutableFlagsMask()
        {
            Dictionary<string, object> tx = new()
            {
                ["TransactionType"] = "MPTokenIssuanceCreate",
                ["Account"] = Account1,
            };

            tx["ImmutableFlags"] = 0u;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateMPTokenIssuanceCreate(tx));
            tx["ImmutableFlags"] = 0x100u; // outside tif* mask
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateMPTokenIssuanceCreate(tx));

            tx["ImmutableFlags"] = (uint)(MPTokenIssuanceImmutableFlags.tifMPTMetadata | MPTokenIssuanceImmutableFlags.tifMPTTransferFee);
            await Validation.ValidateMPTokenIssuanceCreate(tx);

            tx["DomainID"] = 12345;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateMPTokenIssuanceCreate(tx));
        }

        /// <summary>
        /// The fields a format declares on top of the common set shared by every transaction.
        /// The common set comes from <see cref="RippledTransactionFormats.CommonFields"/>, which
        /// <c>TestUTxFormatConformance</c> also reads, so the two conformance surfaces stay in step.
        /// </summary>
        private static Dictionary<BinaryCodec.Enums.Field, TxFormat.Requirement> TypeSpecificFields(
            BinaryCodec.Types.TransactionType transactionType)
        {
            HashSet<BinaryCodec.Enums.Field> common = RippledTransactionFormats.CommonFields();
            return TxFormat.Formats[transactionType]
                .Where(entry => !common.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
        }

        private static void AssertFormat(
            BinaryCodec.Types.TransactionType transactionType,
            Dictionary<BinaryCodec.Enums.Field, TxFormat.Requirement> expected)
        {
            Dictionary<BinaryCodec.Enums.Field, TxFormat.Requirement> actual = TypeSpecificFields(transactionType);
            CollectionAssert.AreEquivalent(
                expected.Keys.ToArray(),
                actual.Keys.ToArray(),
                $"{transactionType} declares the wrong field set");

            foreach (KeyValuePair<BinaryCodec.Enums.Field, TxFormat.Requirement> field in expected)
            {
                Assert.AreEqual(field.Value, actual[field.Key], $"{transactionType}.{field.Key} requirement");
            }
        }

        [TestMethod]
        public void TestUCheckTransactions_DeclareTheirOwnFields()
        {
            // All three Check formats used to be a verbatim copy of PaymentChannelClaim
            // (Channel/Amount/Balance/Signature/PublicKey). Field sets per rippled
            // include/xrpl/protocol/detail/transactions.macro.
            AssertFormat(BinaryCodec.Types.TransactionType.CheckCreate, new Dictionary<BinaryCodec.Enums.Field, TxFormat.Requirement>
            {
                [BinaryCodec.Enums.Field.Destination] = TxFormat.Requirement.Required,
                [BinaryCodec.Enums.Field.SendMax] = TxFormat.Requirement.Required,
                [BinaryCodec.Enums.Field.Expiration] = TxFormat.Requirement.Optional,
                [BinaryCodec.Enums.Field.DestinationTag] = TxFormat.Requirement.Optional,
                [BinaryCodec.Enums.Field.InvoiceID] = TxFormat.Requirement.Optional,
            });

            AssertFormat(BinaryCodec.Types.TransactionType.CheckCash, new Dictionary<BinaryCodec.Enums.Field, TxFormat.Requirement>
            {
                [BinaryCodec.Enums.Field.CheckID] = TxFormat.Requirement.Required,
                [BinaryCodec.Enums.Field.Amount] = TxFormat.Requirement.Optional,
                [BinaryCodec.Enums.Field.DeliverMin] = TxFormat.Requirement.Optional,
            });

            AssertFormat(BinaryCodec.Types.TransactionType.CheckCancel, new Dictionary<BinaryCodec.Enums.Field, TxFormat.Requirement>
            {
                [BinaryCodec.Enums.Field.CheckID] = TxFormat.Requirement.Required,
            });
        }

        [TestMethod]
        public void TestUSignerListSet_DeclaresNoTopLevelWalletLocator()
        {
            // sfWalletLocator is a member of the nested SignerEntry object, not a
            // top-level SignerListSet field: rippled declares only quorum and entries.
            AssertFormat(BinaryCodec.Types.TransactionType.SignerListSet, new Dictionary<BinaryCodec.Enums.Field, TxFormat.Requirement>
            {
                [BinaryCodec.Enums.Field.SignerQuorum] = TxFormat.Requirement.Required,
                [BinaryCodec.Enums.Field.SignerEntries] = TxFormat.Requirement.Optional,
            });
        }

        [TestMethod]
        public void TestULORippleState_SponsorFields_Deserialize()
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["LedgerEntryType"] = "RippleState",
                ["Flags"] = 65536,
                ["Balance"] = new Dictionary<string, object> { ["currency"] = "USD", ["issuer"] = Account1, ["value"] = "5" },
                ["HighLimit"] = new Dictionary<string, object> { ["currency"] = "USD", ["issuer"] = Account1, ["value"] = "10" },
                ["LowLimit"] = new Dictionary<string, object> { ["currency"] = "USD", ["issuer"] = Account2, ["value"] = "0" },
                ["HighSponsor"] = Account1,
                ["LowSponsor"] = Account2,
            });
            LORippleState state = JsonSerializer.Deserialize<LORippleState>(json, XrplJsonOptions.Default);
            Assert.AreEqual(Account1, state.HighSponsor);
            Assert.AreEqual(Account2, state.LowSponsor);
        }

        [TestMethod]
        public void TestULOVault_LEVersion_Deserialize()
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["LedgerEntryType"] = "Vault",
                ["Account"] = Account1,
                ["Owner"] = Account2,
                ["ShareMPTID"] = "00000001A407AF5856CCF3C42619DAA925813FC955C72983",
                ["WithdrawalPolicy"] = 1,
                ["Scale"] = 6,
                ["LEVersion"] = (uint)VaultVersion.CashBasis,
            });
            LOVault vault = JsonSerializer.Deserialize<LOVault>(json, XrplJsonOptions.Default);
            Assert.AreEqual((uint)VaultVersion.CashBasis, vault.LEVersion);

            // A vault created before cash-basis accounting carries no LEVersion at all;
            // rippled resolves that absence as VaultVersion.Legacy rather than an error
            string legacy = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["LedgerEntryType"] = "Vault",
                ["Account"] = Account1,
                ["Owner"] = Account2,
            });
            Assert.IsNull(JsonSerializer.Deserialize<LOVault>(legacy, XrplJsonOptions.Default).LEVersion);
        }

        [TestMethod]
        public void TestULEVersion_BinaryRoundTrip()
        {
            // The field only travels if definitions.json knows it — this fails with an
            // encoding error, not an assertion, when the entry is missing.
            // Parsed from text rather than built from int literals: that is the shape a
            // node response arrives in, and Uint8.FromJson takes a byte, not an Int32
            JsonObject json = JsonNode.Parse("""{"LEVersion":1,"Scale":6}""")!.AsObject();
            string blob = XrplBinaryCodec.Encode(json);
            JsonObject decoded = XrplBinaryCodec.Decode(blob).AsObject();

            Assert.AreEqual(1u, decoded["LEVersion"]!.GetValue<uint>());
            Assert.AreEqual(6u, decoded["Scale"]!.GetValue<uint>());
        }
        private static readonly System.DateTime RippleEpoch = new System.DateTime(2000, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

        [TestMethod]
        public void TestUVaultCreate_ClosedEndedFields_RoundTrip()
        {
            // rippled #7921 (LendingProtocolV1_1): VaultKind/SubscriptionDate/RedemptionDate on VaultCreate
            VaultCreate create = new VaultCreate
            {
                Account = Account1,
                Asset = new IssuedCurrency { Currency = "XRP" },
                VaultKind = (uint)Xrpl.Models.Ledger.VaultKind.ClosedEnded,
                SubscriptionDate = RippleEpoch.AddSeconds(800000000),
                RedemptionDate = RippleEpoch.AddSeconds(800086400),
                Sequence = 1,
                Fee = new Currency { Value = "12" },
                SigningPublicKey = "",
            };
            JsonObject json = JsonNode.Parse(create.ToJson())!.AsObject();
            Assert.AreEqual(800000000u, json["SubscriptionDate"]!.GetValue<uint>(), "dates travel as seconds since the Ripple Epoch");

            string blob = XrplBinaryCodec.Encode(json);
            JsonObject decoded = XrplBinaryCodec.Decode(blob).AsObject();

            Assert.AreEqual(1u, decoded["VaultKind"]!.GetValue<uint>());
            Assert.AreEqual(800000000u, decoded["SubscriptionDate"]!.GetValue<uint>());
            Assert.AreEqual(800086400u, decoded["RedemptionDate"]!.GetValue<uint>());

            TxFormat format = TxFormat.Formats[BinaryCodec.Types.TransactionType.VaultCreate];
            Assert.AreEqual(TxFormat.Requirement.Optional, format[BinaryCodec.Enums.Field.VaultKind]);
            Assert.AreEqual(TxFormat.Requirement.Optional, format[BinaryCodec.Enums.Field.SubscriptionDate]);
            Assert.AreEqual(TxFormat.Requirement.Optional, format[BinaryCodec.Enums.Field.RedemptionDate]);
        }

        [TestMethod]
        public async Task TestUVaultCreate_ClosedEndedPreflightRules()
        {
            // rippled VaultCreate::preflight rules for closed-ended vaults pinned client-side
            Dictionary<string, object> tx = new()
            {
                ["TransactionType"] = "VaultCreate",
                ["Account"] = Account1,
                ["Asset"] = new Dictionary<string, object> { ["currency"] = "XRP" },
            };
            await Validation.ValidateVaultCreate(tx);

            // an unknown kind is temMALFORMED
            tx["VaultKind"] = 2u;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateVaultCreate(tx));

            // the dates belong to closed-ended vaults only
            tx["VaultKind"] = (uint)Xrpl.Models.Ledger.VaultKind.OpenEnded;
            tx["SubscriptionDate"] = 800000000u;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateVaultCreate(tx));

            tx.Remove("VaultKind");
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateVaultCreate(tx));

            // a closed-ended vault needs both dates
            tx["VaultKind"] = (uint)Xrpl.Models.Ledger.VaultKind.ClosedEnded;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateVaultCreate(tx));

            // kMinInvestmentPeriod <= gap < kMaxInvestmentPeriod
            tx["RedemptionDate"] = 800000059u;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateVaultCreate(tx));

            tx["RedemptionDate"] = 800000060u;
            await Validation.ValidateVaultCreate(tx);

            tx["RedemptionDate"] = 800000000u + 946708560u;
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateVaultCreate(tx));

            tx["RedemptionDate"] = 800000000u + 946708559u;
            await Validation.ValidateVaultCreate(tx);
        }

        [TestMethod]
        public void TestULOVault_ClosedEndedFields_Deserialize()
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["LedgerEntryType"] = "Vault",
                ["Account"] = Account1,
                ["Owner"] = Account2,
                ["VaultKind"] = (uint)Xrpl.Models.Ledger.VaultKind.ClosedEnded,
                ["SubscriptionDate"] = 800000000u,
                ["RedemptionDate"] = 800086400u,
            });
            LOVault vault = JsonSerializer.Deserialize<LOVault>(json, XrplJsonOptions.Default);
            Assert.AreEqual((uint)Xrpl.Models.Ledger.VaultKind.ClosedEnded, vault.VaultKind);
            Assert.AreEqual(RippleEpoch.AddSeconds(800000000), vault.SubscriptionDate);
            Assert.AreEqual(RippleEpoch.AddSeconds(800086400), vault.RedemptionDate);

            // An open-ended vault carries none of the three (VaultKind is SoeDefault), exactly
            // like every vault created before the amendment
            string openEnded = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["LedgerEntryType"] = "Vault",
                ["Account"] = Account1,
                ["Owner"] = Account2,
            });
            LOVault legacy = JsonSerializer.Deserialize<LOVault>(openEnded, XrplJsonOptions.Default);
            Assert.IsNull(legacy.VaultKind);
            Assert.IsNull(legacy.SubscriptionDate);
            Assert.IsNull(legacy.RedemptionDate);
        }

        [TestMethod]
        public void TestULOMPTokenIssuance_KeyEpochs_Deserialize()
        {
            // rippled #7915 (ConfidentialMPTKeyRotation): the epochs count rotations and are absent until the first one
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["LedgerEntryType"] = "MPTokenIssuance",
                ["Issuer"] = Account1,
                ["IssuerEncryptionKey"] = new string('C', 66),
                ["AuditorEncryptionKey"] = new string('D', 66),
                ["IssuerKeyEpoch"] = 2u,
                ["AuditorKeyEpoch"] = 1u,
            });
            LOMPTokenIssuance issuance = JsonSerializer.Deserialize<LOMPTokenIssuance>(json, XrplJsonOptions.Default);
            Assert.AreEqual(2u, issuance.IssuerKeyEpoch);
            Assert.AreEqual(1u, issuance.AuditorKeyEpoch);

            string neverRotated = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["LedgerEntryType"] = "MPTokenIssuance",
                ["Issuer"] = Account1,
                ["IssuerEncryptionKey"] = new string('C', 66),
            });
            LOMPTokenIssuance fresh = JsonSerializer.Deserialize<LOMPTokenIssuance>(neverRotated, XrplJsonOptions.Default);
            Assert.IsNull(fresh.IssuerKeyEpoch);
            Assert.IsNull(fresh.AuditorKeyEpoch);
        }

        [TestMethod]
        public void TestUDevelopFields_BinaryRoundTrip()
        {
            // Every field rippled develop declares at e3c8996e that 3.3.0 does not. The mirror
            // epochs and ContractResult belong to no format yet, so nothing but the codec
            // table knows them: this fails with an encoding error when definitions.json lacks one.
            JsonObject json = JsonNode.Parse("""
                {"ContractResult":7,"VaultKind":1,"SubscriptionDate":800000000,"RedemptionDate":800086400,
                 "IssuerKeyEpoch":2,"AuditorKeyEpoch":1,"IssuerKeyMirrorEpoch":2,"AuditorKeyMirrorEpoch":1}
                """)!.AsObject();
            string blob = XrplBinaryCodec.Encode(json);
            JsonObject decoded = XrplBinaryCodec.Decode(blob).AsObject();

            Assert.AreEqual(7u, decoded["ContractResult"]!.GetValue<uint>());
            Assert.AreEqual(1u, decoded["VaultKind"]!.GetValue<uint>());
            Assert.AreEqual(800000000u, decoded["SubscriptionDate"]!.GetValue<uint>());
            Assert.AreEqual(800086400u, decoded["RedemptionDate"]!.GetValue<uint>());
            Assert.AreEqual(2u, decoded["IssuerKeyEpoch"]!.GetValue<uint>());
            Assert.AreEqual(1u, decoded["AuditorKeyEpoch"]!.GetValue<uint>());
            Assert.AreEqual(2u, decoded["IssuerKeyMirrorEpoch"]!.GetValue<uint>());
            Assert.AreEqual(1u, decoded["AuditorKeyMirrorEpoch"]!.GetValue<uint>());
        }

        [TestMethod]
        public void TestUWithdraws_CredentialIDs_RoundTrip()
        {
            // develop adds CredentialIDs to VaultWithdraw and LoanBrokerCoverWithdraw for a
            // Destination that requires deposit authorization
            string credential = new string('A', 64);
            VaultWithdraw withdraw = new VaultWithdraw
            {
                Account = Account1,
                VaultID = new string('E', 64),
                Amount = new Currency { ValueAsXrp = 1m },
                Destination = Account2,
                CredentialIDs = new List<string> { credential },
                Sequence = 1,
                Fee = new Currency { Value = "12" },
                SigningPublicKey = "",
            };
            JsonObject json = JsonNode.Parse(withdraw.ToJson())!.AsObject();
            JsonObject decoded = XrplBinaryCodec.Decode(XrplBinaryCodec.Encode(json)).AsObject();
            Assert.AreEqual(credential, decoded["CredentialIDs"]![0]!.GetValue<string>());

            Assert.IsTrue(TxFormat.Formats[BinaryCodec.Types.TransactionType.VaultWithdraw]
                .ContainsKey(BinaryCodec.Enums.Field.CredentialIDs));
            Assert.IsTrue(TxFormat.Formats[BinaryCodec.Types.TransactionType.LoanBrokerCoverWithdraw]
                .ContainsKey(BinaryCodec.Enums.Field.CredentialIDs));
        }

        [TestMethod]
        public async Task TestUWithdraws_CredentialIDs_Validated()
        {
            Dictionary<string, object> tx = new()
            {
                ["TransactionType"] = "VaultWithdraw",
                ["Account"] = Account1,
                ["VaultID"] = new string('E', 64),
                ["Amount"] = "1000000",
                ["CredentialIDs"] = new List<object> { "not-a-hash" },
            };
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateVaultWithdraw(tx));

            tx["CredentialIDs"] = new List<object> { new string('A', 64) };
            await Validation.ValidateVaultWithdraw(tx);

            Dictionary<string, object> cover = new()
            {
                ["TransactionType"] = "LoanBrokerCoverWithdraw",
                ["Account"] = Account1,
                ["LoanBrokerID"] = new string('E', 64),
                ["Amount"] = "1000000",
                ["CredentialIDs"] = new List<object> { "not-a-hash" },
            };
            await Assert.ThrowsExactlyAsync<Xrpl.Client.Exceptions.ValidationException>(() => Validation.ValidateLoanBrokerCoverWithdraw(cover));

            cover["CredentialIDs"] = new List<object> { new string('A', 64) };
            await Validation.ValidateLoanBrokerCoverWithdraw(cover);
        }
    }
}
