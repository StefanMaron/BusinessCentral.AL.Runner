using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The over-arity guard in MockRecordHandle.ALGet ("Too many key fields were
/// specified...") must only fire when the runner affirmatively knows the
/// table's primary key width. Sources of an understated PK width that must
/// NOT trigger the guard:
///  - quoted PK field names containing parentheses (e.g. "Amount (LCY)"),
///    which the key parser must handle without truncating the key list;
///  - tables never registered with the field registry (fallback PK [1]);
///  - auto-stub tables, whose synthesized single-field PK says nothing about
///    the real table's PK.
/// </summary>
public class GetKeyArityGuardTests
{
    private const int ParenPkTableId = 99761;
    private const int UnregisteredTableId = 99762;
    private const int StubTableId = 99763;
    private const int RegisteredTableId = 99764;

    [Fact]
    public void ParseAndRegister_QuotedPkFieldNameWithParens_RegistersFullPkWidth()
    {
        TableFieldRegistry.ParseAndRegister($$"""
            table {{ParenPkTableId}} "Paren Guard Table"
            {
                fields
                {
                    field(1; "Code (Main)"; Code[20]) { }
                    field(2; "Line No."; Integer) { }
                }
                keys
                {
                    key(PK; "Code (Main)", "Line No.") { Clustered = true; }
                }
            }
            """);

        var handle = new MockRecordHandle(ParenPkTableId);
        Assert.Equal(2, handle.GetPrimaryKeyFieldNos().Length);
    }

    [Fact]
    public void ALGet_UnregisteredTable_MoreValuesThanFallbackPk_DoesNotThrow()
    {
        var handle = new MockRecordHandle(UnregisteredTableId);

        // The [1] fallback PK is a guess, not the table's real PK width —
        // the guard must not reject a call the real table may well accept.
        var found = handle.ALGet(DataError.TrapError, new NavText("A"), NavInteger.Create(1));

        Assert.False(found);
    }

    [Fact]
    public void ALGet_AutoStubTable_MoreValuesThanSynthesizedPk_DoesNotThrow()
    {
        // Exact shape DepExtractor.GenerateStub emits for a missing table.
        TableFieldRegistry.ParseAndRegister(
            $"table {StubTableId} \"Stub Guard Table\" {{ /* {TableFieldRegistry.SynthesizedSchemaMarker} */ fields {{ field(1; \"DummyKey\"; Code[20]) {{}} }} keys {{ key(PK; \"DummyKey\") {{}} }} }}");

        var handle = new MockRecordHandle(StubTableId);
        var found = handle.ALGet(DataError.TrapError, new NavText("A"), NavInteger.Create(1));

        Assert.False(found);
    }

    [Fact]
    public void ALGet_RegisteredPk_MoreValuesThanPkFields_StillThrowsPlatformError()
    {
        TableFieldRegistry.ParseAndRegister($$"""
            table {{RegisteredTableId}} "Known PK Table"
            {
                fields
                {
                    field(1; "Code 1"; Code[20]) { }
                    field(2; "Int 1"; Integer) { }
                }
                keys
                {
                    key(PK; "Code 1", "Int 1") { Clustered = true; }
                }
            }
            """);

        var handle = new MockRecordHandle(RegisteredTableId);
        var ex = Assert.Throws<Exception>(() =>
            handle.ALGet(DataError.TrapError, new NavText("A"), NavInteger.Create(1), NavInteger.Create(42)));

        Assert.Contains("Too many key fields were specified", ex.Message);
        Assert.Contains("The number of fields in the primary key is 2.", ex.Message);
    }

    private const int UnderArityTableId = 99765;

    private static void RegisterUnderArityTable()
    {
        TableFieldRegistry.ParseAndRegister($$"""
            table {{UnderArityTableId}} "Under Arity Table"
            {
                fields
                {
                    field(1; "Code 1"; Code[20]) { }
                    field(2; "Int 1"; Integer) { }
                }
                keys
                {
                    key(PK; "Code 1", "Int 1") { Clustered = true; }
                }
            }
            """);
    }

    private static void InsertRow(string code1, int int1)
    {
        var handle = new MockRecordHandle(UnderArityTableId);
        handle.SetFieldValueSafe(1, NavType.Code, new NavCode(20, code1));
        handle.SetFieldValueSafe(2, NavType.Integer, NavInteger.Create(int1));
        handle.ALInsert(DataError.ThrowError);
    }

    [Fact]
    public void ALGet_RegisteredPk_FewerValues_BindsDefaultNotPrefix()
    {
        RegisterUnderArityTable();
        InsertRow("E", 5);

        var handle = new MockRecordHandle(UnderArityTableId);

        // Real BC binds the missing "Int 1" as 0 and looks up ('E', 0),
        // which does not exist. A prefix match would return the ('E', 5) row.
        Assert.False(handle.ALGet(DataError.TrapError, new NavCode(20, "E")));
    }

    [Fact]
    public void ALGet_RegisteredPk_FewerValues_FindsDefaultKeyedRow()
    {
        RegisterUnderArityTable();
        InsertRow("F", 5);
        InsertRow("F", 0);

        var handle = new MockRecordHandle(UnderArityTableId);
        var found = handle.ALGet(DataError.TrapError, new NavCode(20, "F"));

        // ('F', 5) sits first in insertion order — binding the default must
        // select the exact ('F', 0) row, not the first leading-key match.
        Assert.True(found);
        Assert.Equal(0, (int)(NavInteger)handle.GetFieldValueSafe(2, NavType.Integer));
    }

    [Fact]
    public void ALGet_UnderArity_ThrowError_MessageListsAllPkFieldsWithBoundDefaults()
    {
        RegisterUnderArityTable();

        var handle = new MockRecordHandle(UnderArityTableId);
        var ex = Assert.Throws<Exception>(() =>
            handle.ALGet(DataError.ThrowError, new NavCode(20, "Q")));

        // Container-verified on BC 28.1: every PK field is listed by name
        // with its value — including the bound trailing default — joined
        // with a comma and no space.
        Assert.Contains(
            "does not exist. Identification fields and values: Code 1='Q',Int 1='0'",
            ex.Message);
    }

    private const int DurationPkTableId = 99766;

    private static void RegisterDurationPkTable()
    {
        TableFieldRegistry.ParseAndRegister($$"""
            table {{DurationPkTableId}} "Duration PK Table"
            {
                fields
                {
                    field(1; "Code 1"; Code[20]) { }
                    field(2; "Dur 1"; Duration) { }
                }
                keys
                {
                    key(PK; "Code 1", "Dur 1") { Clustered = true; }
                }
            }
            """);
    }

    private static void InsertDurationRow(string code1, NavDuration dur1)
    {
        var handle = new MockRecordHandle(DurationPkTableId);
        handle.SetFieldValueSafe(1, NavType.Code, new NavCode(20, code1));
        handle.SetFieldValueSafe(2, NavType.Duration, dur1);
        handle.ALInsert(DataError.ThrowError);
    }

    [Fact]
    public void ALGet_DurationPkField_UnderArity_BindsDefaultNotPrefix()
    {
        RegisterDurationPkTable();
        InsertDurationRow("H", 5000L);

        var handle = new MockRecordHandle(DurationPkTableId);

        // Real BC binds the missing "Dur 1" as the Duration default 0 and
        // looks up ('H', 0), which does not exist. A prefix match would
        // return the ('H', 5000ms) row.
        Assert.False(handle.ALGet(DataError.TrapError, new NavCode(20, "H")));
    }

    [Fact]
    public void ALGet_DurationPkField_UnderArity_FindsExplicitDefaultKeyedRowAmongSiblings()
    {
        RegisterDurationPkTable();
        // Non-default duration first, so a prefix match would surface it;
        // the default row stores its value EXPLICITLY so the bound default
        // and the stored value must stringify identically to match.
        InsertDurationRow("G", 5000L);
        InsertDurationRow("G", NavDuration.Default);

        var handle = new MockRecordHandle(DurationPkTableId);
        Assert.True(handle.ALGet(DataError.TrapError, new NavCode(20, "G")));
        Assert.Equal(0L, (long)(NavDuration)handle.GetFieldValueSafe(2, NavType.Duration));
    }

    private const int AbsentTrailingPkTableId = 99767;

    [Fact]
    public void ALGet_FullArity_FindsRowWithUnassignedTrailingPkField()
    {
        TableFieldRegistry.ParseAndRegister($$"""
            table {{AbsentTrailingPkTableId}} "Absent Trailing PK Table"
            {
                fields
                {
                    field(1; "Code 1"; Code[20]) { }
                    field(2; "Int 1"; Integer) { }
                }
                keys
                {
                    key(PK; "Code 1", "Int 1") { Clustered = true; }
                }
            }
            """);

        // "Int 1" is never assigned, so the stored row has no entry for it.
        var insert = new MockRecordHandle(AbsentTrailingPkTableId);
        insert.SetFieldValueSafe(1, NavType.Code, new NavCode(20, "F"));
        insert.ALInsert(DataError.ThrowError);

        var handle = new MockRecordHandle(AbsentTrailingPkTableId);

        // Real BC stores the unassigned field as its default 0, so the row
        // is keyed ('F', 0): the explicit-default lookup must find it and
        // a non-default lookup must not — the absent entry compares as the
        // field type's default, not as "".
        Assert.True(handle.ALGet(DataError.TrapError, new NavCode(20, "F"), NavInteger.Create(0)));
        Assert.False(handle.ALGet(DataError.TrapError, new NavCode(20, "F"), NavInteger.Create(5)));
    }

    private const int ZeroArgTableId = 99768;

    [Fact]
    public void ALGet_ZeroKeyValues_BindsAllPkDefaults()
    {
        TableFieldRegistry.ParseAndRegister($$"""
            table {{ZeroArgTableId}} "Zero Arg Get Table"
            {
                fields
                {
                    field(1; "Code 1"; Code[20]) { }
                    field(2; "Int 1"; Integer) { }
                }
                keys
                {
                    key(PK; "Code 1", "Int 1") { Clustered = true; }
                }
            }
            """);

        var insert = new MockRecordHandle(ZeroArgTableId);
        insert.SetFieldValueSafe(1, NavType.Code, new NavCode(20, "SETUP"));
        insert.SetFieldValueSafe(2, NavType.Integer, NavInteger.Create(0));
        insert.ALInsert(DataError.ThrowError);

        var handle = new MockRecordHandle(ZeroArgTableId);

        // Get() binds every PK field to its type default — ('', 0). The
        // 'SETUP' row must not match (the old first-row semantics would
        // return it).
        Assert.False(handle.ALGet(DataError.TrapError));

        var insertBlank = new MockRecordHandle(ZeroArgTableId);
        insertBlank.SetFieldValueSafe(1, NavType.Code, new NavCode(20, ""));
        insertBlank.SetFieldValueSafe(2, NavType.Integer, NavInteger.Create(0));
        insertBlank.ALInsert(DataError.ThrowError);

        Assert.True(handle.ALGet(DataError.TrapError));
        Assert.Equal("", (string)(NavCode)handle.GetFieldValueSafe(1, NavType.Code));
    }

    [Fact]
    public void ExtractDeps_MissingTableStub_CarriesSynthesizedSchemaMarker()
    {
        // Missing-object detection runs in the trial-compile fixup rounds,
        // so this scenario needs alc (same skip rule as DuplicatePackageTests).
        if (AlcPathResolver.Default == null) return;

        var depRoot = Path.Combine(Path.GetTempPath(), "al-dep-mark-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-out-mark-" + Guid.NewGuid().ToString("N")[..8]);
        var extDir = Path.Combine(Path.GetTempPath(), "al-ext-mark-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // App-structured dep layout — stub emission requires a consumer
            // app to attach the generated file to.
            var depDir = Path.Combine(depRoot, "DepApp");
            Directory.CreateDirectory(depDir);
            Directory.CreateDirectory(extDir);

            // Dependency codeunit references a table that exists nowhere —
            // DepExtractor must emit a stub for it.
            File.WriteAllText(Path.Combine(depDir, "Consumer.al"), """
                codeunit 50020 "Ghost Consumer"
                {
                    procedure Run()
                    var Ghost: Record "Ghost Guard Table";
                    begin Ghost.FindFirst(); end;
                }
                """);
            File.WriteAllText(Path.Combine(extDir, "MyExt.al"), """
                codeunit 50021 "Marker Ext"
                {
                    procedure Run()
                    var C: Codeunit "Ghost Consumer";
                    begin C.Run(); end;
                }
                """);

            int rc = DepExtractor.ExtractDeps(extDir, new[] { depRoot }, outDir);

            Assert.Equal(0, rc);
            var files = Directory.GetFiles(outDir, "*.al", SearchOption.AllDirectories);
            var stub = files.Select(File.ReadAllText)
                .SingleOrDefault(t => t.Contains("Ghost Guard Table") && t.Contains("table "));
            Assert.NotNull(stub);
            Assert.Contains(TableFieldRegistry.SynthesizedSchemaMarker, stub);
        }
        finally
        {
            foreach (var d in new[] { depRoot, outDir, extDir })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }
}
