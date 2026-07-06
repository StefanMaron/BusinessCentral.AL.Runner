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
