using System;
using System.Collections.Generic;

using Xrpl.BinaryCodec.Enums;
using Xrpl.BinaryCodec.Types;

namespace Xrpl.Models.Transaction
{
    //https://github.com/ripple/rippled/blob/112a863e7346793234c973e97818ced4c6e36867/src/ripple/protocol/impl/TxFormats.cpp
    public class TxFormat : Dictionary<Field, TxFormat.Requirement>
    {
        public enum Requirement
        {
            Default,
            Optional,
            Required
        }

        public TxFormat()
        {
            // Common fields
            this[Field.TransactionType] = Requirement.Required;
            this[Field.Account] = Requirement.Required;
            this[Field.Fee] = Requirement.Required;
            this[Field.Sequence] = Requirement.Required;
            this[Field.SigningPubKey] = Requirement.Required;
            this[Field.TicketSequence] = Requirement.Optional;

            this[Field.NetworkID] = Requirement.Optional;
            this[Field.Flags] = Requirement.Optional;
            this[Field.SourceTag] = Requirement.Optional;
            this[Field.PreviousTxnID] = Requirement.Optional;
            this[Field.LastLedgerSequence] = Requirement.Optional;
            this[Field.AccountTxnID] = Requirement.Optional;
            this[Field.OperationLimit] = Requirement.Optional;
            this[Field.Memos] = Requirement.Optional;
            this[Field.TxnSignature] = Requirement.Optional;
            this[Field.Signers] = Requirement.Optional;
            this[Field.Delegate] = Requirement.Optional;
            // XLS-68 sponsored fees/reserves
            this[Field.Sponsor] = Requirement.Optional;
            this[Field.SponsorFlags] = Requirement.Optional;
            this[Field.SponsorSignature] = Requirement.Optional;
        }

        public static void Validate(StObject obj)
        {
            var errors = new List<string>();
            Validate(obj, errors.Add);
            if (errors.Count > 0)
            {
                throw new TxFormatValidationException(string.Join("\n", errors));
            }
        }

        internal static void Validate(StObject obj, Action<string> onError)
        {
            if (!obj.Has(Field.TransactionType))
            {
                onError("Missing `TransactionType` field");
                return;
            }

            var tt = obj[Field.TransactionType];
            if (tt == null)
            {
                onError("`TransactionType` is set to null");
                return;
            }

            var format = Formats[tt];
            var allFields = new SortedSet<Field>(obj.Fields.Keys);
            allFields.UnionWith(format.Keys);

            foreach (var field in allFields)
            {
                var inFormat = format.ContainsKey(field);
                ISerializedType fieldValue;
                var inObject = obj.Fields.TryGetValue(field, out fieldValue);
                if (!inFormat)
                {
                    onError($"`{tt}` has no `{field}` field");
                }
                else if (format[field] == Requirement.Required)
                {
                    if (!inObject)
                    {
                        onError($"`{tt}` has required field `{field}`");
                    }
                    else if (fieldValue == null)
                    {
                        onError($"Required field `{field}` is set to null");
                    }
                    // TODO: associated type for field is wrong
                    // It should be nearly impossible anyway because FromJson
                    // throws when the json is invalid for the field type and
                    // the StObject[] indexers all use typed fields externally
                }
            }
        }

        public static Dictionary<BinaryCodec.Types.TransactionType, TxFormat> Formats;

        static TxFormat()
        {
            Formats = new Dictionary<BinaryCodec.Types.TransactionType, TxFormat>
            {
                [BinaryCodec.Types.TransactionType.Payment] = new TxFormat
                {
                    [Field.Destination] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.SendMax] = Requirement.Optional,
                    [Field.Paths] = Requirement.Default,
                    [Field.InvoiceID] = Requirement.Optional,
                    [Field.DestinationTag] = Requirement.Optional,
                    [Field.DeliverMin] = Requirement.Optional,
                    [Field.DomainID] = Requirement.Optional,
                    [Field.CredentialIDs] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.EscrowCreate] = new TxFormat
                {
                    [Field.Amount] = Requirement.Required,
                    [Field.Destination] = Requirement.Required,
                    [Field.Condition] = Requirement.Optional,
                    [Field.CancelAfter] = Requirement.Optional,
                    [Field.FinishAfter] = Requirement.Optional,
                    [Field.DestinationTag] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.EscrowFinish] = new TxFormat
                {
                    [Field.Owner] = Requirement.Required,
                    [Field.OfferSequence] = Requirement.Required,
                    [Field.Condition] = Requirement.Optional,
                    [Field.Fulfillment] = Requirement.Optional,
                    [Field.CredentialIDs] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.AccountSet] = new TxFormat
                {
                    [Field.EmailHash] = Requirement.Optional,
                    [Field.WalletLocator] = Requirement.Optional,
                    [Field.WalletSize] = Requirement.Optional,
                    [Field.MessageKey] = Requirement.Optional,
                    [Field.Domain] = Requirement.Optional,
                    [Field.TransferRate] = Requirement.Optional,
                    [Field.SetFlag] = Requirement.Optional,
                    [Field.ClearFlag] = Requirement.Optional,
                    [Field.TickSize] = Requirement.Optional,
                    [Field.NFTokenMinter] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.EscrowCancel] = new TxFormat
                {
                    [Field.Owner] = Requirement.Required,
                    [Field.OfferSequence] = Requirement.Required
                },
                [BinaryCodec.Types.TransactionType.SetRegularKey] = new TxFormat
                {
                    [Field.RegularKey] = Requirement.Optional
                },
                // 6
                [BinaryCodec.Types.TransactionType.OfferCreate] = new TxFormat
                {
                    [Field.TakerPays] = Requirement.Required,
                    [Field.TakerGets] = Requirement.Required,
                    [Field.Expiration] = Requirement.Optional,
                    [Field.OfferSequence] = Requirement.Optional,
                    [Field.DomainID] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.OfferCancel] = new TxFormat
                {
                    [Field.OfferSequence] = Requirement.Required
                },
                // 9
                // Since the TicketBatch amendment rippled's TicketCreate carries only sfTicketCount;
                // the pre-amendment sfTarget/sfExpiration pair is gone (sfTarget is retired outright -
                // AccountID nth 7 is marked unused in sfields.macro and absent from definitions.json).
                [BinaryCodec.Types.TransactionType.TicketCreate] = new TxFormat
                {
                    [Field.TicketCount] = Requirement.Required
                },
                // 11
                // WalletLocator belongs to the nested SignerEntry object, not to SignerListSet itself
                [BinaryCodec.Types.TransactionType.SignerListSet] = new TxFormat
                {
                    [Field.SignerQuorum] = Requirement.Required,
                    [Field.SignerEntries] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.PaymentChannelCreate] = new TxFormat()
                {
                    [Field.Destination] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.SettleDelay] = Requirement.Required,
                    [Field.PublicKey] = Requirement.Required,
                    [Field.CancelAfter] = Requirement.Optional,
                    [Field.DestinationTag] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.PaymentChannelFund] = new TxFormat()
                {
                    [Field.Channel] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.Expiration] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.PaymentChannelClaim] = new TxFormat()
                {
                    [Field.Channel] = Requirement.Required,
                    [Field.Amount] = Requirement.Optional,
                    [Field.Balance] = Requirement.Optional,
                    [Field.Signature] = Requirement.Optional,
                    [Field.PublicKey] = Requirement.Optional,
                    [Field.CredentialIDs] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.CheckCreate] = new TxFormat()
                {
                    [Field.Destination] = Requirement.Required,
                    [Field.SendMax] = Requirement.Required,
                    [Field.Expiration] = Requirement.Optional,
                    [Field.DestinationTag] = Requirement.Optional,
                    [Field.InvoiceID] = Requirement.Optional
                },
                // Exactly one of Amount / DeliverMin is expected, which the format cannot express
                [BinaryCodec.Types.TransactionType.CheckCash] = new TxFormat()
                {
                    [Field.CheckID] = Requirement.Required,
                    [Field.Amount] = Requirement.Optional,
                    [Field.DeliverMin] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.CheckCancel] = new TxFormat()
                {
                    [Field.CheckID] = Requirement.Required
                },
                [BinaryCodec.Types.TransactionType.DepositPreauth] = new TxFormat()
                {
                    [Field.Authorize] = Requirement.Optional,
                    [Field.Unauthorize] = Requirement.Optional,
                    [Field.AuthorizeCredentials] = Requirement.Optional,
                    [Field.UnauthorizeCredentials] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.TrustSet] = new TxFormat
                {
                    [Field.LimitAmount] = Requirement.Optional,
                    [Field.QualityIn] = Requirement.Optional,
                    [Field.QualityOut] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.AccountDelete] = new TxFormat
                {
                    [Field.Destination] = Requirement.Required,
                    [Field.DestinationTag] = Requirement.Optional,
                    [Field.CredentialIDs] = Requirement.Optional,
                },

                [BinaryCodec.Types.TransactionType.NFTokenMint] = new TxFormat
                {
                    [Field.NFTokenTaxon] = Requirement.Required,
                    [Field.Issuer] = Requirement.Optional,
                    [Field.TransferFee] = Requirement.Optional,
                    [Field.URI] = Requirement.Optional,
                    // NFTokenMintOffer: the model gained these in 10.7.0, the format lagged behind
                    [Field.Amount] = Requirement.Optional,
                    [Field.Destination] = Requirement.Optional,
                    [Field.Expiration] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.NFTokenModify] = new TxFormat
                {
                    [Field.NFTokenID] = Requirement.Required,
                    [Field.Owner] = Requirement.Optional,
                    [Field.URI] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.NFTokenBurn] = new TxFormat
                {
                    [Field.NFTokenID] = Requirement.Required,
                    [Field.Owner] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.NFTokenCreateOffer] = new TxFormat
                {
                    [Field.NFTokenID] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.Owner] = Requirement.Optional,
                    [Field.Destination] = Requirement.Optional,
                    [Field.Expiration] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.NFTokenCancelOffer] = new TxFormat
                {
                    [Field.NFTokenOffers] = Requirement.Required
                },
                [BinaryCodec.Types.TransactionType.NFTokenAcceptOffer] = new TxFormat
                {
                    //[Field.NFTokenID] = Requirement.Required, //no need this field
                    [Field.NFTokenSellOffer] = Requirement.Optional,
                    [Field.NFTokenBuyOffer] = Requirement.Optional,
                    [Field.NFTokenBrokerFee] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.EnableAmendment] = new TxFormat
                {
                    [Field.LedgerSequence] = Requirement.Required,
                    [Field.Amendment] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.SetFee] = new TxFormat
                {
                    [Field.LedgerSequence] = Requirement.Optional,
                    [Field.BaseFee] = Requirement.Optional,
                    [Field.ReferenceFeeUnits] = Requirement.Optional,
                    [Field.ReserveBase] = Requirement.Optional,
                    [Field.ReserveIncrement] = Requirement.Optional,
                    [Field.BaseFeeDrops] = Requirement.Optional,
                    [Field.ReserveBaseDrops] = Requirement.Optional,
                    [Field.ReserveIncrementDrops] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.UNLModify] = new TxFormat
                {
                    [Field.UNLModifyDisabling] = Requirement.Required,
                    [Field.LedgerSequence] = Requirement.Required,
                    [Field.UNLModifyValidator] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.AMMBid] = new TxFormat
                {
                    [Field.Asset] = Requirement.Required,
                    [Field.Asset2] = Requirement.Required,
                    [Field.BidMin] = Requirement.Optional,
                    [Field.BidMax] = Requirement.Optional,
                    [Field.AuthAccounts] = Requirement.Optional
                },
                [BinaryCodec.Types.TransactionType.AMMCreate] = new TxFormat
                {
                    [Field.Amount] = Requirement.Required,
                    [Field.Amount2] = Requirement.Required,
                    [Field.TradingFee] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.AMMDelete] = new TxFormat
                {
                    [Field.Asset] = Requirement.Required,
                    [Field.Asset2] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.AMMDeposit] = new TxFormat
                {
                    [Field.Asset] = Requirement.Required,
                    [Field.Asset2] = Requirement.Required,
                    [Field.Amount] = Requirement.Optional,
                    [Field.Amount2] = Requirement.Optional,
                    [Field.EPrice] = Requirement.Optional,
                    [Field.LPTokenOut] = Requirement.Optional,
                    [Field.TradingFee] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.AMMVote] = new TxFormat
                {
                    [Field.Asset] = Requirement.Required,
                    [Field.Asset2] = Requirement.Required,
                    [Field.TradingFee] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.AMMWithdraw] = new TxFormat
                {
                    [Field.Asset] = Requirement.Required,
                    [Field.Asset2] = Requirement.Required,
                    [Field.Amount] = Requirement.Optional,
                    [Field.Amount2] = Requirement.Optional,
                    [Field.EPrice] = Requirement.Optional,
                    [Field.LPTokenIn] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.AMMClawback] = new TxFormat
                {
                    [Field.Holder] = Requirement.Required,
                    [Field.Asset] = Requirement.Required,
                    [Field.Asset2] = Requirement.Required,
                    [Field.Amount] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.Batch] = new TxFormat
                {
                    [Field.RawTransactions] = Requirement.Required,
                    [Field.BatchSigners] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.OracleSet] = new TxFormat
                {
                    [Field.OracleDocumentID] = Requirement.Required,
                    [Field.LastUpdateTime] = Requirement.Required,
                    [Field.PriceDataSeries] = Requirement.Required,
                    [Field.Provider] = Requirement.Optional,
                    [Field.AssetClass] = Requirement.Optional,
                    [Field.URI] = Requirement.Optional,
                    // BaseAsset/QuoteAsset/AssetPrice/Scale belong to the nested PriceData
                    // entries of PriceDataSeries, not to OracleSet itself
                },
                [BinaryCodec.Types.TransactionType.OracleDelete] = new TxFormat
                {
                    [Field.OracleDocumentID] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.Clawback] = new TxFormat
                {
                    [Field.Amount] = Requirement.Required,
                    [Field.Holder] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.DIDSet] = new TxFormat
                {
                    [Field.Data] = Requirement.Optional,
                    [Field.DIDDocument] = Requirement.Optional,
                    [Field.URI] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.DIDDelete] = new TxFormat
                {
                },
                [BinaryCodec.Types.TransactionType.PermissionedDomainSet] = new TxFormat
                {
                    [Field.DomainID] = Requirement.Optional,
                    [Field.AcceptedCredentials] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.PermissionedDomainDelete] = new TxFormat
                {
                    [Field.DomainID] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.CredentialCreate] = new TxFormat
                {
                    [Field.Subject] = Requirement.Required,
                    [Field.CredentialType] = Requirement.Required,
                    [Field.Expiration] = Requirement.Optional,
                    [Field.URI] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.CredentialAccept] = new TxFormat
                {
                    [Field.Issuer] = Requirement.Required,
                    [Field.CredentialType] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.CredentialDelete] = new TxFormat
                {
                    [Field.Subject] = Requirement.Optional,
                    [Field.Issuer] = Requirement.Optional,
                    [Field.CredentialType] = Requirement.Required,
                },

                // XChain Bridge (XLS-38d)
                [BinaryCodec.Types.TransactionType.XChainCreateBridge] = new TxFormat
                {
                    [Field.XChainBridge] = Requirement.Required,
                    [Field.SignatureReward] = Requirement.Required,
                    [Field.MinAccountCreateAmount] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.XChainModifyBridge] = new TxFormat
                {
                    [Field.XChainBridge] = Requirement.Required,
                    [Field.SignatureReward] = Requirement.Optional,
                    [Field.MinAccountCreateAmount] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.XChainCreateClaimID] = new TxFormat
                {
                    [Field.XChainBridge] = Requirement.Required,
                    [Field.SignatureReward] = Requirement.Required,
                    [Field.OtherChainSource] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.XChainCommit] = new TxFormat
                {
                    [Field.XChainBridge] = Requirement.Required,
                    [Field.XChainClaimID] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.OtherChainDestination] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.XChainClaim] = new TxFormat
                {
                    [Field.XChainBridge] = Requirement.Required,
                    [Field.XChainClaimID] = Requirement.Required,
                    [Field.Destination] = Requirement.Required,
                    [Field.DestinationTag] = Requirement.Optional,
                    [Field.Amount] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.XChainAccountCreateCommit] = new TxFormat
                {
                    [Field.XChainBridge] = Requirement.Required,
                    [Field.Destination] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.SignatureReward] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.XChainAddClaimAttestation] = new TxFormat
                {
                    [Field.XChainBridge] = Requirement.Required,
                    [Field.XChainClaimID] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.AttestationRewardAccount] = Requirement.Required,
                    [Field.AttestationSignerAccount] = Requirement.Required,
                    [Field.Destination] = Requirement.Optional,
                    [Field.OtherChainSource] = Requirement.Required,
                    [Field.PublicKey] = Requirement.Required,
                    [Field.Signature] = Requirement.Required,
                    [Field.WasLockingChainSend] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.XChainAddAccountCreateAttestation] = new TxFormat
                {
                    [Field.XChainBridge] = Requirement.Required,
                    [Field.XChainAccountCreateCount] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.SignatureReward] = Requirement.Required,
                    [Field.OtherChainSource] = Requirement.Required,
                    [Field.Destination] = Requirement.Required,
                    [Field.AttestationRewardAccount] = Requirement.Required,
                    [Field.AttestationSignerAccount] = Requirement.Required,
                    [Field.PublicKey] = Requirement.Required,
                    [Field.Signature] = Requirement.Required,
                    [Field.WasLockingChainSend] = Requirement.Required,
                },

                // Vault (XLS-65d)
                [BinaryCodec.Types.TransactionType.VaultCreate] = new TxFormat
                {
                    [Field.Asset] = Requirement.Required,
                    [Field.AssetsMaximum] = Requirement.Optional,
                    [Field.MPTokenMetadata] = Requirement.Optional,
                    [Field.WithdrawalPolicy] = Requirement.Optional,
                    [Field.Scale] = Requirement.Optional,
                    [Field.Data] = Requirement.Optional,
                    [Field.DomainID] = Requirement.Optional,
                    [Field.VaultKind] = Requirement.Optional,
                    [Field.SubscriptionDate] = Requirement.Optional,
                    [Field.RedemptionDate] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.VaultSet] = new TxFormat
                {
                    [Field.VaultID] = Requirement.Required,
                    [Field.Data] = Requirement.Optional,
                    [Field.AssetsMaximum] = Requirement.Optional,
                    [Field.DomainID] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.VaultDelete] = new TxFormat
                {
                    [Field.VaultID] = Requirement.Required,
                    [Field.MemoData] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.VaultDeposit] = new TxFormat
                {
                    [Field.VaultID] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.VaultWithdraw] = new TxFormat
                {
                    [Field.VaultID] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.Destination] = Requirement.Optional,
                    [Field.DestinationTag] = Requirement.Optional,
                    [Field.CredentialIDs] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.VaultClawback] = new TxFormat
                {
                    [Field.VaultID] = Requirement.Required,
                    [Field.Holder] = Requirement.Required,
                    [Field.Amount] = Requirement.Optional,
                },

                // Loan/LoanBroker (XLS-66d)
                [BinaryCodec.Types.TransactionType.LoanBrokerSet] = new TxFormat
                {
                    [Field.VaultID] = Requirement.Required,
                    [Field.LoanBrokerID] = Requirement.Optional,
                    [Field.CoverRateMinimum] = Requirement.Optional,
                    [Field.CoverRateLiquidation] = Requirement.Optional,
                    [Field.ManagementFeeRate] = Requirement.Optional,
                    [Field.DebtMaximum] = Requirement.Optional,
                    [Field.Data] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.LoanBrokerDelete] = new TxFormat
                {
                    [Field.LoanBrokerID] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.LoanBrokerCoverDeposit] = new TxFormat
                {
                    [Field.LoanBrokerID] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.LoanBrokerCoverWithdraw] = new TxFormat
                {
                    [Field.LoanBrokerID] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                    [Field.Destination] = Requirement.Optional,
                    [Field.DestinationTag] = Requirement.Optional,
                    [Field.CredentialIDs] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.LoanBrokerCoverClawback] = new TxFormat
                {
                    [Field.LoanBrokerID] = Requirement.Optional,
                    [Field.Amount] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.LoanSet] = new TxFormat
                {
                    [Field.LoanBrokerID] = Requirement.Required,
                    [Field.PrincipalRequested] = Requirement.Required,
                    [Field.Counterparty] = Requirement.Optional,
                    [Field.CounterpartySignature] = Requirement.Optional,
                    [Field.InterestRate] = Requirement.Optional,
                    [Field.LateInterestRate] = Requirement.Optional,
                    [Field.CloseInterestRate] = Requirement.Optional,
                    [Field.OverpaymentInterestRate] = Requirement.Optional,
                    [Field.OverpaymentFee] = Requirement.Optional,
                    [Field.LoanOriginationFee] = Requirement.Optional,
                    [Field.LoanServiceFee] = Requirement.Optional,
                    [Field.LatePaymentFee] = Requirement.Optional,
                    [Field.ClosePaymentFee] = Requirement.Optional,
                    [Field.PaymentInterval] = Requirement.Optional,
                    [Field.GracePeriod] = Requirement.Optional,
                    [Field.PaymentTotal] = Requirement.Optional,
                    [Field.Data] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.LoanDelete] = new TxFormat
                {
                    [Field.LoanID] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.LoanManage] = new TxFormat
                {
                    [Field.LoanID] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.LoanPay] = new TxFormat
                {
                    [Field.LoanID] = Requirement.Required,
                    [Field.Amount] = Requirement.Required,
                },

                // DelegateSet (XLS-75, PermissionDelegationV1_1)
                [BinaryCodec.Types.TransactionType.DelegateSet] = new TxFormat
                {
                    [Field.Authorize] = Requirement.Required,
                    [Field.Permissions] = Requirement.Required,
                },

                // LedgerStateFix
                [BinaryCodec.Types.TransactionType.LedgerStateFix] = new TxFormat
                {
                    [Field.LedgerFixType] = Requirement.Required,
                    [Field.Owner] = Requirement.Optional,
                    [Field.BookDirectory] = Requirement.Optional,
                },

                // MPT (XLS-33 + DynamicMPT + PermissionedDomains + ConfidentialTransfer)
                [BinaryCodec.Types.TransactionType.MPTokenIssuanceCreate] = new TxFormat
                {
                    [Field.AssetScale] = Requirement.Optional,
                    [Field.TransferFee] = Requirement.Optional,
                    [Field.MaximumAmount] = Requirement.Optional,
                    [Field.MPTokenMetadata] = Requirement.Optional,
                    [Field.DomainID] = Requirement.Optional,
                    [Field.ImmutableFlags] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.MPTokenIssuanceDestroy] = new TxFormat
                {
                    [Field.MPTokenIssuanceID] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.MPTokenIssuanceSet] = new TxFormat
                {
                    [Field.MPTokenIssuanceID] = Requirement.Required,
                    [Field.Holder] = Requirement.Optional,
                    [Field.DomainID] = Requirement.Optional,
                    [Field.MPTokenMetadata] = Requirement.Optional,
                    [Field.TransferFee] = Requirement.Optional,
                    [Field.ImmutableFlags] = Requirement.Optional,
                    [Field.IssuerEncryptionKey] = Requirement.Optional,
                    [Field.AuditorEncryptionKey] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.MPTokenAuthorize] = new TxFormat
                {
                    [Field.MPTokenIssuanceID] = Requirement.Required,
                    [Field.Holder] = Requirement.Optional,
                },

                // Sponsor (XLS-68)
                [BinaryCodec.Types.TransactionType.SponsorshipSet] = new TxFormat
                {
                    [Field.CounterpartySponsor] = Requirement.Optional,
                    [Field.Sponsee] = Requirement.Optional,
                    [Field.FeeAmountDelta] = Requirement.Optional,
                    [Field.MaxFee] = Requirement.Optional,
                    [Field.RemainingOwnerCountDelta] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.SponsorshipTransfer] = new TxFormat
                {
                    [Field.ObjectID] = Requirement.Optional,
                    [Field.Sponsee] = Requirement.Optional,
                },

                // ConfidentialTransfer
                [BinaryCodec.Types.TransactionType.ConfidentialMPTConvert] = new TxFormat
                {
                    [Field.MPTokenIssuanceID] = Requirement.Required,
                    [Field.MPTAmount] = Requirement.Required,
                    [Field.HolderEncryptionKey] = Requirement.Optional,
                    [Field.HolderEncryptedAmount] = Requirement.Required,
                    [Field.IssuerEncryptedAmount] = Requirement.Required,
                    [Field.AuditorEncryptedAmount] = Requirement.Optional,
                    [Field.BlindingFactor] = Requirement.Required,
                    [Field.ZKProof] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.ConfidentialMPTMergeInbox] = new TxFormat
                {
                    [Field.MPTokenIssuanceID] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.ConfidentialMPTConvertBack] = new TxFormat
                {
                    [Field.MPTokenIssuanceID] = Requirement.Required,
                    [Field.MPTAmount] = Requirement.Required,
                    [Field.HolderEncryptedAmount] = Requirement.Required,
                    [Field.IssuerEncryptedAmount] = Requirement.Required,
                    [Field.AuditorEncryptedAmount] = Requirement.Optional,
                    [Field.BlindingFactor] = Requirement.Required,
                    [Field.ZKProof] = Requirement.Required,
                    [Field.BalanceCommitment] = Requirement.Required,
                },
                [BinaryCodec.Types.TransactionType.ConfidentialMPTSend] = new TxFormat
                {
                    [Field.MPTokenIssuanceID] = Requirement.Required,
                    [Field.Destination] = Requirement.Required,
                    [Field.DestinationTag] = Requirement.Optional,
                    [Field.SenderEncryptedAmount] = Requirement.Required,
                    [Field.DestinationEncryptedAmount] = Requirement.Required,
                    [Field.IssuerEncryptedAmount] = Requirement.Required,
                    [Field.AuditorEncryptedAmount] = Requirement.Optional,
                    [Field.ZKProof] = Requirement.Required,
                    [Field.AmountCommitment] = Requirement.Required,
                    [Field.BalanceCommitment] = Requirement.Required,
                    [Field.CredentialIDs] = Requirement.Optional,
                },
                [BinaryCodec.Types.TransactionType.ConfidentialMPTClawback] = new TxFormat
                {
                    [Field.MPTokenIssuanceID] = Requirement.Required,
                    [Field.Holder] = Requirement.Required,
                    [Field.MPTAmount] = Requirement.Required,
                    [Field.ZKProof] = Requirement.Required,
                },

            };
        }
    }

    public class TxFormatValidationException : FormatException
    {
        public TxFormatValidationException(string msg) : base(msg)
        {
        }
    }
}