// CalcFormulaSystemFieldResolutionTests — runner-mechanism guard for #3178.
//
// The gap
// -------
// Every BC table carries five system fields it does not declare — SystemId (2000000000),
// SystemCreatedAt (2000000001), SystemCreatedBy (2000000002), SystemModifiedAt (2000000003)
// and SystemModifiedBy (2000000004). RecordPatches materialises all five onto every
// NCLMetaTable it builds, but the field-name lookups in BuildMetaCalcFormula and in the
// TableRelation builder read ParsedTable.Fields only, which does not carry them. So a
// CalcFormula naming one resolved to nothing: an unresolvable SOURCE field lost the whole
// formula (CalcFields then raised BC's "You must define a CalcFormula ..."), while an
// unresolvable WHERE-ARM field was dropped and the FlowField still calculated, over the wrong
// rows. Seven Base Application FlowFields have that shape, and so does every API page's
// `TableRelation = <Table>.SystemId`.
//
// What this file pins
// -------------------
// The runner-only C# mechanism: that RecordPatches declares the system-field set ONCE and that
// its field-name resolver answers from it for a ParsedTable that does not list them. The
// BC-observable claims underneath — what `max(T.SystemCreatedAt where(...))` calculates, and
// that a TableRelation onto SystemId is enforced — are plain BC behaviour and are pinned
// upstream in the al-language corpus (record/TestCalcFormulaSystemFields*, corpus PR #216),
// verified on a BC 28.4.53241.0 container, not duplicated here.
//
// The invariant that matters is "resolver set == materialised set": resolving a name to an id
// the built NCLMetaTable does not carry would fault inside NCL instead of refusing loudly
// here, which is why SystemRowVersion is deliberately NOT in the set.

using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class CalcFormulaSystemFieldResolutionTests
{
    private static readonly Type RecordPatchesType = typeof(RecordPatches);

    private static ParsedField[] SystemParsedFields()
    {
        var f = RecordPatchesType.GetField("SystemParsedFields",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        return (ParsedField[])f!.GetValue(null)!;
    }

    private static bool TryResolve(ParsedTable table, string fieldName, out ParsedField field)
    {
        var m = RecordPatchesType.GetMethod("TryResolveTableFieldByName",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        var args = new object?[] { table, fieldName, null };
        var ok = (bool)m!.Invoke(null, args)!;
        field = ok ? (ParsedField)args[2]! : null!;
        return ok;
    }

    private static ParsedTable TableWithoutSystemFields() => new ParsedTable(
        50700, "CFSF Line",
        new List<ParsedField>
        {
            new ParsedField(1, "Entry No.", "Integer", 0),
            new ParsedField(2, "Doc No.", "Code", 20),
        },
        new List<int> { 1 });

    /// The set is exactly BC's five system fields, with BC's own ids and types. A sixth entry
    /// added here without also being materialised in BuildNCLMetaTable would resolve to an id
    /// the built table does not carry.
    [Fact]
    public void SystemParsedFields_AreExactlyBcsFiveSystemFields()
    {
        var expected = new (int Id, string Name, string Type)[]
        {
            (2000000000, "SystemId",         "Guid"),
            (2000000001, "SystemCreatedAt",  "DateTime"),
            (2000000002, "SystemCreatedBy",  "Guid"),
            (2000000003, "SystemModifiedAt", "DateTime"),
            (2000000004, "SystemModifiedBy", "Guid"),
        };

        var actual = SystemParsedFields();

        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Id, actual[i].FieldId);
            Assert.Equal(expected[i].Name, actual[i].FieldName);
            Assert.Equal(expected[i].Type, actual[i].TypeName);
        }
    }

    /// SystemRowVersion is NOT materialised onto a built table, so it must NOT resolve —
    /// answering an id NCL has no MetaField for is worse than refusing.
    [Fact]
    public void SystemRowVersion_DoesNotResolve()
    {
        Assert.False(TryResolve(TableWithoutSystemFields(), "SystemRowVersion", out _));
        Assert.DoesNotContain(SystemParsedFields(), f => f.FieldName == "SystemRowVersion");
    }

    [Theory]
    [InlineData("SystemId", 2000000000)]
    [InlineData("SystemCreatedAt", 2000000001)]
    [InlineData("SystemCreatedBy", 2000000002)]
    [InlineData("SystemModifiedAt", 2000000003)]
    [InlineData("SystemModifiedBy", 2000000004)]
    public void SystemFieldName_ResolvesOnATableThatDoesNotListIt(string fieldName, int expectedId)
    {
        var table = TableWithoutSystemFields();
        Assert.DoesNotContain(table.Fields, f => f.FieldName == fieldName);

        Assert.True(TryResolve(table, fieldName, out var resolved));
        Assert.Equal(expectedId, resolved.FieldId);
        Assert.Equal(fieldName, resolved.FieldName);
    }

    /// AL is case-insensitive about field names, and BC's own metadata spells these in camel
    /// case; a CalcFormula may not.
    [Fact]
    public void SystemFieldName_ResolvesCaseInsensitively()
    {
        Assert.True(TryResolve(TableWithoutSystemFields(), "systemcreatedat", out var resolved));
        Assert.Equal(2000000001, resolved.FieldId);
    }

    /// The table's own fields still win, and still resolve to their own ids.
    [Fact]
    public void DeclaredField_StillResolvesToItsOwnId()
    {
        Assert.True(TryResolve(TableWithoutSystemFields(), "Doc No.", out var resolved));
        Assert.Equal(2, resolved.FieldId);
        Assert.Equal("Code", resolved.TypeName);
    }

    /// A field that is neither declared nor a system field must stay unresolved, so the callers
    /// keep logging and refusing rather than silently building a formula over the wrong id.
    [Fact]
    public void UnknownFieldName_DoesNotResolve()
    {
        Assert.False(TryResolve(TableWithoutSystemFields(), "No Such Field", out _));
        Assert.False(TryResolve(TableWithoutSystemFields(), "SystemCreatedAtX", out _));
    }

    /// A table that DECLARES a field with a system field's name — legal in AL for a low field
    /// id — must resolve to its own field, not to the system one.
    [Fact]
    public void DeclaredFieldShadowingASystemFieldName_WinsOverTheSystemField()
    {
        var table = new ParsedTable(50701, "Shadowing",
            new List<ParsedField> { new ParsedField(7, "SystemCreatedBy", "Code", 50) },
            new List<int> { 7 });

        Assert.True(TryResolve(table, "SystemCreatedBy", out var resolved));
        Assert.Equal(7, resolved.FieldId);
    }
}
