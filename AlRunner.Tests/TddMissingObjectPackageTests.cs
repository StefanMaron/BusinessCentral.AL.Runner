// #5446: --tdd and a codeunit that a PACKAGE declares but the test app does not depend on. AL0185 is the same as for an
// object no app declares, but an empty codeunit added for it would shadow the real one, so the run refuses and says the
// dependency is probably missing from app.json. The package is a symbols-only .app written here (no DLL: nothing runs
// its body), so the tests do not depend on the BC version of the build. The design: docs/tdd-missing-object.md.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddMissingObjectPackageTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string Fixtures = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TddMissingObjectRefusal", "package");

    private const string PackageAppId = "7a1f4c93-2d68-4b05-9e37-c8d15a6b0e42";
    private const string PackageName = "Tdd Package Only";

    private const string PackageRefusal = $"the package {PackageName} 1.0.0.0 (AL_Runner_Fixtures_Tdd_Package_Only_1.0.0.0.app) declares it - if the test means that codeunit, add the dependency on it to app.json; if it means a new one, give it another name; an empty codeunit would shadow it";

    private readonly string _scratch;

    public TddMissingObjectPackageTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-missing-object-package");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>A symbols-only package declaring codeunit 65400 "Package Only Points", in the shape the compiler's
    /// package scanner reads (the NAVX header of <c>TddPrecompiledTests</c>).</summary>
    internal static byte[] BuildPackage(string codeunitSymbols, string? source = null, string? appId = null, string? name = null)
    {
        appId ??= PackageAppId;
        name ??= PackageName;
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="AL Runner Fixtures" Version="1.0.0.0" Target="Cloud" ShowMyCode="true" PropagateDependencies="false" />
              <IdRanges><IdRange MinObjectId="65400" MaxObjectId="65419" /></IdRanges>
              <Dependencies />
            </Package>
            """;
        var symbols = "{" + codeunitSymbols + $",\"AppId\":\"{appId}\",\"Name\":\"{name}\",\"Publisher\":\"AL Runner Fixtures\",\"Version\":\"1.0.0.0\"}}";
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="" /><Default Extension="json" ContentType="" /></Types>""");
            Add("NavxManifest.xml", manifest);
            Add("SymbolReference.json", symbols);
            if (source != null) Add("src/PackageOnlyPoints.Codeunit.al", source);
        }
        var payload = zipBuffer.ToArray();
        using var app = new MemoryStream();
        using var bw = new BinaryWriter(app);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(40);
        bw.Write(2);
        bw.Write(Guid.Parse(appId).ToByteArray());
        bw.Write((long)payload.Length);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(payload);
        bw.Flush();
        return app.ToArray();
    }

    internal const string PointsSymbols = """
        "Codeunits":[{"Id":65400,"Name":"Package Only Points","Methods":[
          {"Id":1516892452,"Name":"Twice","ReturnTypeDefinition":{"Name":"Integer"},"Parameters":[{"Name":"Value","TypeDefinition":{"Name":"Integer"}}],"Properties":[]}
        ],"ReferenceSourceFileName":"PackageOnlyPoints.Codeunit.al","Properties":[]}]
        """;

    /// <summary>The package's own source, so a run that declares the dependency compiles it (Tier 3) instead of
    /// reporting a package with no implementation.</summary>
    internal const string PointsSource = """
        codeunit 65400 "Package Only Points"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * 2);
            end;
        }
        """;

    /// <summary>The fixture's test folder copied under the scratch directory, with the package in its .alpackages.</summary>
    private string MakeTestFolder(string fixture)
    {
        var dir = TddMissingObjectTests.CopyFolder(Path.Combine(Fixtures, fixture), Path.Combine(_scratch, fixture));
        Directory.CreateDirectory(Path.Combine(dir, ".alpackages"));
        File.WriteAllBytes(Path.Combine(dir, ".alpackages", "AL_Runner_Fixtures_Tdd_Package_Only_1.0.0.0.app"), BuildPackage(PointsSymbols, PointsSource));
        return dir;
    }

    /// <summary>A second, unrelated package in the folder's .alpackages: its own identity and a valid manifest, with
    /// whatever <paramref name="symbols"/> text the test wants (malformed, for the unreadable cases).</summary>
    private static void AddUnrelatedPackage(string dir, string symbols)
    {
        File.WriteAllBytes(Path.Combine(dir, ".alpackages", "Broken_Unrelated_1.0.0.0.app"),
            BuildPackage(symbols, appId: "c3b8e1d2-5a47-4f90-8d16-0e2a7b9f4c55", name: "Broken Unrelated"));
    }

    private (string StdOut, string StdErr, int Exit) RunTdd(string app, params string[] extra) =>
        TddMissingObjectTests.RunRunner(null, new[] { "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json" }
            .Concat(extra).Append($"\"{app}\"").ToArray());

    /// <summary>The package declares the codeunit, the app does not depend on it: AL0185, refused with the package
    /// named and the likely cause stated, the test FAILED on the AL0185, nothing generated, nothing written.</summary>
    [SkippableFact]
    public void ObjectOfAPackageTheAppDoesNotDeclare_IsNotShadowed()
    {
        TestArtifacts.SkipIfMissing();
        var app = MakeTestFolder("refused");
        var before = TddMissingObjectTests.HashTree(app);

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var t = Assert.Single(doc.RootElement.GetProperty("tests").EnumerateArray().ToList());
        Assert.Equal("fail", t.GetProperty("status").GetString());
        Assert.Contains("error AL0185: Codeunit 'Package Only Points' is missing", t.GetProperty("message").GetString());
        Assert.Empty(TddMissingObjectTests.StubsOf(t));
        Assert.Contains($"--tdd: codeunit \"Package Only Points\" not generated - {PackageRefusal}", stderr);
        Assert.DoesNotContain("--tdd: generated codeunit", stderr);
        Assert.Contains("no members were generated", stdout + stderr);
        Assert.Equal(before, TddMissingObjectTests.HashTree(app));
    }

    /// <summary>The refusal does not depend on the order the compile reports its diagnostics in.</summary>
    [SkippableFact]
    public void ObjectOfAPackageTheAppDoesNotDeclare_IsNotShadowed_UnderTheReversedFeed()
    {
        TestArtifacts.SkipIfMissing();
        var app = MakeTestFolder("refused");

        var (stdout, stderr, exit) = TddMissingObjectTests.RunRunner(("AL_RUNNER_TDD_DIAG_ORDER", "reverse"),
            "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{app}\"");

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var t = Assert.Single(doc.RootElement.GetProperty("tests").EnumerateArray().ToList());
        Assert.Equal("fail", t.GetProperty("status").GetString());
        Assert.Contains($"not generated - {PackageRefusal}", stderr);
        Assert.DoesNotContain("--tdd: generated codeunit", stderr);
    }

    /// <summary>The package is in .alpackages but declares nothing of the missing name: still generated, and the
    /// message no longer claims what is not known about the packages.</summary>
    [SkippableFact]
    public void ObjectNoPackageDeclares_IsStillGenerated()
    {
        TestArtifacts.SkipIfMissing();
        var app = MakeTestFolder("control");

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var t = Assert.Single(doc.RootElement.GetProperty("tests").EnumerateArray().ToList());
        Assert.Equal("pass", t.GetProperty("status").GetString());
        Assert.Equal(new[]
        {
            "Nowhere Declared Points: codeunit 65381",
            "Nowhere Declared Points: procedure \"CalcPoints\"(Arg1: Integer): Integer",
        }, TddMissingObjectTests.StubsOf(t));
        Assert.Contains("--tdd: generated codeunit \"Nowhere Declared Points\" (id 65381) in PackageTests.Codeunit.al: no app of the run and no package it can read declares it", stderr);
    }

    /// <summary>The app declares the dependency: the package's codeunit resolves, so there is no AL0185 and nothing
    /// is generated (the object is the package's, not ours).</summary>
    [SkippableFact]
    public void ObjectOfADeclaredPackage_IsNotGenerated()
    {
        TestArtifacts.SkipIfMissing();
        var app = MakeTestFolder("declared");

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var t = Assert.Single(doc.RootElement.GetProperty("tests").EnumerateArray().ToList());
        Assert.Equal("pass", t.GetProperty("status").GetString());
        Assert.Empty(TddMissingObjectTests.StubsOf(t));
        Assert.DoesNotContain("--tdd: generated codeunit", stderr);
        Assert.DoesNotContain("not generated", stderr);
    }

    /// <summary>#5450, the shape the review measured: BC's compile tolerates an unrelated package whose symbols are
    /// malformed (a valid manifest), so the missing codeunit no package declares is still generated, with a note
    /// that names the skipped file. Before: the refusal did not depend on the name and turned the feature off.</summary>
    [SkippableFact]
    public void AnUnreadablePackageThatNeverMentionsTheName_DoesNotRefuseTheGeneration()
    {
        TestArtifacts.SkipIfMissing();
        var app = MakeTestFolder("control");
        AddUnrelatedPackage(app, "\"Codeunits\":[");

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var t = Assert.Single(doc.RootElement.GetProperty("tests").EnumerateArray().ToList());
        Assert.Equal("pass", t.GetProperty("status").GetString());
        Assert.Equal(new[]
        {
            "Nowhere Declared Points: codeunit 65381",
            "Nowhere Declared Points: procedure \"CalcPoints\"(Arg1: Integer): Integer",
        }, TddMissingObjectTests.StubsOf(t));
        Assert.Contains("--tdd: generated codeunit \"Nowhere Declared Points\" (id 65381)", stderr);
        Assert.Contains("--tdd: the package Broken Unrelated 1.0.0.0 (Broken_Unrelated_1.0.0.0.app) could not be read", stderr);
        Assert.Contains("never mentions \"Nowhere Declared Points\"", stderr);
        Assert.DoesNotContain("not generated", stderr);
    }

    /// <summary>The same unreadable package, but its (truncated) symbols text does mention the name: it may be the
    /// object's home, so the run refuses, names the file and says how to clear it.</summary>
    [SkippableFact]
    public void AnUnreadablePackageThatMentionsTheName_StillRefusesAndSaysHowToClearIt()
    {
        TestArtifacts.SkipIfMissing();
        var app = MakeTestFolder("control");
        AddUnrelatedPackage(app, "\"Codeunits\":[{\"Id\":65410,\"Name\":\"nowhere DECLARED points\",\"Methods\":[");

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var t = Assert.Single(doc.RootElement.GetProperty("tests").EnumerateArray().ToList());
        Assert.Equal("fail", t.GetProperty("status").GetString());
        Assert.Empty(TddMissingObjectTests.StubsOf(t));
        Assert.Contains("--tdd: codeunit \"Nowhere Declared Points\" not generated - the package Broken Unrelated 1.0.0.0 (Broken_Unrelated_1.0.0.0.app) could not be read (", stderr);
        Assert.Contains("remove or replace that file", stderr);
        Assert.DoesNotContain("--tdd: generated codeunit", stderr);
    }

    /// <summary>A name the package refuses must not use up an id: "Package Only Points" sorts before "Zulu Nowhere
    /// Points", is refused, and the codeunit generated after it still takes the FIRST free id, 65381 (the fixture's own
    /// codeunit is 65380). #5449 said "a refusal reserves nothing"; moving the package check after the id is reserved
    /// left every test green.</summary>
    [SkippableFact]
    public void ARefusedNameDoesNotUseUpAnId()
    {
        TestArtifacts.SkipIfMissing();
        var app = MakeTestFolder("reserve");

        var (_, stderr, _) = RunTdd(app);

        Assert.Contains($"--tdd: codeunit \"Package Only Points\" not generated - {PackageRefusal}", stderr);
        Assert.Contains("--tdd: generated codeunit \"Zulu Nowhere Points\" (id 65381)", stderr);
    }
}
