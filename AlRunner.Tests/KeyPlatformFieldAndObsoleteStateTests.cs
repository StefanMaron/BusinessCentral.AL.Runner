// Runner-side mechanism tests for #5135 and #5136. The BC behaviour (a key on SystemRowVersion
// is enumerated; a Removed key is not) is measured by corpus codeunit 68540; these pin what each
// of the runner's four key readers hands BuildNCLMetaTable, so a reader that stops resolving a
// platform field or stops carrying ObsoleteState fails here by name.
using System.Collections;
using System.Reflection;
using System.Text.Json;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class KeyPlatformFieldAndObsoleteStateTests
{
    private const int TableId = 61935;
    private const string BaseName = "KPF Probe Base";

    private static readonly Type RecordPatchesType = typeof(RecordPatches);

    private static IDictionary Static(string name) =>
        (IDictionary)RecordPatchesType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    // ─── PlatformKeyFieldId ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SystemRowVersion", 0)]
    [InlineData("systemrowversion", 0)]
    [InlineData("SystemId", 2000000000)]
    [InlineData("SystemCreatedAt", 2000000001)]
    [InlineData("SystemCreatedBy", 2000000002)]
    [InlineData("SystemModifiedAt", 2000000003)]
    [InlineData("SystemModifiedBy", 2000000004)]
    public void PlatformKeyFieldId_PlatformField_ResolvesToItsId(string name, int expected)
        => Assert.Equal(expected, RecordPatches.PlatformKeyFieldId(name));

    [Theory]
    [InlineData("timestamp")] // the metadata name, not an AL identifier a key may write
    [InlineData("Ext Rank")]
    [InlineData("")]
    [InlineData(null)]
    public void PlatformKeyFieldId_AnyOtherName_IsNull(string? name)
        => Assert.Null(RecordPatches.PlatformKeyFieldId(name));

    // ─── AL source: a table's own keys ────────────────────────────────────────────────────

    private static readonly string TableSource = $$"""
        table {{TableId}} "KPF Probe Table"
        {
            fields
            {
                field(1; "Code"; Code[10]) { }
                field(2; "Live"; Integer) { }
                field(3; "Gone"; Integer) { ObsoleteState = Removed; ObsoleteReason = 'gone in probe'; }
            }
            keys
            {
                key(PK; "Code") { Clustered = true; }
                key(RowVersionKey; SystemRowVersion) { }
                key(ModifiedAtKey; SystemModifiedAt, "Live") { }
                key(GoneKey; "Gone") { ObsoleteState = Removed; ObsoleteReason = 'gone in probe'; }
                key(PendingKey; "Live") { ObsoleteState = Pending; }
                key(BrokenKey; "No Such Field") { }
            }
        }
        """;

    private static ParsedTable ParseTable()
    {
        RecordPatchesType.GetMethod("TryParseTableFile", BindingFlags.NonPublic | BindingFlags.Static)!
            .InvokeStatic(TableSource);
        var tables = Static("_parsedTables");
        Assert.True(tables.Contains(TableId), $"table {TableId} was not parsed at all");
        return (ParsedTable)tables[TableId]!;
    }

    [Fact]
    public void SourceTableKey_OnPlatformFields_ResolvesEveryPosition()
    {
        try
        {
            var keys = ParseTable().SecondaryKeys!.ToDictionary(k => k.Name);
            Assert.Equal(new[] { 0 }, keys["RowVersionKey"].FieldIds);
            Assert.Equal(new[] { 2000000003, 2 }, keys["ModifiedAtKey"].FieldIds);
            // A name that is neither declared nor a platform field still resolves to nothing.
            Assert.False(keys.ContainsKey("BrokenKey"));
        }
        finally { Static("_parsedTables").Remove(TableId); }
    }

    [Fact]
    public void SourceTableKey_ObsoleteState_IsCarried()
    {
        try
        {
            var parsed = ParseTable();
            var keys = parsed.SecondaryKeys!.ToDictionary(k => k.Name);
            Assert.Equal("Removed", keys["GoneKey"].ObsoleteState);
            Assert.Equal("gone in probe", keys["GoneKey"].ObsoleteReason);
            Assert.Equal("Pending", keys["PendingKey"].ObsoleteState);
            Assert.Equal("No", keys["RowVersionKey"].ObsoleteState);
            Assert.Null(keys["RowVersionKey"].ObsoleteReason);
            Assert.Equal("No", parsed.PrimaryKey!.ObsoleteState);
        }
        finally { Static("_parsedTables").Remove(TableId); }
    }

    // ─── AL source: a tableextension's keys ───────────────────────────────────────────────

    [Fact]
    public void SourceTableExtensionKey_ObsoleteState_IsCarried()
    {
        var source = $$"""
            tableextension {{TableId}} "KPF Probe Ext" extends "{{BaseName}}"
            {
                fields
                {
                    field(50100; "Legacy"; Integer) { ObsoleteState = Removed; }
                }
                keys
                {
                    key(LegacyKey; "Legacy") { ObsoleteState = Removed; ObsoleteReason = 'replaced'; }
                    key(RowVersionKey; SystemRowVersion) { }
                }
            }
            """;
        var baseKey = BaseName.ToLowerInvariant();
        try
        {
            RecordPatchesType.GetMethod("TryParseTableExtensionFile", BindingFlags.NonPublic | BindingFlags.Static)!
                .InvokeStatic(source);
            var keys = ((List<ParsedExtensionKey>)Static("_parsedExtensionKeys")[baseKey]!).ToDictionary(k => k.Name);

            Assert.Equal("Removed", keys["LegacyKey"].ObsoleteState);
            Assert.Equal("replaced", keys["LegacyKey"].ObsoleteReason);
            Assert.Equal("No", keys["RowVersionKey"].ObsoleteState);
            Assert.Equal(new[] { "SystemRowVersion" }, keys["RowVersionKey"].FieldNames);
        }
        finally
        {
            foreach (var name in new[] { "_parsedExtensionKeys", "_parsedExtensionFields",
                         "_extensionIdsByBaseTable", "_extensionSourceInfo" })
                Static(name).Remove(baseKey);
        }
    }

    // ─── SymbolReference.json: a precompiled table's keys and a precompiled extension's ───

    private static object? InvokeSymbolParser(string method, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return typeof(BcAppSymbolCache).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { doc.RootElement.Clone() });
    }

    [Fact]
    public void SymbolTableKey_OnPlatformField_ResolvesAndCarriesObsoleteState()
    {
        var table = (ParsedTable)InvokeSymbolParser("TryParseTableSymbol", $$"""
            {
              "Id": {{TableId}}, "Name": "KPF Symbol Table",
              "Fields": [
                { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code[10]" }, "Properties": [] },
                { "Id": 3, "Name": "Gone", "TypeDefinition": { "Name": "Integer" }, "Properties": [] }
              ],
              "Keys": [
                { "Name": "PK", "FieldNames": ["Code"] },
                { "Name": "ModifiedAtKey", "FieldNames": ["SystemModifiedAt"] },
                { "Name": "GoneKey", "FieldNames": ["Gone"], "Properties": [
                    { "Name": "ObsoleteState", "Value": "Removed" },
                    { "Name": "ObsoleteReason", "Value": "gone in symbols" } ] }
              ]
            }
            """)!;
        var keys = table.SecondaryKeys!.ToDictionary(k => k.Name);
        Assert.Equal(new[] { 2000000003 }, keys["ModifiedAtKey"].FieldIds);
        Assert.Equal("No", keys["ModifiedAtKey"].ObsoleteState);
        Assert.Equal("Removed", keys["GoneKey"].ObsoleteState);
        Assert.Equal("gone in symbols", keys["GoneKey"].ObsoleteReason);
    }

    [Fact]
    public void SymbolTableExtensionKey_ObsoleteState_IsCarried()
    {
        var ext = (TableExtensionSymbol)InvokeSymbolParser("TryParseTableExtensionSymbol", $$"""
            {
              "Id": {{TableId}}, "Name": "KPF Symbol Ext", "TargetObject": "{{BaseName}}",
              "Fields": [],
              "Keys": [
                { "Name": "LegacyKey", "FieldNames": ["Legacy"], "Properties": [
                    { "Name": "ObsoleteState", "Value": "Removed" } ] },
                { "Name": "LiveKey", "FieldNames": ["Live"] }
              ]
            }
            """)!;
        var keys = ext.Keys!.ToDictionary(k => k.Name);
        Assert.Equal("Removed", keys["LegacyKey"].ObsoleteState);
        Assert.Equal("No", keys["LiveKey"].ObsoleteState);
    }
}
