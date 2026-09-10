// LibraryAssertPlatformlessConsumerTests — #3719: a bundle depending on Microsoft's Library
// Assert failed before any test ran: `[dep-load-fail] Microsoft_Library Assert v28.x: EMIT-ZERO`.
//
// The trigger is the CONSUMER's app.json declaring neither `platform` nor `application` (AL
// requires neither; LethAL's sandbox-data fixture has neither). Library Assert's own manifest
// declares Platform="28.0.0.0" and no <Dependencies>, and DependencyResolver followed only a
// resolved package's <Dependencies>, never its floors — so with no consumer-side `platform`
// root either, System.app was not in the closure Library Assert was source-compiled against:
// BC's emitter saw `Table 'Field' is missing` and `namespace 'Reflection' is unknown` and died
// with `Unexpected value 'None' of type NavTypeKind` at TypeOf(Variant), which the loader
// reports as EMIT-ZERO. Adding `"platform"` to the consumer alone made the fixture pass.
//
// This spawns the runner on that exact shape with the platform and test-toolkit packages
// already on disk and provisioning off, so it proves the RESOLUTION half: the closure must
// carry System.app because the dependency asks for it. The provisioning half (a fresh box must
// download the platform set when the toolkit is needed) is ProvisioningCheckTests.
// Skips (not passes) when no Library Assert package is provisioned on the box.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class LibraryAssertPlatformlessConsumerTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public LibraryAssertPlatformlessConsumerTests()
    {
        _root = TestScratch.Dir("al-runner-libassert-platformless");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>What one provisioned location supplies: both directories, and the Library
    /// Assert package's OWN manifest, which is what the consumer must ask for.</summary>
    private sealed record Provisioned(string TestApps, string PlatformApps, AppManifest LibraryAssert);

    /// <summary>
    /// A test-apps directory holding Microsoft_Library Assert.app together with a platform-apps
    /// directory holding System.app. Both come from ONE candidate location — the legacy
    /// <c>~/.al-runner</c> layout CI populates, or one version directory under the artifacts
    /// root — never mixed across versions, which would measure a pairing no real run produces.
    /// Null when no single location has both, or when the package's manifest will not parse.
    ///
    /// The manifest comes back with it because the toolkit's version tracks the BC leg: the
    /// 27.5 leg provisions Library Assert v27.x, the 28.4 leg v28.x. A consumer asking for a
    /// hardcoded minimum resolves nothing on the lower leg (DependencyResolver skips a
    /// candidate whose version is below the request), so the bundle fails to compile for a
    /// reason that has nothing to do with #3719 — measured as a red 27.5 leg on PR #3793,
    /// reported as the bundle's own EMIT-ZERO rather than the dependency's.
    /// </summary>
    private static Provisioned? FindProvisionedDirs()
    {
        var home = TestArtifacts.HomeDir();
        var candidates = new List<(string TestApps, string PlatformApps)>();
        if (home != null)
            candidates.Add((Path.Combine(home, ".al-runner", "test-apps"), Path.Combine(home, ".al-runner", "platform-apps")));
        var root = AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir;
        if (Directory.Exists(root))
            foreach (var ver in Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal))
                candidates.Add((Path.Combine(ver, "test-apps"), Path.Combine(ver, "platform-apps")));

        foreach (var c in candidates)
        {
            var assertPath = Path.Combine(c.TestApps, "Microsoft_Library Assert.app");
            if (!File.Exists(assertPath) || !File.Exists(Path.Combine(c.PlatformApps, "System.app"))) continue;
            var manifest = AppLoader.ReadManifest(assertPath);
            if (manifest != null) return new Provisioned(c.TestApps, c.PlatformApps, manifest);
        }
        return null;
    }

    private void WriteBundles(AppManifest libraryAssert, out string app, out string tests)
    {
        app = Path.Combine(_root, "app");
        tests = Path.Combine(_root, "tests");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(tests);
        // Deliberately NO "platform" and NO "application" in either manifest — that is the shape.
        File.WriteAllText(Path.Combine(app, "app.json"), """
        {
          "id": "5a3f0b11-3719-4a10-8010-000000003719",
          "name": "LAPC App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 63700, "to": 63709 } ],
          "runtime": "13.0"
        }
        """);
        File.WriteAllText(Path.Combine(app, "Helper.Codeunit.al"), """
        codeunit 63700 "LAPC Helper"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);
        // The Library Assert dependency is written at the version this box actually has, read
        // from the package's own manifest — see FindProvisionedDirs. `al` generates the entry
        // the same way, from the package it compiled against.
        File.WriteAllText(Path.Combine(tests, "app.json"), $$"""
        {
          "id": "5a3f0b11-3719-4a11-8011-000000003719",
          "name": "LAPC Tests",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{libraryAssert.AppId}}", "name": "{{libraryAssert.Name}}", "publisher": "{{libraryAssert.Publisher}}", "version": "{{libraryAssert.Version}}" },
            { "id": "5a3f0b11-3719-4a10-8010-000000003719", "name": "LAPC App", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "idRanges": [ { "from": 63710, "to": 63719 } ],
          "runtime": "13.0"
        }
        """);
        File.WriteAllText(Path.Combine(tests, "T.Codeunit.al"), """
        codeunit 63710 "LAPC Tests"
        {
            Subtype = Test;

            var
                Assert: Codeunit "Library Assert";

            [Test]
            procedure HelperAnswersThroughLibraryAssert()
            var
                H: Codeunit "LAPC Helper";
            begin
                Assert.AreEqual(42, H.Answer(), 'helper');
            end;
        }
        """);
    }

    private (string Output, int Exit) RunRunner(string app, string tests, string testApps, string platformApps)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-cache --no-auto-provision --isolation test");
        args.Append($" --package-cache \"{testApps}\" --package-cache \"{platformApps}\"");
        args.Append($" \"{app}\" \"{tests}\"");
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
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// RED before the fix: `[dep-load-fail] Microsoft_Library Assert v…: EMIT-ZERO`, `FATAL:
    /// dependency compile failed`, exit 1, no test ran. After: Library Assert compiles against
    /// System.app, the one test runs through Assert.AreEqual and passes, exit 0.
    /// </summary>
    [SkippableFact]
    public void ConsumerWithoutPlatformKey_LibraryAssertDependency_CompilesAndRuns()
    {
        TestArtifacts.SkipIfMissing();
        var dirs = FindProvisionedDirs();
        // On CI the toolkit and platform sets are provisioned, so their absence is a broken leg
        // rather than an unavailable environment — fail, do not skip, matching
        // TestArtifacts.SkipIfMissingIn's own rule.
        if (dirs == null && TestArtifacts.RunningOnCi)
            Assert.Fail("no Microsoft_Library Assert.app + System.app pair in one provisioned location; "
                + "CI provisions both (al-runner provision --test-apps --platform-apps).");
        TestArtifacts.SkipIf(dirs == null,
            "no Microsoft_Library Assert.app + System.app pair in one provisioned location on this box.");
        WriteBundles(dirs!.LibraryAssert, out var app, out var tests);

        var (output, exit) = RunRunner(app, tests, dirs.TestApps, dirs.PlatformApps);

        // Each of these prints the whole run. A bare Assert.DoesNotContain reports only the
        // ~40 characters around the hit, which on the red 27.5 leg of PR #3793 named the
        // symptom and hid the AL errors underneath it.
        Assert.False(output.Contains("EMIT-ZERO", StringComparison.Ordinal),
            $"EMIT-ZERO in the run against Library Assert v{dirs.LibraryAssert.Version}. Output:\n{output}");
        Assert.False(output.Contains("dep-load-fail", StringComparison.Ordinal),
            $"a dependency failed to load. Output:\n{output}");
        Assert.True(Regex.IsMatch(output, @"PASS\s+\S*\bHelperAnswersThroughLibraryAssert\b"),
            $"no PASS line for HelperAnswersThroughLibraryAssert. Output:\n{output}");
        Assert.Equal(0, exit);
    }
}
