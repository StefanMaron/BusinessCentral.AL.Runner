// ManifestRuntimeReportExtensionTests — issue #5471.
//
// A report and a reportextension of it that each declare a global of the same name are refused
// by alc with AL0155 when the app's manifest declares runtime 15.0 or lower, and legal from 16.0
// (alc 17.0 measured at runtime 13.0 through 17.0; the gate is the compiler's own
// `runtimeVersion <= RuntimeVersion.Spring2025` in SourceModuleSymbol, see the PR body). The
// runner built every ParseOptions with `runtimeVersion: null`, which the compiler reads as its
// current release, so it took the 16.0+ branch for every app and accepted the collision with
// exit 0 and no diagnostic. The manifest's `runtime` now reaches ParseOptions.
//
// Strategy: BcCompiler.Emit in-process (no spawn) for the diagnostic and for both controls, the
// manifest/ParseOptions/cache-key plumbing as pure unit tests, and ONE CLI spawn for the exit.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ManifestRuntimeReportExtensionTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public ManifestRuntimeReportExtensionTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-manifest-runtime-reportext");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void SkipIfEngineNotReady() =>
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static void WriteAppJson(string dir, string runtimeProp)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Manifest Runtime Report Extension Test App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 61100, "to": 61119 } ]{{runtimeProp}}
        }
        """);
    }

    private static string Runtime(string value) => $",\n  \"runtime\": \"{value}\"";

    /// <summary>A report and a reportextension of it, each declaring the global <paramref name="extensionVar"/>
    /// (the report's is always <c>Shared</c>), each trigger reading its own.</summary>
    private static void WriteReportPair(string dir, string extensionVar)
    {
        File.WriteAllText(Path.Combine(dir, "Pair.al"), $$"""
            table 61102 "MRRE Table"
            {
                fields { field(1; Code; Code[20]) { } }
                keys { key(PK; Code) { } }
            }

            report 61100 "MRRE Report"
            {
                ProcessingOnly = true;
                dataset { dataitem(Item; "MRRE Table") { } }

                trigger OnPreReport()
                begin
                    Shared := 1;
                end;

                var
                    Shared: Integer;
            }

            reportextension 61101 "MRRE Report Ext" extends "MRRE Report"
            {
                trigger OnPreReport()
                begin
                    {{extensionVar}} := 2;
                end;

                var
                    {{extensionVar}}: Integer;
            }
            """);
    }

    // ── the defect, at both runtimes alc refuses it ──────────────────────────────────────

    [SkippableTheory]
    [InlineData("14.0")]
    [InlineData("15.0")]
    public void Emit_RuntimeAtOrBelow15_SharedReportGlobal_RaisesAL0155OnBothDeclarations(string runtime)
    {
        SkipIfEngineNotReady();
        WriteAppJson(_root, Runtime(runtime));
        WriteReportPair(_root, "Shared");

        var output = new BcCompiler().Emit(new[] { _root }, "MrreCollideModule" + runtime.Replace('.', '_'), _root);

        // Distinct: Emit reports a declaration error once as such and once more through the failed EmitResult.
        var al0155 = output.Diagnostics.Where(d => d.Contains("AL0155")).Distinct().ToList();
        Assert.Equal(2, al0155.Count);
        Assert.Contains(al0155, d => d.Contains("GlobalVariable with name 'Shared'") && d.Contains("Report 'MRRE Report'"));
        Assert.Contains(al0155, d => d.Contains("GlobalVariable with name 'Shared'") && d.Contains("ReportExtension 'MRRE Report Ext'"));
    }

    // ── controls: it must not become a blanket refusal ───────────────────────────────────

    [SkippableFact]
    public void Emit_RuntimeAtOrBelow15_DistinctGlobalNames_IsAccepted()
    {
        SkipIfEngineNotReady();
        WriteAppJson(_root, Runtime("14.0"));
        WriteReportPair(_root, "Other");

        var output = new BcCompiler().Emit(new[] { _root }, "MrreDistinctModule", _root);

        Assert.DoesNotContain(output.Diagnostics, d => d.Contains("AL0155"));
        Assert.Contains(output.Sources, s => s.Name == "MRRE Report");
        Assert.Contains(output.Sources, s => s.Name == "MRRE Report Ext");
    }

    /// <summary>The same collision is legal from runtime 16.0 (alc measured), so the manifest's value is what
    /// decides it, not a refusal of the shape. Red when the runtime is ignored in the other direction.</summary>
    [SkippableTheory]
    [InlineData("16.0")]
    [InlineData("17.0")]
    public void Emit_RuntimeFrom16_SharedReportGlobal_IsAccepted(string runtime)
    {
        SkipIfEngineNotReady();
        WriteAppJson(_root, Runtime(runtime));
        WriteReportPair(_root, "Shared");

        var output = new BcCompiler().Emit(new[] { _root }, "MrreLegalModule" + runtime.Replace('.', '_'), _root);

        Assert.DoesNotContain(output.Diagnostics, d => d.Contains("AL0155"));
        Assert.Contains(output.Sources, s => s.Name == "MRRE Report Ext");
    }

    // ── the plumbing, without a compile ──────────────────────────────────────────────────

    private static BcCompiler.ManifestCompilerInputs Inputs(string dir, string runtimeProp)
    {
        WriteAppJson(dir, runtimeProp);
        return BcCompiler.ReadManifestCompilerInputs(Path.Combine(dir, "app.json"));
    }

    [Fact]
    public void ReadManifestCompilerInputs_ReadsTheDeclaredRuntime_AndBuildParseOptionsCarriesIt()
    {
        var inputs = Inputs(_root, Runtime("14.0"));

        Assert.Equal(new Version(14, 0), inputs.Runtime);
        Assert.Equal(new Version(14, 0), BcCompiler.BuildParseOptions(inputs).RuntimeVersion);
    }

    [Theory]
    [InlineData("")]                          // not declared
    [InlineData(",\n  \"runtime\": \"abc\"")] // not a version
    [InlineData(",\n  \"runtime\": 14")]      // not a string
    public void ReadManifestCompilerInputs_NoUsableRuntime_StaysNull(string runtimeProp)
        => Assert.Null(Inputs(_root, runtimeProp).Runtime);

    /// <summary>The AL-output cache key reads this fragment: without a runtime term an edit of `runtime` would replay
    /// the DLL compiled under the old one, and the refusal would depend on what ran before.</summary>
    [Fact]
    public void CacheKeyFragment_DiffersByRuntime()
    {
        var at14 = Inputs(_root, Runtime("14.0")).CacheKeyFragment;
        var at16 = Inputs(_root, Runtime("16.0")).CacheKeyFragment;
        var none = Inputs(_root, "").CacheKeyFragment;

        Assert.NotEqual(at14, at16);
        Assert.NotEqual(at14, none);
        Assert.NotEqual(at16, none);
    }

    // ── end to end: the CLI refuses it ───────────────────────────────────────────────────

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [SkippableFact]
    public void Cli_RuntimeAtOrBelow15_SharedReportGlobal_IsRefusedWithAL0155_NotExitZero()
    {
        SkipIfEngineNotReady();
        WriteAppJson(_root, Runtime("14.0"));
        WriteReportPair(_root, "Shared");

        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append(" --package-cache \"").Append(platformApps).Append('"');
        args.Append(" \"").Append(_root).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output; lock (sb) output = sb.ToString();

        Assert.True(p.ExitCode != 0, $"a compile alc refuses must not exit 0. Output:\n{output}");
        Assert.Contains("AL0155", output);
        Assert.Contains("'Shared'", output);
    }
}
