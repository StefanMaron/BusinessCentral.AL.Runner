// RecordLinkSystemIdTests — issue #4951.
//
// RUNNER-MECHANISM test. Every Record Link row the store writes needs a SystemId, read off BC's
// NCLMetaTable.SystemIdField. These pin the two ways that could silently become "no SystemId":
// a SystemIdField value of the wrong type (refuses, rather than casting to null), and a
// metatable with no SystemId slot at write time (refuses, rather than writing the row without
// one). That a written row carries a SystemId is pinned end to end by
// RecordLinkColumnsEndToEndTests, and against real BC by corpus codeunit 67681.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordLinkSystemIdTests
{
    private const string Surface = "Record Link (2000000068) link store";

    private readonly BcEngineFixture _engine;

    public RecordLinkSystemIdTests(BcEngineFixture engine) => _engine = engine;

    private void RequireEngine()
        => TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static object? Invoke(string name, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
    }

    [SkippableFact]
    public void Coerce_NullValue_IsNoSystemIdField()
    {
        RequireEngine();
        Assert.Null(Invoke("CoerceSystemIdField", new object?[] { null }));
    }

    [SkippableFact]
    public void Coerce_AMetaField_IsReturnedAsIs()
    {
        RequireEngine();
        var field = (NCLMetaField)RuntimeHelpers.GetUninitializedObject(typeof(NCLMetaField));
        Assert.Same(field, Invoke("CoerceSystemIdField", field));
    }

    [SkippableFact]
    public void Coerce_AValueOfAnotherType_Refuses_NotNull()
    {
        RequireEngine();
        var ex = Assert.Throws<BcShapeGapException>(() => Invoke("CoerceSystemIdField", "not a field"));
        Assert.Equal(Surface, ex.Surface);
        Assert.Equal("NCLMetaTable.SystemIdField", ex.Member);
        Assert.Contains("holds a String, not an NCLMetaField", ex.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void FreshSystemId_WithNoSystemIdField_Refuses_RatherThanWritingTheRowWithoutOne()
    {
        RequireEngine();
        var columnsType = typeof(RecordPatches).GetNestedType("RecordLinkColumns", BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("test setup: RecordLinkColumns not found");
        var columns = Activator.CreateInstance(columnsType, nonPublic: true)!;
        var row = new NavValue[4];

        var ex = Assert.Throws<BcShapeGapException>(() => Invoke("SetFreshSystemId", row, columns));
        Assert.Equal("NCLMetaTable.SystemIdField", ex.Member);
        Assert.Contains("written with no SystemId", ex.Detail, StringComparison.Ordinal);
        Assert.All(row, Assert.Null);
    }
}
