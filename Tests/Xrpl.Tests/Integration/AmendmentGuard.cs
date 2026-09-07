using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Models.Methods;

namespace XrplTests.Xrpl.ClientLib.Integration;

/// <summary>
/// Checks amendment activation on the test node so integration tests can be
/// marked inconclusive instead of failing when the node lacks the amendment.
/// </summary>
public static class AmendmentGuard
{
    /// <summary>Well-known index of the Amendments ledger object.</summary>
    public const string AmendmentsLedgerIndex = "7DB0788C020F02780A673DC74757F23823FA3014C1866E72CC4CD8B226CD6EF4";

    /// <summary>Amendment id of BatchV1_1 (sha512half of the name).</summary>
    public const string BatchV11 = "9F287AED3CDB50A7BD1ACEC24296A30C9B5230CCD136219317AC790E3B884377";

    /// <summary>Amendment id of PermissionDelegationV1_1 (sha512half of the name).</summary>
    public const string PermissionDelegationV11 = "0F48FF561C709540328F31F1C97FD512ACC8B4E42138A161CB0E21ECA292540B";

    /// <summary>Amendment id of Sponsor / XLS-68 (sha512half of the name).</summary>
    public const string Sponsor = "BE1F90581635DBCEBFC4678C4B54FEDDC1A17B50FD02CFE765A4132A342126AC";

    /// <summary>Amendment id of ConfidentialTransfer (sha512half of the name).</summary>
    public const string ConfidentialTransfer = "2110E4A19966E2EF517C0A8C56A5F35099D7665B0BB89D7B126B30D50B86AAD5";

    /// <summary>Amendment id of DynamicMPT / XLS-94 (sha512half of the name).</summary>
    public const string DynamicMPT = "58E92F338758479C06084E1B6BA366BAD8F75E5329A7F0EEAFFFDA51E5106B7F";

    /// <summary>Amendment id of PriceOracle / XLS-47 (sha512half of the name).</summary>
    public const string PriceOracle = "96FD2F293A519AE1DB6F8BED23E4AD9119342DA7CB6BAFD00953D16C54205D8B";

    /// <summary>Amendment id of PermissionedDomains / XLS-80 (sha512half of the name).</summary>
    public const string PermissionedDomains = "A730EB18A9D4BB52502C898589558B4CCEB4BE10044500EE5581137A2E80E849";

    /// <summary>Amendment id of AMM / XLS-30 (sha512half of the name).</summary>
    public const string AMM = "8CC0774A3BF66D1D22E76BBDA8E8A232E6B6313834301B3B23E8601196AE6455";

    /// <summary>Amendment id of AMMClawback / XLS-73 (sha512half of the name).</summary>
    public const string AMMClawback = "726F944886BCDF7433203787E93DD9AA87FAB74DFE3AF4785BA03BEFC97ADA1F";

    /// <summary>Amendment id of MPTokensV1 / XLS-33 (sha512half of the name).</summary>
    public const string MPTokensV1 = "950AE2EA4654E47F04AA8739C0B214E242097E802FD372D24047A89AB1F5EC38";

    /// <summary>
    /// Amendment id of MPTokensV2 / XLS-62 (sha512half of the name). On the standalone
    /// stands it is a [features] Rules preset, not an on-ledger amendment, so the guard
    /// reports it disabled there even though MPT-in-AMM transactors work.
    /// </summary>
    public const string MPTokensV2 = "BE2D87DF21B690ED1497B593FDC013CC04276302380B1BD50A033DCF8DEFB2EB";

    /// <summary>
    /// Amendment id of fixCleanup3_4_0 (sha512half of the name): each signing role gets its own
    /// hash prefix, so a SponsorSignature or a CounterpartySignature covers different bytes than
    /// the transaction's own signature.
    /// </summary>
    public const string FixCleanup340 = "98433DD001A5737F773D74F8CA2A25A065089C73B2E611C760BAF369E4FECA76";

    /// <summary>Amendment id of LendingProtocolV1_1 (sha512half of the name): closed-ended vaults.</summary>
    public const string LendingProtocolV11 = "A360E2BFD775A5B0DCE1C36C16DF31B72735A57584FD163655D2F9564F8E7AC8";

    /// <summary>Amendment id of XChainBridge / XLS-38 (sha512half of the name).</summary>
    public const string XChainBridge = "C98D98EE9616ACD36E81FDEB8D41D349BF5F1B41DD64A0ABC1FE9AA5EA267E9C";

    private static bool? roleSignatures;

    /// <summary>
    /// Skips the test when the node still verifies role signatures the pre-fixCleanup3_4_0 way.
    /// </summary>
    /// <remarks>
    /// The SDK signs a SponsorSignature and a CounterpartySignature under the role prefixes the
    /// amendment introduced, and does not carry the older scheme: no public network can use either
    /// field without the amendment, since Sponsor and LendingProtocol are enabled nowhere it is
    /// absent. The CI stand is the one place both are true at once - it runs a release build older
    /// than the amendment with both features voted in at genesis - so these tests belong to the
    /// nightly stand until that release moves on.
    /// </remarks>
    public static async Task RequireRoleSignaturesAsync(IXrplClient client)
    {
        roleSignatures ??= await IsEnabledAsync(client, FixCleanup340);
        if (roleSignatures != true)
            Assert.Inconclusive("The node verifies a role signature the pre-fixCleanup3_4_0 way, over the transaction's own prefix; the SDK signs under the role prefix the amendment introduced. Run these on a stand carrying fixCleanup3_4_0 (.ci-config/docker-compose.batchv11.yml) or on devnet.");
    }

    public static async Task<bool> IsEnabledAsync(IXrplClient client, string amendmentId)
    {
        try
        {
            LedgerEntryRequest request = new LedgerEntryRequest { Index = AmendmentsLedgerIndex };
            JsonNode node = await client.GRequest<JsonNode, LedgerEntryRequest>(request).Typed();
            JsonArray amendments = node?["node"]?["Amendments"]?.AsArray();
            if (amendments != null)
            {
                foreach (JsonNode amendment in amendments)
                {
                    if (string.Equals(amendment?.GetValue<string>(), amendmentId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch (RippledException)
        {
            // The node answered with an error (e.g. entryNotFound before the
            // first flag ledger): fall through to the admin feature check.
            // Transport/parse failures propagate so infrastructure problems
            // fail the run loudly instead of skipping tests as "disabled".
        }

        // Standalone --start force-enables the [features] amendments without
        // recording them in the Amendments ledger object (observed on 3.2.0:
        // the object lists 3 entries while dozens are active). The admin
        // `feature` command reports the node's actual view.
        return await IsEnabledViaFeatureCommandAsync(client, amendmentId);
    }

    private static async Task<bool> IsEnabledViaFeatureCommandAsync(IXrplClient client, string amendmentId)
    {
        try
        {
            FeatureRequest request = new FeatureRequest { Feature = amendmentId };
            JsonNode node = await client.GRequest<JsonNode, FeatureRequest>(request).Typed();
            JsonNode entry = node?[amendmentId];
            return entry?["enabled"]?.GetValue<bool>() == true;
        }
        catch (RippledException)
        {
            // The node rejected the request: the amendment id is unknown to
            // this build (badFeature) or the connection is not admin
            // (noPermission). Either way activation cannot be confirmed, so
            // treat it as disabled. Other exceptions propagate.
            return false;
        }
    }

    private class FeatureRequest : BaseRequest
    {
        public FeatureRequest() => Command = "feature";

        [System.Text.Json.Serialization.JsonPropertyName("feature")]
        public string Feature { get; set; }
    }
}
