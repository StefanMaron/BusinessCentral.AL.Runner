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

    // ── Gap 4: When any package in scope declares a codeunit our built-in test-toolkit
    // stubs also declare (Assert, Library - Random, Library - Test Initialize,
    // Library - Utility, Library - Variable Storage, Any, AL Runner Config),
    // Pipeline.LoadAssertStubs must detect it and compile our stubs separately to
    // avoid AL0197 duplicates → blocked emit. Detection is symbol-driven (reads
    // SymbolReference.json from each .app) — not filename, not manifest-name —
    // so it is robust against package renames, localisation, and third-party forks.

    [Fact]
    public void Pipeline_PackagesProvideTestToolkit_ReturnsFalseWhenNoPackages()
    {
        var detector = typeof(AlRunner.AlRunnerPipeline).GetMethod(
            "PackagesProvideTestToolkit",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(detector);
        var result = (bool)detector!.Invoke(null, new object[] { System.Array.Empty<string>() })!;
        Assert.False(result);
    }

    [Fact]
    public void Pipeline_PackagesProvideTestToolkit_DetectsByCodeunitSymbolOverlap()
    {
        // Build a synthetic .app whose SymbolReference.json declares codeunit "Assert".
        // The package's filename and manifest Name are deliberately unrelated to
        // "Assert" / "Test Library" / "TestLibraries" to prove detection is driven
        // by the symbol table, not by string matching on names.
        var tmpDir = Path.Combine(Path.GetTempPath(), "altr-stub-overlap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var appPath = Path.Combine(tmpDir, "Acme_TotallyUnrelatedName_1.0.0.0.app");
            BuildSyntheticApp(appPath, manifestName: "TotallyUnrelatedName", codeunitNames: new[] { "Assert" });

            var detector = typeof(AlRunner.AlRunnerPipeline).GetMethod(
                "PackagesProvideTestToolkit",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = (bool)detector!.Invoke(null, new object[] { new[] { tmpDir } })!;

            Assert.True(result,
                "PackagesProvideTestToolkit must detect a package that declares codeunit 'Assert' " +
                "regardless of the package filename or manifest name (symbol-driven detection).");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Pipeline_PackagesProvideTestToolkit_IgnoresPackagesWithoutOverlap()
    {
        // A package whose filename contains "Test Library" but declares NO codeunit
        // our stubs declare must NOT trigger detection — proves we are not falling
        // back to filename matching. (This is the inverse of the symbol-overlap test:
        // a benign package that happens to be named "Test Library Helper" should
        // be left alone.)
        var tmpDir = Path.Combine(Path.GetTempPath(), "altr-stub-no-overlap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var appPath = Path.Combine(tmpDir, "Acme_Test_Library_Helper_1.0.0.0.app");
            BuildSyntheticApp(appPath, manifestName: "Test Library Helper", codeunitNames: new[] { "Some Other Codeunit" });

            var detector = typeof(AlRunner.AlRunnerPipeline).GetMethod(
                "PackagesProvideTestToolkit",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = (bool)detector!.Invoke(null, new object[] { new[] { tmpDir } })!;

            Assert.False(result,
                "A package without symbol overlap must not trigger detection, even if its name " +
                "or filename happens to contain 'Test Library'.");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Writes a minimal NAVX-format .app file with a NavxManifest.xml and a
    /// SymbolReference.json declaring the requested codeunits. Sufficient to exercise
    /// DepExtractor.CollectPackageDeclaredObjects without depending on the full
    /// BC compiler toolchain to produce a real app artifact.
    /// </summary>
    private static void BuildSyntheticApp(string appPath, string manifestName, string[] codeunitNames)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("NavxManifest.xml");
            using (var sw = new StreamWriter(manifest.Open()))
            {
                sw.Write($@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/navx/2015/manifest"">
  <App Id=""{Guid.NewGuid()}"" Name=""{manifestName}"" Publisher=""Acme"" Version=""1.0.0.0"" CompatibilityId=""0.0.0.0"" />
</Package>");
            }

            var sym = zip.CreateEntry("SymbolReference.json");
            using (var sw = new StreamWriter(sym.Open()))
            {
                var codeunitJson = string.Join(",", codeunitNames.Select(n => $"{{\"Id\":1,\"Name\":\"{n}\"}}"));
                sw.Write($"{{\"Codeunits\":[{codeunitJson}]}}");
            }
        }
        ms.Position = 0;
        var zipBytes = ms.ToArray();

        // Write NAVX header (magic "NAVX" + 4-byte little-endian zip offset = 8) followed by the zip payload.
        using var fs = File.Create(appPath);
        fs.Write(new byte[] { (byte)'N', (byte)'A', (byte)'V', (byte)'X' }, 0, 4);
        fs.Write(BitConverter.GetBytes((uint)8), 0, 4);
        fs.Write(zipBytes, 0, zipBytes.Length);
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
