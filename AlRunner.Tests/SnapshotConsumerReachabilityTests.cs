// SnapshotConsumerReachabilityTests — issue #4196.
//
// The question #4196 asked
// ------------------------
// NCLMetadata.GetSnapshotOfAllObjects is Cecil-replaced to carry ObjectType.Query only.
// #4196 asked whether that restriction can be lifted so ONE substitution serves all
// seventeen callers of MetadataDataProvider.GetObjectNumberAndInfoWithinRange, without
// changing what the six already-correct tables answer — AllObj (2000000038),
// AllObjWithCaption (2000000058), Field (2000000041), Table Metadata (2000000136),
// Page Metadata (2000000138), Page Control Field (2000000192).
//
// What the measurement found: the premise does not hold
// -----------------------------------------------------
// Consolidation has nothing to consolidate. Of the seventeen callers, only ONE —
// QueryDataProvider — is reachable from AL through this runner at all. That is a
// structural property of two layers, and this file pins both:
//
//   1. THE DISPATCH. RecordPatches.DataAccessDispatch routes exactly four table ids to a
//      BC provider (Key 2000000063, Page Action 2000000143, Query Metadata 2000000142,
//      Table Relations Metadata 2000000141). Every other virtual table — including all
//      six "protected" ones — is served by a hand-written Populate* over a temp store, so
//      its BC provider is never constructed and its snapshot call never runs.
//
//   2. THE REWRITE LEVEL. Three of those four are Cecil-replaced at
//      GetValuesWithinRangeForKeyField, which sits ONE LEVEL ABOVE
//      GetObjectNumberAndInfoWithinRange. Their replaced bodies never call it, so they
//      cannot read the snapshot whatever it carries. Only QueryDataProvider keeps BC's own
//      body and descends into the snapshot.
//
// Measured end to end (28.x binary, sha256 6f2cf682…, BC 27.0.38460.53934 at runtime):
// injecting 716 ObjectType.Table entries into the snapshot left all six protected tables
// byte-identical — AllObj=9435, AllObjWithCaption=9435, Field(2000000038)=12,
// TableMetadata=1888, PageMetadata=2855, PageControlField=38449 — while a probe on the
// helper's entry proved it is not called AT ALL on a run reading only those six, and IS
// called when Query Metadata is read. The full table is in the PR body for #4196.
//
// So the ObjectType.Query restriction is not what protects those six tables; the dispatch
// is. Widening the snapshot would be a no-op for sixteen of the seventeen callers, and the
// consolidation #4196 proposed — routing every caller through one substitution — would
// first require giving each of those tables a BC provider it does not have today.
//
// Why these are RUNNER-INTERNAL claims, not BC-behaviour claims
// ------------------------------------------------------------
// Both assertions are about which bodies THIS RUNNER replaces and which tables ITS
// dispatch chain routes to a BC provider. What BC's providers compute is not in question
// and is not asserted here — that is the corpus's job (codeunit 60913 for Query Metadata,
// 60936 for Key). A corpus test cannot see a rewrite level or a dispatch branch.
using System.Linq;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Xunit;

namespace AlRunner.Tests;

// Reads the Ncl image this process actually loaded, which BcEngineBootstrap has already
// Cecil-rewritten in place — so it must share the serial bc-engine collection.
[Collection(BcEngineCollection.Name)]
public class SnapshotConsumerReachabilityTests
{
    private readonly BcEngineFixture _engine;

    public SnapshotConsumerReachabilityTests(BcEngineFixture engine) => _engine = engine;

    private const string Rt = "Microsoft.Dynamics.Nav.Runtime.";

    private static AssemblyDefinition Ncl()
        => AssemblyDefinition.ReadAssembly(typeof(ITreeObject).Assembly.Location);

    /// <summary>
    /// Does <paramref name="provider"/>'s GetValuesWithinRangeForKeyField body still reach
    /// GetObjectNumberAndInfoWithinRange? A Cecil-replaced body forwards to a runner helper
    /// and calls nothing else, so this answers false for a provider we rewrote at that level
    /// and true for one whose body is still BC's own.
    /// </summary>
    private static bool BodyReachesSnapshotWalk(AssemblyDefinition asm, string providerType)
    {
        var t = asm.MainModule.GetType(Rt + providerType);
        Assert.NotNull(t);

        var m = t!.Methods.FirstOrDefault(x => x.Name == "GetValuesWithinRangeForKeyField");
        Assert.NotNull(m);
        Assert.True(m!.HasBody, $"{providerType}.GetValuesWithinRangeForKeyField has no body to read");

        // BC compiles the iterator into a nested state machine, so a body that still runs
        // BC's own code reaches the walk either directly or through the type it instantiates.
        foreach (var i in m.Body.Instructions)
        {
            if (i.Operand is MethodReference mr)
            {
                if (mr.Name == "GetObjectNumberAndInfoWithinRange") return true;

                // The state machine's ctor — its MoveNext carries the real call.
                var declaring = mr.DeclaringType?.Resolve();
                if (declaring != null && declaring.Name.Contains("GetValuesWithinRangeForKeyField"))
                {
                    var moveNext = declaring.Methods.FirstOrDefault(x => x.Name == "MoveNext");
                    if (moveNext?.HasBody == true
                        && moveNext.Body.Instructions.Any(
                            x => x.Operand is MethodReference r
                                 && r.Name == "GetObjectNumberAndInfoWithinRange"))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Claim 2, the negative half: Key, Page Action and Table Relations Metadata are
    /// rewritten ABOVE the snapshot, so no widening of it can change what they answer.
    ///
    /// This is the assertion that makes #4196's consolidation unnecessary rather than
    /// merely risky — three of the four BC providers the runner constructs do not read the
    /// snapshot at all.
    /// </summary>
    [Theory]
    [InlineData("KeyDataProvider")]                 // 2000000063, rewritten by #4191
    [InlineData("PageActionDataProvider")]          // 2000000143, rewritten by #4192
    [InlineData("TableRelationDataProvider")]       // 2000000141, rewritten by #4088
    public void RewrittenProvidersDoNotReadTheObjectSnapshot(string providerType)
    {
        using var asm = Ncl();
        Assert.False(
            BodyReachesSnapshotWalk(asm, providerType),
            $"{providerType}.GetValuesWithinRangeForKeyField still reaches "
            + "GetObjectNumberAndInfoWithinRange. That would make it a snapshot consumer, "
            + "so widening NCLMetadata.GetSnapshotOfAllObjects beyond ObjectType.Query could "
            + "change what this table answers — re-measure #4196 before widening.");
    }

    /// <summary>
    /// Claim 2, the positive half — and the control that stops the test above passing
    /// vacuously. QueryDataProvider keeps BC's OWN body, so it DOES descend into the
    /// snapshot; that is why filling ObjectType.Query fixed 2000000142 with no row-building.
    ///
    /// Without this arm, a reader of the rewrite that stopped reaching the walk for every
    /// provider — or a Cecil-reading helper that silently answered false — would look
    /// exactly like the finding above.
    /// </summary>
    [Fact]
    public void QueryDataProviderIsTheOneProviderThatStillReadsTheSnapshot()
    {
        using var asm = Ncl();
        Assert.True(
            BodyReachesSnapshotWalk(asm, "QueryDataProvider"),
            "QueryDataProvider.GetValuesWithinRangeForKeyField no longer reaches "
            + "GetObjectNumberAndInfoWithinRange. The Query Metadata table (2000000142) is "
            + "served by filling the object snapshot for ObjectType.Query precisely because "
            + "BC's own body walks it — if that is no longer true, the #4147 substitution is "
            + "reaching nothing and the table answers no rows.");
    }

    /// <summary>
    /// Claim 1: the dispatch, not the ObjectType filter, is what protects the six tables
    /// #4196 was worried about. Each is served by a hand-written populate over a temp store,
    /// so BC's provider — the only thing that would read the snapshot — is never built.
    ///
    /// Pinned by id rather than by reading the if-chain's text: the ids are what AL names.
    /// </summary>
    public static TheoryData<int> ProtectedTableIds() => new()
    {
        RecordPatches.AllObjVirtualTableId,             // 2000000038
        RecordPatches.AllObjWithCaptionVirtualTableId,  // 2000000058
        RecordPatches.FieldVirtualTableId,              // 2000000041
        RecordPatches.TableMetadataVirtualTableId,      // 2000000136
        RecordPatches.PageMetadataVirtualTableId,       // 2000000138
        RecordPatches.PageControlFieldVirtualTableId,   // 2000000192
    };

    [Theory]
    [MemberData(nameof(ProtectedTableIds))]
    public void ProtectedTablesAreNotRoutedToABcVirtualProvider(int tableId)
    {
        Assert.DoesNotContain(tableId, BcVirtualProviderTableIds);
    }

    /// <summary>
    /// The four ids RecordPatches.DataAccessDispatch hands to BC's own GetVirtualDataAccess
    /// factory. Integer (2000000026) and Date (2000000007) also take that route but consume
    /// no object snapshot, so they are out of scope here and deliberately absent.
    ///
    /// Adding an id to this list means a table gained a BC provider, which is exactly when
    /// #4196's question needs re-measuring — so the list is asserted, not merely documented.
    /// </summary>
    private static readonly int[] BcVirtualProviderTableIds =
    {
        RecordPatches.KeyVirtualTableId,
        RecordPatches.PageActionVirtualTableId,
        RecordPatches.QueryMetadataVirtualTableId,
        RecordPatches.TableRelationsMetadataVirtualTableId,
    };

    [Fact]
    public void OnlyQueryMetadataAmongTheSnapshotConsumersKeepsBcsOwnWalk()
    {
        using var asm = Ncl();

        var stillWalking = new[]
            {
                ("KeyDataProvider", RecordPatches.KeyVirtualTableId),
                ("PageActionDataProvider", RecordPatches.PageActionVirtualTableId),
                ("QueryDataProvider", RecordPatches.QueryMetadataVirtualTableId),
                ("TableRelationDataProvider", RecordPatches.TableRelationsMetadataVirtualTableId),
            }
            .Where(p => BodyReachesSnapshotWalk(asm, p.Item1))
            .Select(p => p.Item2)
            .ToArray();

        Assert.Equal(new[] { RecordPatches.QueryMetadataVirtualTableId }, stillWalking);
    }
}
