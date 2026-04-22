using System.IO.Compression;
using System.Xml.Linq;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests that al-runner is resilient against duplicate .app packages in the packages directory.
/// Uses minimal synthetic .app files (NAVX header + NavxManifest.xml only, no AL symbols)
/// to test the proactive deduplication logic at scan time.
/// </summary>
[Collection("Pipeline")]
public class DuplicatePackageTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    // The alc binary path — configurable via env var, falls back to the VS Code extension location.
    private static readonly string? AlcPath = FindAlcPath();

    private static string? FindAlcPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("AL_COMPILER_PATH");
        if (fromEnv != null && File.Exists(fromEnv)) return fromEnv;

        var vscodePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode", "extensions");
        if (!Directory.Exists(vscodePath)) return null;

        foreach (var extDir in Directory.GetDirectories(vscodePath, "ms-dynamics-smb.al-*"))
        {
            var candidate = Path.Combine(extDir, "bin", "linux", "alc");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // --- Proactive deduplication: same publisher/name/version, different GUIDs ---

    [Fact]
    public void PackageScanner_SamePublisherNameVersion_DifferentGuids_KeepsOneEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-duptest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var guid1 = Guid.NewGuid();
            var guid2 = Guid.NewGuid();
            CreateMinimalApp(dir, "App1_copy1.app", "Acme", "My App", "1.0.0.0", guid1);
            CreateMinimalApp(dir, "App1_copy2.app", "Acme", "My App", "1.0.0.0", guid2);

            var specs = PackageScanner.ScanForSpecs(new[] { dir });

            // Should deduplicate to exactly one entry for "My App by Acme (1.0.0.0)"
            var myApp = specs.Where(s =>
                string.Equals(s.Publisher, "Acme", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Name, "My App", StringComparison.OrdinalIgnoreCase) &&
                s.Version == new Version("1.0.0.0")).ToList();
            Assert.Single(myApp);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PackageScanner_SamePublisherNameVersion_DifferentGuids_ChoiceIsDeterministic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-duptest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var guidA = new Guid("aaaaaaaa-0000-0000-0000-000000000000");
            var guidB = new Guid("bbbbbbbb-0000-0000-0000-000000000000");
            CreateMinimalApp(dir, "AppB.app", "Acme", "My App", "1.0.0.0", guidB);
            CreateMinimalApp(dir, "AppA.app", "Acme", "My App", "1.0.0.0", guidA);

            var first = PackageScanner.ScanForSpecs(new[] { dir });
            var second = PackageScanner.ScanForSpecs(new[] { dir });

            // The surviving GUID should be the same across two calls
            var firstGuid = first.First(s => s.Name == "My App").AppId;
            var secondGuid = second.First(s => s.Name == "My App").AppId;
            Assert.Equal(firstGuid, secondGuid);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PackageScanner_SameNameDifferentPublisher_KeepsBoth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-duptest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            CreateMinimalApp(dir, "AppX.app", "PublisherX", "My App", "1.0.0.0", Guid.NewGuid());
            CreateMinimalApp(dir, "AppY.app", "PublisherY", "My App", "1.0.0.0", Guid.NewGuid());

            var specs = PackageScanner.ScanForSpecs(new[] { dir });

            // Different publishers — both should be kept
            Assert.Equal(2, specs.Count(s => s.Name == "My App"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PackageScanner_SamePublisherNameDifferentVersion_KeepsBoth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-duptest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            CreateMinimalApp(dir, "App100.app", "Acme", "My App", "1.0.0.0", Guid.NewGuid());
            CreateMinimalApp(dir, "App200.app", "Acme", "My App", "2.0.0.0", Guid.NewGuid());

            var specs = PackageScanner.ScanForSpecs(new[] { dir });

            // Different versions are NOT self-duplicates — both should be kept
            Assert.Equal(2, specs.Count(s =>
                string.Equals(s.Publisher, "Acme", StringComparison.OrdinalIgnoreCase) &&
                s.Name == "My App"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PackageScanner_SameGuidAcrossTwoDirs_KeepsOne()
    {
        var dir1 = Path.Combine(Path.GetTempPath(), "al-runner-duptest1-" + Guid.NewGuid().ToString("N")[..8]);
        var dir2 = Path.Combine(Path.GetTempPath(), "al-runner-duptest2-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir1);
            Directory.CreateDirectory(dir2);
            var sharedGuid = Guid.NewGuid();
            CreateMinimalApp(dir1, "App.app", "Acme", "My App", "1.0.0.0", sharedGuid);
            CreateMinimalApp(dir2, "App.app", "Acme", "My App", "1.0.0.0", sharedGuid);

            var specs = PackageScanner.ScanForSpecs(new[] { dir1, dir2 });

            Assert.Single(specs, s => s.Name == "My App");
        }
        finally
        {
            if (Directory.Exists(dir1)) Directory.Delete(dir1, true);
            if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
        }
    }

    // --- Source-overlaps-packages filtering (issue #1034) ---

    /// <summary>
    /// RED test: when a source app's GUID appears in a packages directory (common when
    /// .alpackages contains the compiled .app of the extension being compiled from source),
    /// the runner must not include that .app's GUID in the symbol-reference dependencies.
    /// ExtractAllSourceAppIds must return the GUID so the caller can filter depSpecs.
    /// </summary>
    [Fact]
    public void ExtractAllSourceAppIds_ReturnsAllAppIdsFromInputPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-srcids-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var mainAppDir = Path.Combine(dir, "main-app", "src");
            var testAppDir = Path.Combine(dir, "test-app", "src");
            Directory.CreateDirectory(mainAppDir);
            Directory.CreateDirectory(testAppDir);

            var mainAppId = new Guid("aaaa0000-0000-0000-0000-000000000001");
            var testAppId = new Guid("bbbb0000-0000-0000-0000-000000000002");

            // Write app.json files directly in the src dirs (standard BC layout)
            WriteAppJson(mainAppDir, "Main App", "TestPublisher", "1.0.0.0", mainAppId);
            WriteAppJson(testAppDir, "Test App", "TestPublisher", "1.0.0.0", testAppId);

            var ids = AlTranspiler.ExtractAllSourceAppIds(new List<string> { mainAppDir, testAppDir });

            Assert.Contains(mainAppId, ids);
            Assert.Contains(testAppId, ids);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// When source app IDs are extracted, those IDs must not appear in the depSpecs
    /// that get loaded as symbol references. This tests the pipeline integration:
    /// passing source for both main-app and test-app, with the main-app's .app also
    /// present in the packages directory, must NOT produce duplicate-object errors.
    /// </summary>
    [Fact]
    public void Pipeline_SourceAppInPackages_DoesNotCauseDuplicateErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-srcpkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var mainSrcDir = Path.Combine(dir, "main", "src");
            var testSrcDir = Path.Combine(dir, "testapp", "src");
            var pkgDir = Path.Combine(dir, "packages");
            Directory.CreateDirectory(mainSrcDir);
            Directory.CreateDirectory(testSrcDir);
            Directory.CreateDirectory(pkgDir);

            var mainAppId = new Guid("cccc0000-0000-0000-0000-000000000003");
            var testAppId = new Guid("dddd0000-0000-0000-0000-000000000004");

            // Main app: a table
            File.WriteAllText(Path.Combine(mainSrcDir, "Table.al"), @"
table 73001 ""Overlap Source Table""
{
    fields
    {
        field(1; Id; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
codeunit 73001 ""Overlap Source Logic""
{
    procedure GetFortyTwo(): Integer
    begin
        exit(42);
    end;
}
");
            WriteAppJson(mainSrcDir, "Overlap Main App", "OverlapPublisher", "1.0.0.0", mainAppId);

            // Test app: a test codeunit referencing the main app's codeunit
            File.WriteAllText(Path.Combine(testSrcDir, "Test.al"), @"
codeunit 73002 ""Overlap Tests""
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    [Test]
    procedure TestGetFortyTwo()
    var
        Logic: Codeunit ""Overlap Source Logic"";
    begin
        Assert.AreEqual(42, Logic.GetFortyTwo(), 'Expected 42');
    end;
}
");
            WriteAppJsonWithDep(testSrcDir, "Overlap Test App", "OverlapPublisher", "1.0.0.0", testAppId,
                mainAppId, "Overlap Main App", "OverlapPublisher", "1.0.0.0");

            // Put a minimal .app for the main app in the packages directory.
            // This simulates .alpackages containing the compiled version of the source app.
            CreateMinimalApp(pkgDir, "OverlapMainApp_1.0.0.0.app", "OverlapPublisher", "Overlap Main App", "1.0.0.0", mainAppId);

            // Run the pipeline with both source dirs and the packages dir.
            // Use verbose so we can confirm the filter fired.
            var pipeline = new AlRunnerPipeline();
            var result = pipeline.Run(new PipelineOptions
            {
                InputPaths = { mainSrcDir, testSrcDir },
                PackagePaths = { pkgDir },
                Verbose = true
            });

            // Must not contain duplicate-object errors
            Assert.DoesNotContain("AL0275", result.StdErr);
            Assert.DoesNotContain("duplicate", result.StdErr, StringComparison.OrdinalIgnoreCase);
            // The filter must have logged that it skipped the source app's package reference
            Assert.Contains("Skipped", result.StdErr);
            // Pipeline must succeed and run the test
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("PASS", result.StdOut);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // --- DiagnosticClassifier integration: AL0275 self-duplicate suppression ---

    [Fact]
    public void DiagnosticClassifier_SelfDuplicate_IsIdentifiedCorrectly()
    {
        var msg =
            "'My Object' is an ambiguous reference between " +
            "'My Object' defined by the extension " +
            "'My App by My Publisher (1.0.0.0)' and " +
            "'My Object' defined by the extension " +
            "'My App by My Publisher (1.0.0.0)'.";

        Assert.True(DiagnosticClassifier.IsSelfDuplicateAmbiguity(msg));
    }

    // --- Integration test using alc (skipped when alc is not available) ---

    [Fact]
    public async Task DuplicatePackagesWithSymbols_DoNotCauseAl0275Failure()
    {
        if (AlcPath == null)
        {
            // Skip gracefully — alc is not available in this environment
            return;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "al-runner-alc-duptest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Build a minimal AL app with one table and two different GUIDs
            var guid1 = Guid.NewGuid();
            var guid2 = Guid.NewGuid();
            var app1Dir = Path.Combine(workDir, "app1");
            var app2Dir = Path.Combine(workDir, "app2");
            var pkgDir = Path.Combine(workDir, "packages");
            var srcDir = Path.Combine(workDir, "src");
            var testAlDir = Path.Combine(workDir, "test");

            Directory.CreateDirectory(app1Dir);
            Directory.CreateDirectory(app2Dir);
            Directory.CreateDirectory(pkgDir);
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(testAlDir);

            // Write the AL source (shared content, just different GUIDs in app.json)
            var tableAl = @"
table 50100 ""Dup Test Table""
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
";
            File.WriteAllText(Path.Combine(app1Dir, "Table.al"), tableAl);
            File.WriteAllText(Path.Combine(app2Dir, "Table.al"), tableAl);
            WriteAppJson(app1Dir, "Dup Test App", "TestPublisher", "1.0.0.0", guid1);
            WriteAppJson(app2Dir, "Dup Test App", "TestPublisher", "1.0.0.0", guid2);

            // Compile both to .app
            var app1Path = await CompileAlApp(app1Dir);
            var app2Path = await CompileAlApp(app2Dir);
            if (app1Path == null || app2Path == null) return; // compilation failed — skip

            File.Copy(app1Path, Path.Combine(pkgDir, Path.GetFileName(app1Path)));
            File.Copy(app2Path, Path.Combine(pkgDir, Path.GetFileName(app2Path)));

            // Write an AL test that references the table
            File.WriteAllText(Path.Combine(srcDir, "Src.al"), @"
codeunit 50200 ""Dup Test Logic""
{
    procedure GetId(Rec: Record ""Dup Test Table""): Integer
    begin
        exit(Rec.Id);
    end;
}
");
            File.WriteAllText(Path.Combine(testAlDir, "Test.al"), @"
codeunit 50300 ""Dup Test Tests""
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    [Test]
    procedure TestGetId()
    var
        Logic: Codeunit ""Dup Test Logic"";
        Rec: Record ""Dup Test Table"";
    begin
        Rec.Id := 42;
        Assert.AreEqual(42, Logic.GetId(Rec), 'Should return record Id');
    end;
}
");
            var result = await CliRunner.RunAsync(
                $"--packages \"{pkgDir}\" \"{srcDir}\" \"{testAlDir}\"");

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("AL0275", result.StdErr);
            Assert.Contains("PASS", result.StdOut);
        }
        finally { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); }
    }

    // --- Helpers ---

    /// <summary>
    /// Creates a minimal .app file (NAVX header + ZIP with NavxManifest.xml only, no AL symbols).
    /// Sufficient for testing package scanning and deduplication logic.
    /// </summary>
    internal static void CreateMinimalApp(
        string directory, string fileName,
        string publisher, string name, string version, Guid appId)
    {
        var manifest = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(XName.Get("Package", "http://schemas.microsoft.com/navx/2015/manifest"),
                new XElement(XName.Get("App", "http://schemas.microsoft.com/navx/2015/manifest"),
                    new XAttribute("Id", appId.ToString()),
                    new XAttribute("Name", name),
                    new XAttribute("Publisher", publisher),
                    new XAttribute("Version", version))));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using var entryStream = entry.Open();
            manifest.Save(entryStream);
        }

        var zipBytes = ms.ToArray();

        // NAVX header: 4-byte magic "NAVX" + 4-byte LE uint32 header size (8 = no padding)
        var output = new byte[8 + zipBytes.Length];
        output[0] = (byte)'N'; output[1] = (byte)'A'; output[2] = (byte)'V'; output[3] = (byte)'X';
        BitConverter.GetBytes((uint)8).CopyTo(output, 4);
        zipBytes.CopyTo(output, 8);

        File.WriteAllBytes(Path.Combine(directory, fileName), output);
    }

    private static void WriteAppJson(string dir, string name, string publisher, string version, Guid id)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{name}}",
              "publisher": "{{publisher}}",
              "version": "{{version}}",
              "platform": "27.0.0.0",
              "runtime": "14.0"
            }
            """);
    }

    private static void WriteAppJsonWithDep(
        string dir, string name, string publisher, string version, Guid id,
        Guid depId, string depName, string depPublisher, string depVersion)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{name}}",
              "publisher": "{{publisher}}",
              "version": "{{version}}",
              "platform": "27.0.0.0",
              "runtime": "14.0",
              "dependencies": [
                {
                  "id": "{{depId}}",
                  "name": "{{depName}}",
                  "publisher": "{{depPublisher}}",
                  "version": "{{depVersion}}"
                }
              ]
            }
            """);
    }

    private static async Task<string?> CompileAlApp(string projectDir)
    {
        if (AlcPath == null) return null;
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = AlcPath,
            Arguments = $"/project:\"{projectDir}\" /outfolder:\"{projectDir}\" /packagecachepath:\"{projectDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) return null;
        return Directory.GetFiles(projectDir, "*.app").FirstOrDefault();
    }
}
