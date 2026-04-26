using Xunit;

namespace AlRunner.Tests;

public class StubGeneratorTests
{
    private static readonly string TestLibrariesApp =
        "/tmp/data-editor-for-bc/.alpackages/Microsoft_Tests-TestLibraries.app";

    private static readonly string LibraryAssertApp =
        "/tmp/data-editor-for-bc/.alpackages/Microsoft_Library Assert_26.0.30643.32874.app";

    private static bool HasTestApps => File.Exists(TestLibrariesApp);

    [Fact]
    public void Generate_CreatesStubFilesForCodeunits()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));

            var result = StubGenerator.Generate(singlePkgDir, outputDir);

            // Should have generated files
            Assert.True(result.Generated > 0, $"Expected generated > 0, got {result.Generated}");

            // Check a known codeunit: "Library - Sales" (ID 130509)
            var salesStub = Path.Combine(outputDir, "Cod130509.Library-Sales.al");
            Assert.True(File.Exists(salesStub), $"Expected stub file {salesStub} to exist");

            var content = File.ReadAllText(salesStub);
            Assert.Contains("codeunit 130509 \"Library - Sales\"", content);
            Assert.Contains("procedure", content);
            Assert.Contains("Auto-generated stub", content);

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Generate_SkipsExistingFiles()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-test-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));
            Directory.CreateDirectory(outputDir);

            // Pre-create a stub file so it gets skipped
            var preExisting = Path.Combine(outputDir, "Cod130509.Library-Sales.al");
            File.WriteAllText(preExisting, "// hand-edited stub");

            var result = StubGenerator.Generate(singlePkgDir, outputDir);

            Assert.Contains("Cod130509.Library-Sales.al", result.SkippedExisting);
            // The file content should NOT have been overwritten
            Assert.Equal("// hand-edited stub", File.ReadAllText(preExisting));

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Generate_SkipsNativeMockedCodeunits()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-test-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));
            if (File.Exists(LibraryAssertApp))
                File.Copy(LibraryAssertApp, Path.Combine(singlePkgDir, Path.GetFileName(LibraryAssertApp)));

            var result = StubGenerator.Generate(singlePkgDir, outputDir);

            // No file for codeunit 130 should exist (it's in the skip list)
            // Use exact prefix match — "Cod130." not "Cod130*" which would also match Cod130000
            var cod130File = Path.Combine(outputDir, "Cod130.Assert.al");
            Assert.False(File.Exists(cod130File), "Codeunit 130 (Assert) should be skipped as a native mock");
            var cod130LibAssert = Path.Combine(outputDir, "Cod130.LibraryAssert.al");
            Assert.False(File.Exists(cod130LibAssert), "Codeunit 130 (Library Assert) should be skipped as a native mock");

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Generate_EmitsCorrectParameterSignatures()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-test-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));

            var result = StubGenerator.Generate(singlePkgDir, outputDir);

            // Check that codeunit 130000 "Assert" has methods with var parameters
            var assertStub = Path.Combine(outputDir, "Cod130000.Assert.al");
            Assert.True(File.Exists(assertStub), "Assert stub should exist");

            var content = File.ReadAllText(assertStub);
            Assert.Contains("var ", content);
            Assert.Contains("Record \"", content);

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Generate_EmitsReturnTypeWithDefaultValue()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-test-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));

            var result = StubGenerator.Generate(singlePkgDir, outputDir);

            // Codeunit 131033 "Active Directory Mock Events" has "Enabled" returning Boolean
            var adStub = Path.Combine(outputDir, "Cod131033.ActiveDirectoryMockEvents.al");
            Assert.True(File.Exists(adStub), "AD Mock Events stub should exist");

            var content = File.ReadAllText(adStub);
            Assert.Contains(": Boolean", content);
            Assert.Contains("exit(false); // TODO: implement", content);

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Generate_ReportsNonCodeunitCount()
    {
        var baseApp = "/tmp/data-editor-for-bc/.alpackages/Microsoft_Base Application_26.0.30643.32874.app";
        if (!File.Exists(baseApp)) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-test-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(baseApp, Path.Combine(singlePkgDir, Path.GetFileName(baseApp)));
            var result = StubGenerator.Generate(singlePkgDir, outputDir);
            Assert.True(result.SkippedNonCodeunit > 0, "Base Application should have non-codeunit objects");

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Generate_ThrowsForMissingDirectory()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            StubGenerator.Generate("/nonexistent/path", "/tmp/output"));
    }

    [Fact]
    public async Task CliFlag_GeneratesStubs()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-cli-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));

            var result = await CliRunner.RunAsync($"--generate-stubs \"{singlePkgDir}\" \"{outputDir}\"");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Generated", result.StdErr);
            Assert.Contains("stub files", result.StdErr);

            Assert.True(Directory.Exists(outputDir), "Output dir should have been created");
            var alFiles = Directory.GetFiles(outputDir, "*.al");
            Assert.True(alFiles.Length > 0, "Should have generated .al files");

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    // --- Source-filtered stub generation tests ---

    [Fact]
    public void Generate_WithSourceDirs_OnlyGeneratesReferencedCodeunits()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-filtered-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        var srcDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-src-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));

            // Create a source dir with one .al file that references "Library - Sales" only
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "test.al"), @"
codeunit 50100 ""My Test""
{
    var LibSales: Codeunit ""Library - Sales"";

    trigger OnRun()
    begin
        LibSales.CreateSalesHeader(SalesHeader, SalesHeader.""Document Type""::Order);
    end;
}
");

            var result = StubGenerator.Generate(singlePkgDir, outputDir, new[] { srcDir });

            // Should have generated only the referenced codeunit(s), not all 164
            Assert.True(result.Generated > 0, "Should have generated at least 1 stub");
            Assert.True(result.SkippedNotReferenced > 0, "Should have skipped unreferenced codeunits");
            Assert.True(result.Generated < result.TotalAvailable,
                $"Generated {result.Generated} should be less than total {result.TotalAvailable}");
            Assert.True(result.SourceFileCount == 1, $"Expected 1 source file, got {result.SourceFileCount}");

            // Library - Sales (130509) should exist
            var salesStub = Path.Combine(outputDir, "Cod130509.Library-Sales.al");
            Assert.True(File.Exists(salesStub), "Library - Sales stub should exist (referenced in source)");

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
        }
    }

    [Fact]
    public void Generate_WithoutSourceDirs_GeneratesAllCodeunits()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-all-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));

            // No source dirs — should generate all
            var result = StubGenerator.Generate(singlePkgDir, outputDir);

            Assert.True(result.SkippedNotReferenced == 0, "Without source dirs, nothing should be skipped as unreferenced");
            Assert.Equal(0, result.SourceFileCount);

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Generate_WithSourceDirs_FiltersProcedures()
    {
        if (!HasTestApps) return;

        var outputDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-procfilter-" + Guid.NewGuid().ToString("N")[..8]);
        var singlePkgDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        var srcDir = Path.Combine(Path.GetTempPath(), "al-runner-stub-src-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(singlePkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(singlePkgDir, Path.GetFileName(TestLibrariesApp)));

            // Create source that references Library - Sales but only calls CreateSalesHeader
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "test.al"), @"
codeunit 50100 ""My Test""
{
    var LibSales: Codeunit ""Library - Sales"";

    trigger OnRun()
    begin
        LibSales.CreateSalesHeader(SalesHeader, SalesHeader.""Document Type""::Order);
    end;
}
");

            var result = StubGenerator.Generate(singlePkgDir, outputDir, new[] { srcDir });

            var salesStub = Path.Combine(outputDir, "Cod130509.Library-Sales.al");
            Assert.True(File.Exists(salesStub), "Library - Sales stub should exist");

            var content = File.ReadAllText(salesStub);
            // CreateSalesHeader should be present (it's called in source)
            Assert.Contains("CreateSalesHeader", content);

            // Now also generate without filtering to compare procedure count
            var outputDirAll = Path.Combine(Path.GetTempPath(), "al-runner-stub-procfilter-all-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var resultAll = StubGenerator.Generate(singlePkgDir, outputDirAll);
                var salesStubAll = Path.Combine(outputDirAll, "Cod130509.Library-Sales.al");
                var contentAll = File.ReadAllText(salesStubAll);

                // The filtered stub should have fewer procedures than the unfiltered one
                int filteredProcCount = content.Split("procedure ").Length - 1;
                int allProcCount = contentAll.Split("procedure ").Length - 1;
                Assert.True(filteredProcCount < allProcCount,
                    $"Filtered stub has {filteredProcCount} procedures, unfiltered has {allProcCount} — expected fewer in filtered");
            }
            finally
            {
                if (Directory.Exists(outputDirAll)) Directory.Delete(outputDirAll, true);
            }

            Directory.Delete(singlePkgDir, true);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
        }
    }

    [Fact]
    public void IsCodeunitReferenced_MatchesByName()
    {
        var cu = new StubGenerator.CodeunitSymbol(130509, "Library - Sales", new List<StubGenerator.MethodSymbol>());
        var sourceText = "var LibSales: Codeunit \"Library - Sales\";";
        Assert.True(StubGenerator.IsCodeunitReferenced(cu, sourceText));
    }

    [Fact]
    public void IsCodeunitReferenced_MatchesById()
    {
        var cu = new StubGenerator.CodeunitSymbol(130509, "Library - Sales", new List<StubGenerator.MethodSymbol>());
        var sourceText = "// some reference to codeunit 130509 here";
        Assert.True(StubGenerator.IsCodeunitReferenced(cu, sourceText));
    }

    [Fact]
    public void IsCodeunitReferenced_ReturnsFalseWhenNotReferenced()
    {
        var cu = new StubGenerator.CodeunitSymbol(130509, "Library - Sales", new List<StubGenerator.MethodSymbol>());
        var sourceText = "var X: Codeunit \"Library - Purchases\";";
        Assert.False(StubGenerator.IsCodeunitReferenced(cu, sourceText));
    }

    [Fact]
    public void FilterProcedures_KeepsOnlyCalledMethods()
    {
        var methods = new List<StubGenerator.MethodSymbol>
        {
            new("CreateSalesHeader", new List<StubGenerator.ParameterSymbol>(), null),
            new("CreateSalesLine", new List<StubGenerator.ParameterSymbol>(), null),
            new("PostSalesDocument", new List<StubGenerator.ParameterSymbol>(), null),
        };
        var cu = new StubGenerator.CodeunitSymbol(130509, "Library - Sales", methods);
        var sourceText = "LibSales.CreateSalesHeader(X, Y);\nLibSales.PostSalesDocument(Z);";

        var filtered = StubGenerator.FilterProcedures(cu, sourceText);

        Assert.Equal(2, filtered.Methods.Count);
        Assert.Contains(filtered.Methods, m => m.Name == "CreateSalesHeader");
        Assert.Contains(filtered.Methods, m => m.Name == "PostSalesDocument");
        Assert.DoesNotContain(filtered.Methods, m => m.Name == "CreateSalesLine");
    }

    [Fact]
    public void RenderType_HandlesAllCommonTypes()
    {
        // Simple types
        Assert.Equal("Boolean", StubGenerator.RenderType(new StubGenerator.TypeSymbol("Boolean", null, false, null)));
        Assert.Equal("Integer", StubGenerator.RenderType(new StubGenerator.TypeSymbol("Integer", null, false, null)));
        Assert.Equal("Text[100]", StubGenerator.RenderType(new StubGenerator.TypeSymbol("Text[100]", null, false, null)));
        Assert.Equal("Code[20]", StubGenerator.RenderType(new StubGenerator.TypeSymbol("Code[20]", null, false, null)));

        // Compound types
        Assert.Equal("Record \"Sales Header\"",
            StubGenerator.RenderType(new StubGenerator.TypeSymbol("Record", "Sales Header", false, null)));
        Assert.Equal("Record \"Sales Header\" temporary",
            StubGenerator.RenderType(new StubGenerator.TypeSymbol("Record", "Sales Header", true, null)));
        Assert.Equal("Codeunit \"Temp Blob\"",
            StubGenerator.RenderType(new StubGenerator.TypeSymbol("Codeunit", "Temp Blob", false, null)));
        Assert.Equal("Enum \"Purchase Document Type\"",
            StubGenerator.RenderType(new StubGenerator.TypeSymbol("Enum", "Purchase Document Type", false, null)));

        // Generic types
        Assert.Equal("List of [Text]",
            StubGenerator.RenderType(new StubGenerator.TypeSymbol("List", null, false,
                new List<StubGenerator.TypeSymbol> { new("Text", null, false, null) })));
    }

    // --- Issue #1419: Enum/Option parameter types in stubs ---

    [Fact]
    public void RenderType_Option_EmitsOptionNotInteger()
    {
        // Regression for #1419: Option-typed parameters must emit "Option" in AL,
        // not "Integer". NavOption at runtime is NavOption, not int.
        var result = StubGenerator.RenderType(new StubGenerator.TypeSymbol("Option", null, false, null));
        Assert.Equal("Option", result);
        Assert.NotEqual("Integer", result);
    }

    [Fact]
    public void RenderCodeunit_WithOptionParam_EmitsOptionType()
    {
        // Regression for #1419: a stub generated from SymbolReference.json with an
        // Option-typed parameter must emit "Option", not "Integer".
        var methods = new List<StubGenerator.MethodSymbol>
        {
            new("SetDocumentType",
                new List<StubGenerator.ParameterSymbol>
                {
                    new("DocType", new StubGenerator.TypeSymbol("Option", null, false, null), IsVar: false),
                },
                ReturnType: null)
        };
        var cu = new StubGenerator.CodeunitSymbol(99001, "Test Lib", methods);
        var stub = StubGenerator.RenderCodeunit(cu, "test.app");

        Assert.Contains("DocType: Option", stub);
        Assert.DoesNotContain("DocType: Integer", stub);
    }

    [Fact]
    public void RenderCodeunit_WithEnumParam_EmitsEnumType()
    {
        // Regression for #1419: a stub generated from SymbolReference.json with an
        // Enum "Sales Document Type"-typed parameter must emit that enum type, not "Variant".
        var methods = new List<StubGenerator.MethodSymbol>
        {
            new("CreateSalesHeader",
                new List<StubGenerator.ParameterSymbol>
                {
                    new("DocumentType",
                        new StubGenerator.TypeSymbol("Enum", "Sales Document Type", false, null),
                        IsVar: false),
                    new("CustomerNo",
                        new StubGenerator.TypeSymbol("Code[20]", null, false, null),
                        IsVar: false),
                },
                ReturnType: null)
        };
        var cu = new StubGenerator.CodeunitSymbol(130509, "Library - Sales", methods);
        var stub = StubGenerator.RenderCodeunit(cu, "test.app");

        Assert.Contains("DocumentType: Enum \"Sales Document Type\"", stub);
        Assert.DoesNotContain("DocumentType: Variant", stub);
        Assert.Contains("CustomerNo: Code[20]", stub);
    }

    // --- Problem 1: EventSubscriber reference detection ---

    [Fact]
    public void IsCodeunitReferenced_DetectsEventSubscriberQuotedName()
    {
        // EventSubscriber attribute: Codeunit::"Company-Initialize"
        var cu = new StubGenerator.CodeunitSymbol(2, "Company-Initialize", new List<StubGenerator.MethodSymbol>());
        var sourceText = "[EventSubscriber(ObjectType::Codeunit, Codeunit::\"Company-Initialize\", 'OnCompanyInitialize', '', false, false)]";
        Assert.True(StubGenerator.IsCodeunitReferenced(cu, sourceText),
            "Codeunit::\"Name\" in EventSubscriber attribute should be detected as a reference");
    }

    [Fact]
    public void IsCodeunitReferenced_DetectsEventSubscriberUnquotedName()
    {
        // EventSubscriber attribute: Codeunit::SomeName (single-word, no quotes)
        var cu = new StubGenerator.CodeunitSymbol(42, "MyHelper", new List<StubGenerator.MethodSymbol>());
        var sourceText = "[EventSubscriber(ObjectType::Codeunit, Codeunit::MyHelper, 'OnDoSomething', '', false, false)]";
        Assert.True(StubGenerator.IsCodeunitReferenced(cu, sourceText),
            "Codeunit::Name (no quotes) in EventSubscriber attribute should be detected as a reference");
    }

    [Fact]
    public void IsCodeunitReferenced_DetectsCodeunitDoubleColonQuoted()
    {
        // General Codeunit::"Name" usage, e.g. in run statements
        var cu = new StubGenerator.CodeunitSymbol(1000, "My Library", new List<StubGenerator.MethodSymbol>());
        var sourceText = "Codeunit.Run(Codeunit::\"My Library\");";
        Assert.True(StubGenerator.IsCodeunitReferenced(cu, sourceText),
            "Codeunit::\"Name\" pattern should be detected as a reference");
    }

    [Fact]
    public void IsCodeunitReferenced_CountIncreasesWithEventSubscriberPatterns()
    {
        // Demonstrate that EventSubscriber references cause more codeunits to be detected
        // as referenced compared to variable-declaration-only detection.

        // A codeunit that is ONLY referenced via EventSubscriber (no var decl, no ID literal)
        var cu = new StubGenerator.CodeunitSymbol(99999, "Unique-Init-Handler", new List<StubGenerator.MethodSymbol>());

        // Source that does NOT contain a variable declaration or the numeric ID
        var sourceWithoutEventSubscriber = "// some other code";
        Assert.False(StubGenerator.IsCodeunitReferenced(cu, sourceWithoutEventSubscriber),
            "Should not be detected when only other code is present");

        // Source that DOES contain an EventSubscriber reference
        var sourceWithEventSubscriber =
            "[EventSubscriber(ObjectType::Codeunit, Codeunit::\"Unique-Init-Handler\", 'OnRun', '', false, false)]";
        Assert.True(StubGenerator.IsCodeunitReferenced(cu, sourceWithEventSubscriber),
            "Should be detected via EventSubscriber Codeunit::\"Name\" pattern");
    }

    // --- Problem 2: Multiple package directories ---

    [Fact]
    public void Generate_MultiplePackageDirs_ScansAllDirs()
    {
        // Arrange: two separate package directories, each with one .app
        // Use temp files that are empty/minimal — we just verify both dirs are scanned.
        // We test this using the static Generate overload that accepts multiple dirs.
        // Since we cannot create real .app files easily, we test the ThrowsForMissing case
        // for the multi-dir overload to confirm it exists, then test with real dirs if available.

        if (!HasTestApps) return;

        var pkgDir1 = Path.Combine(Path.GetTempPath(), "al-stub-pkg1-" + Guid.NewGuid().ToString("N")[..8]);
        var pkgDir2 = Path.Combine(Path.GetTempPath(), "al-stub-pkg2-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "al-stub-out-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(pkgDir1);
            Directory.CreateDirectory(pkgDir2);

            // Split: put TestLibraries in dir1, nothing in dir2 (empty is valid)
            File.Copy(TestLibrariesApp, Path.Combine(pkgDir1, Path.GetFileName(TestLibrariesApp)));

            var result = StubGenerator.Generate(new[] { pkgDir1, pkgDir2 }, outDir);

            // Should generate the same as scanning pkgDir1 alone
            Assert.True(result.Generated > 0, "Should generate stubs from pkgDir1");
            Assert.True(result.TotalAvailable > 0, "Should find codeunits in pkgDir1");
        }
        finally
        {
            if (Directory.Exists(pkgDir1)) Directory.Delete(pkgDir1, true);
            if (Directory.Exists(pkgDir2)) Directory.Delete(pkgDir2, true);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void Generate_MultiplePackageDirs_CombinesCodeunitsFromBothDirs()
    {
        // Verify that codeunits from BOTH package dirs appear in the output.
        if (!HasTestApps) return;

        // We need two app files. If we only have one, copy it to both dirs under different names
        // to simulate two packages — both will have the same codeunits, so we just verify
        // that TotalAvailable reflects scanning both.
        var pkgDir1 = Path.Combine(Path.GetTempPath(), "al-stub-pkgA-" + Guid.NewGuid().ToString("N")[..8]);
        var pkgDir2 = Path.Combine(Path.GetTempPath(), "al-stub-pkgB-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir1 = Path.Combine(Path.GetTempPath(), "al-stub-outA-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir2 = Path.Combine(Path.GetTempPath(), "al-stub-outB-" + Guid.NewGuid().ToString("N")[..8]);
        var outDirBoth = Path.Combine(Path.GetTempPath(), "al-stub-outBoth-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(pkgDir1);
            Directory.CreateDirectory(pkgDir2);

            File.Copy(TestLibrariesApp, Path.Combine(pkgDir1, Path.GetFileName(TestLibrariesApp)));
            // Use LibraryAssert in pkgDir2 if available, otherwise skip
            if (!File.Exists(LibraryAssertApp)) return;
            File.Copy(LibraryAssertApp, Path.Combine(pkgDir2, Path.GetFileName(LibraryAssertApp)));

            // Single-dir scans for reference counts
            var resultDir1 = StubGenerator.Generate(pkgDir1, outDir1);
            var resultDir2 = StubGenerator.Generate(pkgDir2, outDir2);
            var resultBoth = StubGenerator.Generate(new[] { pkgDir1, pkgDir2 }, outDirBoth);

            // TotalAvailable when scanning both dirs should equal sum of both individual scans
            // (or at least be >= max of the two)
            Assert.True(resultBoth.TotalAvailable >= Math.Max(resultDir1.TotalAvailable, resultDir2.TotalAvailable),
                $"Multi-dir scan ({resultBoth.TotalAvailable}) should find at least as many codeunits as the larger single-dir scan");

            // The combined scan should have generated at least as many files
            Assert.True(resultBoth.Generated >= Math.Max(resultDir1.Generated, resultDir2.Generated),
                "Multi-dir scan should generate at least as many stubs as single-dir scan");
        }
        finally
        {
            if (Directory.Exists(pkgDir1)) Directory.Delete(pkgDir1, true);
            if (Directory.Exists(pkgDir2)) Directory.Delete(pkgDir2, true);
            if (Directory.Exists(outDir1)) Directory.Delete(outDir1, true);
            if (Directory.Exists(outDir2)) Directory.Delete(outDir2, true);
            if (Directory.Exists(outDirBoth)) Directory.Delete(outDirBoth, true);
        }
    }

    [Fact]
    public void Generate_MultiplePackageDirs_ThrowsForMissingDirectory()
    {
        // The multi-dir overload should throw when any dir is missing
        Assert.Throws<DirectoryNotFoundException>(() =>
            StubGenerator.Generate(new[] { "/nonexistent/path1", "/nonexistent/path2" }, "/tmp/output"));
    }
    // --- Symbol-table overload tests ---

    [Fact]
    public void Generate_WithNullCompilation_BehavesLikeOldOverload()
    {
        // Verify backward compatibility: Generate(dirs, outDir, sourceDirs, null) produces
        // the same result as Generate(dirs, outDir, sourceDirs) — no regressions.
        if (!HasTestApps) return;

        var pkgDir = Path.Combine(Path.GetTempPath(), "al-stub-sym-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        var outDirOld = Path.Combine(Path.GetTempPath(), "al-stub-sym-old-" + Guid.NewGuid().ToString("N")[..8]);
        var outDirNew = Path.Combine(Path.GetTempPath(), "al-stub-sym-new-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(pkgDir);
            File.Copy(TestLibrariesApp, Path.Combine(pkgDir, Path.GetFileName(TestLibrariesApp)));

            // Old overload (no compilation)
            var resultOld = StubGenerator.Generate(new[] { pkgDir }, outDirOld);
            // New 4-arg overload with compilation: null
            var resultNew = StubGenerator.Generate(new[] { pkgDir }, outDirNew, null, compilation: null);

            Assert.Equal(resultOld.Generated, resultNew.Generated);
            Assert.Equal(resultOld.SkippedNativeMock, resultNew.SkippedNativeMock);
            Assert.Equal(resultOld.TotalAvailable, resultNew.TotalAvailable);
            Assert.Equal(0, resultNew.GeneratedFromSymbolTable);
        }
        finally
        {
            if (Directory.Exists(pkgDir)) Directory.Delete(pkgDir, true);
            if (Directory.Exists(outDirOld)) Directory.Delete(outDirOld, true);
            if (Directory.Exists(outDirNew)) Directory.Delete(outDirNew, true);
        }
    }

    [Fact]
    public void GenerateResult_HasGeneratedFromSymbolTableField()
    {
        // Verify the new field exists and defaults to 0
        var result = new StubGenerator.GenerateResult(
            Generated: 5,
            SkippedExisting: new List<string>(),
            SkippedNonCodeunit: 0,
            SkippedNativeMock: 1,
            SkippedNotReferenced: 0,
            TotalAvailable: 6,
            SourceFileCount: 0);

        // Default value of 0
        Assert.Equal(0, result.GeneratedFromSymbolTable);

        // Can be set explicitly
        var resultWithSymbols = result with { GeneratedFromSymbolTable = 3 };
        Assert.Equal(3, resultWithSymbols.GeneratedFromSymbolTable);
        Assert.Equal(5, resultWithSymbols.Generated);
    }
}
