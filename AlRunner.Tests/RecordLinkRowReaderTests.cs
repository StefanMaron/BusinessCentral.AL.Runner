// RecordLinkRowReaderTests — issue #4944.
//
// RUNNER-MECHANISM test. RecordPatches.ReadRecordLinkRows reads BC's private
// TempTableDataProvider.primaryTree, and GetRecordLinkStore reads its private `table`, to serve
// every Record Link surface (AddLink, HasLinks, DeleteLink(s), CopyLinks, the census). Both
// used `GetType().GetField(NonPublic)?.GetValue(...)`, so a bind that failed — BC renamed the
// field, or the provider was a subclass, whose base-class private field GetField does not
// return — read as "no links stored". These pin the two nulls apart: a failed BIND refuses with
// BcShapeGapException; a bound field whose VALUE is null is BC's own "nothing stored" and
// answers empty. What BC does with links is measured upstream (corpus codeunit 60777).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordLinkRowReaderTests
{
    private const string Surface = "Record Link (2000000068) link store";

    private readonly BcEngineFixture _engine;

    public RecordLinkRowReaderTests(BcEngineFixture engine) => _engine = engine;

    private void RequireEngine()
        => TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static object? Invoke(string name, object provider)
    {
        var m = typeof(RecordPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");
        try { return m.Invoke(null, new[] { provider }); }
        catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
    }

    private static List<NavValue[]> ReadRows(object provider)
        => (List<NavValue[]>)Invoke("ReadRecordLinkRows", provider)!;

    /// <summary>A TempTableRecordBuffer carrying <paramref name="width"/> slots, built without
    /// a metatable: ReadRecordLinkRows only calls ToArray(), which copies BC's private items.</summary>
    private static TempTableRecordBuffer Buffer(int width)
    {
        var buffer = (TempTableRecordBuffer)RuntimeHelpers.GetUninitializedObject(typeof(TempTableRecordBuffer));
        var items = PrivateMemberLookup.Field(typeof(TempTableRecordBuffer), "items")
                    ?? throw new InvalidOperationException("test setup: TempTableRecordBuffer.items not found");
        items.SetValue(buffer, new NavValue[width]);
        return buffer;
    }

    // ── A failed bind refuses ─────────────────────────────────────────────────────────

    [SkippableFact]
    public void Rows_ProviderWithoutPrimaryTree_Refuses_NotZeroLinks()
    {
        RequireEngine();
        var ex = Assert.Throws<BcShapeGapException>(() => ReadRows(new ProviderWithoutFields()));
        Assert.Equal(Surface, ex.Surface);
        Assert.Equal($"{nameof(ProviderWithoutFields)}.primaryTree", ex.Member);
    }

    [SkippableFact]
    public void Rows_PrimaryTreeHoldingANonEnumerable_Refuses()
    {
        RequireEngine();
        var ex = Assert.Throws<BcShapeGapException>(() => ReadRows(new ProviderWithOddShapes()));
        Assert.Equal(Surface, ex.Surface);
        Assert.Contains("cannot be enumerated", ex.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Table_ProviderWithoutTableField_Refuses()
    {
        RequireEngine();
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("ReadRecordLinkProviderTable", new ProviderWithoutFields()));
        Assert.Equal($"{nameof(ProviderWithoutFields)}.table", ex.Member);
    }

    [SkippableFact]
    public void Table_FieldHoldingSomethingElse_Refuses()
    {
        RequireEngine();
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("ReadRecordLinkProviderTable", new ProviderWithOddShapes()));
        Assert.Contains("not an NCLMetaTable", ex.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void SystemIdField_BindsOnThisBcBuild()
    {
        RequireEngine();
        var p = (PropertyInfo)typeof(RecordPatches)
            .GetMethod("RecordLinkSystemIdProperty", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;
        Assert.Equal("SystemIdField", p.Name);
        Assert.Equal(typeof(NCLMetaField), p.PropertyType);
    }

    // ── A bound field's null VALUE is an answer, not a refusal ────────────────────────

    [SkippableFact]
    public void Rows_NullPrimaryTree_IsNoLinksStored_AndDoesNotThrow()
    {
        RequireEngine();
        Assert.Empty(ReadRows(new FakeProvider(null)));
    }

    [SkippableFact]
    public void Table_NullValue_IsNoStore_AndDoesNotThrow()
    {
        RequireEngine();
        Assert.Null(Invoke("ReadRecordLinkProviderTable", new FakeProvider(null)));
    }

    // ── Rows that are there come back, including through a subclass ──────────────────

    [SkippableFact]
    public void Rows_PopulatedTree_ReturnsEveryBuffer()
    {
        RequireEngine();
        var rows = ReadRows(new FakeProvider(new List<object> { Buffer(3), Buffer(3) }));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(3, r.Length));
    }

    // The #4812 cause: GetField(NonPublic) on a derived type does not return a base class's
    // private field. Before #4944 this read zero rows from a store holding two.
    [SkippableFact]
    public void Rows_DerivedProvider_ReadsTheBaseClassField()
    {
        RequireEngine();
        var rows = ReadRows(new DerivedProvider(new List<object> { Buffer(2), Buffer(2) }));
        Assert.Equal(2, rows.Count);
    }

    // ── Reflected-shape fakes: same member names and private-instance shape as BC's ───

    private sealed class ProviderWithoutFields
    {
    }

#pragma warning disable CS0414, CS0169   // read only by the reflection under test
    private sealed class ProviderWithOddShapes
    {
        private readonly object primaryTree = 42;
        private readonly object table = "not a metatable";
    }

    private class FakeProvider
    {
        private readonly IEnumerable? primaryTree;
        private readonly NCLMetaTable? table;
        public FakeProvider(IEnumerable? rows) => primaryTree = rows;
    }
#pragma warning restore CS0414, CS0169

    private sealed class DerivedProvider : FakeProvider
    {
        public DerivedProvider(IEnumerable? rows) : base(rows) { }
    }
}
