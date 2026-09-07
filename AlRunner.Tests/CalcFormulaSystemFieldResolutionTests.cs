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
// here.
//
// #3307 — that invariant is about IDS, not about which array a name lives in, and this file
// used to conflate the two. SystemRowVersion was asserted here as deliberately unresolvable,
// on the reading that it is a sixth system field at id 2000000005 the runner does not
// materialise. Both halves were wrong: the AL compiler synthesizes it at field id 0 with
// metadata name `timestamp`, and no field 2000000005 exists anywhere in BC. Since
// BuildNCLMetaTable already materialises id 0 as the synthetic `timestamp` column, resolving
// SystemRowVersion to 0 SATISFIES the invariant — the built table does carry that id. It stays
// out of SystemParsedFields because that array is what gets APPENDED, and appending id 0 a
// second time would corrupt the field layout. Resolvable, not appended.

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

    /// #3307 — SystemRowVersion resolves, and it resolves to field id 0.
    ///
    /// It used to be asserted here as deliberately UNRESOLVABLE, on the reading that it was a
    /// sixth system field at id 2000000005 that the runner does not materialise. That reading
    /// was wrong in both halves. Microsoft's AL compiler synthesizes it at field id 0 with
    /// metadata name `timestamp` — SynthesizedFieldHelper.AppendSystemFields in
    /// Microsoft.Dynamics.Nav.CodeAnalysis:
    ///
    ///     if (runtimeVersionOrCurrent >= RuntimeVersion.Fall2022)
    ///         builder.Add(SynthesizedFieldSymbol.Create(
    ///             owner, 0, "SystemRowVersion", NavCorLib.BigIntegerType, "timestamp"));
    ///
    /// and there is no field 2000000005 anywhere: NCL's NCLMetaTable.SetSystemFields switches
    /// on 2000000000-2000000004 with no 2000000005 case, and NCL carries no SystemRowVersion
    /// string at all.
    ///
    /// So the resolver-set == materialised-set invariant is SATISFIED by resolving it, not by
    /// refusing it: BuildNCLMetaTable already puts id 0 in the MetaField[] as the synthetic
    /// `timestamp` column, so id 0 is a field the built table genuinely carries.
    [Fact]
    public void SystemRowVersion_ResolvesToFieldZero()
    {
        Assert.True(TryResolve(TableWithoutSystemFields(), "SystemRowVersion", out var resolved));
        Assert.Equal(0, resolved.FieldId);
        Assert.Equal("SystemRowVersion", resolved.FieldName);
        Assert.Equal("BigInteger", resolved.TypeName);
    }

    /// It resolves case-insensitively, like every other field name in a CalcFormula.
    [Fact]
    public void SystemRowVersion_ResolvesCaseInsensitively()
    {
        Assert.True(TryResolve(TableWithoutSystemFields(), "SYSTEMROWVERSION", out var resolved));
        Assert.Equal(0, resolved.FieldId);
    }

    /// It must NOT be in SystemParsedFields, and that is a live constraint rather than
    /// bookkeeping: BuildNCLMetaTable APPENDS that array to a MetaField[] that already opens
    /// with the synthetic `timestamp` column at id 0. Adding SystemRowVersion there would put
    /// id 0 in the array twice and corrupt the field layout R2R-precompiled BC code holds
    /// offsets for. Resolvable, not appended.
    [Fact]
    public void SystemRowVersion_IsNotAppendedToTheMaterialisedSet()
    {
        Assert.DoesNotContain(SystemParsedFields(), f => f.FieldName == "SystemRowVersion");
        Assert.DoesNotContain(SystemParsedFields(), f => f.FieldId == 0);
    }

    /// `timestamp` is the field's METADATA name, not an AL identifier, so a CalcFormula may
    /// not name it. Only the AL-visible spelling resolves. Without this, "resolve field 0 by
    /// name" could be satisfied by opening the door to both spellings, which would accept AL
    /// the real compiler rejects.
    [Fact]
    public void MetadataNameTimestamp_DoesNotResolve()
    {
        Assert.False(TryResolve(TableWithoutSystemFields(), "timestamp", out _));
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
