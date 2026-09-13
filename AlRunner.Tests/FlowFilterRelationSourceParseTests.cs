// FlowFilterRelationSourceParseTests — runner-mechanism guard for #2789 on the AL-SOURCE parser.
//
// BC keeps a TableRelation declared on a FlowFilter or FlowField (FieldRef.Relation answers it,
// Validate checks it; corpus codeunit 60483 asserts that on a service tier) and excludes
// non-Normal fields from rename propagation at the consumer, by FieldClass. The runner's three
// readers of the property all dropped it for those two classes. BcAppSymbolCacheTableExtRelationTests
// pins the two symbol-file loops; this file pins the AL-source parser, so the three readers
// cannot disagree about the same declaration.
//
// Drives the real parser by reflection (TryParseTableFile), so it joins
// RecordPatchesSerialCollection — see ParserStaticsIsolationGuardTests.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class FlowFilterRelationSourceParseTests
{
    private const int TableId = 61789;

    private static ParsedTable Parse()
    {
        var text = $$"""
            table {{TableId}} "FlowFilter Relation Parse"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; "Location Filter"; Code[10])
                    {
                        FieldClass = FlowFilter;
                        TableRelation = Location;
                    }
                    field(3; "Account Id"; Guid)
                    {
                        FieldClass = FlowField;
                        CalcFormula = lookup("G/L Account".SystemId where("No." = field("No.")));
                        TableRelation = "G/L Account".SystemId;
                    }
                    field(4; "Plain Filter"; Code[10])
                    {
                        FieldClass = FlowFilter;
                    }
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """;
        typeof(RecordPatches)
            .GetMethod("TryParseTableFile", BindingFlags.NonPublic | BindingFlags.Static)!
            .InvokeStatic(text);
        var tables = (Dictionary<int, ParsedTable>)typeof(RecordPatches)
            .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        return tables[TableId];
    }

    [Fact]
    public void FlowFilterTableRelation_IsCarried()
    {
        var field = Parse().Fields.Single(f => f.FieldId == 2);

        Assert.True(field.IsFlowFilter);
        Assert.NotNull(field.RelationArms);
        Assert.Equal("Location", Assert.Single(field.RelationArms!).TableName);
    }

    [Fact]
    public void FlowFieldTableRelation_IsCarried()
    {
        var field = Parse().Fields.Single(f => f.FieldId == 3);

        Assert.True(field.IsFlowField);
        Assert.NotNull(field.RelationArms);
        Assert.Equal("G/L Account", Assert.Single(field.RelationArms!).TableName);
    }

    [Fact]
    public void FlowFilterWithoutTableRelation_KeepsNullArms()
    {
        var field = Parse().Fields.Single(f => f.FieldId == 4);

        Assert.True(field.IsFlowFilter);
        Assert.Null(field.RelationArms);
    }
}
