// SnapshotConsumerReachabilityTests — issue #4196.
//
// The question #4196 asked
// ------------------------
// NCLMetadata.GetSnapshotOfAllObjects was Cecil-replaced to carry ObjectType.Query only.
// #4196 asked whether that restriction can be lifted so ONE substitution serves all
// seventeen callers of MetadataDataProvider.GetObjectNumberAndInfoWithinRange, without
// changing what the six already-correct tables answer — AllObj (2000000038),
// AllObjWithCaption (2000000058), Field (2000000041), Table Metadata (2000000136),
// Page Metadata (2000000138), Page Control Field (2000000192).
//
// What the measurement found: the premise does not hold
// -----------------------------------------------------
// Two layers decide which callers are reachable, and this file pins both: THE DISPATCH (which
// table ids RecordPatches.GetDataAccessForTableCore hands to BC's own factory, read out of the
// compiled if-chain) and THE REWRITE LEVEL (which of those providers keep BC's own body and so
// descend into the snapshot). The protected tables are held by the dispatch, not by the
// snapshot's ObjectType filter. The measurement behind that is in the PR body for #4196; the
// set of snapshot-walking providers grew to Query and XmlPort Metadata in #4461.
//
// Why these are RUNNER-INTERNAL claims, not BC-behaviour claims
// ------------------------------------------------------------
// Both assertions are about which bodies THIS RUNNER replaces and which tables ITS
// dispatch chain routes to a BC provider. What BC's providers compute is not in question
// and is not asserted here — that is the corpus's job (codeunit 60913 for Query Metadata,
// 67450 for XmlPort Metadata, 60936 for Key). A corpus test cannot see a rewrite level or a dispatch branch.
using System;
using System.Collections.Generic;
using System.Linq;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
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
            + "so widening NCLMetadata.GetSnapshotOfAllObjects beyond Query and XmlPort could "
            + "change what this table answers — re-measure #4196 before widening.");
    }

    /// <summary>
    /// Claim 2, the positive half — and the control that stops the test above passing
    /// vacuously. These providers keep BC's OWN body, so they DO descend into the snapshot;
    /// that is why filling the snapshot for their object type serves their table with no
    /// row-building (Query #4147, XmlPort #4461).
    /// </summary>
    [Theory]
    [InlineData("QueryDataProvider")]    // 2000000142
    [InlineData("XmlPortDataProvider")]  // 2000000280
    public void BcWalkingProvidersStillReadTheSnapshot(string providerType)
    {
        using var asm = Ncl();
        Assert.True(
            BodyReachesSnapshotWalk(asm, providerType),
            $"{providerType}.GetValuesWithinRangeForKeyField no longer reaches "
            + "GetObjectNumberAndInfoWithinRange, so the snapshot substitution for its object "
            + "type reaches nothing and its table answers no rows.");
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
        Assert.DoesNotContain(tableId, BcVirtualProviderTableIds());
    }

    /// <summary>
    /// The table ids RecordPatches.GetDataAccessForTableCore actually hands to BC's own
    /// virtual-provider factory, READ OUT OF THE COMPILED DISPATCH CHAIN rather than listed
    /// here.
    ///
    /// <para>An earlier version of this file declared the answer as a second literal list, so
    /// the theory above compared one list in this file against another list in this file and
    /// could not fail. A reviewer proved it by adding a real branch routing AllObj — one of the
    /// protected six — to BC's factory and still getting 11 green (#4196). Deriving it from the
    /// IL is what makes "a table gained a BC provider" an assertion instead of a comment.</para>
    ///
    /// <para>Reading the id off each <c>Is*VirtualTable</c> predicate rather than off the
    /// dispatch body is deliberate: the dispatch compares through the predicate, so the constant
    /// only ever appears in the predicate, and that is also the single place a table's id is
    /// declared.</para>
    /// </summary>
    private static int[] BcVirtualProviderTableIds()
    {
        using var runner = AssemblyDefinition.ReadAssembly(
            typeof(RecordPatches).Assembly.Location);

        var core = runner.MainModule
            .GetType("AlRunner.Patches.RecordPatches")
            ?.Methods.FirstOrDefault(m => m.Name == "GetDataAccessForTableCore");
        Assert.NotNull(core);
        Assert.True(core!.HasBody, "GetDataAccessForTableCore has no body to read");

        var ids = new List<int>();
        var ins = core.Body.Instructions;
        for (var k = 0; k < ins.Count; k++)
        {
            if (ins[k].Operand is not MethodReference guard
                || !guard.Name.StartsWith("Is")
                || !(guard.Name.EndsWith("VirtualTable") || guard.Name.EndsWith("SystemTable")))
                continue;

            // Walk the guarded block forward to whichever comes first: BC's factory (this table
            // is served by BC's own provider) or a runner populate / temp-store creation (it is
            // not). Bounded so a guard whose block does neither cannot run into the next branch
            // and inherit its answer.
            var routed = false;
            for (var j = k + 1; j < Math.Min(k + 60, ins.Count); j++)
            {
                if (ins[j].Operand is not MethodReference call) continue;
                if (call.Name == "GetBcVirtualDataAccess"
                    || (call.Name.StartsWith("Get") && call.Name.EndsWith("VirtualDataAccess")))
                {
                    routed = true;
                    break;
                }
                if (call.Name.StartsWith("Populate") || call.Name.Contains("CreateTempDataAccess"))
                    break;
            }
            if (!routed) continue;

            var id = TableIdOf(guard.Resolve());
            Assert.True(id > 0, $"could not read the table id {guard.Name} compares against");
            ids.Add(id);
        }

        // The snapshot consumers plus Integer (2000000026) and Date (2000000007), which take
        // BC's factory too but consume no object snapshot.
        Assert.NotEmpty(ids);
        return ids.ToArray();
    }

    /// <summary>The constant an <c>Is*VirtualTable</c> predicate compares TableId against.</summary>
    private static int TableIdOf(MethodDefinition? predicate)
    {
        if (predicate?.HasBody != true) return -1;
        foreach (var i in predicate.Body.Instructions)
        {
            if (i.OpCode == OpCodes.Ldc_I4) return (int)i.Operand;
            if (i.Operand is FieldReference fr)
            {
                var fd = fr.Resolve();
                if (fd != null && fd.HasConstant && fd.Constant is int c) return c;
            }
        }
        return -1;
    }

    /// <summary>
    /// #4461 added XMLport Metadata (2000000280) as a fifth dispatched snapshot consumer. Its
    /// XmlPortDataProvider keeps BC's own body exactly as QueryDataProvider does, which is why
    /// the snapshot substitution carries ObjectType.XmlPort as well as ObjectType.Query.
    /// </summary>
    [Fact]
    public void OnlyQueryAndXmlPortMetadataAmongTheSnapshotConsumersKeepBcsOwnWalk()
    {
        using var asm = Ncl();

        var stillWalking = new[]
            {
                ("KeyDataProvider", RecordPatches.KeyVirtualTableId),
                ("PageActionDataProvider", RecordPatches.PageActionVirtualTableId),
                ("QueryDataProvider", RecordPatches.QueryMetadataVirtualTableId),
                ("TableRelationDataProvider", RecordPatches.TableRelationsMetadataVirtualTableId),
                ("XmlPortDataProvider", RecordPatches.XmlPortMetadataVirtualTableId),
            }
            .Where(p => BodyReachesSnapshotWalk(asm, p.Item1))
            .Select(p => p.Item2)
            .ToArray();

        Assert.Equal(
            new[] { RecordPatches.QueryMetadataVirtualTableId, RecordPatches.XmlPortMetadataVirtualTableId },
            stillWalking);
    }

    /// <summary>
    /// The dispatch half of the same claim: 2000000280 is handed to BC's own factory, read out
    /// of the compiled if-chain like the protected-table theory above. Without it
    /// XmlPortDataProvider is never constructed and the snapshot entry reaches nothing.
    /// </summary>
    [Fact]
    public void XmlPortMetadataIsRoutedToBcsVirtualProvider()
    {
        Assert.Contains(RecordPatches.XmlPortMetadataVirtualTableId, BcVirtualProviderTableIds());
    }
}
