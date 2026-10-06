// DependencyResolvedClosureCacheKeyTests — issue #5053.
//
// A dependency that ships source is compiled against the packages the RUN resolved for it, and
// two persisted caches keep the output of that compile: compiled-deps (the DLL) and dep-metadata
// (BC's documents). Their keys named the dependency's own bytes and its DECLARED dependencies, whose
// version is a minimum, so the same dependency compiled against one version of a library and
// against another (a newer one in the project's .alpackages) shared an entry and the second run
// replayed the first run's output.
//
// Each test runs three times on ONE --cache root: library version 1, then version 2 with the
// dependency's own bytes untouched, then version 1 again. A key that never moves (the defect)
// replays on run 2; a key that never repeats (a destroyed cache) misses on run 3.
//
// The library ships symbols only, as a real compiled package does, so it is a binding input and
// not code the run executes: what the dependency's compile bakes in is the only thing that can differ.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyResolvedClosureCacheKeyTests : IDisposable
{
    private const int SpawnTimeoutMs = 240_000;
    private const string LibraryId = "5053c0de-1b2e-4f3e-9a41-5e2b8c9d0001";
    private const string DependencyId = "5053c0de-1b2e-4f3e-9a41-5e2b8c9d0002";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly string _root;
    private readonly string _cache;
    private readonly string _packages;

    public DependencyResolvedClosureCacheKeyTests()
    {
        _root = TestScratch.Dir("al-runner-resolved-closure-key");
        _cache = Path.Combine(_root, "cache");
        _packages = Path.Combine(_root, "test", ".alpackages");
        Directory.CreateDirectory(_packages);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // A field typed by the library's enum: BC's metadata document for it records the enum's values.
    private const string ProbeTableSource = """
        table 50311 ClosureProbe
        {
            fields
            {
                field(1; Code; Code[10]) { }
                field(2; Kind; Enum "Closure Kind") { InitValue = Second; }
                field(3; Related; Code[10]) { TableRelation = "Closure Related".Code; }
            }
            keys { key(PK; Code) { Clustered = true; } }
        }
        """;

    private const string TestJson = """
        {"id":"5053c0de-1b2e-4f3e-9a41-5e2b8c9d0003","name":"closure-key-test","publisher":"repro","version":"1.0.0.0","runtime":"16.0","idRanges":[{"from":50350,"to":50399}],"dependencies":[{"id":"5053c0de-1b2e-4f3e-9a41-5e2b8c9d0002","name":"closure-key-dep","publisher":"repro","version":"1.0.0.0"}]}
        """;

    // The dependency's compile binds this enum literal to the ordinal the library declares.
    private const string AnswerSource = """
        codeunit 50310 ClosureAnswer
        {
            procedure Value(): Integer
            begin
                exit(Enum::"Closure Kind"::Second.AsInteger());
            end;
        }
        """;

    private static string TestSource(int expected) => $$"""
        codeunit 50350 ClosureAnswerTests
        {
            Subtype = Test;

            [Test]
            procedure ValueIsTheRunsOrdinal()
            var
                A: Codeunit ClosureAnswer;
            begin
                if A.Value() <> {{expected}} then
                    Error('Value() should be {{expected}}, got %1', A.Value());
            end;
        }
        """;

    private void WriteBundle(int expected)
    {
        var tst = Directory.CreateDirectory(Path.Combine(_root, "test")).FullName;
        File.WriteAllText(Path.Combine(tst, "app.json"), TestJson);
        File.WriteAllText(Path.Combine(tst, "ClosureAnswerTests.Codeunit.al"), TestSource(expected));
    }

    /// <summary>The library, replaced in place: a project updating its .alpackages.</summary>
    private void InstallLibrary(string version, int secondOrdinal, int codeFieldId)
    {
        foreach (var old in Directory.GetFiles(_packages, "repro_closure-key-lib_*.app")) File.Delete(old);
        File.WriteAllBytes(
            Path.Combine(_packages, $"repro_closure-key-lib_{version}.app"),
            BuildLibrary(version, secondOrdinal, codeFieldId));
    }

    private void InstallDependency()
        => File.WriteAllBytes(Path.Combine(_packages, "repro_closure-key-dep_1.0.0.0.app"), BuildDependency());

    private (string Output, int Exit) Run()
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --cache \"{_cache}\" --isolation test --verbose");
        args.Append($" \"{Path.Combine(_root, "test")}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_DEP_METADATA_FROM_BC"] = "closure-key-dep";
        psi.Environment["AL_RUNNER_TRACE_DEP_METADATA"] = "1";
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(SpawnTimeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void AssertPassed((string Output, int Exit) r, string run)
    {
        Assert.True(r.Exit == 0, $"{run}: exit {r.Exit}\n{r.Output}");
        Assert.Contains("passed 1", r.Output);
        Assert.Empty(RunnerFailureLines.All(r.Output));
    }

    private static void AssertOutput((string Output, int Exit) r, string needle, string run)
        => Assert.True(r.Output.Contains(needle), $"{run}: expected '{needle}'\n{r.Output}");

    /// <summary>The library field id each persisted document of the probe table's relation names, sorted.</summary>
    private string[] RelatedFieldIdsInEntries()
        => Directory.GetFiles(Path.Combine(_cache, "dep-metadata"), "*.object-metadata.json")
            .SelectMany(f => System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement
                .GetProperty("objects").EnumerateArray()
                .Where(o => o.GetProperty("kind").GetString() == "Table")
                .Select(o => System.Text.RegularExpressions.Regex.Match(
                    o.GetProperty("xml").GetString()!, "<TableRelations TableID=\"50301\"[^>]*FieldID=\"(\\d+)\"").Groups[1].Value))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private int CompiledDepEntries() =>
        Directory.GetFiles(Path.Combine(_cache, "compiled-deps"), "*.dll").Length;

    /// <summary>
    /// One pass through both caches: the dependency's compiled DLL (a procedure returning the library's
    /// enum ordinal, which the compile bakes in) and BC's document for its table (a relation naming the
    /// field id the library declared). A run that resolved library 2.0 must compile both afresh, and a
    /// run back on 1.0 must hit what the first run wrote.
    /// </summary>
    [SkippableFact]
    public void LibraryVersionChanged_OneCacheRoot_BothCachesRecompileAgainstTheRunsLibrary()
    {
        TestArtifacts.SkipIfMissing();
        InstallDependency();

        InstallLibrary("1.0.0.0", secondOrdinal: 7, codeFieldId: 1);
        WriteBundle(expected: 7);
        var first = Run();
        AssertPassed(first, "run 1 (library 1.0, Second = 7)");
        AssertOutput(first, "source-cache MISS", "run 1");
        AssertOutput(first, "[dep-metadata] WROTE closure-key-dep", "run 1");

        InstallLibrary("2.0.0.0", secondOrdinal: 9, codeFieldId: 3);
        WriteBundle(expected: 9);
        var second = Run();
        // The replayed DLL answers 7 here, so the run itself fails before any key is read.
        AssertPassed(second, "run 2 (library 2.0, Second = 9)");
        Assert.DoesNotContain("source-cache HIT: closure-key-dep", second.Output);
        AssertOutput(second, "[dep-metadata] WROTE closure-key-dep", "run 2");
        Assert.DoesNotContain("[dep-metadata] cache HIT closure-key-dep", second.Output);

        InstallLibrary("1.0.0.0", secondOrdinal: 7, codeFieldId: 1);
        WriteBundle(expected: 7);
        var third = Run();
        AssertPassed(third, "run 3 (library 1.0 again)");
        AssertOutput(third, "source-cache HIT: closure-key-dep", "run 3");
        AssertOutput(third, "[dep-metadata] cache HIT closure-key-dep", "run 3");

        // Exactly one entry per library in each cache, each holding its own library's answer.
        Assert.Equal(2, CompiledDepEntries());
        Assert.Equal(new[] { "1", "3" }, RelatedFieldIdsInEntries());
    }

    private static byte[] BuildLibrary(string version, int secondOrdinal, int codeFieldId)
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{LibraryId}" Name="closure-key-lib" Publisher="repro" Version="{version}" Runtime="16.0" Target="Cloud" />
              <IdRanges><IdRange MinObjectId="50300" MaxObjectId="50309" /></IdRanges>
              <Dependencies />
            </Package>
            """;
        var symbols = $$"""
            {"RuntimeVersion":"16.0","Codeunits":[],"Tables":[{"Fields":[{"TypeDefinition":{"Name":"Code","Length":10},"Id":{{codeFieldId}},"Name":"Code"}],"Keys":[{"FieldNames":["Code"],"Clustered":true,"Name":"PK"}],"Id":50301,"Name":"Closure Related"}],"Reports":[],"XmlPorts":[],"Queries":[],"ControlAddIns":[],"EnumTypes":[{"Values":[{"Name":"First"},{"Ordinal":{{secondOrdinal}},"Name":"Second"}],"Id":50300,"Name":"Closure Kind"}],"DotNetPackages":[],"Interfaces":[],"PermissionSets":[],"PermissionSetExtensions":[],"ReportExtensions":[],"InternalsVisibleToModules":[],"AppId":"{{LibraryId}}","Name":"closure-key-lib","Publisher":"repro","Version":"{{version}}"}
            """;
        return Package(LibraryId, manifest, symbols, sources: Array.Empty<(string, string)>());
    }

    private static byte[] BuildDependency()
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DependencyId}" Name="closure-key-dep" Publisher="repro" Version="1.0.0.0" Runtime="16.0" Target="Cloud" ShowMyCode="True" />
              <IdRanges><IdRange MinObjectId="50310" MaxObjectId="50349" /></IdRanges>
              <Dependencies>
                <Dependency Id="{LibraryId}" Name="closure-key-lib" Publisher="repro" MinVersion="1.0.0.0"/>
              </Dependencies>
              <ResourceExposurePolicy AllowDebugging="true" AllowDownloadingSource="true" IncludeSourceInSymbolFile="true" />
            </Package>
            """;
        var symbols = $$"""
            {"RuntimeVersion":"16.0","Codeunits":[{"Methods":[{"ReturnTypeDefinition":{"Name":"Integer"},"Id":-163786484,"Name":"Value"}],"ReferenceSourceFileName":"ClosureAnswer.Codeunit.al","Id":50310,"Name":"ClosureAnswer"}],"Reports":[],"XmlPorts":[],"Queries":[],"ControlAddIns":[],"EnumTypes":[],"DotNetPackages":[],"Interfaces":[],"PermissionSets":[],"PermissionSetExtensions":[],"ReportExtensions":[],"InternalsVisibleToModules":[],"AppId":"{{DependencyId}}","Name":"closure-key-dep","Publisher":"repro","Version":"1.0.0.0"}
            """;
        return Package(DependencyId, manifest, symbols, new[]
        {
            ("src/ClosureAnswer.Codeunit.al", AnswerSource),
            ("src/ClosureProbe.Table.al", ProbeTableSource),
        });
    }

    private static byte[] Package(string appId, string manifest, string symbols, (string Name, string Content)[] sources)
    {
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                // Fixed, so a package built twice from the same inputs is byte-identical and the
                // third run can match the first run's content term.
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
            // Without the OPC content-types part BC's compiler rejects the package (AL1023).
            Add("[Content_Types].xml", """
                <?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="" /><Default Extension="al" ContentType="" /><Default Extension="json" ContentType="" /></Types>
                """);
            Add("NavxManifest.xml", manifest);
            Add("SymbolReference.json", symbols);
            foreach (var (name, content) in sources) Add(name, content);
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
}
