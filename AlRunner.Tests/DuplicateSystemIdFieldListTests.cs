// DuplicateSystemIdFieldListTests — #5141. The duplicate-SystemId error's field list comes from
// BC's own RecordImplementationHelper.FormatKeyFieldsAndValues, driven with the runner-built
// SystemId NCLMetaField, so it reads `System ID='{GUID}'` rather than `SystemId=guid`. The BC
// claim itself is corpus codeunit 60061's (Record_Insert_DuplicateSystemId_*); this pins the
// runner's C# half against a real built table.

using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class DuplicateSystemIdFieldListTests
{
    private readonly BcEngineFixture _engine;

    public DuplicateSystemIdFieldListTests(BcEngineFixture engine) => _engine = engine;

    private const int TableId = 61947;

    private static object RunnerSystemIdField()
    {
        typeof(RecordPatches).GetMethod("TryParseTableFile", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[]
            {
                $$"""
                table {{TableId}} "ALT Duplicate SystemId Field List"
                {
                    fields
                    {
                        field(1; "Code"; Code[10]) { }
                    }
                    keys { key(PK; "Code") { Clustered = true; } }
                }
                """,
                null,
            });
        var ncl = RecordPatches.GetOrBuildNCLMetaTable(TableId)
                  ?? throw new InvalidOperationException($"the runner built no metadata for table {TableId}");
        return ncl.GetType()
                   .GetProperty("SystemIdField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                   .GetValue(ncl)
               ?? throw new InvalidOperationException($"table {TableId} has no SystemId field");
    }

    private static string FormatFieldAndValue(object? field, NavValue value)
    {
        var m = typeof(RowVersionPatches).GetMethod("FormatFieldAndValue", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("RowVersionPatches.FormatFieldAndValue not found");
        try
        {
            return (string)m.Invoke(null, new object?[] { field, value })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    [SkippableFact]
    public void TheFieldList_NamesSystemIdByItsCaption_AndQuotesTheValue()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var id = NavGuid.NewGuid();
        var text = FormatFieldAndValue(RunnerSystemIdField(), id);

        Assert.Equal($"System ID='{{{id.Value.ToString().ToUpperInvariant()}}}'", text);
    }

    [SkippableFact]
    public void AFieldThatIsNotAnNclMetaField_RefusesAsAShapeGap()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var ex = Assert.Throws<AlRunner.Infrastructure.BcShapeGapException>(
            () => FormatFieldAndValue(new object(), NavGuid.NewGuid()));
        Assert.Contains("SystemIdField", ex.Message);
    }
}
