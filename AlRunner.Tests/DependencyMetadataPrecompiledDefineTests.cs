// DependencyMetadataPrecompiledDefineTests — issue #5051.
//
// A dependency that ships a precompiled DLL (Tier 1: `.deps-bin/<Publisher>_<Name>_<Version>.dll`)
// runs code whose shape the publisher's own compile fixed. The dep-metadata producer
// (AL_RUNNER_DEP_METADATA_FROM_BC) compiles the package's embedded source for BC's metadata
// documents, and the document it produces is what RecordRef reads. So that compile must use the
// package's own symbols, never the test run's --define: otherwise a `#if FLAG` field the DLL does
// not have becomes visible to AL.
//
// The DLL is built by the test from the dependency's source with no symbols, the way a publisher
// ships it. The dependency's source is kept OUTSIDE the test bundle's directory tree: a sibling
// workspace folder is source-compiled with the run's --define (SiblingCompile), which would
// replace the Tier-1 load this class is about.
//
// The Tier-3 side of the boundary — a dependency the runner compiles from source keeps the
// --define in its metadata, because its code is compiled with it — is pinned by
// DependencyMetadataCacheKeyTests.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyMetadataPrecompiledDefineTests : IDisposable
{
    private const int SpawnTimeoutMs = 240_000;
    private const string AppId = "5051c0de-1b2e-4f3e-9a41-5e2b8c9d5051";
    private const string DepName = "dep-metadata-dll-app";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly string _root;
    private readonly string _cache;

    public DependencyMetadataPrecompiledDefineTests()
    {
        _root = TestScratch.Dir("al-runner-dep-metadata-dll-define");
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string DepJson = """
        {"id":"5051c0de-1b2e-4f3e-9a41-5e2b8c9d5051","name":"dep-metadata-dll-app","publisher":"repro","version":"1.0.0.0","runtime":"16.0","target":"Cloud","idRanges":[{"from":50200,"to":50249}],"dependencies":[]}
        """;

    private const string TestJson = """
        {"id":"5051c0de-1b2e-4f3e-9a41-5e2b8c9e5051","name":"dep-metadata-dll-test","publisher":"repro","version":"1.0.0.0","runtime":"16.0","target":"Cloud","idRanges":[{"from":50250,"to":50299}],"dependencies":[{"id":"5051c0de-1b2e-4f3e-9a41-5e2b8c9d5051","name":"dep-metadata-dll-app","publisher":"repro","version":"1.0.0.0"}]}
        """;

    private const string GatedTable = """
        table 50200 DllProbe
        {
            fields
            {
                field(1; Code; Code[10]) { }
        #if FLAG
                field(20; FlagOnly; Integer) { }
        #endif
            }
            keys { key(PK; Code) { Clustered = true; } }
        }
        """;

    /// <summary>
    /// The DLL was compiled without FLAG, so the table has field 1 and nothing else, whatever
    /// the run defines. Field 1 is the control: a table the run could not see at all would fail
    /// it, where it would pass the field-20 test.
    /// </summary>
    private const string TestSource = """
        codeunit 50250 DllProbeTests
        {
            Subtype = Test;

            [Test]
            procedure TableHasOnlyTheFieldTheDllHas()
            var
                R: RecordRef;
            begin
                R.Open(50200);
                if not R.FieldExist(1) then
                    Error('DLLPROBE field 1 missing');
                if R.FieldExist(20) then
                    Error('DLLPROBE field 20 present, FieldCount=%1', R.FieldCount());
                if R.FieldCount() <> 1 then
                    Error('DLLPROBE FieldCount=%1, expected 1', R.FieldCount());
            end;
        }
        """;

    private void WriteFixture()
    {
        // The dependency's source, outside the test bundle's tree (see the header).
        var depSrc = Directory.CreateDirectory(Path.Combine(_root, "dep-source", DepName)).FullName;
        File.WriteAllText(Path.Combine(depSrc, "app.json"), DepJson);
        File.WriteAllText(Path.Combine(depSrc, "DllProbe.Table.al"), GatedTable);

        var tst = Directory.CreateDirectory(Path.Combine(_root, "work", "test")).FullName;
        File.WriteAllText(Path.Combine(tst, "app.json"), TestJson);
        File.WriteAllText(Path.Combine(tst, "DllProbeTests.Codeunit.al"), TestSource);

        var packages = Directory.CreateDirectory(Path.Combine(tst, ".alpackages")).FullName;
        File.WriteAllBytes(Path.Combine(packages, $"repro_{DepName}_1.0.0.0.app"), BuildPackagedApp());

        var depsBin = Directory.CreateDirectory(Path.Combine(tst, ".deps-bin")).FullName;
        File.Copy(BuildDependencyDll(depSrc), Path.Combine(depsBin, $"repro_{DepName}_1.0.0.0.dll"));
    }

    /// <summary>
    /// Compiles the dependency as an ordinary bundle, with no symbols, and returns the DLL the
    /// runner wrote to that run's own cache root — the recipe the checked-in Tier-1 fixtures
    /// use (tests/runner-extras/precompiled-tableext-keys/REGENERATE-FIXTURES.txt).
    /// </summary>
    private string BuildDependencyDll(string depSrc)
    {
        var depCache = Path.Combine(_root, "dep-build-cache");
        var r = Spawn($" --cache \"{depCache}\" \"{depSrc}\"", withProducer: false);
        Assert.True(r.Exit == 0, $"building the dependency DLL: exit {r.Exit}\n{r.Output}");
        var dlls = Directory.GetFiles(depCache, "*.dll", SearchOption.TopDirectoryOnly);
        Assert.True(dlls.Length == 1,
            $"expected one compiled DLL at the top of {depCache}, found {dlls.Length}\n{r.Output}");
        return dlls[0];
    }

    private (string Output, int Exit) Run(params string[] defines)
    {
        var args = new StringBuilder($" --cache \"{_cache}\" --isolation test");
        foreach (var d in defines) args.Append($" --define {d}");
        args.Append($" \"{Path.Combine(_root, "work", "test")}\"");
        return Spawn(args.ToString(), withProducer: true);
    }

    private static (string Output, int Exit) Spawn(string runnerArgs, bool withProducer)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(runnerArgs);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        if (withProducer)
        {
            psi.Environment["AL_RUNNER_DEP_METADATA_FROM_BC"] = DepName;
            psi.Environment["AL_RUNNER_TRACE_DEP_METADATA"] = "1";
        }
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
        Assert.DoesNotContain("DLLPROBE", r.Output);
        Assert.Empty(RunnerFailureLines.All(r.Output));
    }

    private string[] EntryTexts() =>
        Directory.GetFiles(Path.Combine(_cache, "dep-metadata"), "*.object-metadata.json")
            .Select(File.ReadAllText).ToArray();

    /// <summary>
    /// Run 1 defines FLAG and has an empty cache, so the producer compiles: the document it
    /// writes must describe the DLL (no field 20). Run 2 defines nothing and must REPLAY run 1's
    /// entry, because for a DLL-shipping dependency the --define is not an input to the compile
    /// and so must not be a term of the key. One entry, without FlagOnly, is both halves.
    /// </summary>
    [SkippableFact]
    public void PrecompiledDependency_DefineDoesNotReachItsMetadata_NorItsCacheKey()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture();

        var first = Run("FLAG");
        AssertPassed(first, "run 1 (--define FLAG, empty cache)");
        Assert.True(first.Output.Contains($"[dep-metadata] WROTE {DepName}"),
            $"run 1: expected the producer to compile the dependency's metadata\n{first.Output}");

        var second = Run();
        AssertPassed(second, "run 2 (no symbols)");
        Assert.True(second.Output.Contains($"[dep-metadata] cache HIT {DepName}"),
            $"run 2: expected run 1's entry to be replayed\n{second.Output}");
        Assert.DoesNotContain($"[dep-metadata] WROTE {DepName}", second.Output);

        var entries = EntryTexts();
        Assert.Single(entries);
        Assert.Contains("DllProbe", entries[0]);
        Assert.DoesNotContain("FlagOnly", entries[0]);
    }

    private static byte[] BuildPackagedApp()
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{AppId}" Name="{DepName}" Publisher="repro" Version="1.0.0.0" Target="Cloud" ShowMyCode="True" />
              <IdRanges><IdRange MinObjectId="50200" MaxObjectId="50249" /></IdRanges>
              <Dependencies />
              <ResourceExposurePolicy AllowDebugging="true" AllowDownloadingSource="true" IncludeSourceInSymbolFile="true" />
            </Package>
            """;
        // Describes what the DLL has: field 1 only.
        var symbols = $$"""
            {"Tables":[{"Id":50200,"Name":"DllProbe","Fields":[{"Id":1,"Name":"Code","TypeDefinition":{"Name":"Code[10]"},"Properties":[]}],"Keys":[{"Name":"PK","FieldNames":["Code"],"Properties":[{"Name":"Clustered","Value":"1"}]}],"ReferenceSourceFileName":"DllProbe.Table.al","Properties":[]}],"AppId":"{{AppId}}","Name":"{{DepName}}","Publisher":"repro","Version":"1.0.0.0"}
            """;
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
            // Without the OPC content-types part BC's compiler rejects the package (AL1023).
            Add("[Content_Types].xml", """
                <?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="" /><Default Extension="al" ContentType="" /><Default Extension="json" ContentType="" /></Types>
                """);
            Add("NavxManifest.xml", manifest);
            Add("SymbolReference.json", symbols);
            Add("src/DllProbe.Table.al", GatedTable);
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
