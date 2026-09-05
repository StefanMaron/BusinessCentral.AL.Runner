// ObjectMetadataProviderRowProbeTests — contract tests for issue #2786.
//
// RecordPatches.ObjectMetadataSystemTable.ProviderHasAnyRow is the guard that decides
// whether the Object Metadata (2000000071) populator synthesises its 43 rows or leaves a
// --test-data-restored store alone. It answers that question by reading BC's PRIVATE
// TempTableDataProvider.primaryTree field by reflection, and it used to answer "no rows,
// go ahead and synthesise" for TWO different reasons that mean opposite things:
//
//   * primaryTree is null            — BC's own representation of "no row was ever
//                                      inserted". Synthesising is correct.
//   * the field is not there at all  — BC renamed or restructured it. The runner has no
//                                      idea what the store holds, and synthesising would
//                                      silently shadow real --test-data rows, disabling the
//                                      #2519 precedence rule with no diagnostic anywhere.
//
// Four sibling readers of the SAME private field already fail loud on the second case
// (RecordPatches.StoredTableCensus.cs x2 and RecordPatches.TransactionSnapshot.cs /
// RecordPatches.InstallBaseline.cs via RequiredField -> MissingFieldException;
// RowVersionPatches.SystemIdIntegrity.cs via PrivateMemberLookup -> InvalidOperationException
// naming the member). This one did not. See .claude/rules/loud-failures.md.
//
// WHY THESE LIVE HERE AND NOT IN THE UPSTREAM CORPUS
//   The claim is about how the RUNNER reflects on a BC private field, not about anything AL
//   code can observe from Business Central: on every BC version the runner supports,
//   TempTableDataProvider.primaryTree exists, so the loud branch is unreachable from AL and a
//   service tier has nothing to adjudicate. Runner-internal reflection-resolution behaviour
//   belongs in AlRunner.Tests — see .claude/rules/bc-behavior-tests-go-upstream.md, and the
//   same reasoning in RowVersionPatchesTests' header.
//
// The fakes below are reflected-shape POCOs, the pattern RowVersionPatchesTests uses: they
// reproduce the exact private-instance field shape the production reader walks, without
// needing a loaded BC runtime. ProviderHasAnyRow keeps no reflection cache, so no per-test
// static reset is needed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class ObjectMetadataProviderRowProbeTests
{
    private static bool ProviderHasAnyRow(object provider)
    {
        var m = typeof(RecordPatches).GetMethod(
            "ProviderHasAnyRow", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "test setup: RecordPatches.ProviderHasAnyRow(object) not found");
        try
        {
            return (bool)m.Invoke(null, new[] { provider })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // reflection wrapper is not part of the contract
        }
    }

    // ── The defect: a moved BC layout must not read as "no rows" ─────────────────────

    [Fact]
    public void MissingPrimaryTreeField_ThrowsNamingTheFieldAndTheTable()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => ProviderHasAnyRow(new ProviderWithoutPrimaryTree()));

        // Names the member that moved, so a BC layout change points at its own fix.
        Assert.Contains("primaryTree", ex.Message);
        Assert.Contains(nameof(ProviderWithoutPrimaryTree), ex.Message);
        // Names the surface and carries the docs/scope.md reason anchor the manifest matches on.
        Assert.Equal("Object Metadata (system table 2000000071)", ex.Api);
        Assert.StartsWith("object-metadata-system-table", ex.Reason);
        // Says WHY it refuses rather than guessing, so the message is actionable.
        Assert.Contains("--test-data", ex.Message);
    }

    // primaryTree present but holding something the runner cannot enumerate is the same
    // "layout moved" case wearing a different hat — it must not read as "no rows" either.
    [Fact]
    public void NonEnumerablePrimaryTree_ThrowsNamingTheFieldAndTheTable()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => ProviderHasAnyRow(new ProviderWithNonEnumerablePrimaryTree()));

        Assert.Contains("primaryTree", ex.Message);
        Assert.Equal("Object Metadata (system table 2000000071)", ex.Api);
        Assert.StartsWith("object-metadata-system-table", ex.Reason);
    }

    // ── The positive arms: BC's genuine answers still come back, unchanged ───────────
    // Without these, a fix that simply threw on every input would pass the two above.

    [Fact]
    public void NullPrimaryTree_IsBcsOwnNoRowsAndAnswersFalse()
    {
        Assert.False(ProviderHasAnyRow(new FakeTempTableDataProvider(null)));
    }

    [Fact]
    public void EmptyPrimaryTree_AnswersFalse()
    {
        Assert.False(ProviderHasAnyRow(new FakeTempTableDataProvider(new List<object>())));
    }

    [Fact]
    public void PopulatedPrimaryTree_AnswersTrue()
    {
        Assert.True(ProviderHasAnyRow(new FakeTempTableDataProvider(new List<object> { new() })));
    }

    // A single row is enough — the reader must short-circuit rather than count, because a
    // --test-data restore can put a large table here and this runs on the record-open path.
    [Fact]
    public void PopulatedPrimaryTree_StopsAtTheFirstRow()
    {
        var rows = new CountingRows(3);
        Assert.True(ProviderHasAnyRow(new FakeTempTableDataProvider(rows)));
        Assert.Equal(1, rows.Yielded);
    }

    // ── The derived-provider case (#2725) is "found", never "layout moved" ───────────
    // GetField(NonPublic) on a derived type does NOT return a base class's private field,
    // and BC's own CrmTableConnection.CrmTestDataProvider derives from TempTableDataProvider.
    // Reading the base's field as "absent" would have turned a perfectly readable store into
    // a hard failure once this reader started refusing.

    [Fact]
    public void DerivedProvider_ReadsTheBaseClassPrivateField_AndAnswersTrue()
    {
        Assert.True(ProviderHasAnyRow(new DerivedProvider(new List<object> { new() })));
    }

    [Fact]
    public void DerivedProvider_WithNullBaseClassPrivateField_AnswersFalse()
    {
        Assert.False(ProviderHasAnyRow(new DerivedProvider(null)));
    }

    // ── Reflected-shape fakes ────────────────────────────────────────────────────────

    private sealed class ProviderWithoutPrimaryTree
    {
    }

#pragma warning disable CS0414   // assigned and read only by the reflection under test
    private sealed class ProviderWithNonEnumerablePrimaryTree
    {
        private readonly object primaryTree = new();
    }

    // Same member name, same private-instance shape as BC's TempTableDataProvider.primaryTree.
    private sealed class FakeTempTableDataProvider
    {
        private readonly IEnumerable? primaryTree;
        public FakeTempTableDataProvider(IEnumerable? rows) => primaryTree = rows;
    }

    private class BaseDeclaresPrimaryTree
    {
        private readonly IEnumerable? primaryTree;
        protected BaseDeclaresPrimaryTree(IEnumerable? rows) => primaryTree = rows;
    }
#pragma warning restore CS0414

    private sealed class DerivedProvider : BaseDeclaresPrimaryTree
    {
        public DerivedProvider(IEnumerable? rows) : base(rows) { }
    }

    /// <summary>Counts how many rows the reader actually pulled.</summary>
    private sealed class CountingRows : IEnumerable
    {
        private readonly int _count;
        public CountingRows(int count) => _count = count;
        public int Yielded { get; private set; }

        public IEnumerator GetEnumerator()
        {
            for (var i = 0; i < _count; i++)
            {
                Yielded++;
                yield return new object();
            }
        }
    }
}
