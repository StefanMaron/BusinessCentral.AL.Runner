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

            Assert.Single(specs.Where(s => s.Name == "My App"));
        }
        finally
        {
            if (Directory.Exists(dir1)) Directory.Delete(dir1, true);
            if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
        }
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
