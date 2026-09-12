// SystemFieldMetadataOracleTests — RED→GREEN guard for the SYSTEM-FIELD cluster of #3568.
//
// The runner appends BC's six platform-defined fields itself (SystemParsedFields plus the
// synthetic `timestamp` in RecordPatches.NclMetaTableBuilder.cs) and builds each one through
// MetaField's POSITIONAL constructor. BC builds the same six from boilerplate XML through
// MetaField(XmlNode, string). The two constructors do not agree, and where the positional one
// was never passed a value the ctor's own default stood — so four members were the ctor's
// default rather than BC's answer, on every table the runner builds:
//
//     TestRelations          False        vs BC True   (all six fields)
//     AllowInCustomizations  ToBeClassified vs AsReadOnly (the five 2000000000-block fields)
//     ValidateRelation       True         vs BC False  (SystemCreatedBy / SystemModifiedBy)
//     ClrType                ""           vs the real CLR type (all six)
//
// THE ORACLE IS BC'S OWN CODE, not a table written here. SystemFieldsHelper.SystemIdField,
// .SystemRowVersionField and .AuditFields are the MetaField[] BC's own runtime hands to
// MetaTable when it adds platform fields, so these tests read the expected value out of
// Microsoft.Dynamics.Nav.Types at run time and compare the runner's field against it. A BC
// version that changes one of these values changes both sides together, which is the point:
// the claim under test is "the runner's six agree with BC's six", not a list of constants
// somebody transcribed.
//
// Measured on 28.1.49838.53910 over Business Foundation + System Application (150 tables) by
// the metadata-equivalence harness: TestRelations 900 differences, AllowInCustomizations 750,
// ValidateRelation 300, and 750 of ClrType's 1,888. See docs/metadata-equivalence.md.

using System.Collections;
using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SystemFieldMetadataOracleTests
{
    private readonly BcEngineFixture _engine;

    public SystemFieldMetadataOracleTests(BcEngineFixture engine) => _engine = engine;

    /// <summary>The six field ids BC's platform adds, in the order BC's helper exposes them.</summary>
    private const int TimestampId = 0;
    private const int SystemIdId = 2000000000;
    private const int SystemCreatedAtId = 2000000001;
    private const int SystemCreatedById = 2000000002;
    private const int SystemModifiedAtId = 2000000003;
    private const int SystemModifiedById = 2000000004;

    private static Type MetaFieldType() =>
        Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaField, Microsoft.Dynamics.Nav.Types")
        ?? throw new InvalidOperationException("MetaField is not reachable.");

    private static Type SystemFieldsHelperType() =>
        Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.SystemFieldsHelper, Microsoft.Dynamics.Nav.Types")
        ?? throw new InvalidOperationException("SystemFieldsHelper is not reachable.");

    private static object? Read(object metaField, string property) =>
        MetaFieldType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(metaField);

    /// <summary>
    /// BC's own six platform MetaFields, keyed by id. This is the ORACLE — every expected value
    /// in this file comes from here rather than from a constant, so the assertions track BC.
    /// </summary>
    private static Dictionary<int, object> BcPlatformFields()
    {
        var helper = SystemFieldsHelperType();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        var result = new Dictionary<int, object>();

        void Absorb(string propertyName)
        {
            var value = helper.GetProperty(propertyName, flags)?.GetValue(null)
                        ?? throw new InvalidOperationException(
                            $"SystemFieldsHelper.{propertyName} is not reachable — the oracle for "
                            + "this test would be empty, which would make every assertion vacuous.");
            foreach (var field in value is Array array ? array.Cast<object>() : new[] { value })
                result[(int)Read(field, "Id")!] = field;
        }

        Absorb("SystemIdField");
        Absorb("SystemRowVersionField");
        Absorb("AuditFields");
        return result;
    }

    /// <summary>
    /// The runner's own six, read off a real built table. Table 2000000001 is not usable here
    /// (it is refused as a system table), so this drives an ordinary parsed table and reads the
    /// platform fields the builder appended to it.
    /// </summary>
    private static Dictionary<int, object> RunnerPlatformFields(int tableId)
    {
        var ncl = RecordPatches.GetOrBuildNCLMetaTable(tableId)
                  ?? throw new InvalidOperationException($"the runner built no metadata for table {tableId}");
        var original = ncl.GetType()
            .GetMethod("GetMetaTableOriginal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(ncl, null)!;
        var fields = (IEnumerable)original.GetType()
            .GetProperty("Fields", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(original)!;

        var result = new Dictionary<int, object>();
        foreach (var field in fields)
        {
            var id = (int)Read(field!, "Id")!;
            if (id == TimestampId || id >= SystemIdId) result[id] = field!;
        }
        return result;
    }

    private static int FixtureTable()
    {
        // A table the runner parses from AL source, so the builder runs its own append path
        // rather than replaying one of BC's emitted documents.
        const int tableId = 61893;
        var parse = typeof(RecordPatches).GetMethod("TryParseTableFile",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        parse.Invoke(null, new object?[]
        {
            // TryParseTableFile(string text, string? filePath = null) — reflection does not
            // apply optional-parameter defaults, so filePath is passed explicitly.
            $$"""
            table {{tableId}} "ALT System Field Oracle"
            {
                fields
                {
                    field(1; "Code"; Code[10]) { }
                    field(2; "Description"; Text[100]) { }
                }
                keys { key(PK; "Code") { Clustered = true; } }
            }
            """,
            null
        });
        return tableId;
    }

    /// <summary>
    /// The oracle is real. If SystemFieldsHelper stopped yielding fields — a renamed member, a
    /// BC version that moved them — every comparison below would silently compare an empty
    /// dictionary against an empty one and pass. This fails first instead.
    /// </summary>
    [SkippableFact]
    public void Bcs_own_platform_fields_are_readable_and_carry_the_values_the_runner_must_match()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bc = BcPlatformFields();
        Assert.Equal(6, bc.Count);

        // Every one of the six answers TestRelations = true, from the shared attribute block
        // BC formats into its boilerplate XML (TestTableRelation="1").
        foreach (var (id, field) in bc)
            Assert.True((bool)Read(field, "TestRelations")!, $"BC field {id} should answer TestRelations=true");

        // AsReadOnly on the 2000000000 block only — MetaField's ctor sets it from
        // `if (Id >= 2000000000)`, so the id-0 timestamp keeps ToBeClassified.
        Assert.Equal("ToBeClassified", Read(bc[TimestampId], "AllowInCustomizations")!.ToString());
        foreach (var id in new[] { SystemIdId, SystemCreatedAtId, SystemCreatedById, SystemModifiedAtId, SystemModifiedById })
            Assert.Equal("AsReadOnly", Read(bc[id], "AllowInCustomizations")!.ToString());

        // ValidateTableRelation="0" on exactly the two audit-by fields, which also carry the
        // relation to User (2000000120); the other four state "1" and carry none.
        Assert.Equal(false, Read(bc[SystemCreatedById], "ValidateRelation"));
        Assert.Equal(false, Read(bc[SystemModifiedById], "ValidateRelation"));
        Assert.Equal(true, Read(bc[SystemIdId], "ValidateRelation"));

        // ClrType is computed by MetaField's ctor from the declared Datatype.
        Assert.Equal("System.Guid", Read(bc[SystemIdId], "ClrType"));
        Assert.Equal("System.Int64", Read(bc[TimestampId], "ClrType"));
        Assert.Equal("System.DateTime", Read(bc[SystemCreatedAtId], "ClrType"));
    }

    /// <summary>
    /// TestRelations: BC answers true on all six. This was the ctor default (false) on all six,
    /// 900 differences across the 150 measured tables.
    /// </summary>
    [SkippableFact]
    public void Every_platform_field_answers_TestRelations_as_BC_does()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bc = BcPlatformFields();
        var runner = RunnerPlatformFields(FixtureTable());
        Assert.Equal(6, runner.Count);

        foreach (var (id, expected) in bc)
        {
            Assert.True(runner.ContainsKey(id), $"the runner built no platform field {id}");
            Assert.Equal(Read(expected, "TestRelations"), Read(runner[id], "TestRelations"));
        }
    }

    /// <summary>
    /// AllowInCustomizations: AsReadOnly on the five, ToBeClassified on the id-0 timestamp.
    /// Asserting BOTH halves is what makes this a test of the id rule rather than of a constant
    /// — blanket AsReadOnly would pass a five-field check and fail here.
    /// </summary>
    [SkippableFact]
    public void Every_platform_field_answers_AllowInCustomizations_as_BC_does()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bc = BcPlatformFields();
        var runner = RunnerPlatformFields(FixtureTable());

        foreach (var (id, expected) in bc)
        {
            Assert.True(runner.ContainsKey(id), $"the runner built no platform field {id}");
            Assert.Equal(
                Read(expected, "AllowInCustomizations")!.ToString(),
                Read(runner[id], "AllowInCustomizations")!.ToString());
        }

        // The negative half, stated separately so a regression to a blanket value is named.
        Assert.Equal("ToBeClassified", Read(runner[TimestampId], "AllowInCustomizations")!.ToString());
        Assert.Equal("AsReadOnly", Read(runner[SystemIdId], "AllowInCustomizations")!.ToString());
    }

    /// <summary>
    /// ValidateRelation: false on SystemCreatedBy/SystemModifiedBy, true on the other four.
    /// Both directions asserted — a blanket false would be as wrong as the blanket true that
    /// produced the 300 differences.
    /// </summary>
    [SkippableFact]
    public void Every_platform_field_answers_ValidateRelation_as_BC_does()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bc = BcPlatformFields();
        var runner = RunnerPlatformFields(FixtureTable());

        foreach (var (id, expected) in bc)
        {
            Assert.True(runner.ContainsKey(id), $"the runner built no platform field {id}");
            Assert.Equal(Read(expected, "ValidateRelation"), Read(runner[id], "ValidateRelation"));
        }

        Assert.Equal(false, Read(runner[SystemCreatedById], "ValidateRelation"));
        Assert.Equal(false, Read(runner[SystemModifiedById], "ValidateRelation"));
        Assert.Equal(true, Read(runner[SystemIdId], "ValidateRelation"));
        Assert.Equal(true, Read(runner[TimestampId], "ValidateRelation"));
    }

    /// <summary>
    /// ClrType: BC computes it in MetaField's ctor from the Datatype, so the runner must state
    /// the same string rather than the ctor's empty-string default. Reads BC's own answer per
    /// id — a hardcoded table here would be a second implementation of the thing under test.
    /// </summary>
    [SkippableFact]
    public void Every_platform_field_answers_ClrType_as_BC_does()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var bc = BcPlatformFields();
        var runner = RunnerPlatformFields(FixtureTable());

        foreach (var (id, expected) in bc)
        {
            Assert.True(runner.ContainsKey(id), $"the runner built no platform field {id}");
            var want = (string)Read(expected, "ClrType")!;
            Assert.False(string.IsNullOrEmpty(want),
                $"BC's ClrType for field {id} is empty — the oracle would assert nothing");
            Assert.Equal(want, Read(runner[id], "ClrType"));
        }
    }
}
