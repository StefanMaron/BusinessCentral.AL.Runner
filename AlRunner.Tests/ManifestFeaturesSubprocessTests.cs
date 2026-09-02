// ManifestFeaturesSubprocessTests — #1941: app.json `features` -> NavCA.CompilerFeatures.
//
// Why this is a SUBPROCESS test, not an in-process BcCompiler.Emit() call
// -------------------------------------------------------------------------
// NoImplicitWith's whole observable effect is whether the implicit-with binder lets a
// SourceTable record's own members (procedures, in this fixture) SHADOW a page's own
// local variable/procedure of the same bare name. Measured directly: calling
// BcCompiler.Emit() with no package cache wired produces ZERO AL0129/AL0135 diagnostics
// for the exact repro from #1941, REGARDLESS of whether "features": ["NoImplicitWith"] is
// declared — the shadowing binder pathway needs the full symbol-resolution context a real
// run provides (a --package-cache pointing at the platform apps). A test that passes
// identically whether the fix exists or not is exactly the noise .claude/rules/tdd.md
// warns about, so this class spawns the real runner instead — the same way the issue's own
// reproduction did (see #1941's "Reproduction" section, which used the CLI with
// --package-cache, not a bare compiler call).
//
// Two pairs, both directions:
//   - Top-level bundle (BcCompiler.Emit): manifest declares NoImplicitWith -> the page
//     compiles and its test passes; manifest omits it -> the SAME AL still fails
//     AL0129/AL0135 and the page is EMIT-EXCLUDED (exit 3).
//   - Source dependency (BcCompiler.EmitDepSymbols, via the layered pre-pass): a dep
//     declaring NoImplicitWith in its OWN manifest compiles under it too, mirroring
//     LayeredDepManifestTests' shape for the sibling #1898/contextSensitiveHelpUrl case.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ManifestFeaturesSubprocessTests : IClassFixture<SharedCliServer>, IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;
    private readonly SharedCliServer _shared;

    public ManifestFeaturesSubprocessTests(SharedCliServer shared)
    {
        _shared = shared;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-manifest-features-subprocess", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>#2377: the one `--cache` dir this class's shared server is started with,
    /// fresh per test run so nothing an earlier invocation cached can answer here.</summary>
    private static readonly string CacheDir = Path.Combine(
        Path.GetTempPath(), "al-runner-manifest-features-cache", Guid.NewGuid().ToString("N"));

    private static IEnumerable<string> ServerArgs()
    {
        yield return "--cache";
        yield return CacheDir;
        foreach (var a in ExtraPackageCacheArgs()) yield return a;
    }

    /// <summary>
    /// The same two env vars <see cref="RunRunner"/> sets on its spawn, hoisted to the
    /// server process (SharedCliServer applies them on the spawning call only). They
    /// widen what the runner REPORTS about an emit-retry exclusion — without them the
    /// EMIT-EXCLUDED payload names only the excluded OBJECT and never the AL0129/AL0135
    /// diagnostics that identified it — and change nothing about what it compiles, so
    /// they are safe to apply to every fact in the class rather than per request.
    /// </summary>
    private static readonly Dictionary<string, string> DiagEnv = new()
    {
        ["AL_RUNNER_DIAG_EMITRETRY"] = "1",
        ["BCCOMPILER_DIAG"] = "1",
    };

    private static string Req(params string[] bundles)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = bundles,
            packagePaths = Array.Empty<string>(),
        });

    /// <summary>
    /// The summary line's compile errors as one string. This is where the server surfaces
    /// EMIT-EXCLUDED, EMIT-ZERO, COMPILE-FAIL, LAYERED-PREPASS-FAIL and BC's own AL
    /// diagnostics (RunBundleForServer / RunAllBundlesForServer) — i.e. every string the
    /// CLI-spawning version of these facts used to look for in the console dump, but
    /// scoped to THIS request and carried by the protocol rather than scraped.
    /// </summary>
    private static string CompileErrorText(JsonElement summary)
    {
        if (!summary.TryGetProperty("compilationErrors", out var ce) || ce.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var sb = new StringBuilder();
        foreach (var group in ce.EnumerateArray())
        {
            if (group.TryGetProperty("file", out var f)) sb.AppendLine(f.GetString());
            if (group.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                foreach (var e in errs.EnumerateArray()) sb.AppendLine(e.GetString());
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string[] ExtraPackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    // NOTE (do not re-add a BC-version floor here without re-measuring): an earlier
    // revision of this class carried a MeetsNoImplicitWithBcFloor() skip gate, added
    // because the fixture's app.json used to declare platform 28.0.0.0 / application
    // 28.1.0.0 — a hardcoded BC-28.1-specific pair, copied from #1941's own repro —
    // while the CI matrix compiles each leg against a DIFFERENT-major BC artifact. That
    // mismatch, not any real difference in BC's implicit-with binder, was what made
    // AL0129/AL0135 stop reproducing on BC 27.0/27.3/27.5/28.0: the compiler was being
    // asked to honour a manifest for a platform it wasn't. Once platform/application were
    // fixed to the version-agnostic 1.0.0.0 (see WriteNoImplicitWithFixture below), the
    // hazard was confirmed to reproduce identically on BC 27.0 and BC 28.1 (rebuilt the
    // runner with -p:_BCVersion=27.0.38460.53260 and ran both directions directly — same
    // AL0129/AL0135-on-omit, same clean compile-with-NoImplicitWith-declared, on both).
    // The gate was built on the pre-fix, confounded evidence and never re-validated
    // against the corrected fixture before being added — it was measuring the mismatch
    // bug, not a genuine version split.

    private static (string output, int exit) RunRunner(params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var a in ExtraPackageCacheArgs()) args.Append($" \"{a}\"");
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        // Without these, an EMIT-EXCLUDED bundle's default (non-diag) output names only
        // the excluded OBJECT ("Re-run with --verbose for the AL diagnostics that
        // identified them") — it never prints the AL0129/AL0135 diagnostic IDs themselves.
        // Matches the exact env vars #1941's own reproduction command used.
        psi.EnvironmentVariables["AL_RUNNER_DIAG_EMITRETRY"] = "1";
        psi.EnvironmentVariables["BCCOMPILER_DIAG"] = "1";
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    // The exact fixture from #1941: an unqualified bare-name assignment/call in a page's
    // trigger that BC's implicit-with binder resolves against the SourceTable record's own
    // same-named members instead of the page's own local var/procedure, UNLESS
    // NoImplicitWith is on.
    /// <param name="tag">
    /// #2377: a per-FACT tag folded into the app name AND the AL object names. Fresh GUID
    /// app ids alone are not enough once these facts share one server process: dependency
    /// resolution matches on name/publisher/version and RunAllBundlesForServer accumulates
    /// each request's layered workspace dirs into the server-level packageCacheDirs, so an
    /// app one fact built stays resolvable by name for every later fact. AL object names
    /// matter for the same reason one layer down — AL resolves by name, .NET cannot unload
    /// an assembly, and three facts compiling three same-named "MFS NIW Table"s into one
    /// process is ambiguity fed straight into the resolution the runner has to get right.
    /// The measured cost of getting this wrong is not a crash: LayeredSourceChainTests'
    /// absent-dependency fact silently stopped failing for its own reason when a sibling
    /// fact's identically-named app was still in the search set.
    /// </param>
    private static void WriteNoImplicitWithFixture(
        string dir, int tableId, int pageId, string featuresLine, string tag, string? appId = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId ?? Guid.NewGuid().ToString()}}",
          "name": "MFS Repro App {{tag}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{Math.Min(tableId, pageId)}}, "to": {{Math.Max(tableId, pageId) + 5}} } ],
          "runtime": "17.0"{{featuresLine}}
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Niw.Table.al"), $$"""
        table {{tableId}} "MFS NIW Table {{tag}}"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "No."; Code[20]) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "No.") { Clustered = true; } }

            procedure IsFlagged(): Boolean
            begin
                exit("No." <> '');
            end;

            procedure Refresh(Delta: Integer)
            begin
                "No." := Format(Delta);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Niw.Page.al"), $$"""
        page {{pageId}} "MFS NIW Page {{tag}}"
        {
            PageType = Card;
            ApplicationArea = All;
            UsageCategory = Administration;
            SourceTable = "MFS NIW Table {{tag}}";

            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                    }
                }
            }

            var
                IsFlagged: Boolean;

            trigger OnAfterGetRecord()
            begin
                IsFlagged := Rec.IsFlagged();
                Refresh();
            end;

            local procedure Refresh()
            begin
                IsFlagged := false;
            end;
        }
        """);
    }

    // ── Top-level bundle (BcCompiler.Emit) ────────────────────────────────────────────

    [SkippableFact]
    public async Task TopLevel_ManifestDeclaresNoImplicitWith_CompilesCleanly()
    {
        TestArtifacts.SkipIfMissing();
        WriteNoImplicitWithFixture(_root, 61060, 61061, ",\n  \"features\": [ \"NoImplicitWith\" ]", "TLPos");

        var server = await _shared.GetAsync(ServerArgs(), DiagEnv);
        var lines = await server.SendRequestStreamingAsync(Req(_root), TimeSpan.FromSeconds(300));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        var compileErrors = CompileErrorText(summary);
        Assert.DoesNotContain("AL0129", compileErrors);
        Assert.DoesNotContain("AL0135", compileErrors);
        Assert.DoesNotContain("EMIT-EXCLUDED", compileErrors);
        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
    }

    [SkippableFact]
    public async Task TopLevel_ManifestOmitsFeatures_SameAlStillFailsAL0129AL0135()
    {
        TestArtifacts.SkipIfMissing();
        WriteNoImplicitWithFixture(_root, 61070, 61071, "", "TLNeg");

        var server = await _shared.GetAsync(ServerArgs(), DiagEnv);
        var lines = await server.SendRequestStreamingAsync(Req(_root), TimeSpan.FromSeconds(300));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        // The server has its OWN EMIT-EXCLUDED guard (Program.cs, RunBundleForServer —
        // added by #2152 precisely because the server path used to run the surviving
        // objects and report exitCode 0 with a whole test codeunit missing), so this is
        // the same claim asserted against the code path an editor integration actually
        // drives, not a weaker restatement of the CLI one.
        var compileErrors = CompileErrorText(summary);
        Assert.Contains("AL0129", compileErrors);
        Assert.Contains("AL0135", compileErrors);
        Assert.Contains("EMIT-EXCLUDED", compileErrors);
        Assert.Equal(3, summary.GetProperty("exitCode").GetInt32());
    }

    // ── Source dependency (BcCompiler.EmitDepSymbols via the layered pre-pass) ───────

    private static void WriteMainDependingOn(string dir, string depId, string depName, int idFrom, string tag)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "MFS Main App {{tag}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "{{depName}}", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 9}} } ],
          "runtime": "17.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{idFrom}} "MFS Main Tests {{tag}}"
        {
            Subtype = Test;

            [Test]
            procedure DummyPasses()
            begin
                // The layered pre-pass must reach this bundle's own compile+run at all —
                // proving the DEP compiled (and was not skipped/errored out) is enough here.
            end;
        }
        """);
    }

    [SkippableFact]
    public async Task SourceDependency_ManifestDeclaresNoImplicitWith_CompilesCleanly_BothBundlesRun()
    {
        TestArtifacts.SkipIfMissing();

        var depDir = Path.Combine(_root, "dep");
        var mainDir = Path.Combine(_root, "main");
        var depId = Guid.NewGuid().ToString();
        WriteNoImplicitWithFixture(depDir, 61080, 61081, ",\n  \"features\": [ \"NoImplicitWith\" ]", "DepPos", depId);
        WriteMainDependingOn(mainDir, depId, "MFS Repro App DepPos", 61090, "DepPos");

        var server = await _shared.GetAsync(ServerArgs(), DiagEnv);
        var mark = server.StdErrMark;
        var lines = await server.SendRequestStreamingAsync(Req(depDir, mainDir), TimeSpan.FromSeconds(300));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        var compileErrors = CompileErrorText(summary);
        Assert.DoesNotContain("AL0129", compileErrors);
        Assert.DoesNotContain("AL0135", compileErrors);
        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        Assert.Equal(0, summary.GetProperty("errors").GetInt32());

        // Precondition: this really took the layered two-bundle source-dependency path,
        // not a degenerate single-bundle one.
        var stderr = await server.StdErrSinceAsync(mark, "[layered] pre-built");
        Assert.Contains("MFS Repro App DepPos", stderr);
    }

    [SkippableFact]
    public void SourceDependency_ManifestOmitsFeatures_StillFailsAL0129AL0135_AsFormattedCompileFail()
    {
        TestArtifacts.SkipIfMissing();

        var depDir = Path.Combine(_root, "dep");
        var mainDir = Path.Combine(_root, "main");
        var depId = Guid.NewGuid().ToString();
        WriteNoImplicitWithFixture(depDir, 61100, 61101, "", "DepNeg", depId);
        WriteMainDependingOn(mainDir, depId, "MFS Repro App DepNeg", 61110, "DepNeg");

        // #2377: NOT converted to the shared server, unlike the three facts above. Its
        // decisive assertion is "Unhandled exception" being ABSENT — i.e. that a dep whose
        // manifest is genuinely invalid produces a formatted exit 3 rather than the CLR's
        // default handler aborting the PROCESS with exit 134, which is what #1898 fixed.
        // That is a claim about the CLI's own Main-level exception handling, and it can
        // only be observed by watching a process exit. RunAllBundlesForServer wraps the
        // same pre-pass in its own try/catch, so a server request cannot reach the code
        // path this fact exists to hold down — converting it would trade a true slow test
        // for a fast one asserting something else. Same reasoning keeps all of
        // LayeredDepManifestTests spawning.
        var (output, exit) = RunRunner(depDir, mainDir);

        Assert.Contains("AL0129", output);
        // Must be a formatted, documented runner outcome — never the raw CLR
        // unhandled-exception path #1898 fixed for the sibling contextSensitiveHelpUrl case.
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.Equal(3, exit);
    }
}
