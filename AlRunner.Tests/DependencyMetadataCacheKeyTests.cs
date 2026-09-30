// DependencyMetadataCacheKeyTests — issue #5039.
//
// The dep-metadata cache (AL_RUNNER_DEP_METADATA_FROM_BC) stores BC's metadata documents from
// compiling a dependency's embedded source. Two inputs change those documents without changing
// the dependency's id or version: the package's source, and the --define symbols the compile
// applies (BcCompiler.BuildParseOptions). Each test runs three times on ONE --cache root,
// changing one input on the second run and restoring it on the third, then counts the entries
// and reads which documents each one holds: a key that never moves (the defect) leaves one
// entry, and a key that never repeats (a destroyed cache) leaves three.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyMetadataCacheKeyTests : IDisposable
{
    private const int SpawnTimeoutMs = 240_000;
    private const string AppId = "5039c0de-1b2e-4f3e-9a41-5e2b8c9d5039";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly string _root;
    private readonly string _cache;

    public DependencyMetadataCacheKeyTests()
    {
        _root = TestScratch.Dir("al-runner-dep-metadata-key");
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string TestJson = """
        {"id":"5039c0de-1b2e-4f3e-9a41-5e2b8c9e5039","name":"dep-metadata-key-test","publisher":"repro","version":"1.0.0.0","runtime":"16.0","idRanges":[{"from":50250,"to":50299}],"dependencies":[{"id":"5039c0de-1b2e-4f3e-9a41-5e2b8c9d5039","name":"dep-metadata-key-app","publisher":"repro","version":"1.0.0.0"}]}
        """;

    private const string AnswerSource = """
        codeunit 50200 Answer
        {
            procedure Value(): Integer
            begin
                exit(1);
            end;
        }
        """;

    private const string TestSource = """
        codeunit 50250 AnswerTests
        {
            Subtype = Test;

            [Test]
            procedure ValueIsOne()
            var
                A: Codeunit Answer;
            begin
                if A.Value() <> 1 then
                    Error('Value() should be 1, got %1', A.Value());
            end;
        }
        """;

    /// <summary>The dependency's table: field 20 under FLAG, field 21 under OTHER.</summary>
    private const string GatedTable = """
        table 50200 KeyProbe
        {
            fields
            {
                field(1; Code; Code[10]) { }
        #if FLAG
                field(20; FlagOnly; Integer) { }
        #endif
        #if OTHER
                field(21; OtherOnly; Integer) { }
        #endif
            }
            keys { key(PK; Code) { Clustered = true; } }
        }
        """;

    private const string TableWithoutField20 = """
        table 50200 KeyProbe
        {
            fields
            {
                field(1; Code; Code[10]) { }
            }
            keys { key(PK; Code) { Clustered = true; } }
        }
        """;

    private const string TableWithField20 = """
        table 50200 KeyProbe
        {
            fields
            {
                field(1; Code; Code[10]) { }
                field(20; FlagOnly; Integer) { }
            }
            keys { key(PK; Code) { Clustered = true; } }
        }
        """;

    private void WriteFixture(string tableSource)
    {
        var tst = Directory.CreateDirectory(Path.Combine(_root, "test")).FullName;
        File.WriteAllText(Path.Combine(tst, "app.json"), TestJson);
        File.WriteAllText(Path.Combine(tst, "AnswerTests.Codeunit.al"), TestSource);
        WritePackage(tableSource);
    }

    private void WritePackage(string tableSource)
    {
        var packages = Directory.CreateDirectory(Path.Combine(_root, "test", ".alpackages")).FullName;
        File.WriteAllBytes(
            Path.Combine(packages, "repro_dep-metadata-key-app_1.0.0.0.app"),
            BuildPackagedApp(tableSource));
    }

    private (string Output, int Exit) Run(params string[] defines)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --cache \"{_cache}\" --isolation test");
        foreach (var d in defines) args.Append($" --define {d}");
        args.Append($" \"{Path.Combine(_root, "test")}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_DEP_METADATA_FROM_BC"] = "dep-metadata-key-app";
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

    /// <summary>The run compiled the dependency's source rather than replaying an entry.</summary>
    private static void AssertCompiled((string Output, int Exit) r, string run)
    {
        Assert.True(r.Output.Contains("[dep-metadata] WROTE dep-metadata-key-app"),
            $"{run}: expected a fresh compile of the dependency's metadata\n{r.Output}");
        Assert.DoesNotContain("[dep-metadata] cache HIT dep-metadata-key-app", r.Output);
    }

    private static void AssertReplayed((string Output, int Exit) r, string run)
    {
        Assert.True(r.Output.Contains("[dep-metadata] cache HIT dep-metadata-key-app"),
            $"{run}: expected the entry an earlier run wrote to be replayed\n{r.Output}");
        Assert.DoesNotContain("[dep-metadata] WROTE dep-metadata-key-app", r.Output);
    }

    private string[] EntryTexts() =>
        Directory.GetFiles(Path.Combine(_cache, "dep-metadata"), "*.object-metadata.json")
            .Select(File.ReadAllText).ToArray();

    /// <summary>Entries whose KeyProbe document declares each of the two gated fields.</summary>
    private (int Total, int Flag, int Other) Census()
    {
        var texts = EntryTexts();
        return (texts.Length,
            texts.Count(t => t.Contains("FlagOnly")),
            texts.Count(t => t.Contains("OtherOnly")));
    }

    /// <summary>
    /// The issue's shape: a --define symbol gates a field of a dependency table. The second run's
    /// symbols differ, so it must compile rather than replay the first run's documents.
    /// </summary>
    [SkippableFact]
    public void DefineAdded_OneCacheRoot_SecondRunCompilesItsOwnDocuments()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture(GatedTable);

        var first = Run();
        AssertPassed(first, "run 1 (no symbols)");
        AssertCompiled(first, "run 1 (no symbols)");

        var second = Run("FLAG");
        AssertPassed(second, "run 2 (--define FLAG)");
        AssertCompiled(second, "run 2 (--define FLAG)");

        var third = Run();
        AssertPassed(third, "run 3 (no symbols again)");
        AssertReplayed(third, "run 3 (no symbols again)");

        Assert.Equal((2, 1, 0), Census());
    }

    /// <summary>
    /// One symbol replaced by a different one: the same COUNT of symbols, so a term that
    /// recorded only whether any were set would not separate these runs.
    /// </summary>
    [SkippableFact]
    public void SymbolSetChanged_FlagToOther_OneCacheRoot_EachSetGetsItsOwnDocuments()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture(GatedTable);

        var first = Run("FLAG");
        AssertPassed(first, "run 1 (--define FLAG)");
        AssertCompiled(first, "run 1 (--define FLAG)");

        var second = Run("OTHER");
        AssertPassed(second, "run 2 (--define OTHER)");
        AssertCompiled(second, "run 2 (--define OTHER)");

        var third = Run("FLAG");
        AssertPassed(third, "run 3 (--define FLAG again)");
        AssertReplayed(third, "run 3 (--define FLAG again)");

        Assert.Equal((2, 1, 1), Census());
    }

    /// <summary>
    /// The dependency is rebuilt with a new field and the SAME id and version. Its package bytes
    /// are the only thing that differs, so only a content term separates the two entries.
    /// </summary>
    [SkippableFact]
    public void PackageContentChanged_SameVersion_OneCacheRoot_SecondRunCompilesTheNewSource()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture(TableWithoutField20);

        var first = Run();
        AssertPassed(first, "run 1 (package without field 20)");
        AssertCompiled(first, "run 1 (package without field 20)");

        WritePackage(TableWithField20);
        var second = Run();
        AssertPassed(second, "run 2 (package with field 20)");
        AssertCompiled(second, "run 2 (package with field 20)");

        WritePackage(TableWithoutField20);
        var third = Run();
        AssertPassed(third, "run 3 (package without field 20 again)");
        AssertReplayed(third, "run 3 (package without field 20 again)");

        Assert.Equal((2, 1, 0), Census());
    }

    private static byte[] BuildPackagedApp(string tableSource)
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{AppId}" Name="dep-metadata-key-app" Publisher="repro" Version="1.0.0.0" Runtime="16.0" Target="Cloud" ShowMyCode="True" />
              <IdRanges><IdRange MinObjectId="50200" MaxObjectId="50249" /></IdRanges>
              <Dependencies />
              <ResourceExposurePolicy AllowDebugging="true" AllowDownloadingSource="true" IncludeSourceInSymbolFile="true" />
            </Package>
            """;
        var symbols = $$"""
            {"RuntimeVersion":"16.0","Codeunits":[{"Methods":[{"ReturnTypeDefinition":{"Name":"Integer"},"Id":-163786484,"Name":"Value"}],"ReferenceSourceFileName":"Answer.Codeunit.al","Id":50200,"Name":"Answer"}],"Reports":[],"XmlPorts":[],"Queries":[],"ControlAddIns":[],"EnumTypes":[],"DotNetPackages":[],"Interfaces":[],"PermissionSets":[],"PermissionSetExtensions":[],"ReportExtensions":[],"InternalsVisibleToModules":[],"AppId":"{{AppId}}","Name":"dep-metadata-key-app","Publisher":"repro","Version":"1.0.0.0"}
            """;
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                // Fixed, so two packages built from the same source are byte-identical and the
                // restored third run can match the first run's content term.
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
            Add("src/Answer.Codeunit.al", AnswerSource);
            Add("src/KeyProbe.Table.al", tableSource);
        }
        var payload = zipBuffer.ToArray();
        using var app = new MemoryStream();
        using var bw = new BinaryWriter(app);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(40);
        bw.Write(2);
        bw.Write(Guid.Parse(AppId).ToByteArray());
        bw.Write((long)payload.Length);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(payload);
        bw.Flush();
        return app.ToArray();
    }
}
