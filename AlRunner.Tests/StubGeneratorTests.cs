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
}
