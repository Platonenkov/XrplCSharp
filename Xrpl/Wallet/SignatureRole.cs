namespace Xrpl.Wallet
{
    /// <summary>
    /// Which signature of a transaction a key is producing.
    /// </summary>
    /// <remarks>
    /// Before rippled's <c>fixCleanup3_4_0</c> every signature on a transaction covered the same
    /// bytes, so the role was decided when the parts were composed rather than when they were
    /// signed, and a signature could be lifted from one role into another. Since the amendment
    /// each role signs under its own hash prefix, so a signer has to know its role before signing.
    /// Mirrors rippled's <c>SignatureRole</c> in <c>include/xrpl/protocol/Sign.h</c>.
    /// <para>
    /// The SDK works the role out on its own wherever the transaction says it: a wallet named as
    /// the <c>Sponsor</c> signs as sponsor, a LoanSet <c>Counterparty</c> as counterparty, and a
    /// multi-signature entry on a transaction whose main signature is single can only belong to
    /// the co-signing side. It is genuinely ambiguous in one shape only, where the main signature
    /// and a co-signature are both multi-signed, and there the role has to be passed in.
    /// </para>
    /// </remarks>
    public enum SignatureRole
    {
        /// <summary>The transaction's own signature: <c>TxnSignature</c>, or an entry in <c>Signers</c>.</summary>
        Transaction,

        /// <summary>The sponsor's signature (XLS-68), in <c>SponsorSignature</c>.</summary>
        Sponsor,

        /// <summary>The counterparty's signature (XLS-66 LoanSet), in <c>CounterpartySignature</c>.</summary>
        Counterparty,
    }
}
