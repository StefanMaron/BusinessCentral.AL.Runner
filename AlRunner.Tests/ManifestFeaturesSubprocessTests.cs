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
using Xunit;

namespace AlRunner.Tests;

public sealed class ManifestFeaturesSubprocessTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public ManifestFeaturesSubprocessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-manifest-features-subprocess", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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
    private static void WriteNoImplicitWithFixture(
        string dir, int tableId, int pageId, string featuresLine, string? appId = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId ?? Guid.NewGuid().ToString()}}",
          "name": "MFS Repro App",
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
        table {{tableId}} "MFS NIW Table"
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
        page {{pageId}} "MFS NIW Page"
        {
            PageType = Card;
            ApplicationArea = All;
            UsageCategory = Administration;
            SourceTable = "MFS NIW Table";

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
    public void TopLevel_ManifestDeclaresNoImplicitWith_CompilesCleanly()
    {
        TestArtifacts.SkipIfMissing();
        WriteNoImplicitWithFixture(_root, 61060, 61061, ",\n  \"features\": [ \"NoImplicitWith\" ]");

        var (output, exit) = RunRunner(_root);

        Assert.DoesNotContain("AL0129", output);
        Assert.DoesNotContain("AL0135", output);
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.Equal(0, exit);
    }

    [SkippableFact]
    public void TopLevel_ManifestOmitsFeatures_SameAlStillFailsAL0129AL0135()
    {
        TestArtifacts.SkipIfMissing();
        WriteNoImplicitWithFixture(_root, 61070, 61071, "");

        var (output, exit) = RunRunner(_root);

        Assert.Contains("AL0129", output);
        Assert.Contains("AL0135", output);
        Assert.Contains("EMIT-EXCLUDED", output);
        Assert.Equal(3, exit);
    }

    // ── Source dependency (BcCompiler.EmitDepSymbols via the layered pre-pass) ───────

    private static void WriteMainDependingOn(string dir, string depId, string depName, int idFrom)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "MFS Main App",
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
        codeunit {{idFrom}} "MFS Main Tests"
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
    public void SourceDependency_ManifestDeclaresNoImplicitWith_CompilesCleanly_BothBundlesRun()
    {
        TestArtifacts.SkipIfMissing();

        var depDir = Path.Combine(_root, "dep");
        var mainDir = Path.Combine(_root, "main");
        var depId = Guid.NewGuid().ToString();
        WriteNoImplicitWithFixture(depDir, 61080, 61081, ",\n  \"features\": [ \"NoImplicitWith\" ]", depId);
        WriteMainDependingOn(mainDir, depId, "MFS Repro App", 61090);

        var (output, exit) = RunRunner(depDir, mainDir);

        Assert.Contains("[layered]", output);
        Assert.DoesNotContain("AL0129", output);
        Assert.DoesNotContain("AL0135", output);
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"),
            $"a dependency whose manifest genuinely declares NoImplicitWith must compile and run (exit {exit}):\n{output}");
    }

    [SkippableFact]
    public void SourceDependency_ManifestOmitsFeatures_StillFailsAL0129AL0135_AsFormattedCompileFail()
    {
        TestArtifacts.SkipIfMissing();

        var depDir = Path.Combine(_root, "dep");
        var mainDir = Path.Combine(_root, "main");
        var depId = Guid.NewGuid().ToString();
        WriteNoImplicitWithFixture(depDir, 61100, 61101, "", depId);
        WriteMainDependingOn(mainDir, depId, "MFS Repro App", 61110);

        var (output, exit) = RunRunner(depDir, mainDir);

        Assert.Contains("AL0129", output);
        // Must be a formatted, documented runner outcome — never the raw CLR
        // unhandled-exception path #1898 fixed for the sibling contextSensitiveHelpUrl case.
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.Equal(3, exit);
    }
}
