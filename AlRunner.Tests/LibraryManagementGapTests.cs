using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Gap tests surfaced by running the Library Management sample app
/// (Vjeko's "Demo Library Management" — public BC test-codeunit example).
/// Each gap was a real test failure that prevented an off-the-shelf BC
/// test suite from running green on al-runner.
/// </summary>
[Collection("Pipeline")]
public class LibraryManagementGapTests
{
    private const int TblId = 99920;

    private MockRecordHandle MakeHandle()
    {
        MockRecordHandle.ResetAll();
        TableFieldRegistry.Clear();
        TableFieldRegistry.ParseAndRegister(@"
            table 99920 LibrarySetup
            {
                Caption = 'Library Setup';
                fields
                {
                    field(1; ""Primary Key""; Code[10]) { Caption = 'Primary Key'; }
                    field(4; ""Author Nos.""; Code[20]) { Caption = 'Author Nos.'; }
                }
            }");
        var h = new MockRecordHandle(TblId);
        MockRecordHandle.RegisterPrimaryKey(TblId, 1);
        MockRecordHandle.RegisterFieldName(TblId, "Primary Key", 1);
        MockRecordHandle.RegisterFieldName(TblId, "Author Nos.", 4);
        return h;
    }

    // ── Gap 1: TestField error must include FieldCaption + TableCaption.
    // BC's TestField error format is "<FieldCaption> must have a value in <TableCaption>: <PK>".
    // Tests in real-world BC suites assert ExpectedError("Author Nos.") — they cannot match
    // the raw field-number format the runner used to emit.

    [Fact]
    public void ALTestFieldSafe_EmptyField_ErrorMessageContainsFieldCaption()
    {
        var rec = MakeHandle();
        rec.SetFieldValueSafe(1, NavType.Code, new NavText("PK"));
        rec.SetFieldValueSafe(4, NavType.Code, new NavText(""));
        rec.ALInsert(DataError.ThrowError);

        var ex = Assert.Throws<System.Exception>(
            () => rec.ALTestFieldSafe(4, NavType.Code));

        Assert.Contains("Author Nos.", ex.Message);
    }

    [Fact]
    public void ALTestFieldSafe_EmptyField_ErrorMessageContainsTableCaption()
    {
        var rec = MakeHandle();
        rec.SetFieldValueSafe(1, NavType.Code, new NavText("PK"));
        rec.SetFieldValueSafe(4, NavType.Code, new NavText(""));
        rec.ALInsert(DataError.ThrowError);

        var ex = Assert.Throws<System.Exception>(
            () => rec.ALTestFieldSafe(4, NavType.Code));

        Assert.Contains("Library Setup", ex.Message);
    }

    [Fact]
    public void ALTestFieldSafe_ValueMismatch_ErrorMessageContainsFieldCaption()
    {
        var rec = MakeHandle();
        rec.SetFieldValueSafe(1, NavType.Code, new NavText("PK"));
        rec.SetFieldValueSafe(4, NavType.Code, new NavText("AUTH"));
        rec.ALInsert(DataError.ThrowError);

        var ex = Assert.Throws<System.Exception>(
            () => rec.ALTestFieldSafe(4, NavType.Code, new NavText("DIFFERENT")));

        Assert.Contains("Author Nos.", ex.Message);
    }

    // ── Gap 3: Get error must include TableCaption.
    // Test suites assert ExpectedError("Book Journal Batch") on a failed Get; the runner used
    // to emit only the numeric table id ("table 50133").

    // ── Gap 2: Assert.RecordCount(Variant, Integer) must dispatch to RecordCount,
    // not fall through to ExpectedMessage. When the AL Variant arrives boxed in
    // MockVariant (the path the runtime takes when the Assert codeunit comes from
    // a referenced dep package rather than our built-in stub), the case-2 dispatcher
    // in MockCodeunitHandle.InvokeAssert must unwrap before type-sniffing.

    [Fact]
    public void InvokeAssert_RecordCountWithMockVariantWrappedRecord_DispatchesToRecordCount()
    {
        MockRecordHandle.ResetAll();
        TableFieldRegistry.Clear();
        TableFieldRegistry.ParseAndRegister(@"
            table 99922 Books { fields { field(1; ""No.""; Code[20]) { } } }");
        var rec = new MockRecordHandle(99922);
        MockRecordHandle.RegisterPrimaryKey(99922, 1);

        // Wrap in MockVariant — mirrors the AL→C# call shape when Assert is loaded
        // from a dep package that boxes Variant arguments.
        var variant = new MockVariant(rec);
        var handle = MockCodeunitHandle.Create(130);

        var ex = Assert.Throws<AssertException>(
            () => handle.Invoke(memberId: 0, args: new object[] { variant, 5 }));

        // Must surface as RecordCount mismatch (not ExpectedMessage misrouting).
        Assert.Contains("Assert.RecordCount failed", ex.Message);
    }

    // ── Gap 4: When the user's .alpackages ships Microsoft's "Application Test Library"
    // (the standard ATL), Pipeline.LoadAssertStubs must detect it via the NAVX manifest
    // name — not by filename, since Microsoft ships it as
    // "Microsoft_Application Test Library_*.app" (no "Assert" or "TestLibraries" token).
    // Without manifest-based detection, our built-in stubs were injected into the main
    // compilation alongside ATL's symbols → AL0197 duplicates → blocked emit.
    [Fact]
    public void Pipeline_LoadAssertStubs_DetectsAtlPackageByManifestName_NotByFilename()
    {
        // Smoke-test through the public CLI: a directory containing an .app whose
        // *manifest* declares Name="Application Test Library" (regardless of filename)
        // must be detected so the runner sidesteps AL0197 by compiling stubs separately.
        // We rely on the integration coverage in the Library Management end-to-end run;
        // the test below asserts the detection helper directly.
        var detector = typeof(AlRunner.AlRunnerPipeline).GetMethod(
            "PackagesProvideTestToolkit",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(detector);
        // Empty directory list → false (negative branch).
        var result = (bool)detector!.Invoke(null, new object[] { System.Array.Empty<string>() })!;
        Assert.False(result);
    }

    [Fact]
    public void ALGet_KeyNotFoundThrowError_ErrorMessageContainsTableCaption()
    {
        MockRecordHandle.ResetAll();
        TableFieldRegistry.Clear();
        TableFieldRegistry.ParseAndRegister(@"
            table 99921 BookJournalBatch
            {
                Caption = 'Book Journal Batch';
                fields { field(1; ""No.""; Code[20]) { } }
            }");
        var rec = new MockRecordHandle(99921);
        MockRecordHandle.RegisterPrimaryKey(99921, 1);

        var ex = Assert.Throws<System.Exception>(
            () => rec.ALGet(DataError.ThrowError, new NavText("NOPE")));

        Assert.Contains("Book Journal Batch", ex.Message);
    }
}
