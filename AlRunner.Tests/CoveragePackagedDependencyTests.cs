// CoveragePackagedDependencyTests — #4273, the packaged half of #3965.
//
// The subject app is consumed ONLY as a packaged .app in the consuming bundle's .alpackages —
// #3965's own layout. The package is built here from the checked-in
// Fixtures/CoverageDependencySource/dep source, in the shape `alc` produced for that app
// (NavxManifest.xml + SymbolReference.json + src/*.al; the JSON below is alc 17.0's output,
// verbatim). It carries no DLL, so the runner compiles it from the embedded AL (Tier 3).
//
// Two outcomes, one per way a packaged dependency's code can reach the run:
//   - compiled here from its source  -> attributed, with the never-called Never() as the control;
//   - precompiled (.deps-bin)        -> not attributed, and the run says so by name.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class CoveragePackagedDependencyTests : IDisposable
{
    private const int SpawnTimeoutMs = 240_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "CoverageDependencySource");

    private const string DepAppId = "c9a37e51-6d24-4b83-a15f-8e2760d4bb31";
    private const string DepName = "Runner Tests Fixture - Coverage Dependency Source Subject";
    private const string DepPublisher = "AL Runner";
    private const string DepPackageFile = "AL Runner_" + DepName + "_1.0.0.0.app";
    private const string NoteHeader = "Coverage note: AL executed in these dependencies is not in the report above";

    // The executed statement and the never-executed one, read from dep/CdsSubject.Codeunit.al.
    private const int TwiceLine = 9;
    private const int NeverLine = 14;

    private const string SymbolReferenceJson = """
        {"RuntimeVersion":"15.0","Codeunits":[{"Methods":[{"ReturnTypeDefinition":{"Name":"Integer"},"Parameters":[{"Name":"Value","TypeDefinition":{"Name":"Integer"}}],"Id":1516892452,"Name":"Twice"},{"ReturnTypeDefinition":{"Name":"Integer"},"Parameters":[{"Name":"Value","TypeDefinition":{"Name":"Integer"}}],"Id":-889937171,"Name":"Never"}],"ReferenceSourceFileName":"CdsSubject.Codeunit.al","Id":70860,"Name":"CDS Subject"}],"Reports":[],"XmlPorts":[],"Queries":[],"ControlAddIns":[],"EnumTypes":[],"DotNetPackages":[],"Interfaces":[],"PermissionSets":[],"PermissionSetExtensions":[],"ReportExtensions":[],"InternalsVisibleToModules":[],"AppId":"c9a37e51-6d24-4b83-a15f-8e2760d4bb31","Name":"Runner Tests Fixture - Coverage Dependency Source Subject","Publisher":"AL Runner","Version":"1.0.0.0"}
        """;

    private readonly string _scratch;

    public CoveragePackagedDependencyTests()
    {
        _scratch = TestScratch.Dir("al-runner-coverage-packaged-dep");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private static byte[] BuildSubjectApp()
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepAppId}" Name="{DepName}" Publisher="{DepPublisher}" Version="1.0.0.0" Platform="27.0.0.0" Runtime="15.0" Target="OnPrem" ShowMyCode="False" />
              <IdRanges><IdRange MinObjectId="70860" MaxObjectId="70869" /></IdRanges>
              <Dependencies />
              <ResourceExposurePolicy AllowDebugging="true" AllowDownloadingSource="true" IncludeSourceInSymbolFile="true" />
            </Package>
            """;
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
            // Without the OPC content-types part BC's compiler rejects the package (AL1023).
            Add("[Content_Types].xml", """
                <?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="" /><Default Extension="al" ContentType="" /><Default Extension="json" ContentType="" /></Types>
                """);
            Add("NavxManifest.xml", manifest);
            Add("SymbolReference.json", SymbolReferenceJson);
            Add("src/CdsSubject.Codeunit.al", File.ReadAllText(Path.Combine(FixtureRoot, "dep", "CdsSubject.Codeunit.al")));
        }
        var payload = zipBuffer.ToArray();
        using var app = new MemoryStream();
        using var bw = new BinaryWriter(app);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(40);
        bw.Write(2);
        bw.Write(Guid.Parse(DepAppId).ToByteArray());
        bw.Write((long)payload.Length);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(payload);
        bw.Flush();
        return app.ToArray();
    }

    /// <summary>The consuming bundle — Fixtures/CoverageDependencySource/main — with the subject
    /// app beside it ONLY as a package. No `dep/` directory anywhere near it.</summary>
    private string BuildIsolatedBundle()
    {
        var tests = Path.Combine(_scratch, "isolated", "tests");
        var packages = Path.Combine(tests, ".alpackages");
        Directory.CreateDirectory(packages);
        foreach (var f in Directory.GetFiles(Path.Combine(FixtureRoot, "main")))
            File.Copy(f, Path.Combine(tests, Path.GetFileName(f)));
        File.WriteAllBytes(Path.Combine(packages, DepPackageFile), BuildSubjectApp());
        return tests;
    }

    private (string Output, int Exit) Spawn(params string[] args)
    {
        var sb0 = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        foreach (var a in args) sb0.Append(" \"").Append(a).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = sb0.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _scratch,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(SpawnTimeoutMs), $"runner did not exit within {SpawnTimeoutMs / 1000}s");
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private (string Output, int Exit) RunCoverage(string bundle, string cache, string covPath)
    {
        var args = new List<string> { bundle, "--cache", cache, "--coverage", "--coverage-out", covPath };
        args.AddRange(TestBuildConfig.BcVersionArg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Spawn(args.ToArray());
    }

    private static XElement? ClassFor(XDocument doc, string suffix) =>
        doc.Descendants("class").FirstOrDefault(c =>
            c.Attribute("filename")!.Value.Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal));

    private static int? Hits(XElement cls, int line) =>
        cls.Descendants("line").FirstOrDefault(l => (int)l.Attribute("number")! == line) is { } el
            ? (int)el.Attribute("hits")! : null;

    private static string Files(XDocument doc) =>
        string.Join(", ", doc.Descendants("class").Select(c => c.Attribute("filename")!.Value));

    /// <summary>
    /// #4273: a package the run compiled from its embedded AL is attributed — Twice's statement
    /// hit, Never's present and unhit — on a cold run AND on the warm run that replays the
    /// compiled dependency from the cache, against ONE cache root (local-test-scope.md). And
    /// nothing else is: the Microsoft Test Runner the runner also compiles this way stays out.
    /// </summary>
    [SkippableFact]
    public void PackagedOnlyDependency_CompiledFromItsSource_IsAttributed_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = BuildIsolatedBundle();
        var cache = Path.Combine(_scratch, "cache");

        foreach (var pass in new[] { "cold", "warm" })
        {
            var covPath = Path.Combine(_scratch, $"{pass}.xml");
            var (output, exit) = RunCoverage(bundle, cache, covPath);
            Assert.True(exit == 0, $"{pass}: the fixture's one test must pass, exit was {exit}.\n{output}");
            var doc = XDocument.Load(covPath);

            var subject = ClassFor(doc, "/src/CdsSubject.Codeunit.al");
            Assert.True(subject is not null,
                $"{pass}: the packaged dependency's executed statement is absent from the report. Files: {Files(doc)}");
            Assert.True(Hits(subject!, TwiceLine) >= 1, $"{pass}: Twice() executed, so line {TwiceLine} must carry a hit");
            Assert.True(Hits(subject!, NeverLine) == 0,
                $"{pass}: Never() is not called, so line {NeverLine} must be present with no hit; got {Hits(subject!, NeverLine)}");

            // Exactly the two apps the user wrote: the consumer and the subject.
            Assert.True(doc.Descendants("class").Count() == 2, $"{pass}: expected 2 files, got {Files(doc)}");
            Assert.DoesNotContain(NoteHeader, output, StringComparison.Ordinal);
        }
        Assert.True(Directory.GetFiles(Path.Combine(cache, "compiled-deps"), "*.dll").Length > 0,
            "the warm pass must have had a compiled-deps entry to replay");
    }

    /// <summary>
    /// #4273: the same package served as a PRECOMPILED sidecar (.deps-bin, Tier 1) runs code the
    /// runner cannot tie to the package's text, so it is not attributed — and the run names it
    /// rather than dropping it in silence.
    /// </summary>
    [SkippableFact]
    public void PrecompiledPackagedDependency_IsNotAttributed_AndTheRunNamesIt()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = BuildIsolatedBundle();
        var dll = Path.Combine(_scratch, "Subject.dll");
        var precompileArgs = new List<string> { "--precompile", Path.Combine(bundle, ".alpackages", DepPackageFile), "--out", dll };
        var (preOut, preExit) = Spawn(precompileArgs.ToArray());
        Assert.True(preExit == 0 && File.Exists(dll), $"--precompile failed, exit {preExit}.\n{preOut}");
        var depsBin = Path.Combine(bundle, ".deps-bin");
        Directory.CreateDirectory(depsBin);
        File.Copy(dll, Path.Combine(depsBin, "AL_Runner_" + DepName.Replace(' ', '_') + "_1.0.0.0.dll"));

        var covPath = Path.Combine(_scratch, "precompiled.xml");
        var (output, exit) = RunCoverage(bundle, Path.Combine(_scratch, "cache"), covPath);
        Assert.True(exit == 0, $"exit was {exit}.\n{output}");

        var doc = XDocument.Load(covPath);
        Assert.Null(ClassFor(doc, "CdsSubject.Codeunit.al"));
        Assert.NotNull(ClassFor(doc, "CdsTests.Codeunit.al"));
        Assert.Contains(NoteHeader, output, StringComparison.Ordinal);
        Assert.Contains($"  AL Runner_{DepName}_1.0.0.0  (1 object(s) executed)", output, StringComparison.Ordinal);
    }
}

public sealed class PackagedDependencySourcesTests : IDisposable
{
    private readonly string _scratch = TestScratch.Dir("al-runner-packaged-dep-sources");

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("src/Base64%2520Convert/X.Codeunit.al", "src/Base64 Convert/X.Codeunit.al")]
    [InlineData("src/A%20B/Y.al", "src/A B/Y.al")]
    [InlineData("src/../../escape.al", "src/_/_/escape.al")]
    [InlineData("src/./x.al", "src/_/x.al")]
    public void SafeRelativePath_DecodesAndStaysUnderItsRoot(string entry, string expected) =>
        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), PackagedDependencySources.SafeRelativePath(entry));

    [Fact]
    public void Materialize_WritesThePackagesSourceAtItsPaths_AndAnEmptyManifest()
    {
        var app = Path.Combine(_scratch, "p.app");
        using (var zipBuffer = new MemoryStream())
        {
            using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, text) in new[] { ("src/A/Same.Codeunit.al", "codeunit 1 A {}"), ("src/B/Same.Codeunit.al", "codeunit 2 B {}"), ("other/Z.al", "codeunit 3 Z {}") })
                {
                    using var w = new StreamWriter(zip.CreateEntry(name).Open());
                    w.Write(text);
                }
            }
            var payload = zipBuffer.ToArray();
            Directory.CreateDirectory(_scratch);
            File.WriteAllBytes(app, Encoding.ASCII.GetBytes("NAVX").Concat(BitConverter.GetBytes(8)).Concat(payload).ToArray());
        }

        var dir = PackagedDependencySources.Materialize(app, "key1", _scratch);

        Assert.Equal(Path.Combine(_scratch, "key1.src"), dir);
        // Both files sharing a base name survive — the flat Tier-3 extraction keeps only one.
        Assert.Equal("codeunit 1 A {}", File.ReadAllText(Path.Combine(dir, "src", "A", "Same.Codeunit.al")));
        Assert.Equal("codeunit 2 B {}", File.ReadAllText(Path.Combine(dir, "src", "B", "Same.Codeunit.al")));
        Assert.False(File.Exists(Path.Combine(dir, "other", "Z.al")), "only src/ is compiled, so only src/ is mapped");
        Assert.Equal("{}", File.ReadAllText(Path.Combine(dir, "app.json")));
        Assert.Empty(Directory.GetDirectories(_scratch, "key1.src.tmp-*"));
    }

    [Fact]
    public void TheUnattributedNote_NamesARegisteredNonMicrosoftApp_Only()
    {
        Assert.True(AlCoverageTracker.NamedInUnattributedNote(("Subject", "AL Runner", "1.0.0.0")));
        Assert.False(AlCoverageTracker.NamedInUnattributedNote(("Test Runner", "Microsoft", "28.1.0.0")));
        // An assembly no dependency load registered: the service-tier DLLs, all Microsoft's.
        Assert.False(AlCoverageTracker.NamedInUnattributedNote(null));
    }

    [Theory]
    [InlineData("Microsoft", false)]
    [InlineData("microsoft", false)]
    [InlineData("AL Runner", true)]
    public void Register_RecordsEveryPublisherButMicrosoft(string publisher, bool expectRoot)
    {
        PackagedDependencySources.ResetForTests();
        try
        {
            PackagedDependencySources.Register(Guid.NewGuid(), publisher, Path.Combine(_scratch, "none.app"), "k");
            Assert.Equal(expectRoot, PackagedDependencySources.RegisteredCount == 1);
        }
        finally { PackagedDependencySources.ResetForTests(); }
    }
}

/// <summary>
/// #4273: a packaged root must only ADD objects. Build keeps the last root's mapping of an
/// object, so the packaged roots go first and an object that also has a source root keeps it.
/// </summary>
[Collection(BcEngineCollection.Name)]
public sealed class PackagedDependencySourcePrecedenceTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root = TestScratch.Dir("al-runner-packaged-precedence");

    public PackagedDependencySourcePrecedenceTests(BcEngineFixture engine) => _engine = engine;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [SkippableFact]
    public void AnObjectWithASourceRoot_KeepsItsSourceFile_WhileAPackagedOnlyObjectIsAdded()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var source = Path.Combine(_root, "source");
        var packaged = Path.Combine(_root, "key.src");
        Write(Path.Combine(source, "Shared.Codeunit.al"), "codeunit 79860 \"Prec Shared\"\n{\n}\n");
        Write(Path.Combine(packaged, "app.json"), "{}");
        Write(Path.Combine(packaged, "src", "Shared.Codeunit.al"), "codeunit 79860 \"Prec Shared\"\n{\n}\n");
        Write(Path.Combine(packaged, "src", "OnlyPackaged.Codeunit.al"), "codeunit 79861 \"Prec Only Packaged\"\n{\n}\n");

        var map = AlCoverageSourceMap.Build(
            AlCoverageSourceMap.RootsWithParsedSourceDependencies(new[] { source }, new[] { packaged }),
            relativeTo: null);

        string Abs(string p) => Path.GetFullPath(p).Replace('\\', '/');
        Assert.Equal(Abs(Path.Combine(source, "Shared.Codeunit.al")), map[("CodeUnit", 79860)]);
        Assert.Equal(Abs(Path.Combine(packaged, "src", "OnlyPackaged.Codeunit.al")), map[("CodeUnit", 79861)]);
    }
}
