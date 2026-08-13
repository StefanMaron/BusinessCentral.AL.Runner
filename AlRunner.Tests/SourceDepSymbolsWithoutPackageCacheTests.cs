using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// A bundle whose only dependency is a SIBLING SOURCE app must compile against that
/// dep even when the run has <b>no package-cache directory at all</b>.
///
/// The sibling source-dependency pre-pass (Program.cs <c>BuildSiblingSourceDeps</c>)
/// splits the handoff in two: a synthetic <c>.app</c> carries the RUNTIME half, and a
/// <c>*.symbols.json</c> sidecar written by <c>BcCompiler.EmitDepSymbols</c> carries the
/// COMPILE half (the synthetic .app has no <c>SymbolReference.json</c>, so BC's own .app
/// scanner cannot serve it). <c>GetSharedReferences</c> is what wires that sidecar in, via
/// a <c>JsonSymbolReferenceLoader</c> chain over <c>_extraSymbolDirs</c>.
///
/// It used to build that chain only AFTER an early
/// <c>if (loaderScanDirs.Count == 0) return (null, empty)</c> bail-out — so with zero
/// package dirs the sidecar was never read. The dep loaded fine at runtime
/// (<c>loaded 1 dep assembl(ies)</c>) and was invisible to the compiler:
/// <code>
///   error AL0185: Codeunit 'X' is missing
///   emit-crash: … — Unexpected value 'None' of type NavTypeKind
///   &lt;bundled&gt;: EMIT-ZERO — 0 sources emitted
/// </code>
/// (the emit-crash is BC's emitter meeting the now-unresolved local variable type; the
/// AL0185 above it is the real cause).
///
/// Zero package dirs is not exotic — it is exactly what CI does. The default set is
/// <c>~/.bcartifacts.cache/sandbox/&lt;ver&gt;/…</c>,
/// <c>~/.local/share/al-runner/symbols/&lt;ver&gt;</c> and the provisioned
/// <c>&lt;artifacts&gt;/&lt;ver&gt;/{test-apps,platform-apps}</c>; a CI runner has none of
/// them (it downloads to <c>~/.al-runner/…</c> and passes <c>--package-cache</c> explicitly
/// on the steps that need it), while a dev box that once ran <c>--auto-provision</c> has
/// them and silently took the working path.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
/// </summary>
public class SourceDepSymbolsWithoutPackageCacheTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    [SkippableFact]
    public void SiblingSourceDep_CompilesWithZeroPackageCacheDirs()
    {
        TestArtifacts.SkipIfMissing();

        var scratchRoot = Path.Combine(
            Path.GetTempPath(), "al-runner-srcdep-nopkgcache", Guid.NewGuid().ToString("N"));
        var depDir = Path.Combine(scratchRoot, "dep-app");
        var testsDir = Path.Combine(scratchRoot, "tests-app");
        var alCacheDir = Path.Combine(scratchRoot, "al-out");
        // Deliberately NEVER created: ExpandPackageCacheDirs drops non-existent dirs, so
        // passing it takes the explicit-arg branch and resolves to the EMPTY set rather
        // than falling back to DefaultPackageCacheDirs. That reproduces CI's
        // "package caches: 0 dir(s)" on any machine, provisioned or not.
        var absentPackageCache = Path.Combine(scratchRoot, "no-such-package-cache");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testsDir);

        // Fresh identities per run so no cache (workspace-deps, compiled-deps, AL output)
        // from a previous run of this test can answer for the dep.
        var depId = Guid.NewGuid();
        var testsId = Guid.NewGuid();

        // Nothing Microsoft is referenced (platform/application 1.0.0.0, no declared
        // deps), so zero package caches is a legitimate configuration for this bundle.
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "Repro1731B Dep App",
          "publisher": "Repro1731B",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61930, "to": 61939 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Repro1731BDep.al"), """
        codeunit 61930 "Repro1731B Service"
        {
            procedure Echo(Input: Text): Text
            begin
                exit('dep:' + Input);
            end;

            procedure Boom()
            begin
                Error('dep exploded');
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "Repro1731B Tests",
          "publisher": "Repro1731B",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "Repro1731B Dep App", "publisher": "Repro1731B", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61940, "to": 61949 } ],
          "runtime": "14.0"
        }
        """);
        // Positive: the dep procedure returns its REAL value (not '' from a blank
        // auto-generated shell). Negative: the dep's Error surfaces with its exact text.
        // Both fail loudly rather than silently degrading, so neither passes against a
        // stubbed-out dependency.
        File.WriteAllText(Path.Combine(testsDir, "Repro1731BTests.al"), """
        codeunit 61940 "Repro1731B Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepProcedureReturnsRealValue()
            var
                Svc: Codeunit "Repro1731B Service";
                Actual: Text;
            begin
                Actual := Svc.Echo('abc');
                if Actual <> 'dep:abc' then
                    Error('Expected ''dep:abc'', got ''%1''', Actual);
            end;

            [Test]
            procedure DepProcedureErrorSurfaces()
            var
                Svc: Codeunit "Repro1731B Service";
                Actual: Text;
            begin
                asserterror Svc.Boom();
                Actual := GetLastErrorText();
                if Actual <> 'dep exploded' then
                    Error('Expected ''dep exploded'', got ''%1''', Actual);
            end;
        }
        """);

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{testsDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        args.Append($" --package-cache \"{absentPackageCache}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output;
        lock (sb) output = sb.ToString();

        // Precondition: the configuration under test really is the zero-package-cache
        // one. If the runner ever falls back to the default dirs here, this test would
        // stop covering the CI path and go green for the wrong reason.
        Assert.Contains("package caches: 0 dir(s)", output);
        // The dep must be BOTH runtime-loadable and compile-visible.
        Assert.Contains("loaded 1 dep assembl(ies)", output);
        Assert.DoesNotContain("AL0185", output);
        Assert.DoesNotContain("EMIT-ZERO", output);
        Assert.True(p.ExitCode == 0 && output.Contains("2P/0F/0E"),
            $"sibling source dep must compile + run with zero package-cache dirs:\n{output}");
    }
}
