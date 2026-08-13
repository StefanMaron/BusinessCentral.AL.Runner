using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #1731 — a source-compiled dependency served from the `compiled-deps`
/// cache (Tier 3 of <see cref="DependencyLoader"/>) on a "dep HIT + bundle MISS"
/// run loses its enum metadata: the dep's own emit is skipped (cache HIT), and the
/// bundle's own emit only registers the ENUMS ITS OWN sources declare — the dep's
/// enums are simply never (re-)registered in <see cref="AlEnumMetadataRegistry"/>
/// for that process. Enum-to-interface dispatch on a dep enum then throws
/// <c>InvalidOperationException: Unable to cast enum '' value '0' to interface</c>
/// (the enum's Name comes back blank because no entry exists at all).
///
/// Root cause: unlike the bundle's own AL-output cache (`.dll` + `.enum-registry.json`
/// sidecar, replayed on HIT — see Program.cs `SaveEnumRegistrySidecar`/
/// `LoadEnumRegistrySidecar`), the dependency-loader's source-dep cache
/// (`<cache-root>/compiled-deps/<key>.dll`) persisted only report/page/xmlport
/// metadata sidecars — no enum-registry sidecar — so a cache HIT for the dep replayed
/// everything except its enums.
///
/// Reproduces the exact two-process sequence from the issue: two SEPARATE runner
/// invocations sharing the same `--cache` dir (matching `rm -rf ~/.cache/al-runner;
/// al-runner ... tests` twice in the issue — before #1821, `compiled-deps` ignored
/// `--cache` entirely and always used the real `~/.cache/al-runner/compiled-deps`;
/// since #1821 it follows `--cache` like every other cache, so this test now pins an
/// isolated scratch dir instead of the real shared one — the two-invocations-share-a-
/// cache setup below is unaffected either way, since both calls pass the identical
/// `alCacheDir` value). Uses a fresh random AppId + a fresh scratch dir per test run,
/// so the dep's Tier-3 cache key has never been seen before — run 1 is guaranteed a
/// dep MISS (in-process compile, so the bug cannot manifest there: this is exactly why
/// the issue itself observed "fresh cache -> PASS" every time). Touching a `tests`-
/// bundle source file between the two runs forces the BUNDLE's own cache key to change
/// (content hash) while the DEP's synthetic `.app` bytes (and therefore its Tier-3
/// cache key) stay byte-identical — producing the exact "dep HIT + bundle MISS"
/// condition the bug requires.
///
/// RED (pre-fix): run 2 fails with "Unable to cast enum '' value '0' to interface".
/// GREEN (post-fix): both runs pass identically.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
/// </summary>
public class SourceDepCacheEnumMetadataTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundleDir, string alCacheDir, string absentPackageCache)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        // Pin `package caches: 0 dir(s)` — the fixture depends on NOTHING Microsoft
        // (platform/application 1.0.0.0, no declared deps), so no package cache is
        // needed, and passing an --package-cache path that does not exist makes the
        // runner take the explicit-arg branch and resolve it to the empty set
        // (ExpandPackageCacheDirs skips non-existent dirs) instead of falling back to
        // DefaultPackageCacheDirs.
        //
        // Why pin it: the default set includes `<artifacts>/<version>/test-apps` and
        // `<artifacts>/<version>/platform-apps`, which a dev machine that has ever run
        // --auto-provision HAS and a CI runner (which downloads to ~/.al-runner/… and
        // passes --package-cache explicitly on other steps) does NOT. That single
        // difference decided whether this test passed: with zero package dirs the
        // compiler's reference loader used to bail out before the source-dep
        // *.symbols.json chain was built, so the dep was compile-invisible (AL0185) and
        // the bundle emitted zero sources. Green locally, red on all 8 BC legs. Pinning
        // it means the harder configuration is the one that is always tested.
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
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void SourceDepCacheHit_BundleMiss_KeepsDepEnumMetadata()
    {
        TestArtifacts.SkipIfMissing();

        var scratchRoot = Path.Combine(Path.GetTempPath(), "al-runner-depcache-enum", Guid.NewGuid().ToString("N"));
        var depDir = Path.Combine(scratchRoot, "dep-app");
        var testsDir = Path.Combine(scratchRoot, "tests-app");
        var alCacheDir = Path.Combine(scratchRoot, "al-out");
        // Deliberately NEVER created — see RunRunner.
        var absentPackageCache = Path.Combine(scratchRoot, "no-such-package-cache");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testsDir);

        // Fresh random identities every run: guarantees the dep's Tier-3 cache key
        // (which hashes the app's own bytes + identity) has never been seen before,
        // so run 1 is unconditionally a dep MISS — the only way to reach a genuine
        // "dep HIT" on run 2 without the bug's fresh-cache masking (see class doc).
        var depId = Guid.NewGuid();
        var testsId = Guid.NewGuid();

        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "Repro1731 Dep App",
          "publisher": "Repro1731",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61900, "to": 61909 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Repro1731Dep.al"), """
        interface "Repro1731 IGreeter"
        {
            procedure Greet(): Text;
        }

        codeunit 61900 "Repro1731 Greeter" implements "Repro1731 IGreeter"
        {
            procedure Greet(): Text
            begin
                exit('hello');
            end;
        }

        enum 61901 "Repro1731 Greeter Kind" implements "Repro1731 IGreeter"
        {
            value(0; Default)
            {
                Implementation = "Repro1731 IGreeter" = "Repro1731 Greeter";
            }
        }

        codeunit 61902 "Repro1731 Service"
        {
            procedure GreetViaEnum(): Text
            var
                Greeter: Interface "Repro1731 IGreeter";
                Kind: Enum "Repro1731 Greeter Kind";
            begin
                Kind := Kind::Default;
                Greeter := Kind;
                exit(Greeter.Greet());
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "Repro1731 Tests",
          "publisher": "Repro1731",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "Repro1731 Dep App", "publisher": "Repro1731", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61910, "to": 61919 } ],
          "runtime": "14.0"
        }
        """);
        var testsAlPath = Path.Combine(testsDir, "Repro1731Tests.al");
        File.WriteAllText(testsAlPath, """
        codeunit 61910 "Repro1731 Poison Test"
        {
            Subtype = Test;

            [Test]
            procedure EnumInterfaceDispatchWorks()
            var
                Service: Codeunit "Repro1731 Service";
                Result: Text;
            begin
                Result := Service.GreetViaEnum();
                if Result <> 'hello' then
                    Error('Expected hello, got ''%1''', Result);
            end;
        }
        """);

        // Run 1: fresh cache all round (dep MISS, bundle MISS) — must pass, and the
        // issue itself confirms this direction is never where the bug shows up.
        var (output1, exit1) = RunRunner(testsDir, alCacheDir, absentPackageCache);
        // Guard the precondition itself: if the runner ever stops honouring an
        // --package-cache that does not exist and silently falls back to the default
        // dirs, this test would quietly stop covering the zero-package-cache path
        // (the one CI actually runs) and go green for the wrong reason.
        Assert.Contains("package caches: 0 dir(s)", output1);
        // Name the specific regression this direction guards: with no package cache
        // dir the compiler used to skip building the source-dep *.symbols.json loader
        // chain entirely, so the sibling source dep was runtime-loadable but
        // compile-invisible.
        Assert.DoesNotContain("AL0185", output1);
        Assert.True(exit1 == 0 && output1.Contains("1P/0F/0E"),
            $"run 1 (fresh cache) must pass:\n{output1}");

        // Touch only the TESTS bundle's own source — changes the bundle's cache key
        // (forces bundle MISS) while leaving the dep's synthesized .app byte-identical
        // (dep stays a cache HIT).
        File.AppendAllText(testsAlPath, "\n// touched\n");

        // Run 2: dep HIT + bundle MISS — the exact condition from the issue.
        var (output2, exit2) = RunRunner(testsDir, alCacheDir, absentPackageCache);
        Assert.Contains("package caches: 0 dir(s)", output2);

        // The exact reported defect. Never let a run that hits this pass silently —
        // the assertion below is the whole point of the RED -> GREEN cycle.
        Assert.DoesNotContain("Unable to cast enum", output2);
        Assert.True(exit2 == 0 && output2.Contains("1P/0F/0E"),
            $"run 2 (dep HIT, bundle MISS) must ALSO pass — dep enum metadata must " +
            $"survive being served from the compiled-deps cache:\n{output2}");
    }
}
