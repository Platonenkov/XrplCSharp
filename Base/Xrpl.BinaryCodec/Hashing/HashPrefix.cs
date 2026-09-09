// https://github.com/XRPLF/xrpl.js/blob/main/packages/ripple-binary-codec/src/hash-prefixes.ts

namespace Xrpl.BinaryCodec.Hashing
{
    /// <summary> hash prefix </summary>
    public enum HashPrefix : uint
    {
        /// <summary>
        /// TransactionId
        /// </summary>
        TransactionId = 0x54584E00u,
        /// <summary>
        /// Transaction
        /// </summary>
        Transaction = 0x534E4400u,
        /// <summary>
        /// AccountStateEntry
        /// </summary>
        AccountStateEntry = 0x4D4C4E00u,
        /// <summary>
        /// inner node in tree
        /// </summary>
        InnerNode = 0x4D494E00u,
        /// <summary>
        /// ledger master data for signing
        /// </summary>
        LedgerHeader = 0x4C575200u,
        /// <summary>
        /// TransactionSig
        /// </summary>
        TransactionSig = 0x53545800u,
        /// <summary>
        /// TransactionMultiSig
        /// </summary>
        TransactionMultiSig = 0x534D5400u,
        /// <summary>
        /// CounterpartyTransactionSig: the preimage a LoanSet counterparty signs into
        /// CounterpartySignature (rippled <c>HashPrefix::CounterpartyTxSign</c>).
        /// </summary>
        /// <remarks>
        /// Before fixCleanup3_4_0 every signature on a transaction covered the same bytes, so a
        /// signature could be lifted from one role and pasted into another. Since the amendment
        /// each role has its own prefix, and the roles are the only thing that changed:
        /// <see cref="TransactionSig"/> and <see cref="TransactionMultiSig"/> still cover an
        /// ordinary TxnSignature, before and after.
        /// </remarks>
        CounterpartyTransactionSig = 0x43505400u,
        /// <summary>
        /// CounterpartyTransactionMultiSig: what a signer on the counterparty's SignerList signs,
        /// for a CounterpartySignature carrying Signers rather than one signature
        /// (rippled <c>HashPrefix::CounterpartyTxMultiSign</c>).
        /// </summary>
        CounterpartyTransactionMultiSig = 0x43504D00u,
        /// <summary>
        /// SponsorTransactionSig: the preimage a sponsor signs into SponsorSignature
        /// (rippled <c>HashPrefix::SponsorTxSign</c>). See <see cref="CounterpartyTransactionSig"/>.
        /// </summary>
        SponsorTransactionSig = 0x53504E00u,
        /// <summary>
        /// SponsorTransactionMultiSig: what a signer on the sponsor's SignerList signs
        /// (rippled <c>HashPrefix::SponsorTxMultiSign</c>).
        /// </summary>
        SponsorTransactionMultiSig = 0x53504D00u,
        /// <summary>
        /// Validation
        /// </summary>
        Validation = 0x56414C00u,
        /// <summary>
        /// Proposal
        /// </summary>
        Proposal = 0x50525000u,
        /// <summary>
        /// PaymentChannelClaim
        /// </summary>
        PaymentChannelClaim = 0x434C4D00u,
        /// <summary>
        /// Batch
        /// </summary>
        Batch = 0x42434800
    }
}